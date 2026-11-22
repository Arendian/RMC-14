using Robust.Shared.GameStates;

namespace Content.Shared._RMC14.Camera.PhotoCamera;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class RMCPhotoComponent : Component
{
    [DataField, AutoNetworkedField]
    public string PhotoName = "Photo";

    [DataField]
    public List<string> ExamineText = new();

    [DataField]
    public List<EntityInPhoto> EntitiesInPhoto = new();

    [DataField, AutoNetworkedField]
    public RMCPhotoSceneSnapshot? Snapshot;
}
