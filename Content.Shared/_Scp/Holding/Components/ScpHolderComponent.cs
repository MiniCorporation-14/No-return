using Content.Shared._Scp.Holding.Systems;
using Content.Shared.Whitelist;
using Robust.Shared.GameStates;

namespace Content.Shared._Scp.Holding.Components;

/// <summary>
/// Grants the owner the ability to contribute to SCP holding.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true), AutoGenerateComponentPause]
[Access(typeof(SharedScpHoldingSystem))]
public sealed partial class ScpHolderComponent : Component
{
    /// <summary>
    /// Next timestamp when this entity may start a new hold contribution.
    /// </summary>
    [AutoNetworkedField, AutoPausedField]
    public TimeSpan? HoldAvailableAt;

    /// <summary>
    /// Optional whitelist of entities this holder may grab.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntityWhitelist? HoldableWhitelist;

    /// <summary>
    /// Optional blacklist of entities this holder may not grab.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntityWhitelist? HoldableBlacklist;

    /// <summary>
    /// Cooldown applied after each successful hold contribution start.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan HoldActionCooldown = TimeSpan.FromSeconds(1);
}
