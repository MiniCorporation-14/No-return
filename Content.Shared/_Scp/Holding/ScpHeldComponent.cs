using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._Scp.Holding;

/// <summary>
/// Runtime state stored on a target while at least one holder is contributing.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true), AutoGenerateComponentPause]
[Access(typeof(SharedScpHoldingSystem))]
public sealed partial class ScpHeldComponent : Component
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

    /// <summary>
    /// Copied soft breakout cooldown configuration from the initial holdable target.
    /// </summary>
    /// Leave it unused for some time for balance reasons
    public TimeSpan SoftEscapeCooldown = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Copied full hold delay configuration from the initial holdable target.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public TimeSpan FullHoldDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Copied full breakout duration configuration from the initial holdable target.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public TimeSpan FullBreakoutDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Copied post-breakout immunity duration from the initial holdable target.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public TimeSpan PostBreakoutImmunity = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Copied maximum hold range from the initial holdable target.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public float HoldRange = 1f;

    /// <summary>
    /// Copied walk slowdown applied through <see cref="ScpHolderComponent"/>.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public float WalkModifier = 0.5f;

    /// <summary>
    /// Copied sprint slowdown applied through <see cref="ScpHolderComponent"/>.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public float SprintModifier = 0.5f;
}
