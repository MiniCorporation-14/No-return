using Content.Shared.Whitelist;
using Robust.Shared.GameStates;

namespace Content.Shared._Scp.Holding;

/// <summary>
/// Grants the owner the ability to contribute to SCP holding.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true), AutoGenerateComponentPause]
[Access(typeof(SharedScpHoldingSystem))]
public sealed partial class ScpHoldComponent : Component
{
    /// <summary>
    /// Next timestamp when this entity may start a new hold contribution.
    /// </summary>
    [AutoNetworkedField, AutoPausedField]
    public TimeSpan? HoldAvailableAt;

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
    /// Cooldown applied after each successful hold contribution start.
    /// </summary>
    [DataField]
    public TimeSpan HoldActionCooldown = TimeSpan.FromSeconds(1);
}
