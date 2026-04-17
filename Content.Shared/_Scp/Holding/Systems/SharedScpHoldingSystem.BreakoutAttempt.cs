using Content.Shared._Scp.Holding.Components;
using Content.Shared.DoAfter;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;

namespace Content.Shared._Scp.Holding.Systems;

public abstract partial class SharedScpHoldingSystem
{
    /*
     * Breakout-attempt query cache, event routing, semantic state, and do-after handle tracking.
     */

    private EntityQuery<ScpBreakoutAttemptComponent> _breakoutAttemptQuery;

    private void InitializeBreakoutAttemptQueries()
    {
        _breakoutAttemptQuery = GetEntityQuery<ScpBreakoutAttemptComponent>();
    }

    private void InitializeBreakoutAttemptEvents()
    {
        SubscribeLocalEvent<ActiveScpHoldableComponent, ScpHoldBreakoutAlertEvent>(OnBreakoutAlert);
        SubscribeLocalEvent<ActiveScpHoldableComponent, ScpHoldBreakoutDoAfterEvent>(OnBreakoutDoAfter);
        SubscribeLocalEvent<ActiveScpHoldableComponent, MoveInputEvent>(OnHeldMoveInput);
        SubscribeLocalEvent<ScpBreakoutAttemptComponent, ComponentStartup>(OnBreakoutAttemptStartup);
        SubscribeLocalEvent<ScpBreakoutAttemptComponent, ComponentShutdown>(OnBreakoutAttemptShutdown);
    }

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

    private void OnBreakoutAlert(Entity<ActiveScpHoldableComponent> ent, ref ScpHoldBreakoutAlertEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        TryBreakOut(ent, viaMovement: false);
    }

    private void OnBreakoutDoAfter(Entity<ActiveScpHoldableComponent> ent, ref ScpHoldBreakoutDoAfterEvent args)
    {
        EndBreakoutAttempt(ent.Owner, cancelDoAfter: false);

        if (args.Handled)
            return;

        if (args.Cancelled)
        {
            Popup(ent, "scp-hold-breakout-interrupted");
            return;
        }

        BreakOut(ent, args.ViaMovement, applyImmunity: true);
        args.Handled = true;
    }

    private void OnBreakoutAttemptStartup(Entity<ScpBreakoutAttemptComponent> ent, ref ComponentStartup args)
    {
        if (!_activeHoldableQuery.TryComp(ent.Owner, out var held))
            return;

        ShowBreakoutAttemptFeedback((ent.Owner, held));
    }

    private void OnBreakoutAttemptShutdown(Entity<ScpBreakoutAttemptComponent> ent, ref ComponentShutdown args)
    {
        if (!_breakoutDoAfterIds.Remove(ent.Owner, out var doAfterId))
            return;

        CancelBreakoutAttemptDoAfter(doAfterId);
    }

    private void OnHeldMoveInput(Entity<ActiveScpHoldableComponent> ent, ref MoveInputEvent args)
    {
        if (!IsBreakoutMovementPress(args))
            return;

        TryBreakOut(ent, viaMovement: true);
    }

    private static bool IsBreakoutMovementPress(MoveInputEvent args)
    {
        if (!args.State)
            return false;

        var pressedButton = args.Dir switch
        {
            Direction.East => MoveButtons.Right,
            Direction.North => MoveButtons.Up,
            Direction.West => MoveButtons.Left,
            Direction.South => MoveButtons.Down,
            _ => MoveButtons.None,
        };

        if (pressedButton == MoveButtons.None)
            return false;

        return (args.OldMovement & pressedButton) == MoveButtons.None;
    }
}
