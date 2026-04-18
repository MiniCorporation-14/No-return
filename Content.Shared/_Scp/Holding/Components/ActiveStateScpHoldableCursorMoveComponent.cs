using Content.Shared._Scp.Holding.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Map;

namespace Content.Shared._Scp.Holding.Components;

/// <summary>
/// Runtime cursor-move state stored on a held target while it is being moved or parked at a cursor-selected point.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(SharedScpHoldingSystem))]
public sealed partial class ActiveStateScpHoldableCursorMoveComponent : Component
{
    /// <summary>
    /// Holder that issued the most recent valid cursor-move command.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public EntityUid Holder;

    /// <summary>
    /// Clamped cursor target stored in entity coordinates for shared prediction and reconciliation.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public EntityCoordinates TargetCoordinates = EntityCoordinates.Invalid;

    /// <summary>
    /// True while the held target is still travelling toward the stored cursor point.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public bool Active = true;
}
