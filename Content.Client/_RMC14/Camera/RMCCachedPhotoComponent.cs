using Robust.Client.Graphics;

namespace Content.Client._RMC14.Camera;

[RegisterComponent]
public sealed partial class RMCCachedPhotoComponent : Component
{
    public Texture? CachedPhoto;
    public IRenderTexture? RenderTarget;
}
