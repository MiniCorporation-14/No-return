using Content.Shared.Hands;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Throwing;
using Robust.Shared.Physics.Events;

namespace Content.Shared._Scp.Holding;

public sealed partial class SharedScpHoldingSystem
{
    /*
     * Event subscription wiring plus routing/lifecycle reactions for held and holder entities.
     */
    private void SubscribeHoldingEvents()
    {
        SubscribeLocalEvent<ScpHoldComponent, ComponentShutdown>(OnHoldShutdown);

        SubscribeLocalEvent<ScpHeldComponent, ComponentStartup>(OnHeldStartup);
        SubscribeLocalEvent<ScpHeldComponent, ComponentShutdown>(OnHeldShutdown);
        SubscribeLocalEvent<ScpHeldComponent, ScpHoldBreakoutAlertEvent>(OnBreakoutAlert);
        SubscribeLocalEvent<ScpHeldComponent, ScpHoldBreakoutDoAfterEvent>(OnBreakoutDoAfter);
        SubscribeLocalEvent<ScpHeldComponent, MoveInputEvent>(OnHeldMoveInput);
        SubscribeLocalEvent<ScpHeldComponent, HandCountChangedEvent>(OnHandCountChanged);
        SubscribeLocalEvent<ScpHeldComponent, UpdateCanMoveEvent>(OnHeldUpdateCanMove);
        SubscribeLocalEvent<ScpHeldComponent, AttemptMobCollideEvent>(OnHeldAttemptMobCollide);
        SubscribeLocalEvent<ScpHeldComponent, AttemptMobTargetCollideEvent>(OnHeldAttemptMobTargetCollide);
        SubscribeLocalEvent<ScpHeldComponent, PreventCollideEvent>(OnHeldPreventCollide);

        SubscribeLocalEvent<ScpHolderComponent, ComponentStartup>(OnHolderStartup);
        SubscribeLocalEvent<ScpHolderComponent, ComponentShutdown>(OnHolderShutdown);
        SubscribeLocalEvent<ScpHolderComponent, RefreshMovementSpeedModifiersEvent>(OnHolderRefreshMoveSpeed);
        SubscribeLocalEvent<ScpHolderComponent, BeforeThrowEvent>(OnHolderBeforeThrow);
        SubscribeLocalEvent<ScpHolderComponent, DidEquipHandEvent>(OnHolderHandsModified);
        SubscribeLocalEvent<ScpHolderComponent, PreventCollideEvent>(OnHolderPreventCollide);
        SubscribeLocalEvent<ScpHoldHandBlockerComponent, GettingDroppedAttemptEvent>(OnHolderBlockerDropped);
    }

    private void OnHoldShutdown(Entity<ScpHoldComponent> ent, ref ComponentShutdown args)
    {
        if (_net.IsClient ||
            TerminatingOrDeleted(ent.Owner) ||
            !_holderQuery.TryComp(ent.Owner, out var holder) ||
            holder.Target == null ||
            TerminatingOrDeleted(holder.Target.Value))
        {
            return;
        }

        ReleaseHolderContribution(ent.Owner, holder.Target.Value, clearIfEmpty: true);
    }

    private void OnHeldStartup(Entity<ScpHeldComponent> ent, ref ComponentStartup args)
    {
        RefreshHeldState(ent);
    }

    private void OnHeldShutdown(Entity<ScpHeldComponent> ent, ref ComponentShutdown args)
    {
        _alerts.ClearAlert(ent.Owner, "ScpHoldGrabbed");
        _statusEffects.TryRemoveStatusEffect(ent.Owner, GrabbedStatusEffect);
        DeleteHeldHandBlockers(ent.Owner);

        if (!_timing.ApplyingState)
            CancelBreakoutDoAfter(ent);

        if (!_net.IsClient)
        {
            foreach (var holderUid in ent.Comp.Holders)
            {
                if (!TerminatingOrDeleted(holderUid) && _holderQuery.HasComp(holderUid))
                    RemComp<ScpHolderComponent>(holderUid);
            }
        }

        _actionBlocker.UpdateCanMove(ent.Owner);

        if (_net.IsClient)
            _physics.UpdateIsPredicted(ent.Owner);
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
        SetBreakoutDoAfterId(ent, null);

        if (args.Handled)
            return;

        args.Handled = true;

        if (args.Cancelled)
        {
            PopupTarget(ent.Owner, "scp-hold-breakout-interrupted");
            return;
        }

        RaiseBreakoutEvent(ent, args.ViaMovement, applyImmunity: true);
        ClearHoldState(ent, applyImmunity: true);
    }

    private void OnHeldMoveInput(Entity<ScpHeldComponent> ent, ref MoveInputEvent args)
    {
        if (!args.State)
            return;

        TryBreakOut(ent, viaMovement: true);
    }

    private void OnHandCountChanged(Entity<ScpHeldComponent> ent, ref HandCountChangedEvent args)
    {
        if (_net.IsClient)
            return;

        SyncHeldState(ent);
    }

    private void OnHeldUpdateCanMove(Entity<ScpHeldComponent> ent, ref UpdateCanMoveEvent args)
    {
        if (ent.Comp.LifeStage > ComponentLifeStage.Running)
            return;

        if (ent.Comp.FullHold)
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

    private void OnHolderStartup(Entity<ScpHolderComponent> ent, ref ComponentStartup args)
    {
        RefreshHolderState(ent);
    }

    private void OnHolderShutdown(Entity<ScpHolderComponent> ent, ref ComponentShutdown args)
    {
        ent.Comp.Target = null;
        ent.Comp.SlowdownEnabled = false;
        DeleteHolderHandBlockers(ent.Owner);
        _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnHolderRefreshMoveSpeed(Entity<ScpHolderComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (!ent.Comp.SlowdownEnabled)
            return;

        args.ModifySpeed(ent.Comp.WalkModifier, ent.Comp.SprintModifier);
    }

    private void OnHolderBeforeThrow(Entity<ScpHolderComponent> ent, ref BeforeThrowEvent args)
    {
        if (ent.Comp.Target == null ||
            !TryComp<ScpHoldHandBlockerComponent>(args.ItemUid, out var blocker) ||
            blocker.Target != ent.Comp.Target.Value)
        {
            return;
        }

        ReleaseHolderContribution(ent.Owner, ent.Comp.Target.Value, clearIfEmpty: true);
        args.Cancelled = true;
    }

    private void OnHolderHandsModified(Entity<ScpHolderComponent> ent, ref DidEquipHandEvent args)
    {
        if (ent.Comp.LifeStage > ComponentLifeStage.Running ||
            TerminatingOrDeleted(ent.Owner) ||
            ent.Comp.Target == null ||
            TerminatingOrDeleted(ent.Comp.Target.Value))
        {
            return;
        }

        RefreshHolderState(ent);
    }

    private void OnHolderPreventCollide(Entity<ScpHolderComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled ||
            ent.Comp.Target == null ||
            ent.Comp.Target != args.OtherEntity)
        {
            return;
        }

        args.Cancelled = true;
    }

    private void OnHolderBlockerDropped(Entity<ScpHoldHandBlockerComponent> ent, ref GettingDroppedAttemptEvent args)
    {
        if (!_holderQuery.TryComp(args.User, out var holder) ||
            holder.Target == null ||
            holder.Target != ent.Comp.Target)
        {
            return;
        }

        ReleaseHolderContribution(args.User, ent.Comp.Target, clearIfEmpty: true);
    }
}
