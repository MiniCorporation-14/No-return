using Content.Shared._Scp.Holding.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._Scp.Holding.Components;

/// <summary>
/// Runtime state stored on a target while at least one holder is contributing.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true), AutoGenerateComponentPause]
[Access(typeof(SharedScpHoldingSystem))]
public sealed partial class ActiveScpHoldableComponent : Component
{
    /// <summary>
    /// Next timestamp when a soft breakout attempt may succeed.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan SoftEscapeAvailableAt;

    /// <summary>
    /// Ordered holder list used for reassignment and contribution counting.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public List<EntityUid> Holders = [];

    /// <summary>
    /// Current primary holder used as the soft hold drag anchor.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public EntityUid? PrimaryHolder;

    /// <summary>
    /// Required contributor count for entering full hold.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public int RequiredHolderCount = 2;
}
