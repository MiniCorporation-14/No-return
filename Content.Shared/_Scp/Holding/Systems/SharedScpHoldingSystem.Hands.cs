using Content.Shared._Scp.Holding.Components;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Components;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Throwing;

namespace Content.Shared._Scp.Holding.Systems;

public abstract partial class SharedScpHoldingSystem
{
    /*
     * Hand-local dependencies, caches, placeholders, virtual blockers, and held-status visuals.
     */

    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedVirtualItemSystem _virtualItem = default!;

    private readonly List<EntityUid> _placeholderIcons = [];
    private readonly List<Entity<VirtualItemComponent>> _virtualBlockersToDelete = [];

    private EntityQuery<HandsComponent> _handsQuery;
    private EntityQuery<VirtualItemComponent> _virtualItemQuery;
    private EntityQuery<ScpHeldHandBlockerComponent> _heldHandBlockerQuery;
    private EntityQuery<ScpHoldHandBlockerComponent> _holdHandBlockerQuery;
    private EntityQuery<UnremoveableComponent> _unremoveableQuery;

    private void InitializeHandQueries()
    {
        _handsQuery = GetEntityQuery<HandsComponent>();
        _virtualItemQuery = GetEntityQuery<VirtualItemComponent>();
        _heldHandBlockerQuery = GetEntityQuery<ScpHeldHandBlockerComponent>();
        _holdHandBlockerQuery = GetEntityQuery<ScpHoldHandBlockerComponent>();
        _unremoveableQuery = GetEntityQuery<UnremoveableComponent>();
    }

    private void InitializeHandEvents()
    {
        SubscribeLocalEvent<ActiveScpHolderComponent, BeforeThrowEvent>(OnHolderBeforeThrow);
        SubscribeLocalEvent<ActiveScpHolderComponent, DidEquipHandEvent>(OnHolderHandsModified);
        SubscribeLocalEvent<ActiveScpHolderComponent, VirtualItemDeletedEvent>(OnHolderVirtualItemDeleted);
        SubscribeLocalEvent<ScpHoldHandBlockerComponent, GettingDroppedAttemptEvent>(OnHolderBlockerGettingDropped);
    }

    protected void SyncPlaceholderHands(Entity<ActiveScpHoldableComponent> held)
    {
        if (!_handsQuery.TryComp(held, out var hands))
            return;

        if (!_activeHoldableFullHoldStateQuery.HasComp(held))
        {
            DeleteHeldHandBlockers(held);
            return;
        }

        CollectPlaceholderIconHolders(held);

        if (_placeholderIcons.Count == 0)
        {
            DeleteHeldHandBlockers(held);
            return;
        }

        var heldHands = (held, hands);
        DropHeldItemsForPlaceholders(heldHands);
        DeleteInvalidHeldHandBlockers(heldHands);
        EnsureHeldHandBlockers(heldHands);
    }

    private void CollectPlaceholderIconHolders(Entity<ActiveScpHoldableComponent> held)
    {
        _placeholderIcons.Clear();

        foreach (var holderUid in held.Comp.Holders)
        {
            if (!_activeHolderQuery.TryComp(holderUid, out var holder))
                continue;

            if (holder.Target != held)
                continue;

            _placeholderIcons.Add(holderUid);
        }
    }

    private void DropHeldItemsForPlaceholders(Entity<HandsComponent?> held)
    {
        foreach (var hand in _hands.EnumerateHands(held))
        {
            if (!_hands.TryGetHeldItem(held, hand, out var heldItem))
                continue;

            if (_unremoveableQuery.HasComp(heldItem.Value))
                continue;

            _hands.DoDrop(held, hand, doDropInteraction: true);
        }
    }

    private void DeleteInvalidHeldHandBlockers(Entity<HandsComponent?> held)
    {
        _virtualBlockersToDelete.Clear();

        foreach (var heldItem in _hands.EnumerateHeld(held))
        {
            if (!_heldHandBlockerQuery.HasComp(heldItem))
                continue;

            if (!_virtualItemQuery.TryComp(heldItem, out var virtualItem))
                continue;

            if (!IsValidHeldHandBlocker(virtualItem))
                _virtualBlockersToDelete.Add((heldItem, virtualItem));
        }

        foreach (var virtualItem in _virtualBlockersToDelete)
        {
            _virtualItem.DeleteVirtualItem(virtualItem, held);
        }
    }

    private bool IsValidHeldHandBlocker(VirtualItemComponent virtualItem)
    {
        return _placeholderIcons.Contains(virtualItem.BlockingEntity);
    }

    private void EnsureHeldHandBlockers(Entity<HandsComponent?> held)
    {
        var iconIndex = 0;
        while (_hands.TryGetEmptyHand(held, out var emptyHand))
        {
            var holderUid = _placeholderIcons[iconIndex % _placeholderIcons.Count];
            if (!_virtualItem.TrySpawnVirtualItem(holderUid, held, out var virtualItem))
                break;

            EnsureComp<UnremoveableComponent>(virtualItem.Value);
            EnsureComp<ScpHeldHandBlockerComponent>(virtualItem.Value);
            _hands.DoPickup(held, emptyHand, virtualItem.Value, held.Comp);

            iconIndex++;
        }
    }

    private void SyncHolderHandBlocker(Entity<ActiveScpHolderComponent> holder)
    {
        _virtualBlockersToDelete.Clear();
        var target = holder.Comp.Target;
        var holderActive = holder.Comp.LifeStage <= ComponentLifeStage.Running;
        var validBlockerCount = 0;
        var requiredHolderHandCount = 0;

        if (holderActive
            && _activeHoldableQuery.HasComp(target)
            && TryGetRequiredHolderHandCount(target.Value, out var resolvedRequiredHolderHandCount))
        {
            requiredHolderHandCount = resolvedRequiredHolderHandCount;
        }

        foreach (var heldItem in _hands.EnumerateHeld((EntityUid) holder))
        {
            if (!_virtualItemQuery.TryComp(heldItem, out var virtualItem))
                continue;

            var ownedBlocker = _holdHandBlockerQuery.HasComp(heldItem);
            var matchesCurrentTarget = holderActive
                                       && target != null
                                       && virtualItem.BlockingEntity == target.Value;

            if (ownedBlocker && matchesCurrentTarget)
            {
                if (validBlockerCount < requiredHolderHandCount)
                {
                    validBlockerCount++;
                    RemComp<UnremoveableComponent>(heldItem);
                    continue;
                }
            }

            if (ownedBlocker)
                _virtualBlockersToDelete.Add((heldItem, virtualItem));
        }

        foreach (var virtualItem in _virtualBlockersToDelete)
        {
            RemoveHolderHandBlocker(holder, virtualItem);
        }

        if (!holderActive || target == null)
            return;

        if (!_handsQuery.TryComp(holder, out var hands))
        {
            ReleaseHolderContribution(holder, target.Value, clearIfEmpty: true);
            return;
        }

        var holderHands = (holder, hands);

        while (validBlockerCount < requiredHolderHandCount)
        {
            if (!_hands.TryGetEmptyHand(holderHands, out var emptyHand))
                break;

            if (!_virtualItem.TrySpawnVirtualItem(target.Value, holder, out var spawnedVirtualItem))
                break;

            EnsureComp<ScpHoldHandBlockerComponent>(spawnedVirtualItem.Value);
            _hands.DoPickup(holder, emptyHand, spawnedVirtualItem.Value, hands);
            validBlockerCount++;
        }

        validBlockerCount = CountOwnedHolderHandBlockers(holder, target.Value);
        if (validBlockerCount < requiredHolderHandCount)
            ReleaseHolderContribution(holder, target.Value, clearIfEmpty: true);
    }

    private bool HasAvailableHolderHands(EntityUid holderUid, int requiredHandCount)
    {
        return _handsQuery.TryComp(holderUid, out var hands)
               && _hands.CountFreeHands((holderUid, hands)) >= requiredHandCount;
    }

    private int CountOwnedHolderHandBlockers(EntityUid holderUid, EntityUid targetUid)
    {
        var blockerCount = 0;
        foreach (var heldItem in _hands.EnumerateHeld(holderUid))
        {
            if (!_holdHandBlockerQuery.HasComp(heldItem))
                continue;

            if (!_virtualItemQuery.TryComp(heldItem, out var virtualItem))
                continue;

            if (virtualItem.BlockingEntity != targetUid)
                continue;

            blockerCount++;
        }

        return blockerCount;
    }

    private void RemoveHolderHandBlocker(EntityUid holderUid, Entity<VirtualItemComponent> virtualItem)
    {
        if (_handsQuery.TryComp(holderUid, out var hands) &&
            _hands.IsHolding((holderUid, hands), virtualItem, out var hand))
        {
            _hands.DoDrop((holderUid, hands), hand, doDropInteraction: false, log: false);
            return;
        }

        _virtualItem.DeleteVirtualItem(virtualItem, holderUid);
    }

    private void DeleteHolderHandBlockers(EntityUid holderUid)
    {
        _virtualBlockersToDelete.Clear();

        foreach (var heldItem in _hands.EnumerateHeld(holderUid))
        {
            if (!_holdHandBlockerQuery.HasComp(heldItem))
                continue;

            if (!_virtualItemQuery.TryComp(heldItem, out var virtualItem))
                continue;

            _virtualBlockersToDelete.Add((heldItem, virtualItem));
        }

        foreach (var virtualItem in _virtualBlockersToDelete)
        {
            RemoveHolderHandBlocker(holderUid, virtualItem);
        }
    }

    private void DeleteHeldHandBlockers(EntityUid heldUid)
    {
        _virtualBlockersToDelete.Clear();

        foreach (var heldItem in _hands.EnumerateHeld(heldUid))
        {
            if (_heldHandBlockerQuery.HasComp(heldItem) &&
                _virtualItemQuery.TryComp(heldItem, out var virtualItem))
            {
                _virtualBlockersToDelete.Add((heldItem, virtualItem));
            }
        }

        foreach (var virtualItem in _virtualBlockersToDelete)
        {
            _virtualItem.DeleteVirtualItem(virtualItem, heldUid);
        }
    }

    private void OnHolderBeforeThrow(Entity<ActiveScpHolderComponent> ent, ref BeforeThrowEvent args)
    {
        if (ent.Comp.Target == null)
            return;

        if (!HasComp<ScpHoldHandBlockerComponent>(args.ItemUid))
            return;

        if (!_virtualItemQuery.TryComp(args.ItemUid, out var virtualItem))
            return;

        if (virtualItem.BlockingEntity != ent.Comp.Target.Value)
            return;

        ReleaseHolderContribution(ent, ent.Comp.Target.Value, clearIfEmpty: true);
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

    private void OnHolderBlockerGettingDropped(Entity<ScpHoldHandBlockerComponent> ent, ref GettingDroppedAttemptEvent args)
    {
        if (!_virtualItemQuery.TryComp(ent, out var virtualItem))
            return;

        if (!TryComp<ScpHolderComponent>(args.User, out var holder))
            return;

        if (!TryReleaseHold((args.User, holder), virtualItem.BlockingEntity))
            return;

        args.Cancelled = true;
    }

    private void OnHolderVirtualItemDeleted(Entity<ActiveScpHolderComponent> ent, ref VirtualItemDeletedEvent args)
    {
        if (_timing.ApplyingState)
            return;

        if (ent.Comp.Target == null || ent.Comp.Target != args.BlockingEntity)
            return;

        if (!TryGetRequiredHolderHandCount(args.BlockingEntity, out var requiredHolderHandCount))
            return;

        if (CountOwnedHolderHandBlockers(ent, args.BlockingEntity) >= requiredHolderHandCount)
            return;

        SyncHolderState(ent);
    }
}
