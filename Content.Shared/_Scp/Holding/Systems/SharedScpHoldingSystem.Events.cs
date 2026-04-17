using Content.Shared._Scp.Holding.Components;
using Content.Shared.Alert;
using Content.Shared.Hands;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Throwing;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;

namespace Content.Shared._Scp.Holding.Systems;

public abstract partial class SharedScpHoldingSystem
{
    /*
     * Event subscription wiring plus routing/lifecycle reactions for held and holder entities.
     */

    private static readonly ProtoId<AlertPrototype> HeldAlert = "ScpHoldGrabbed";

    private void SubscribeHoldingEvents()
    {
        SubscribeLocalEvent<ActiveScpHoldableComponent, ComponentStartup>(OnHeldStartup);
        SubscribeLocalEvent<ActiveScpHoldableComponent, ComponentShutdown>(OnHeldShutdown);
        SubscribeLocalEvent<ActiveScpHoldableComponent, ComponentRemove>(OnHeldRemove);
        SubscribeLocalEvent<ActiveScpHoldableComponent, ScpHoldBreakoutAlertEvent>(OnBreakoutAlert);
        SubscribeLocalEvent<ActiveScpHoldableComponent, ScpHoldBreakoutDoAfterEvent>(OnBreakoutDoAfter);
        SubscribeLocalEvent<ActiveScpHoldableComponent, MoveInputEvent>(OnHeldMoveInput);
        SubscribeLocalEvent<ActiveScpHoldableComponent, AttemptMobCollideEvent>(OnHeldAttemptMobCollide);
        SubscribeLocalEvent<ActiveScpHoldableComponent, AttemptMobTargetCollideEvent>(OnHeldAttemptMobTargetCollide);
        SubscribeLocalEvent<ActiveScpHoldableComponent, PreventCollideEvent>(OnHeldPreventCollide);
        SubscribeLocalEvent<ScpBreakoutAttemptComponent, ComponentStartup>(OnBreakoutAttemptStartup);
        SubscribeLocalEvent<ScpBreakoutAttemptComponent, ComponentShutdown>(OnBreakoutAttemptShutdown);
        SubscribeLocalEvent<ActiveStateScpHoldableFullHoldComponent, ComponentStartup>(OnFullHeldStartup);
        SubscribeLocalEvent<ActiveStateScpHoldableFullHoldComponent, ComponentRemove>(OnFullHeldRemove);
        SubscribeLocalEvent<ActiveStateScpHoldableFullHoldComponent, UpdateCanMoveEvent>(OnFullHeldUpdateCanMove);

        SubscribeLocalEvent<ActiveScpHolderComponent, ComponentStartup>(OnHolderStartup);
        SubscribeLocalEvent<ActiveScpHolderComponent, ComponentShutdown>(OnHolderShutdown);
        SubscribeLocalEvent<ActiveScpHolderComponent, BeforeThrowEvent>(OnHolderBeforeThrow);
        SubscribeLocalEvent<ActiveScpHolderComponent, DidEquipHandEvent>(OnHolderHandsModified);
        SubscribeLocalEvent<ActiveScpHolderComponent, PreventCollideEvent>(OnHolderPreventCollide);
        SubscribeLocalEvent<ActiveStateScpHolderSlowdownComponent, ComponentRemove>(OnHolderSlowdownRemove);
        SubscribeLocalEvent<ActiveStateScpHolderSlowdownComponent, AfterAutoHandleStateEvent>(OnHolderSlowdownAfterState);
        SubscribeLocalEvent<ActiveStateScpHolderSlowdownComponent, RefreshMovementSpeedModifiersEvent>(OnHolderSlowdownRefreshMoveSpeed);
        SubscribeLocalEvent<ScpHoldHandBlockerComponent, GettingDroppedAttemptEvent>(OnHolderBlockerDropped);
    }

    private void OnHeldStartup(Entity<ActiveScpHoldableComponent> ent, ref ComponentStartup args)
    {
        _alerts.ShowAlert(ent.Owner, HeldAlert);
        SyncHeldStatusEffect(ent.Owner);
        OnHeldStateRefreshed(ent);
        ValidateAllActions(ent.Owner);
    }

    private void OnHeldShutdown(Entity<ActiveScpHoldableComponent> ent, ref ComponentShutdown args)
    {
        _alerts.ClearAlert(ent.Owner, HeldAlert);
        _statusEffects.TryRemoveStatusEffect(ent, GrabbedStatusEffect);
        OnHeldStateShutdown(ent);
    }

    private void OnHeldRemove(Entity<ActiveScpHoldableComponent> ent, ref ComponentRemove args)
    {
        ValidateAllActions(ent.Owner);
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
            PopupTarget(ent.Owner, "scp-hold-breakout-interrupted");
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

    private static void OnFullHeldUpdateCanMove(Entity<ActiveStateScpHoldableFullHoldComponent> ent, ref UpdateCanMoveEvent args)
    {
        args.Cancel();
    }

    private static void OnHeldAttemptMobCollide(Entity<ActiveScpHoldableComponent> ent, ref AttemptMobCollideEvent args)
    {
        args.Cancelled = true;
    }

    private static void OnHeldAttemptMobTargetCollide(Entity<ActiveScpHoldableComponent> ent, ref AttemptMobTargetCollideEvent args)
    {
        args.Cancelled = true;
    }

    private void OnHeldPreventCollide(Entity<ActiveScpHoldableComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled)
            return;

        if (_activeHolderQuery.TryComp(args.OtherEntity, out var holder) &&
            holder.Target == ent.Owner)
        {
            args.Cancelled = true;
        }
    }

    private void OnFullHeldStartup(Entity<ActiveStateScpHoldableFullHoldComponent> ent, ref ComponentStartup args)
    {
        if (_activeHoldableQuery.TryComp(ent.Owner, out var held))
            SyncPlaceholderHands((ent.Owner, held));

        ZeroHeldVelocity(ent.Owner);
        _actionBlocker.UpdateCanMove(ent.Owner);
    }

    private void OnFullHeldRemove(Entity<ActiveStateScpHoldableFullHoldComponent> ent, ref ComponentRemove args)
    {
        DeleteHeldHandBlockers(ent.Owner);
        _actionBlocker.UpdateCanMove(ent.Owner);
    }

    private void OnHolderStartup(Entity<ActiveScpHolderComponent> ent, ref ComponentStartup args)
    {
        SyncHolderState(ent);
    }

    private void OnHolderShutdown(Entity<ActiveScpHolderComponent> ent, ref ComponentShutdown args)
    {
        var target = ent.Comp.Target;
        ent.Comp.Target = null;
        DeleteHolderHandBlockers(ent.Owner);

        if (!_timing.ApplyingState)
            RemComp<ActiveStateScpHolderSlowdownComponent>(ent.Owner);

        OnHolderStateShutdown(ent.Owner, target);
    }

    private void OnHolderSlowdownRemove(Entity<ActiveStateScpHolderSlowdownComponent> ent, ref ComponentRemove args)
    {
        _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnHolderSlowdownAfterState(Entity<ActiveStateScpHolderSlowdownComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnHolderSlowdownRefreshMoveSpeed(Entity<ActiveStateScpHolderSlowdownComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        args.ModifySpeed(ent.Comp.WalkModifier, ent.Comp.SprintModifier);
    }

    private void OnHolderBeforeThrow(Entity<ActiveScpHolderComponent> ent, ref BeforeThrowEvent args)
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

    private void OnHolderHandsModified(Entity<ActiveScpHolderComponent> ent, ref DidEquipHandEvent args)
    {
        if (ent.Comp.LifeStage > ComponentLifeStage.Running)
            return;

        if (ent.Comp.Target == null)
            return;

        if (!_activeHoldableQuery.HasComp(ent.Comp.Target.Value))
            return;

        SyncHolderState(ent);
    }

    private void OnHolderPreventCollide(Entity<ActiveScpHolderComponent> ent, ref PreventCollideEvent args)
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
        if (!_activeHolderQuery.TryComp(args.User, out var holder))
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
