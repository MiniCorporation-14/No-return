using Content.Shared._Scp.Holding.Components;
using Content.Shared.DoAfter;
using Content.Shared.Movement.Components;
using Robust.Shared.Physics;

namespace Content.Shared._Scp.Holding.Systems;

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

    public bool TryToggleHold(Entity<ScpHolderComponent> holder, EntityUid target, bool attemptChecked = false)
    {
        if (_activeHolderQuery.TryComp(holder, out var activeHolder) && activeHolder.Target != null)
        {
            if (activeHolder.Target.Value == target)
                return TryReleaseHold(holder, target);

            Popup(holder, "scp-hold-already-holding-other");
            return false;
        }

        if (!CanStartHold(holder))
            return false;

        if (!CanToggleHold(holder, target, checkAttempt: !attemptChecked))
            return false;

        var held = EnsureHeldState(target);
        AddHolderContribution(holder, held);
        SyncHeldState(held);

        StartHoldCooldown(holder);
        return true;
    }

    public bool TryReleaseHold(Entity<ScpHolderComponent> holder, EntityUid target)
    {
        if (!CanReleaseHold(holder, target))
            return false;

        ReleaseHolderContribution(holder, target, clearIfEmpty: true);
        return true;
    }

    public bool CanReleaseHold(Entity<ScpHolderComponent> holder, EntityUid target, bool quiet = false)
    {
        if (!_activeHolderQuery.TryComp(holder, out var activeHolder) ||
            activeHolder.Target == null)
        {
            return false;
        }

        if (activeHolder.Target != target)
        {
            if (!quiet)
                Popup(holder, "scp-hold-already-holding-other");

            return false;
        }

        return true;
    }

    public bool CanToggleHold(
        Entity<ScpHolderComponent> holder,
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
                Popup(holder, "scp-hold-target-not-holdable", ("target", target));

            return false;
        }

        if (!_whitelist.CheckBoth(target, holder.Comp.HoldableBlacklist, holder.Comp.HoldableWhitelist))
        {
            if (!quiet)
                Popup(holder, "scp-hold-target-invalid", ("target", target));

            return false;
        }

        if (!_whitelist.CheckBoth(holder, holdable.HolderBlacklist, holdable.HolderWhitelist))
        {
            if (!quiet)
                Popup(holder, "scp-hold-target-invalid", ("target", target));

            return false;
        }

        if (!_moverQuery.HasComp(holder))
        {
            if (!quiet)
                Popup(holder, "scp-hold-target-invalid", ("target", target));

            return false;
        }

        if (!_moverQuery.HasComp(target))
        {
            if (!quiet)
                Popup(holder, "scp-hold-target-invalid", ("target", target));

            return false;
        }

        if (!_physicsQuery.TryComp(target, out var targetPhysics))
        {
            if (!quiet)
                Popup(holder, "scp-hold-target-invalid", ("target", target));

            return false;
        }

        if (targetPhysics.BodyType == BodyType.Static)
        {
            if (!quiet)
                Popup(holder, "scp-hold-target-invalid", ("target", target));

            return false;
        }

        if (!_container.IsInSameOrNoContainer(holder.Owner, target))
        {
            if (!quiet)
                Popup(holder, "scp-hold-target-invalid", ("target", target));

            return false;
        }

        if (TryComp<ScpHoldImmuneComponent>(target, out _))
        {
            if (!quiet)
                Popup(holder, "scp-hold-target-immune", ("target", target));

            return false;
        }

        if (!ignoreHandAvailability && !HasAvailableHolderHand(holder))
        {
            if (!quiet)
                Popup(holder, "scp-hold-holder-no-free-hand", ("target", target));

            return false;
        }

        var range = holdable.HoldRange;
        if (_activeHoldableQuery.HasComp(target))
        {
            if (_activeHoldableFullHoldStateQuery.HasComp(target))
            {
                if (!quiet)
                    Popup(holder.Owner, "scp-hold-target-fully-held", ("target", target));

                return false;
            }
        }

        if (!_interaction.InRangeUnobstructed(holder.Owner, target, range))
        {
            if (!quiet)
                Popup(holder, "scp-hold-target-too-far", ("target", target));

            return false;
        }

        if (checkAttempt && !CanPassHoldAttempt(holder, target))
            return false;

        return true;
    }

    public bool TryBreakOut(Entity<ActiveScpHoldableComponent> held, bool viaMovement)
    {
        return _activeHoldableFullHoldStateQuery.HasComp(held.Owner)
            ? TryStartFullBreakout(held, viaMovement)
            : TrySoftBreakOut(held, viaMovement);
    }

    public bool TryForceBreakOut(Entity<ActiveScpHoldableComponent?> held, bool viaMovement = false, bool applyImmunity = false)
    {
        if (!Resolve(held, ref held.Comp, false))
            return false;

        BreakOut((held.Owner, held.Comp), viaMovement, applyImmunity);
        return true;
    }

    public void SyncHolderState(Entity<ActiveScpHolderComponent> holder)
    {
        SyncHolderHandBlocker(holder);
    }

    private bool TrySoftBreakOut(Entity<ActiveScpHoldableComponent> held, bool viaMovement)
    {
        if (_timing.CurTime < held.Comp.SoftEscapeAvailableAt)
            return false;

        if (!viaMovement)
            Popup(held, "scp-hold-breakout-start");

        BreakOut(held, viaMovement, applyImmunity: false);
        return true;
    }

    private bool TryStartFullBreakout(Entity<ActiveScpHoldableComponent> held, bool viaMovement)
    {
        if (!_activeHoldableFullHoldStateQuery.TryComp(held.Owner, out var fullHeld))
            return false;

        if (!TryGetHeldHoldable(held, out var holdable))
            return false;

        if (fullHeld.StartedAt == TimeSpan.Zero)
        {
            Popup(held, "scp-hold-breakout-too-early", ("seconds", 1));
            return false;
        }

        var breakoutAvailableAt = fullHeld.StartedAt + holdable.FullHoldDelay;
        if (_timing.CurTime < breakoutAvailableAt)
        {
            var remaining = breakoutAvailableAt - _timing.CurTime;
            var remainingSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
            Popup(held, "scp-hold-breakout-too-early", ("seconds", remainingSeconds));
            return false;
        }

        if (_breakoutAttemptQuery.HasComp(held.Owner))
            return true;

        var doAfter = new DoAfterArgs(
            EntityManager,
            held.Owner,
            holdable.FullBreakoutDuration,
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

        StartBreakoutAttempt(held.Owner, id.Value);

        Popup(held, "scp-hold-breakout-start");
        return true;
    }

    private bool CanStartHold(Entity<ScpHolderComponent> holder, bool quiet = false)
    {
        if (!IsHoldCoolingDown(holder, out var remaining))
            return true;

        if (!quiet)
        {
            var remainingSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
            Popup(holder, "scp-hold-holder-action-on-cooldown", ("seconds", remainingSeconds));
        }

        return false;
    }

    private bool IsHoldCoolingDown(Entity<ScpHolderComponent> holder, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;

        if (holder.Comp.HoldAvailableAt is not { } availableAt || availableAt <= _timing.CurTime)
            return false;

        remaining = availableAt - _timing.CurTime;
        return true;
    }

    private void StartHoldCooldown(Entity<ScpHolderComponent> holder)
    {
        SetHoldAvailableAt(holder, _timing.CurTime + holder.Comp.HoldActionCooldown);
    }

    private void ApplyFullBreakoutHolderCooldown(EntityUid holderUid)
    {
        if (!_holderConfigQuery.TryComp(holderUid, out var hold))
            return;

        var cooldownEnd = _timing.CurTime + TimeSpan.FromTicks(hold.HoldActionCooldown.Ticks * 2);
        if (hold.HoldAvailableAt != null && hold.HoldAvailableAt.Value >= cooldownEnd)
            return;

        SetHoldAvailableAt((holderUid, hold), cooldownEnd);
    }

    private void SetHoldAvailableAt(Entity<ScpHolderComponent> holder, TimeSpan? holdAvailableAt)
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

        DirtyField(holder.Owner, holder.Comp, nameof(ScpHolderComponent.HoldAvailableAt));
    }

    private bool CanPassHoldAttempt(EntityUid holderUid, EntityUid targetUid)
    {
        var attempt = new ScpHoldAttemptEvent(holderUid, targetUid);
        RaiseLocalEvent(targetUid, ref attempt);
        RaiseLocalEvent(holderUid, ref attempt);
        return !attempt.Cancelled;
    }

    private void RaiseBreakoutEvent(Entity<ActiveScpHoldableComponent> held, bool viaMovement, bool applyImmunity)
    {
        var ev = new ScpHoldBreakoutEvent(viaMovement, _activeHoldableFullHoldStateQuery.HasComp(held.Owner), applyImmunity);
        RaiseLocalEvent(held.Owner, ref ev);
    }

    private void BreakOut(Entity<ActiveScpHoldableComponent> held, bool viaMovement, bool applyImmunity)
    {
        RaiseBreakoutEvent(held, viaMovement, applyImmunity);
        ClearHoldState(held, applyImmunity);
    }
}
