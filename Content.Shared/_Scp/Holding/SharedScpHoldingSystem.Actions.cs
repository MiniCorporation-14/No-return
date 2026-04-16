using Content.Shared.DoAfter;
using Content.Shared.Movement.Components;
using Robust.Shared.Physics;

namespace Content.Shared._Scp.Holding;

public abstract partial class SharedScpHoldingSystem
{
    /*
     * Hold-local query caches, hold toggling API, breakout flow, and cooldown helpers.
     */

    private EntityQuery<InputMoverComponent> _moverQuery;
    private EntityQuery<ScpHoldableComponent> _holdableQuery;

    private void InitializeHoldQueries()
    {
        _moverQuery = GetEntityQuery<InputMoverComponent>();
        _holdableQuery = GetEntityQuery<ScpHoldableComponent>();
    }

    public bool TryToggleHold(Entity<ScpHoldComponent> holder, EntityUid target, bool attemptChecked = false)
    {
        if (_holderQuery.TryComp(holder.Owner, out var activeHolder) && activeHolder.Target != null)
        {
            if (activeHolder.Target.Value == target)
            {
                ReleaseHolderContribution(holder.Owner, target, clearIfEmpty: true);
                return true;
            }

            PopupHolder(holder.Owner, "scp-hold-already-holding-other");
            return false;
        }

        if (!CanStartHold(holder))
            return false;

        if (!CanToggleHold(holder, target, checkAttempt: !attemptChecked))
            return false;

        var holdable = _holdableQuery.Comp(target);
        var held = EnsureHeldState(target, holdable, out var heldCreated);
        AddHolderContribution(holder.Owner, held);
        SyncHeldState(held);

        if (heldCreated)
            Dirty(held);

        StartHoldCooldown(holder);
        return true;
    }

    public bool CanToggleHold(
        Entity<ScpHoldComponent> holder,
        EntityUid target,
        bool quiet = false,
        bool ignoreHandAvailability = false,
        bool checkAttempt = false)
    {
        if (holder.Owner == target)
            return false;

        if (!CanStartHold(holder, quiet))
            return false;

        if (!_holdableQuery.TryComp(target, out var holdable))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-not-holdable", ("target", target));
            return false;
        }

        if (!_whitelist.CheckBoth(target, holder.Comp.HoldableBlacklist, holder.Comp.HoldableWhitelist))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-invalid", ("target", target));
            return false;
        }

        if (!_whitelist.CheckBoth(holder.Owner, holdable.HolderBlacklist, holdable.HolderWhitelist))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-invalid", ("target", target));
            return false;
        }

        if (!_moverQuery.HasComp(holder.Owner))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-invalid", ("target", target));
            return false;
        }

        if (!_moverQuery.HasComp(target))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-invalid", ("target", target));
            return false;
        }

        if (!_physicsQuery.TryComp(target, out var targetPhysics))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-invalid", ("target", target));
            return false;
        }

        if (targetPhysics.BodyType == BodyType.Static)
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-invalid", ("target", target));
            return false;
        }

        if (!_container.IsInSameOrNoContainer(holder.Owner, target))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-invalid", ("target", target));
            return false;
        }

        if (TryComp<ScpHoldImmuneComponent>(target, out _))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-immune", ("target", target));
            return false;
        }

        if (!ignoreHandAvailability && !HasAvailableHolderHand(holder.Owner))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-holder-no-free-hand", ("target", target));
            return false;
        }

        var range = holdable.HoldRange;
        if (_heldQuery.TryComp(target, out var held))
        {
            range = held.HoldRange;

            if (held.FullHold)
            {
                if (!quiet)
                    PopupHolder(holder.Owner, "scp-hold-target-fully-held", ("target", target));
                return false;
            }
        }

        if (!_interaction.InRangeUnobstructed(holder.Owner, target, range))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-too-far", ("target", target));
            return false;
        }

        if (checkAttempt && !CanPassHoldAttempt(holder.Owner, target))
            return false;

        return true;
    }

    public bool TryBreakOut(Entity<ScpHeldComponent> held, bool viaMovement)
    {
        return held.Comp.FullHold
            ? TryStartFullBreakout(held, viaMovement)
            : TrySoftBreakOut(held, viaMovement);
    }

    public bool TryForceBreakOut(Entity<ScpHeldComponent?> held, bool viaMovement = false, bool applyImmunity = false)
    {
        if (!Resolve(held, ref held.Comp, false))
            return false;

        BreakOut((held.Owner, held.Comp), viaMovement, applyImmunity);
        return true;
    }

    public void RefreshHeldState(Entity<ScpHeldComponent> held)
    {
        _alerts.ShowAlert(held.Owner, "ScpHoldGrabbed");
        SyncHeldStatusEffect(held.Owner);
        SyncPlaceholderHands(held);
        _actionBlocker.UpdateCanMove(held.Owner);
        EnsureCombatModeDisabled(held.Owner);
        _physics.UpdateIsPredicted(held.Owner);
    }

    public void RefreshHolderState(Entity<ScpHolderComponent> holder)
    {
        SyncHolderHandBlocker(holder);
        _movement.RefreshMovementSpeedModifiers(holder.Owner);
    }

    private bool TrySoftBreakOut(Entity<ScpHeldComponent> held, bool viaMovement)
    {
        if (_timing.CurTime < held.Comp.SoftEscapeAvailableAt)
            return false;

        if (!viaMovement)
            PopupTarget(held.Owner, "scp-hold-breakout-start");

        BreakOut(held, viaMovement, applyImmunity: false);
        return true;
    }

    private bool TryStartFullBreakout(Entity<ScpHeldComponent> held, bool viaMovement)
    {
        if (held.Comp.FullHoldStartedAt == null)
        {
            PopupTarget(held.Owner, "scp-hold-breakout-too-early", ("seconds", 1));
            return false;
        }

        var breakoutAvailableAt = held.Comp.FullHoldStartedAt.Value + held.Comp.FullHoldDelay;
        if (_timing.CurTime < breakoutAvailableAt)
        {
            var remaining = breakoutAvailableAt - _timing.CurTime;
            var remainingSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
            PopupTarget(held.Owner, "scp-hold-breakout-too-early", ("seconds", remainingSeconds));
            return false;
        }

        if (held.Comp.BreakoutDoAfterId != null)
            return true;

        var doAfter = new DoAfterArgs(
            EntityManager,
            held.Owner,
            held.Comp.FullBreakoutDuration,
            new ScpHoldBreakoutDoAfterEvent(viaMovement),
            held.Owner,
            target: held.Owner)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = false,
            Hidden = false,
        };

        if (!_doAfter.TryStartDoAfter(doAfter, out var id))
            return false;

        SetBreakoutDoAfterId(held, id.Value.Index);
        ShowBreakoutAttemptFeedback(held);

        PopupTarget(held.Owner, "scp-hold-breakout-start");
        return true;
    }

    private bool CanStartHold(Entity<ScpHoldComponent> holder, bool quiet = false)
    {
        if (!IsHoldCoolingDown(holder, out var remaining))
            return true;

        if (!quiet)
        {
            var remainingSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
            PopupHolder(holder.Owner, "scp-hold-holder-action-on-cooldown", ("seconds", remainingSeconds));
        }

        return false;
    }

    private bool IsHoldCoolingDown(Entity<ScpHoldComponent> holder, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;

        if (holder.Comp.HoldAvailableAt is not { } availableAt || availableAt <= _timing.CurTime)
            return false;

        remaining = availableAt - _timing.CurTime;
        return true;
    }

    private void StartHoldCooldown(Entity<ScpHoldComponent> holder)
    {
        SetHoldAvailableAt(holder, _timing.CurTime + holder.Comp.HoldActionCooldown);
    }

    private void ApplyFullBreakoutHolderCooldown(EntityUid holderUid)
    {
        if (!_holdQuery.TryComp(holderUid, out var hold))
            return;

        var cooldownEnd = _timing.CurTime + TimeSpan.FromTicks(hold.HoldActionCooldown.Ticks * 2);
        if (hold.HoldAvailableAt != null && hold.HoldAvailableAt.Value >= cooldownEnd)
            return;

        SetHoldAvailableAt((holderUid, hold), cooldownEnd);
    }

    private void SetHoldAvailableAt(Entity<ScpHoldComponent> holder, TimeSpan? holdAvailableAt)
    {
        if (holder.Comp.HoldAvailableAt == holdAvailableAt)
            return;

        var previousHoldAvailableAt = holder.Comp.HoldAvailableAt;
        holder.Comp.HoldAvailableAt = holdAvailableAt;

        if (previousHoldAvailableAt == null || holdAvailableAt == null)
        {
            Dirty(holder);
            return;
        }

        DirtyHoldField(holder, nameof(ScpHoldComponent.HoldAvailableAt));
    }

    private bool CanPassHoldAttempt(EntityUid holderUid, EntityUid targetUid)
    {
        var attempt = new ScpHoldAttemptEvent(holderUid, targetUid);
        RaiseLocalEvent(targetUid, attempt);
        RaiseLocalEvent(holderUid, attempt);
        return !attempt.Cancelled;
    }

    private void RaiseBreakoutEvent(Entity<ScpHeldComponent> held, bool viaMovement, bool applyImmunity)
    {
        var ev = new ScpHoldBreakoutEvent(viaMovement, held.Comp.FullHold, applyImmunity);
        RaiseLocalEvent(held.Owner, ev);
    }

    private void BreakOut(Entity<ScpHeldComponent> held, bool viaMovement, bool applyImmunity)
    {
        RaiseBreakoutEvent(held, viaMovement, applyImmunity);
        ClearHoldState(held, applyImmunity);
    }
}
