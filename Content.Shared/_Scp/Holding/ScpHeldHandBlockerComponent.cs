using Robust.Shared.GameStates;

namespace Content.Shared._Scp.Holding;

/// <summary>
/// Marks a victim hand placeholder virtual item created by SCP holding.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true)]
[Access(typeof(SharedScpHoldingSystem))]
public sealed partial class ScpHeldHandBlockerComponent : Component
{
    /// <summary>
    /// Held target whose hand is occupied by this placeholder.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public EntityUid Target;

    /// <summary>
    /// Holder whose sprite is shown in this placeholder.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public EntityUid Holder;
}
