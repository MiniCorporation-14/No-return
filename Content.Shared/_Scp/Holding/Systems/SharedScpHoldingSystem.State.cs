using System.Diagnostics.CodeAnalysis;
using Content.Shared._Scp.Holding.Components;
using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;

namespace Content.Shared._Scp.Holding.Systems;

public abstract partial class SharedScpHoldingSystem
{
    /*
     * State-local dependencies, caches, held lifecycle, holder membership, and full-hold transitions.
     */

    [Dependency] private readonly SharedBodySystem _body = default!;

    private readonly List<EntityUid> _holdersToRemove = [];
    private readonly List<EntityUid> _holderCooldownsToApply = [];

    private EntityQuery<BodyComponent> _bodyQuery;

    private void InitializeStateQueries()
    {
        _bodyQuery = GetEntityQuery<BodyComponent>();
    }

    private void UpdateHeld(Entity<ActiveScpHoldableComponent> held)
    {
        if (!TryGetHeldHoldable(held, out var holdable))
            return;

        if (!EnsurePrimaryHolder(held))
        {
            ClearHoldState(held, applyImmunity: false);
            return;
        }

        var desiredSoftDragDistance = GetDesiredSoftDragDistance(holdable);
        var maintenanceRange = GetHoldMaintenanceRange(holdable, desiredSoftDragDistance);

        if (!_activeHoldableFullHoldStateQuery.HasComp(held.Owner))
            UpdateSoftDrag(held, holdable, maintenanceRange, desiredSoftDragDistance);
        else
            ZeroHeldVelocity(held.Owner);

        _holdersToRemove.Clear();

        foreach (var holderUid in held.Comp.Holders)
        {
            if (ShouldReleaseHolder(holderUid, held.Owner, maintenanceRange))
                _holdersToRemove.Add(holderUid);
        }

        foreach (var holderUid in _holdersToRemove)
        {
            ReleaseHolderContribution(holderUid, held.Owner, clearIfEmpty: false);

            if (!_activeHoldableQuery.TryComp(held.Owner, out _))
                return;
        }

        if (_activeHoldableQuery.TryComp(held.Owner, out var refreshed))
            SyncHeldState((held.Owner, refreshed));
    }

    private Entity<ActiveScpHoldableComponent> EnsureHeldState(EntityUid target)
    {
        var created = !_activeHoldableQuery.TryComp(target, out var held);
        held ??= EnsureComp<ActiveScpHoldableComponent>(target);

        if (created)
            held.SoftEscapeAvailableAt = _timing.CurTime;

        held.RequiredHolderCount = GetRequiredHolderCount(target);
        return (target, held);
    }

    private void AddHolderContribution(EntityUid holderUid, Entity<ActiveScpHoldableComponent> held)
    {
        if (!held.Comp.Holders.Contains(holderUid))
        {
            held.Comp.Holders.Add(holderUid);
            Dirty(held);
        }

        var holderCreated = !_activeHolderQuery.TryComp(holderUid, out var holder);
        holder ??= EnsureComp<ActiveScpHolderComponent>(holderUid);
        SetHolderTarget((holderUid, holder), held.Owner);
        SyncHolderState((holderUid, holder));

        if (holderCreated)
            Dirty(holderUid, holder);
    }

    protected void ReleaseHolderContribution(EntityUid holderUid, EntityUid targetUid, bool clearIfEmpty)
    {
        if (!_activeHoldableQuery.TryComp(targetUid, out var held))
            return;

        var removed = false;
        for (var i = held.Holders.Count - 1; i >= 0; i--)
        {
            if (held.Holders[i] == holderUid)
            {
                held.Holders.RemoveAt(i);
                removed = true;
            }
        }

        if (removed)
            Dirty(targetUid, held);

        if (_activeHolderQuery.HasComp(holderUid))
            RemComp<ActiveScpHolderComponent>(holderUid);
        else if (_activeHolderSlowdownStateQuery.HasComp(holderUid))
            RemComp<ActiveStateScpHolderSlowdownComponent>(holderUid);

        if (held.PrimaryHolder == holderUid)
            SetHeldPrimaryHolder((targetUid, held), null);

        if (held.Holders.Count == 0)
        {
            if (clearIfEmpty)
                ClearHoldState((targetUid, held), applyImmunity: false);
            return;
        }

        SyncHeldState((targetUid, held));
    }

    protected void SyncHeldState(Entity<ActiveScpHoldableComponent> held)
    {
        if (!_activeHoldableQuery.TryComp(held.Owner, out var heldComp))
            return;

        held.Comp = heldComp;

        if (!TryGetHeldHoldable(held, out var holdable))
            return;

        held.Comp.RequiredHolderCount = GetRequiredHolderCount(held.Owner);

        if (held.Comp.Holders.Count == 0)
        {
            ClearHoldState(held, applyImmunity: false);
            return;
        }

        if (!EnsurePrimaryHolder(held))
        {
            ClearHoldState(held, applyImmunity: false);
            return;
        }

        if (held.Comp.Holders.Count >= held.Comp.RequiredHolderCount)
        {
            EnterFullHold(held, holdable);
            return;
        }

        ExitFullHold(held);
        var desiredSoftDragDistance = GetDesiredSoftDragDistance(holdable);
        var maintenanceRange = GetHoldMaintenanceRange(holdable, desiredSoftDragDistance);
        UpdateSoftDrag(held, holdable, maintenanceRange, desiredSoftDragDistance);
        UpdateHolderSlowdowns(held, holdable);
        SyncPlaceholderHands(held);
    }

    private void EnterFullHold(Entity<ActiveScpHoldableComponent> held, ScpHoldableComponent holdable)
    {
        var fullHeldCreated = !_activeHoldableFullHoldStateQuery.TryComp(held.Owner, out var fullHeld);
        fullHeld ??= EnsureComp<ActiveStateScpHoldableFullHoldComponent>(held.Owner);

        if (fullHeldCreated)
        {
            fullHeld.StartedAt = _timing.CurTime;
            Dirty(held.Owner, fullHeld);
        }

        UpdateHolderSlowdowns(held, holdable);

        if (fullHeldCreated)
            return;

        SyncPlaceholderHands(held);
        ZeroHeldVelocity(held.Owner);
    }

    private void ExitFullHold(Entity<ActiveScpHoldableComponent> held)
    {
        if (!_activeHoldableFullHoldStateQuery.HasComp(held.Owner))
            return;

        EndBreakoutAttempt(held.Owner, cancelDoAfter: true);
        RemComp<ActiveStateScpHoldableFullHoldComponent>(held.Owner);
    }

    private bool EnsurePrimaryHolder(Entity<ActiveScpHoldableComponent> held)
    {
        if (held.Comp.PrimaryHolder != null && IsValidPrimaryHolder(held, held.Comp.PrimaryHolder.Value))
            return true;

        SetHeldPrimaryHolder(held, null);

        foreach (var holderUid in held.Comp.Holders)
        {
            if (!_activeHolderQuery.TryComp(holderUid, out var holder))
                continue;

            if (holder.Target != held.Owner)
                continue;

            SetHeldPrimaryHolder(held, holderUid);
            return true;
        }

        return false;
    }

    private void ClearHoldState(Entity<ActiveScpHoldableComponent> held, bool applyImmunity)
    {
        if (_activeHoldableQuery.TryComp(held.Owner, out var refreshed))
            held = (held.Owner, refreshed);

        EndBreakoutAttempt(held.Owner, cancelDoAfter: true);

        if (_activeHoldableFullHoldStateQuery.HasComp(held.Owner))
            RemComp<ActiveStateScpHoldableFullHoldComponent>(held.Owner);

        _holderCooldownsToApply.Clear();

        foreach (var holderUid in held.Comp.Holders)
        {
            if (applyImmunity)
                _holderCooldownsToApply.Add(holderUid);

            if (_activeHolderQuery.HasComp(holderUid))
                RemComp<ActiveScpHolderComponent>(holderUid);
            else if (_activeHolderSlowdownStateQuery.HasComp(holderUid))
                RemComp<ActiveStateScpHolderSlowdownComponent>(holderUid);
        }

        held.Comp.Holders.Clear();
        held.Comp.PrimaryHolder = null;

        if (applyImmunity)
        {
            if (_holdableQuery.TryComp(held.Owner, out var holdable))
            {
                if (!TryComp<ScpHoldImmuneComponent>(held.Owner, out var immune))
                    immune = EnsureComp<ScpHoldImmuneComponent>(held.Owner);

                immune.ExpiresAt = _timing.CurTime + holdable.PostBreakoutImmunity;
                Dirty(held.Owner, immune);
            }
        }

        foreach (var holderUid in _holderCooldownsToApply)
        {
            ApplyFullBreakoutHolderCooldown(holderUid);
        }

        RemComp<ActiveScpHoldableComponent>(held.Owner);
    }

    private void UpdateHolderSlowdowns(Entity<ActiveScpHoldableComponent> held, ScpHoldableComponent holdable)
    {
        foreach (var holderUid in held.Comp.Holders)
        {
            SetHolderSlowdown(holderUid, holdable.HolderWalkModifier, holdable.HolderSprintModifier);
        }
    }

    private void SetHolderSlowdown(EntityUid holderUid, float walkModifier, float sprintModifier)
    {
        var slowdownCreated = !_activeHolderSlowdownStateQuery.TryComp(holderUid, out var slowdown);
        slowdown ??= EnsureComp<ActiveStateScpHolderSlowdownComponent>(holderUid);

        if (!slowdownCreated &&
            MathHelper.CloseTo(slowdown.WalkModifier, walkModifier) &&
            MathHelper.CloseTo(slowdown.SprintModifier, sprintModifier))
        {
            return;
        }

        slowdown.WalkModifier = walkModifier;
        slowdown.SprintModifier = sprintModifier;
        Dirty(holderUid, slowdown);
        _movement.RefreshMovementSpeedModifiers(holderUid);
    }

    private int GetRequiredHolderCount(EntityUid target)
    {
        if (_bodyQuery.TryComp(target, out var body))
        {
            var handCount = 0;
            foreach (var _ in _body.GetBodyChildrenOfType(target, BodyPartType.Hand, body))
            {
                handCount++;
            }

            if (handCount > 0)
                return handCount;
        }

        return 2;
    }

    private bool TryGetHeldHoldable(Entity<ActiveScpHoldableComponent> held, [NotNullWhen(true)] out ScpHoldableComponent? holdable)
    {
        if (_holdableQuery.TryComp(held.Owner, out holdable))
            return true;

        ClearHoldState(held, applyImmunity: false);
        holdable = null;
        return false;
    }

    private bool ShouldReleaseHolder(EntityUid holderUid, EntityUid heldUid, float maintenanceRange)
    {
        if (!_holderConfigQuery.HasComp(holderUid))
            return true;

        if (!_activeHolderQuery.TryComp(holderUid, out var holder))
            return true;

        if (holder.Target != heldUid)
            return true;

        if (!_container.IsInSameOrNoContainer(holderUid, heldUid))
            return true;

        return !_interaction.InRangeUnobstructed(holderUid, heldUid, maintenanceRange);
    }

    private bool IsValidPrimaryHolder(Entity<ActiveScpHoldableComponent> held, EntityUid primaryHolderUid)
    {
        if (!_activeHolderQuery.TryComp(primaryHolderUid, out var holder))
            return false;

        if (holder.Target != held.Owner)
            return false;

        return held.Comp.Holders.Contains(primaryHolderUid);
    }

    private void SetHolderTarget(Entity<ActiveScpHolderComponent> holder, EntityUid? target)
    {
        if (holder.Comp.Target == target)
            return;

        holder.Comp.Target = target;
        Dirty(holder);
    }

    private void SetHeldPrimaryHolder(Entity<ActiveScpHoldableComponent> held, EntityUid? primaryHolder)
    {
        if (held.Comp.PrimaryHolder == primaryHolder)
            return;

        held.Comp.PrimaryHolder = primaryHolder;
        Dirty(held);
    }
}
