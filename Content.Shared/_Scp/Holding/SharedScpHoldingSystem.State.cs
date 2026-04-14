using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;

namespace Content.Shared._Scp.Holding;

public sealed partial class SharedScpHoldingSystem
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

    private void UpdateHeld(Entity<ScpHeldComponent> held)
    {
        if (!EnsurePrimaryHolder(held))
        {
            ClearHoldState(held, applyImmunity: false);
            return;
        }

        var desiredSoftDragDistance = GetDesiredSoftDragDistance(held);
        var maintenanceRange = GetHoldMaintenanceRange(held.Comp.HoldRange, desiredSoftDragDistance);

        if (!held.Comp.FullHold)
            UpdateSoftDrag(held, maintenanceRange, desiredSoftDragDistance);
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

            if (!_heldQuery.TryComp(held.Owner, out _))
                return;
        }

        if (_heldQuery.TryComp(held.Owner, out var refreshed))
            SyncHeldState((held.Owner, refreshed));
    }

    private Entity<ScpHeldComponent> EnsureHeldState(EntityUid target, ScpHoldableComponent config, out bool created)
    {
        created = !_heldQuery.TryComp(target, out var held);
        held ??= EnsureComp<ScpHeldComponent>(target);

        if (created)
            CopyConfig(config, held);

        held.RequiredHolderCount = GetRequiredHolderCount(target);
        return (target, held);
    }

    private void AddHolderContribution(EntityUid holderUid, Entity<ScpHeldComponent> held)
    {
        if (!held.Comp.Holders.Contains(holderUid))
        {
            held.Comp.Holders.Add(holderUid);
            DirtyHeldField(held, nameof(ScpHeldComponent.Holders));
        }

        var holderCreated = !_holderQuery.TryComp(holderUid, out var holder);
        holder ??= EnsureComp<ScpHolderComponent>(holderUid);
        SetHolderTarget((holderUid, holder), held.Owner);
        SetHolderSlowdown((holderUid, holder), false, held.Comp.WalkModifier, held.Comp.SprintModifier);
        RefreshHolderState((holderUid, holder));

        if (holderCreated)
            Dirty(holderUid, holder);
    }

    private void ReleaseHolderContribution(EntityUid holderUid, EntityUid targetUid, bool clearIfEmpty)
    {
        if (!_heldQuery.TryComp(targetUid, out var held))
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
            DirtyHeldField(targetUid, held, nameof(ScpHeldComponent.Holders));

        if (_holderQuery.HasComp(holderUid))
            RemComp<ScpHolderComponent>(holderUid);

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

    private void SyncHeldState(Entity<ScpHeldComponent> held)
    {
        if (!_heldQuery.TryComp(held.Owner, out var heldComp))
            return;

        held.Comp = heldComp;
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
            EnterFullHold(held);
            return;
        }

        ExitFullHold(held);
        var desiredSoftDragDistance = GetDesiredSoftDragDistance(held);
        var maintenanceRange = GetHoldMaintenanceRange(held.Comp.HoldRange, desiredSoftDragDistance);
        UpdateSoftDrag(held, maintenanceRange, desiredSoftDragDistance);
        UpdateHolderSlowdowns(held);
        SyncPlaceholderHands(held);
    }

    private void EnterFullHold(Entity<ScpHeldComponent> held)
    {
        if (!held.Comp.FullHold)
        {
            held.Comp.FullHold = true;
            held.Comp.FullHoldStartedAt = _timing.CurTime;
            DirtyHeldFields(
                held,
                nameof(ScpHeldComponent.FullHold),
                nameof(ScpHeldComponent.FullHoldStartedAt));
        }

        UpdateHolderSlowdowns(held);
        SyncPlaceholderHands(held);
        ZeroHeldVelocity(held.Owner);
        _actionBlocker.UpdateCanMove(held.Owner);
    }

    private void ExitFullHold(Entity<ScpHeldComponent> held)
    {
        CancelBreakoutDoAfter(held);

        if (!held.Comp.FullHold && held.Comp.FullHoldStartedAt == null)
            return;

        held.Comp.FullHold = false;
        held.Comp.FullHoldStartedAt = null;
        DirtyHeldFields(
            held,
            nameof(ScpHeldComponent.FullHold),
            nameof(ScpHeldComponent.FullHoldStartedAt));
        SyncPlaceholderHands(held);
        _actionBlocker.UpdateCanMove(held.Owner);
    }

    private bool EnsurePrimaryHolder(Entity<ScpHeldComponent> held)
    {
        if (held.Comp.PrimaryHolder != null && IsValidPrimaryHolder(held, held.Comp.PrimaryHolder.Value))
            return true;

        SetHeldPrimaryHolder(held, null);

        foreach (var holderUid in held.Comp.Holders)
        {
            if (!_holderQuery.TryComp(holderUid, out var holder))
                continue;

            if (holder.Target != held.Owner)
                continue;

            SetHeldPrimaryHolder(held, holderUid);
            return true;
        }

        return false;
    }

    private void ClearHoldState(Entity<ScpHeldComponent> held, bool applyImmunity)
    {
        if (_heldQuery.TryComp(held.Owner, out var refreshed))
            held = (held.Owner, refreshed);

        CancelBreakoutDoAfter(held);
        DeleteHeldHandBlockers(held.Owner);
        _actionBlocker.UpdateCanMove(held.Owner);
        _holderCooldownsToApply.Clear();

        foreach (var holderUid in held.Comp.Holders)
        {
            if (applyImmunity)
                _holderCooldownsToApply.Add(holderUid);

            if (_holderQuery.HasComp(holderUid))
                RemComp<ScpHolderComponent>(holderUid);
        }

        held.Comp.Holders.Clear();
        held.Comp.PrimaryHolder = null;

        if (applyImmunity)
        {
            var immuneCreated = !TryComp<ScpHoldImmuneComponent>(held.Owner, out var immune);
            immune ??= EnsureComp<ScpHoldImmuneComponent>(held.Owner);
            immune.ExpiresAt = _timing.CurTime + held.Comp.PostBreakoutImmunity;
            DirtyImmuneField((held.Owner, immune), nameof(ScpHoldImmuneComponent.ExpiresAt));

            if (immuneCreated)
                Dirty(held.Owner, immune);
        }

        foreach (var holderUid in _holderCooldownsToApply)
        {
            ApplyFullBreakoutHolderCooldown(holderUid);
        }

        RemComp<ScpHeldComponent>(held.Owner);
    }

    private void UpdateHolderSlowdowns(Entity<ScpHeldComponent> held)
    {
        foreach (var holderUid in held.Comp.Holders)
        {
            if (!_holderQuery.TryComp(holderUid, out var holder))
                continue;

            SetHolderSlowdown((holderUid, holder), true, held.Comp.WalkModifier, held.Comp.SprintModifier);
        }
    }

    private void SetHolderSlowdown(Entity<ScpHolderComponent> holder, bool enabled, float walkModifier, float sprintModifier)
    {
        if (holder.Comp.SlowdownEnabled == enabled &&
            MathHelper.CloseTo(holder.Comp.WalkModifier, walkModifier) &&
            MathHelper.CloseTo(holder.Comp.SprintModifier, sprintModifier))
        {
            return;
        }

        if (holder.Comp.SlowdownEnabled != enabled)
        {
            holder.Comp.SlowdownEnabled = enabled;
            DirtyHolderField(holder, nameof(ScpHolderComponent.SlowdownEnabled));
        }

        if (!MathHelper.CloseTo(holder.Comp.WalkModifier, walkModifier))
        {
            holder.Comp.WalkModifier = walkModifier;
            DirtyHolderField(holder, nameof(ScpHolderComponent.WalkModifier));
        }

        if (!MathHelper.CloseTo(holder.Comp.SprintModifier, sprintModifier))
        {
            holder.Comp.SprintModifier = sprintModifier;
            DirtyHolderField(holder, nameof(ScpHolderComponent.SprintModifier));
        }

        _movement.RefreshMovementSpeedModifiers(holder.Owner);
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

    private void CopyConfig(ScpHoldableComponent source, ScpHeldComponent target)
    {
        target.SoftEscapeCooldown = source.SoftEscapeCooldown;
        target.FullHoldDelay = source.FullHoldDelay;
        target.FullBreakoutDuration = source.FullBreakoutDuration;
        target.PostBreakoutImmunity = source.PostBreakoutImmunity;
        target.HoldRange = source.HoldRange;
        target.WalkModifier = source.HolderWalkModifier;
        target.SprintModifier = source.HolderSprintModifier;
        target.SoftEscapeAvailableAt = _timing.CurTime;
        target.FullHoldStartedAt = null;
    }

    private bool ShouldReleaseHolder(EntityUid holderUid, EntityUid heldUid, float maintenanceRange)
    {
        if (!_holdQuery.HasComp(holderUid))
            return true;

        if (!_holderQuery.TryComp(holderUid, out var holder))
            return true;

        if (holder.Target != heldUid)
            return true;

        if (!_container.IsInSameOrNoContainer(holderUid, heldUid))
            return true;

        return !_interaction.InRangeUnobstructed(holderUid, heldUid, maintenanceRange);
    }

    private bool IsValidPrimaryHolder(Entity<ScpHeldComponent> held, EntityUid primaryHolderUid)
    {
        if (!_holderQuery.TryComp(primaryHolderUid, out var holder))
            return false;

        if (holder.Target != held.Owner)
            return false;

        return held.Comp.Holders.Contains(primaryHolderUid);
    }

    private void SetHolderTarget(Entity<ScpHolderComponent> holder, EntityUid? target)
    {
        if (holder.Comp.Target == target)
            return;

        holder.Comp.Target = target;
        DirtyHolderField(holder, nameof(ScpHolderComponent.Target));
    }

    private void SetHeldPrimaryHolder(Entity<ScpHeldComponent> held, EntityUid? primaryHolder)
    {
        if (held.Comp.PrimaryHolder == primaryHolder)
            return;

        held.Comp.PrimaryHolder = primaryHolder;
        DirtyHeldField(held, nameof(ScpHeldComponent.PrimaryHolder));
    }
}
