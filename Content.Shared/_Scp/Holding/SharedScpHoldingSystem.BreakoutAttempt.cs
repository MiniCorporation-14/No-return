using Content.Shared.DoAfter;

namespace Content.Shared._Scp.Holding;

public abstract partial class SharedScpHoldingSystem
{
    /*
     * Semantic breakout-attempt state plus private do-after handle tracking.
     */

    private void StartBreakoutAttempt(EntityUid uid, DoAfterId doAfterId)
    {
        _breakoutDoAfterIds[uid] = doAfterId;
        EnsureComp<ScpBreakoutAttemptComponent>(uid);
    }

    private void EndBreakoutAttempt(EntityUid uid, bool cancelDoAfter)
    {
        var hadAttempt = _breakoutAttemptQuery.HasComp(uid);
        var hadDoAfter = _breakoutDoAfterIds.Remove(uid, out var doAfterId);

        if (hadAttempt)
            RemComp<ScpBreakoutAttemptComponent>(uid);

        if (cancelDoAfter && hadDoAfter)
            CancelBreakoutAttemptDoAfter(doAfterId);
    }

    private void CancelBreakoutAttemptDoAfter(DoAfterId doAfterId)
    {
        if (!_doAfter.IsRunning(doAfterId))
            return;

        _doAfter.Cancel(doAfterId);
    }
}
