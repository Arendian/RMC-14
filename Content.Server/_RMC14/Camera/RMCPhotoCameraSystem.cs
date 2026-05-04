using System.Linq;
using System.Numerics;
using Content.Server.Decals;
using Content.Shared._RMC14.Camera.PhotoCamera;
using Content.Shared._RMC14.Hands;
using Content.Shared.Coordinates.Helpers;
using Content.Shared.Damage;
using Content.Shared.Decals;
using Content.Shared.Eye;
using Content.Shared.Hands.Components;
using Content.Shared.IdentityManagement;
using Content.Shared.Light.Components;
using Content.Shared.Maps;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Robust.Server.Containers;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.Server._RMC14.Camera;

public sealed class RMCPhotoCameraSystem : SharedRmcPhotoCameraSystem
{
    [Dependency] private readonly ContainerSystem _container = default!;
    [Dependency] private readonly DecalSystem _decal = default!;
    [Dependency] private readonly EntityLookupSystem _entityLookup = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly RMCHandsSystem _rmcHands = default!;
    [Dependency] private readonly TurfSystem _turf = default!;

    private readonly HashSet<EntityUid> _generalResults = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RequestPhotoCaptureEvent>(OnRequestPhotoCapture);
        SubscribeNetworkEvent<RequestStoredPhotoDescriptionEvent>(OnRequestStoredPhotoDescription);
    }

    private void OnRequestPhotoCapture(RequestPhotoCaptureEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } sessionEntity)
            return;

        if (!TryGetCamera(sessionEntity, out var camera))
            return;

        if (camera.Value.Comp.PhotoPrintedAt != null)
            return;

        if (camera.Value.Comp.RemainingCharges <= 0)
        {
            Popup.PopupClient(Loc.GetString("rmc-photo-camera-make-photo-failed-empty", ("camera", camera)), sessionEntity, sessionEntity);
            return;
        }

        var targetCoords = GetCoordinates(ev.Coordinates);
        if (!targetCoords.IsValid(EntityManager))
            return;

        if (!Examine.InRangeUnOccluded(sessionEntity, targetCoords, camera.Value.Comp.Range))
            return;

        var photoCoords = targetCoords;
        if (camera.Value.Comp.AutoCenter)
            photoCoords = photoCoords.SnapToGrid();

        var photoArea = GetPhotoAreaRange(camera.Value.Comp.ZoomMode);
        var visibleEntities = GetVisibleEntities(photoCoords, photoArea);

        var snapshot = new RMCPhotoSceneSnapshot(camera.Value.Comp.ZoomMode, camera.Value.Comp.ZoomLevel, camera.Value.Comp.Resolution);
        var entityInPhotoList = new List<EntityInPhoto>();

        var photoMapCoords = TransformSystem.ToMapCoordinates(photoCoords);
        if (_mapManager.TryFindGridAt(photoMapCoords, out var gridUid, out var mapGrid))
        {
            var tileAreaSize = photoArea * 2 + 2f;
            var worldBox = Box2.CenteredAround(photoMapCoords.Position, new Vector2(tileAreaSize, tileAreaSize));
            foreach (var tileRef in _mapSystem.GetTilesIntersecting(gridUid, mapGrid, worldBox, false))
            {
                if (tileRef.Tile.IsEmpty)
                    continue;

                var tileCenterMap = TransformSystem.ToMapCoordinates(_turf.GetTileCenter(tileRef));
                var offset = tileCenterMap.Position - photoMapCoords.Position;
                snapshot.Tiles.Add(new RMCPhotoTileSnap(
                    offset,
                    tileRef.Tile.TypeId,
                    tileRef.Tile.Variant,
                    tileRef.Tile.RotationMirroring));
            }

            var localPhotoCenter = _mapSystem.WorldToLocal(gridUid, mapGrid, photoMapCoords.Position);
            var localBox = Box2.CenteredAround(localPhotoCenter, new Vector2(tileAreaSize, tileAreaSize));
            foreach (var (_, decal) in _decal.GetDecalsIntersecting(gridUid, localBox))
            {
                var worldDecalPos = _mapSystem.LocalToWorld(gridUid, mapGrid, decal.Coordinates);
                var decalOffset = worldDecalPos - photoMapCoords.Position;
                snapshot.Decals.Add(new RMCPhotoDecalSnap(decalOffset, decal.Id, decal.Color, (float)decal.Angle));
            }
        }

        foreach (var entity in visibleEntities)
        {
            var entCoords = TransformSystem.GetMoverCoordinates(entity);
            var offset = entCoords.Position - photoCoords.Position;
            var direction = TransformSystem.GetWorldRotation(entity).GetDir();
            snapshot.Entities.Add(new RMCPhotoEntitySnap(GetNetEntity(entity), offset, direction));

            if (!HasComp<MobStateComponent>(entity))
                continue;

            var heldItems = new List<NetEntity>();
            if (TryComp(entity, out HandsComponent? hands))
            {
                foreach (var hand in hands.Hands)
                {
                    var heldItem = Hands.GetHeldItem((entity, hands), hand.Key);
                    if (heldItem != null)
                        heldItems.Add(GetNetEntity(heldItem.Value));
                }
            }

            entityInPhotoList.Add(new EntityInPhoto(GetNetEntity(entity), heldItems));
        }

        foreach (var entity in visibleEntities)
        {
            if (!TryComp(entity, out PointLightComponent? light))
                continue;

            var isEnabled = light.Enabled;
            var lightRadius = light.Radius;
            if (!isEnabled && TryComp(entity, out ExpendableLightComponent? expLight) && expLight.Activated)
            {
                isEnabled = true;
                if (lightRadius < 2f)
                    lightRadius = 6f;
            }

            if (!isEnabled && TryComp(entity, out HandheldLightComponent? handheld))
                isEnabled = handheld.Activated;

            if (!isEnabled)
                continue;

            var entMapCoords = TransformSystem.ToMapCoordinates(TransformSystem.GetMoverCoordinates(entity));
            var lightOffset = entMapCoords.Position - photoMapCoords.Position + light.Offset;
            snapshot.Lights.Add(new RMCPhotoLightSnap(lightOffset, lightRadius, light.Energy, light.Color));
        }

        camera.Value.Comp.Snapshot = snapshot;
        camera.Value.Comp.EntitiesInPhoto = entityInPhotoList;
        camera.Value.Comp.PhotoPrintedAt = Timing.CurTime + camera.Value.Comp.PrintDelay;
        Dirty(camera.Value);

        Audio.PlayPvs(camera.Value.Comp.ShutterSound, camera.Value);
    }

    private void OnRequestStoredPhotoDescription(RequestStoredPhotoDescriptionEvent ev, EntitySessionEventArgs args)
    {
        if (!TryComp(GetEntity(ev.Photo), out RMCPhotoComponent? photo))
            return;

        if (args.SenderSession.AttachedEntity is not { } attachedEntity)
            return;

        var examineText = GetPhotoDescription(photo.EntitiesInPhoto, attachedEntity);
        RaiseNetworkEvent(new ReceiveStoredPhotoDescriptionEvent(ev.Photo, examineText), args.SenderSession);
    }

    private List<string> GetPhotoDescription(List<EntityInPhoto> entitiesInPhoto, EntityUid user)
    {
        var photoText = new List<string>();
        foreach (var entity in entitiesInPhoto)
        {
            var uid = GetEntity(entity.Entity);
            var name = Identity.Name(uid, EntityManager, user);

            var description = GetVisibilityText(uid, name);

            var items = entity.HeldItems.Select(GetEntity).ToList();
            var holdingText = _rmcHands.GetExamineText(uid, user, items);

            photoText.Add($"{description} {holdingText}".Trim());
        }

        return photoText;
    }

    protected override List<string> GetExamineText(RMCPhotoComponent photo, EntityUid user)
    {
        return GetPhotoDescription(photo.EntitiesInPhoto, user);
    }

    private string GetVisibilityText(EntityUid uid, string name)
    {
        var text = Loc.GetString("rmc-photo-camera-entity-in-photo-entity-see", ("name", name));

        if (!TryComp(uid, out DamageableComponent? damageable) ||
            !TryComp(uid, out MobThresholdsComponent? thresholds))
            return text;

        foreach (var (threshold, state) in thresholds.Thresholds)
        {
            if (state != MobState.Dead)
                continue;

            if (damageable.TotalDamage >= threshold)
                return Loc.GetString("rmc-photo-camera-entity-in-photo-entity-dead", ("name", name));
        }

        return text;
    }

    private HashSet<EntityUid> GetVisibleEntities(EntityCoordinates photoCoords, float range)
    {
        var visible = new HashSet<EntityUid>();

        _generalResults.Clear();
        _entityLookup.GetEntitiesInRange(photoCoords, range, _generalResults, LookupFlags.Uncontained);

        foreach (var entity in _generalResults)
        {
            if (!HasComp<PhysicsComponent>(entity) && Comp<TransformComponent>(entity).Anchored)
                continue;

            if (TryComp(entity, out VisibilityComponent? vis) &&
                (vis.Layer & (ushort)VisibilityFlags.Normal) == 0)
                continue;

            if (!photoCoords.TryDistance(EntityManager, TransformSystem.GetMoverCoordinates(entity), out var dist) || dist > range)
                continue;

            visible.Add(entity);
        }

        return visible;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var query = EntityQueryEnumerator<RMCPhotoCameraComponent>();

        while (query.MoveNext(out var uid, out var camera))
        {
            if (camera.PhotoPrintedAt == null || Timing.CurTime < camera.PhotoPrintedAt.Value)
                continue;

            var photo = SpawnAtPosition(camera.PhotoPrototype, TransformSystem.GetMoverCoordinates(uid));
            var photoComp = EnsureComp<RMCPhotoComponent>(photo);

            photoComp.Snapshot = camera.Snapshot;
            photoComp.EntitiesInPhoto = camera.EntitiesInPhoto.ToList();
            Dirty(photo, photoComp);

            camera.PhotoPrintedAt = null;
            camera.Snapshot = null;
            camera.RemainingCharges -= 1;
            camera.EntitiesInPhoto.Clear();
            Dirty(uid, camera);

            if (_container.TryGetContainingContainer(uid, out var container) &&
                TryComp(container.Owner, out HandsComponent? hands))
            {
                Hands.TryPickupAnyHand(container.Owner, photo, handsComp: hands);
            }
        }
    }
}

