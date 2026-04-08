using Content.Shared.Alert;
using Content.Shared.DoAfter;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared._Scp.Holding;

public sealed partial class ScpHoldBreakoutAlertEvent : BaseAlertEvent;

public sealed partial class ScpHoldAttemptEvent(EntityUid holder, EntityUid target) : CancellableEntityEventArgs
{
    public EntityUid Holder { get; } = holder;
    public EntityUid Target { get; } = target;
}

public sealed partial class ScpHoldBreakoutEvent(bool viaMovement, bool wasFullHold, bool appliedImmunity) : EntityEventArgs
{
    public bool ViaMovement { get; } = viaMovement;
    public bool WasFullHold { get; } = wasFullHold;
    public bool AppliedImmunity { get; } = appliedImmunity;
}

[Serializable, NetSerializable]
public sealed partial class ScpHoldBreakoutDoAfterEvent : SimpleDoAfterEvent
{
    public bool ViaMovement;

    public ScpHoldBreakoutDoAfterEvent()
    {
    }

    public ScpHoldBreakoutDoAfterEvent(bool viaMovement)
    {
        ViaMovement = viaMovement;
    }
}
