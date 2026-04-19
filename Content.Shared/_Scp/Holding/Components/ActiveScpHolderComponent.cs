using Content.Shared._Scp.Holding.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Map;

namespace Content.Shared._Scp.Holding.Components;

/// <summary>
/// Runtime contribution state stored on each active holder.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(raiseAfterAutoHandleState: true)]
[Access(typeof(SharedScpHoldingSystem))]
public sealed partial class ActiveScpHolderComponent : Component
{
    /// <summary>
    /// Target currently being contributed to.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public EntityUid? Target;

    /// <summary>
    /// Per-holder cursor target used to contribute cursor-driven movement without a global leader.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public EntityCoordinates CursorTargetCoordinates = EntityCoordinates.Invalid;

    /// <summary>
    /// True while this holder is still actively pulling the held target toward its stored cursor target.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public bool CursorMoveActive;
}
