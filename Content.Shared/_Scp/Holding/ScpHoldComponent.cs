using Content.Shared.Actions;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Scp.Holding;

/// <summary>
/// Grants the owner the ability to contribute to SCP holding.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(SharedScpHoldingSystem))]
public sealed partial class ScpHoldComponent : Component
{
    /// <summary>
    /// Action prototype used to start or release a hold.
    /// </summary>
    [DataField]
    public EntProtoId Action = "ActionScpHoldTarget";

    /// <summary>
    /// Runtime action entity granted to the holder.
    /// </summary>
    [AutoNetworkedField]
    public EntityUid? ActionEntity;

    /// <summary>
    /// Minimum delay between soft breakout attempts while the hold is active.
    /// </summary>
    [DataField]
    public TimeSpan SoftEscapeCooldown = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Minimum uninterrupted full hold duration before a breakout do-after may start.
    /// </summary>
    [DataField]
    public TimeSpan FullHoldDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Duration of the visible breakout do-after for a full hold.
    /// </summary>
    [DataField]
    public TimeSpan FullBreakoutDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Duration of immunity after a successful full breakout.
    /// </summary>
    [DataField]
    public TimeSpan PostBreakoutImmunity = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum unobstructed range allowed between holder and target.
    /// </summary>
    [DataField]
    public float HoldRange = 1f;

    /// <summary>
    /// Walk speed modifier applied to holders when this system supplies slowdown.
    /// </summary>
    [DataField]
    public float WalkModifier = 0.5f;

    /// <summary>
    /// Sprint speed modifier applied to holders when this system supplies slowdown.
    /// </summary>
    [DataField]
    public float SprintModifier = 0.5f;
}
