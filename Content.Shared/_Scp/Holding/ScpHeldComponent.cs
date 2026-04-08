using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
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
    /// Temporary breakout action prototype granted to the target.
    /// </summary>
    [DataField]
    public EntProtoId BreakoutAction = "ActionScpHoldBreakout";

    /// <summary>
    /// Runtime breakout action entity.
    /// </summary>
    [AutoNetworkedField]
    public EntityUid? BreakoutActionEntity;

    /// <summary>
    /// Whether the target is currently in the immobile full hold stage.
    /// </summary>
    [AutoNetworkedField]
    public bool FullHold;

    /// <summary>
    /// Next timestamp when a soft breakout attempt may succeed.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan SoftEscapeAvailableAt;

    /// <summary>
    /// Timestamp when the current uninterrupted full hold started.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan? FullHoldStartedAt;

    /// <summary>
    /// Ordered holder list used for reassignment and contribution counting.
    /// </summary>
    [AutoNetworkedField]
    public List<EntityUid> Holders = new();

    /// <summary>
    /// Current primary holder used as the soft hold drag anchor.
    /// </summary>
    [AutoNetworkedField]
    public EntityUid? PrimaryHolder;

    /// <summary>
    /// Required contributor count for entering full hold.
    /// </summary>
    [AutoNetworkedField]
    public int RequiredHolderCount = 2;

    /// <summary>
    /// Copied soft breakout cooldown configuration from the initial holdable target.
    /// </summary>
    [AutoNetworkedField]
    public TimeSpan SoftEscapeCooldown = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Copied full hold delay configuration from the initial holdable target.
    /// </summary>
    [AutoNetworkedField]
    public TimeSpan FullHoldDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Copied full breakout duration configuration from the initial holdable target.
    /// </summary>
    [AutoNetworkedField]
    public TimeSpan FullBreakoutDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Copied post-breakout immunity duration from the initial holdable target.
    /// </summary>
    [AutoNetworkedField]
    public TimeSpan PostBreakoutImmunity = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Copied maximum hold range from the initial holdable target.
    /// </summary>
    [AutoNetworkedField]
    public float HoldRange = 1f;

    /// <summary>
    /// Copied walk slowdown applied through <see cref="ScpHolderComponent"/>.
    /// </summary>
    [AutoNetworkedField]
    public float WalkModifier = 0.5f;

    /// <summary>
    /// Copied sprint slowdown applied through <see cref="ScpHolderComponent"/>.
    /// </summary>
    [AutoNetworkedField]
    public float SprintModifier = 0.5f;

    /// <summary>
    /// Active breakout do-after id for a full hold, if one exists.
    /// </summary>
    [AutoNetworkedField]
    public ushort? BreakoutDoAfterId;
}
