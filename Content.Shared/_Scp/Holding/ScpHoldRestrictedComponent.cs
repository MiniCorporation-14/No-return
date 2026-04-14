using Robust.Shared.GameStates;

namespace Content.Shared._Scp.Holding;

[RegisterComponent, NetworkedComponent]
public sealed partial class ScpHoldRestrictedComponent : Component
{
    [DataField]
    public ScpHoldStage Stage = ScpHoldStage.Full;
}
