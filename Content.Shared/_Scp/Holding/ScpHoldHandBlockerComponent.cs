using Robust.Shared.GameStates;

namespace Content.Shared._Scp.Holding;

/// <summary>
/// Marks a virtual item that reserves one holder hand for an active SCP hold.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(raiseAfterAutoHandleState: true, fieldDeltas: true)]
[Access(typeof(SharedScpHoldingSystem))]
public sealed partial class ScpHoldHandBlockerComponent : Component
{
    /// <summary>
    /// The held target represented by this virtual item.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public EntityUid Target;
}
