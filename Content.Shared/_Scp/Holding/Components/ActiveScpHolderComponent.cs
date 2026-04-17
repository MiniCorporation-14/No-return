using Content.Shared._Scp.Holding.Systems;
using Robust.Shared.GameStates;

namespace Content.Shared._Scp.Holding.Components;

/// <summary>
/// Runtime contribution state stored on each active holder.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(SharedScpHoldingSystem))]
public sealed partial class ActiveScpHolderComponent : Component
{
    /// <summary>
    /// Target currently being contributed to.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public EntityUid? Target;
}
