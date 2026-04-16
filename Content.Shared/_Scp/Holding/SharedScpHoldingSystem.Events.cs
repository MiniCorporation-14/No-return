using Content.Shared.Actions.Events;
using Content.Shared.CombatMode;
using Content.Shared.Hands;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Throwing;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Events;

namespace Content.Shared._Scp.Holding;

public abstract partial class SharedScpHoldingSystem
{
    /*
     * Event subscription wiring plus routing/lifecycle reactions for held and holder entities.
     */

    private void SubscribeHoldingEvents()
    {
        SubscribeLocalEvent<ScpHeldComponent, ComponentStartup>(OnHeldStartup);
        SubscribeLocalEvent<ScpHeldComponent, ComponentShutdown>(OnHeldShutdown);
        SubscribeLocalEvent<ScpHeldComponent, ScpHoldBreakoutAlertEvent>(OnBreakoutAlert);
        SubscribeLocalEvent<ScpHeldComponent, ScpHoldBreakoutDoAfterEvent>(OnBreakoutDoAfter);
        SubscribeLocalEvent<ScpHeldComponent, MoveInputEvent>(OnHeldMoveInput);
        SubscribeLocalEvent<ScpHeldComponent, AttemptMobCollideEvent>(OnHeldAttemptMobCollide);
        SubscribeLocalEvent<ScpHeldComponent, AttemptMobTargetCollideEvent>(OnHeldAttemptMobTargetCollide);
        SubscribeLocalEvent<ScpHeldComponent, CombatModeChangedEvent>(OnHeldCombatModeChanged);
        SubscribeLocalEvent<ScpHeldComponent, PreventCollideEvent>(OnHeldPreventCollide);
        SubscribeLocalEvent<ScpHoldRestrictedComponent, ActionAttemptEvent>(OnHoldRestrictedActionAttempt);
        SubscribeLocalEvent<ScpBreakoutAttemptComponent, ComponentStartup>(OnBreakoutAttemptStartup);
        SubscribeLocalEvent<ScpBreakoutAttemptComponent, ComponentShutdown>(OnBreakoutAttemptShutdown);
        SubscribeLocalEvent<ScpFullHeldComponent, ComponentStartup>(OnFullHeldStartup);
        SubscribeLocalEvent<ScpFullHeldComponent, ComponentRemove>(OnFullHeldRemove);
        SubscribeLocalEvent<ScpFullHeldComponent, UpdateCanMoveEvent>(OnFullHeldUpdateCanMove);

        SubscribeLocalEvent<ScpHolderComponent, ComponentStartup>(OnHolderStartup);
        SubscribeLocalEvent<ScpHolderComponent, ComponentShutdown>(OnHolderShutdown);
        SubscribeLocalEvent<ScpHolderComponent, BeforeThrowEvent>(OnHolderBeforeThrow);
        SubscribeLocalEvent<ScpHolderComponent, DidEquipHandEvent>(OnHolderHandsModified);
        SubscribeLocalEvent<ScpHolderComponent, PreventCollideEvent>(OnHolderPreventCollide);
        SubscribeLocalEvent<ScpHolderSlowdownComponent, ComponentRemove>(OnHolderSlowdownRemove);
        SubscribeLocalEvent<ScpHolderSlowdownComponent, AfterAutoHandleStateEvent>(OnHolderSlowdownAfterState);
        SubscribeLocalEvent<ScpHolderSlowdownComponent, RefreshMovementSpeedModifiersEvent>(OnHolderSlowdownRefreshMoveSpeed);
        SubscribeLocalEvent<ScpHoldHandBlockerComponent, GettingDroppedAttemptEvent>(OnHolderBlockerDropped);
    }

    private void OnHeldStartup(Entity<ScpHeldComponent> ent, ref ComponentStartup args)
    {
        _alerts.ShowAlert(ent.Owner, "ScpHoldGrabbed");
        SyncHeldStatusEffect(ent.Owner);
        EnsureCombatModeDisabled(ent.Owner);
        OnHeldStateRefreshed(ent);
    }

    private void OnHeldShutdown(Entity<ScpHeldComponent> ent, ref ComponentShutdown args)
    {
        _alerts.ClearAlert(ent.Owner, "ScpHoldGrabbed");
        _statusEffects.TryRemoveStatusEffect(ent, GrabbedStatusEffect);
        OnHeldStateShutdown(ent);
    }

    private void OnBreakoutAlert(Entity<ScpHeldComponent> ent, ref ScpHoldBreakoutAlertEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        TryBreakOut(ent, viaMovement: false);
    }

    private void OnBreakoutDoAfter(Entity<ScpHeldComponent> ent, ref ScpHoldBreakoutDoAfterEvent args)
    {
        EndBreakoutAttempt(ent.Owner, cancelDoAfter: false);

        if (args.Handled)
            return;

        if (args.Cancelled)
        {
            PopupTarget(ent.Owner, "scp-hold-breakout-interrupted");
            return;
        }

        BreakOut(ent, args.ViaMovement, applyImmunity: true);
        args.Handled = true;
    }

    private void OnBreakoutAttemptStartup(Entity<ScpBreakoutAttemptComponent> ent, ref ComponentStartup args)
    {
        if (!_heldQuery.TryComp(ent.Owner, out var held))
            return;

        ShowBreakoutAttemptFeedback((ent.Owner, held));
    }

    private void OnBreakoutAttemptShutdown(Entity<ScpBreakoutAttemptComponent> ent, ref ComponentShutdown args)
    {
        if (!_breakoutDoAfterIds.Remove(ent.Owner, out var doAfterId))
            return;

        CancelBreakoutAttemptDoAfter(doAfterId);
    }

    private void OnHeldMoveInput(Entity<ScpHeldComponent> ent, ref MoveInputEvent args)
    {
        if (!IsBreakoutMovementPress(args))
            return;

        TryBreakOut(ent, viaMovement: true);
    }

    private static void OnFullHeldUpdateCanMove(Entity<ScpFullHeldComponent> ent, ref UpdateCanMoveEvent args)
    {
        args.Cancel();
    }

    private static void OnHeldAttemptMobCollide(Entity<ScpHeldComponent> ent, ref AttemptMobCollideEvent args)
    {
        args.Cancelled = true;
    }

    private static void OnHeldAttemptMobTargetCollide(Entity<ScpHeldComponent> ent, ref AttemptMobTargetCollideEvent args)
    {
        args.Cancelled = true;
    }

    private void OnHeldPreventCollide(Entity<ScpHeldComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled)
            return;

        if (_holderQuery.TryComp(args.OtherEntity, out var holder) &&
            holder.Target == ent.Owner)
        {
            args.Cancelled = true;
        }
    }

    private void OnFullHeldStartup(Entity<ScpFullHeldComponent> ent, ref ComponentStartup args)
    {
        if (_heldQuery.TryComp(ent.Owner, out var held))
            SyncPlaceholderHands((ent.Owner, held));

        ZeroHeldVelocity(ent.Owner);
        _actionBlocker.UpdateCanMove(ent.Owner);
    }

    private void OnFullHeldRemove(Entity<ScpFullHeldComponent> ent, ref ComponentRemove args)
    {
        DeleteHeldHandBlockers(ent.Owner);
        _actionBlocker.UpdateCanMove(ent.Owner);
    }

    private void OnHolderStartup(Entity<ScpHolderComponent> ent, ref ComponentStartup args)
    {
        SyncHolderState(ent);
    }

    private void OnHolderShutdown(Entity<ScpHolderComponent> ent, ref ComponentShutdown args)
    {
        var target = ent.Comp.Target;
        ent.Comp.Target = null;
        DeleteHolderHandBlockers(ent.Owner);

        if (!_timing.ApplyingState)
            RemComp<ScpHolderSlowdownComponent>(ent.Owner);

        OnHolderStateShutdown(ent.Owner, target);
    }

    private void OnHolderSlowdownRemove(Entity<ScpHolderSlowdownComponent> ent, ref ComponentRemove args)
    {
        _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnHolderSlowdownAfterState(Entity<ScpHolderSlowdownComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnHolderSlowdownRefreshMoveSpeed(Entity<ScpHolderSlowdownComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        args.ModifySpeed(ent.Comp.WalkModifier, ent.Comp.SprintModifier);
    }

    private void OnHolderBeforeThrow(Entity<ScpHolderComponent> ent, ref BeforeThrowEvent args)
    {
        if (ent.Comp.Target == null)
            return;

        if (!TryComp<ScpHoldHandBlockerComponent>(args.ItemUid, out var blocker))
            return;

        if (blocker.Target != ent.Comp.Target.Value)
            return;

        ReleaseHolderContribution(ent.Owner, ent.Comp.Target.Value, clearIfEmpty: true);
        args.Cancelled = true;
    }

    private void OnHolderHandsModified(Entity<ScpHolderComponent> ent, ref DidEquipHandEvent args)
    {
        if (ent.Comp.LifeStage > ComponentLifeStage.Running)
            return;

        if (ent.Comp.Target == null)
            return;

        if (!_heldQuery.HasComp(ent.Comp.Target.Value))
            return;

        SyncHolderState(ent);
    }

    private void OnHolderPreventCollide(Entity<ScpHolderComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled)
            return;

        if (ent.Comp.Target == null)
            return;

        if (ent.Comp.Target != args.OtherEntity)
            return;

        args.Cancelled = true;
    }

    private void OnHolderBlockerDropped(Entity<ScpHoldHandBlockerComponent> ent, ref GettingDroppedAttemptEvent args)
    {
        if (!_holderQuery.TryComp(args.User, out var holder))
            return;

        if (holder.Target == null)
            return;

        if (holder.Target != ent.Comp.Target)
            return;

        ReleaseHolderContribution(args.User, ent.Comp.Target, clearIfEmpty: true);
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
