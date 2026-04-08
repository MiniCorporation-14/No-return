using Content.Shared.Whitelist;
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
    /// Optional whitelist of entities this holder may grab.
    /// </summary>
    [DataField]
    public EntityWhitelist? HoldableWhitelist;

    /// <summary>
    /// Optional blacklist of entities this holder may not grab.
    /// </summary>
    [DataField]
    public EntityWhitelist? HoldableBlacklist;

    /// <summary>
    /// Cooldown applied to the hold action after each successful use.
    /// </summary>
    [DataField]
    public TimeSpan HoldActionCooldown = TimeSpan.FromSeconds(1);
}
