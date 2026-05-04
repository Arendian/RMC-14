using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Content.Client._RMC14.Photo;
using Content.Shared._RMC14.Camera.PhotoCamera;
using Content.Shared.Coordinates.Helpers;
using Content.Shared.Decals;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._RMC14.Camera;

public sealed class RMCPhotoCameraSystem : SharedRmcPhotoCameraSystem
{
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly IInputManager _inputManager = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;

    private readonly PhotoRenderControl _control = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<ReceiveStoredPhotoDescriptionEvent>(OnReceivedStoredPhotoDescription);

        SubscribeLocalEvent<RMCPhotoCameraComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<RMCPhotoComponent, ComponentStartup>(OnPhotoComponentStartup);
        SubscribeLocalEvent<RMCCachedPhotoComponent, ComponentRemove>(OnCachedPhotoRemove);

        _ui.RootControl.AddChild(_control);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _ui.RootControl.RemoveChild(_control);
    }

    private void OnReceivedStoredPhotoDescription(ReceiveStoredPhotoDescriptionEvent ev, EntitySessionEventArgs args)
    {
        var photo = GetEntity(ev.Photo);
        if (!TryComp(photo, out RMCPhotoComponent? photoComp))
            return;

        photoComp.ExamineText = ev.ExamineText;
    }

    private void OnAfterInteract(Entity<RMCPhotoCameraComponent> ent, ref AfterInteractEvent args)
    {
        if (!Timing.IsFirstTimePredicted || args.Handled)
            return;

        if (ent.Comp.PhotoPrintedAt != null)
            return;

        var world = _eye.PixelToMap(_inputManager.MouseScreenPosition);
        if (!_map.MapExists(world.MapId))
            return;

        var coordinates = TransformSystem.ToCoordinates(world);
        if (ent.Comp.AutoCenter)
            coordinates = coordinates.SnapToGrid();

        if (!coordinates.IsValid(EntityManager))
            return;

        if (ent.Comp.RemainingCharges <= 0)
        {
            Popup.PopupClient(Loc.GetString("rmc-photo-camera-make-photo-failed-empty", ("camera", ent)), args.User, args.User, PopupType.SmallCaution);
            return;
        }

        RaiseNetworkEvent(new RequestPhotoCaptureEvent(GetNetCoordinates(coordinates)));
    }

    private void OnPhotoComponentStartup(Entity<RMCPhotoComponent> ent, ref ComponentStartup args)
    {
        RaiseNetworkEvent(new RequestStoredPhotoDescriptionEvent(GetNetEntity(ent)));
    }

    private void OnCachedPhotoRemove(Entity<RMCCachedPhotoComponent> ent, ref ComponentRemove args)
    {
        ent.Comp.RenderTarget?.Dispose();
    }

    public bool TryGetPhoto(EntityUid photo, [NotNullWhen(true)] out Texture? photoTexture, out string photoName)
    {
        photoName = "";
        photoTexture = null;

        if (!TryComp(photo, out RMCPhotoComponent? photoComp))
            return false;

        photoName = photoComp.PhotoName;

        if (TryComp(photo, out RMCCachedPhotoComponent? cached) && cached.CachedPhoto != null)
        {
            photoTexture = cached.CachedPhoto;
            return true;
        }

        if (photoComp.Snapshot != null && !HasComp<RMCCachedPhotoComponent>(photo))
        {
            EnsureComp<RMCCachedPhotoComponent>(photo);
            _control.Queue.Enqueue((photo, photoComp.Snapshot));
        }

        return false;
    }

    public bool InPhotoRange(EntityUid uid)
    {
        return false;
    }

    private sealed class PhotoRenderControl : Control
    {
        private const int LightSegments = 24;
        private static readonly Color AmbientColor = new(0.6f, 0.6f, 0.6f, 1f);
        private static readonly Color AmbientNoLightColor = new(0.75f, 0.75f, 0.75f, 1f);

        [Dependency] private readonly IClyde _clyde = default!;
        [Dependency] private readonly IEntityManager _entManager = default!;
        [Dependency] private readonly IPrototypeManager _protoManager = default!;
        [Dependency] private readonly IResourceCache _resourceCache = default!;
        [Dependency] private readonly ITileDefinitionManager _tileDefManager = default!;

        private ShaderInstance? _addShader;
        private ShaderInstance? _multiplyShader;
        private RMCPhotoCameraSystem? _photoSystem;
        private SpriteSystem? _spriteSystem;
        private UserInterfaceSystem? _uiSystem;
        private RMCPhotoCameraSystem PhotoSystem => _photoSystem ??= _entManager.System<RMCPhotoCameraSystem>();
        private SpriteSystem SpriteSystem => _spriteSystem ??= _entManager.System<SpriteSystem>();
        private UserInterfaceSystem UiSystem => _uiSystem ??= _entManager.System<UserInterfaceSystem>();

        internal readonly Queue<(EntityUid Photo, RMCPhotoSceneSnapshot Snapshot)> Queue = new();

        public PhotoRenderControl()
        {
            IoCManager.InjectDependencies(this);
        }

        private ShaderInstance AddShader => _addShader ??= _protoManager.Index<ShaderPrototype>("RMCPhotoLightAdd").Instance();
        private ShaderInstance MultiplyShader => _multiplyShader ??= _protoManager.Index<ShaderPrototype>("RMCPhotoLightMultiply").Instance();

        protected override void Draw(DrawingHandleScreen handle)
        {
            base.Draw(handle);

            if (!Queue.TryDequeue(out var job))
                return;

            var (photo, snapshot) = job;

            if (!_entManager.EntityExists(photo))
                return;

            try
            {
                var size = new Vector2i(snapshot.Resolution, snapshot.Resolution);
                var center = new Vector2(size.X / 2f, size.Y / 2f);
                var pixelsPerUnit = 32f / snapshot.ZoomLevel;
                var scale = new Vector2(1f / snapshot.ZoomLevel, 1f / snapshot.ZoomLevel);
                var fullRect = new UIBox2(0, 0, size.X, size.Y);

                var sceneTarget = _clyde.CreateRenderTarget(size, new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8Srgb), name: "rmc_photo_scene");
                handle.RenderInRenderTarget(sceneTarget, () =>
                {
                    foreach (var tileSnap in snapshot.Tiles)
                    {
                        var tileDef = _tileDefManager[tileSnap.TypeId];
                        if (tileDef.Sprite is not { } spritePath)
                            continue;

                        if (!_resourceCache.TryGetResource<TextureResource>(new ResPath(spritePath.ToString()), out var texRes))
                            continue;

                        var tex = texRes.Texture;
                        var variants = tileDef.Variants < 1 ? 1 : (int)tileDef.Variants;
                        var variantWidth = tex.Width / variants;
                        var srcX = tileSnap.Variant % variants * variantWidth;
                        var srcRegion = new UIBox2i(srcX, 0, srcX + variantWidth, tex.Height);

                        var tileCenter = center + new Vector2(tileSnap.Offset.X, -tileSnap.Offset.Y) * pixelsPerUnit;
                        var half = pixelsPerUnit / 2f;
                        var destRect = new UIBox2(tileCenter.X - half, tileCenter.Y - half, tileCenter.X + half, tileCenter.Y + half);

                        handle.DrawTextureRectRegion(tex, destRect, srcRegion);
                    }

                    foreach (var decalSnap in snapshot.Decals)
                    {
                        if (!_protoManager.TryIndex<DecalPrototype>(decalSnap.Id, out var decalProto))
                            continue;

                        var tex = SpriteSystem.Frame0(decalProto.Sprite);
                        var decalCenter = center + new Vector2(decalSnap.Offset.X, -decalSnap.Offset.Y) * pixelsPerUnit;
                        var half = pixelsPerUnit / 2f;
                        var decalRect = new UIBox2(decalCenter.X - half, decalCenter.Y - half, decalCenter.X + half, decalCenter.Y + half);
                        handle.DrawTextureRect(tex, decalRect, decalSnap.Color);
                    }
                }, Color.Transparent);

                var entityTarget = _clyde.CreateRenderTarget(size, new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8Srgb), name: "rmc_photo_entities");
                handle.RenderInRenderTarget(entityTarget, () =>
                {
                    var sorted = snapshot.Entities
                        .Select(e => (Snap: e, Uid: _entManager.GetEntity(e.Entity)))
                        .Where(e => _entManager.EntityExists(e.Uid))
                        .OrderBy(e => _entManager.TryGetComponent<SpriteComponent>(e.Uid, out var sp) ? sp.DrawDepth : 0)
                        .ToList();

                    foreach (var (entSnap, entity) in sorted)
                    {
                        var pixelPos = center + new Vector2(entSnap.Offset.X, -entSnap.Offset.Y) * pixelsPerUnit;
                        handle.DrawEntity(entity, pixelPos, scale, Angle.Zero, overrideDirection: entSnap.Direction);
                    }
                }, Color.Transparent);

                var ambientColor = snapshot.Lights.Count > 0 ? AmbientColor : AmbientNoLightColor;
                var lightTarget = _clyde.CreateRenderTarget(size, new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8Srgb), name: "rmc_photo_light");
                handle.RenderInRenderTarget(lightTarget, () =>
                {
                    if (snapshot.Lights.Count > 0)
                    {
                        handle.UseShader(AddShader);
                        foreach (var light in snapshot.Lights)
                            DrawLightBlob(handle, center, pixelsPerUnit, light);
                        handle.UseShader(null);
                    }
                }, ambientColor);

                var finalTarget = _clyde.CreateRenderTarget(size, new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8Srgb), name: "rmc_photo");
                handle.RenderInRenderTarget(finalTarget, () =>
                {
                    handle.DrawTextureRect(sceneTarget.Texture, fullRect);
                    handle.DrawTextureRect(entityTarget.Texture, fullRect);
                    handle.UseShader(MultiplyShader);
                    handle.DrawTextureRect(lightTarget.Texture, fullRect);
                    handle.UseShader(null);
                }, Color.Transparent);

                sceneTarget.Dispose();
                entityTarget.Dispose();
                lightTarget.Dispose();

                var cached = _entManager.EnsureComponent<RMCCachedPhotoComponent>(photo);
                cached.RenderTarget?.Dispose();
                cached.RenderTarget = finalTarget;
                cached.CachedPhoto = finalTarget.Texture;

                if (UiSystem.TryGetOpenUi(photo, RMCPhotoUi.Key, out PhotoBui? bui))
                    bui.Refresh();
            }
            catch (Exception ex)
            {
                PhotoSystem.Log.Error($"Failed to render photo snapshot: {ex}");
            }
        }

        private static void DrawLightBlob(DrawingHandleScreen handle, Vector2 center, float pixelsPerUnit, RMCPhotoLightSnap light)
        {
            var lightCenter = center + new Vector2(light.Offset.X, -light.Offset.Y) * pixelsPerUnit;
            var radiusPx = light.Radius * pixelsPerUnit;
            var energy = Math.Clamp(light.Energy, 0f, 1f);
            var centerColor = Color.FromSrgb(new Color(light.Color.R * energy, light.Color.G * energy, light.Color.B * energy, 1f));
            var edgeColor = new Color(0f, 0f, 0f, 0f);

            var verts = new DrawVertexUV2DColor[LightSegments + 2];
            verts[0] = new DrawVertexUV2DColor(lightCenter, centerColor);
            for (var i = 0; i <= LightSegments; i++)
            {
                var angle = i * (MathF.PI * 2f / LightSegments);
                var pos = lightCenter + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radiusPx;
                verts[i + 1] = new DrawVertexUV2DColor(pos, edgeColor);
            }

            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, Texture.White, verts);
        }
    }
}
