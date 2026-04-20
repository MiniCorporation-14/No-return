using Content.Shared.Buckle.Components;
using Content.Shared.Pulling.Events;

namespace Content.Shared._Scp.Holding.Systems;

public abstract partial class SharedScpHoldingSystem
{
    private bool CanPassPullAttempt(EntityUid holderUid, EntityUid targetUid)
    {
        if (!_actionBlocker.CanInteract(holderUid, targetUid))
            return false;

        if (TryComp<BuckleComponent>(targetUid, out var buckleComponent) && buckleComponent.Buckled)
            return false;

        var beingPulledAttempt = new BeingPulledAttemptEvent(holderUid, targetUid);
        RaiseLocalEvent(targetUid, beingPulledAttempt, true);

        if (beingPulledAttempt.Cancelled)
            return false;

        var startPullAttempt = new StartPullAttemptEvent(holderUid, targetUid);
        RaiseLocalEvent(holderUid, startPullAttempt, true);
        return !startPullAttempt.Cancelled;
    }
}
