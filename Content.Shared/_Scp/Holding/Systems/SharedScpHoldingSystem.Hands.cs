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

    private void SyncPlaceholderHands(Entity<ActiveScpHoldableComponent> held)
    {
        if (!_handsQuery.TryComp(held.Owner, out var hands))
            return;

        if (!_activeHoldableFullHoldStateQuery.HasComp(held.Owner))
        {
            DeleteHeldHandBlockers(held.Owner);
            return;
        }

        CollectPlaceholderIconHolders(held);

        if (_placeholderIcons.Count == 0)
        {
            DeleteHeldHandBlockers(held.Owner);
            return;
        }

        var heldHands = (held.Owner, hands);
        DropHeldItemsForPlaceholders(heldHands);
        DeleteInvalidHeldHandBlockers(heldHands);
        EnsureHeldHandBlockers(heldHands);
    }

    private void CollectPlaceholderIconHolders(Entity<ActiveScpHoldableComponent> held)
    {
        _placeholderIcons.Clear();

        foreach (var holderUid in held.Comp.Holders)
        {
            if (_activeHolderQuery.TryComp(holderUid, out var holder) &&
                holder.Target == held.Owner)
            {
                _placeholderIcons.Add(holderUid);
            }
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
            if (!_virtualItemQuery.TryComp(heldItem, out var virtualItem))
                continue;

            if (!_heldHandBlockerQuery.TryComp(heldItem, out var blocker))
                continue;

            if (!IsValidHeldHandBlocker(virtualItem))
            {
                _virtualBlockersToDelete.Add((heldItem, virtualItem));
            }
        }

        foreach (var virtualItem in _virtualBlockersToDelete)
        {
            _virtualItem.DeleteVirtualItem(virtualItem, held.Owner);
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
            if (!_virtualItem.TrySpawnVirtualItem(holderUid, held.Owner, out var virtualItem))
                break;

            EnsureComp<UnremoveableComponent>(virtualItem.Value);
            EnsureComp<ScpHeldHandBlockerComponent>(virtualItem.Value);
            _hands.DoPickup(held.Owner, emptyHand, virtualItem.Value, held.Comp);

            iconIndex++;
        }
    }

    private void SyncHolderHandBlocker(Entity<ActiveScpHolderComponent> holder)
    {
        _virtualBlockersToDelete.Clear();
        EntityUid? validBlocker = null;
        var target = holder.Comp.Target;
        var holderActive = holder.Comp.LifeStage <= ComponentLifeStage.Running;

        foreach (var heldItem in _hands.EnumerateHeld(holder.Owner))
        {
            if (!_virtualItemQuery.TryComp(heldItem, out var virtualItem))
                continue;

            var ownedBlocker = _holdHandBlockerQuery.HasComp(heldItem);
            var matchesCurrentTarget = holderActive &&
                target != null &&
                virtualItem.BlockingEntity == target.Value;

            if (ownedBlocker && matchesCurrentTarget)
            {
                if (validBlocker == null)
                {
                    validBlocker = heldItem;
                    RemComp<UnremoveableComponent>(heldItem);
                    continue;
                }
            }

            if (ownedBlocker)
                _virtualBlockersToDelete.Add((heldItem, virtualItem));
        }

        foreach (var virtualItem in _virtualBlockersToDelete)
        {
            RemoveHolderHandBlocker(holder.Owner, virtualItem);
        }

        if (!holderActive ||
            target == null ||
            validBlocker != null)
        {
            return;
        }

        if (!_handsQuery.TryComp(holder.Owner, out var hands) ||
            !_hands.TryGetEmptyHand((holder.Owner, hands), out var emptyHand))
        {
            return;
        }

        if (!_virtualItem.TrySpawnVirtualItem(target.Value, holder.Owner, out var spawnedVirtualItem))
            return;

        EnsureComp<ScpHoldHandBlockerComponent>(spawnedVirtualItem.Value);
        _hands.DoPickup(holder.Owner, emptyHand, spawnedVirtualItem.Value, hands);
    }

    private bool HasAvailableHolderHand(EntityUid holderUid)
    {
        return _handsQuery.TryComp(holderUid, out var hands) &&
               _hands.TryGetEmptyHand((holderUid, hands), out _);
    }

    private bool HasOwnedHolderHandBlocker(EntityUid holderUid, EntityUid targetUid)
    {
        foreach (var heldItem in _hands.EnumerateHeld(holderUid))
        {
            if (!_holdHandBlockerQuery.HasComp(heldItem) ||
                !_virtualItemQuery.TryComp(heldItem, out var virtualItem) ||
                virtualItem.BlockingEntity != targetUid)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private void RemoveHolderHandBlocker(EntityUid holderUid, Entity<VirtualItemComponent> virtualItem)
    {
        if (_handsQuery.TryComp(holderUid, out var hands) &&
            _hands.IsHolding((holderUid, hands), virtualItem.Owner, out var hand))
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
            if (_holdHandBlockerQuery.HasComp(heldItem) &&
                _virtualItemQuery.TryComp(heldItem, out var virtualItem))
            {
                _virtualBlockersToDelete.Add((heldItem, virtualItem));
            }
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

        if (!TryComp<ScpHoldHandBlockerComponent>(args.ItemUid, out _))
            return;

        if (!_virtualItemQuery.TryComp(args.ItemUid, out var virtualItem))
            return;

        if (virtualItem.BlockingEntity != ent.Comp.Target.Value)
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

    private void OnHolderBlockerGettingDropped(Entity<ScpHoldHandBlockerComponent> ent, ref GettingDroppedAttemptEvent args)
    {
        if (!_virtualItemQuery.TryComp(ent.Owner, out var virtualItem) ||
            !TryComp<ScpHolderComponent>(args.User, out var holder) ||
            !TryReleaseHold((args.User, holder), virtualItem.BlockingEntity))
        {
            return;
        }

        args.Cancelled = true;
    }

    private void OnHolderVirtualItemDeleted(Entity<ActiveScpHolderComponent> ent, ref VirtualItemDeletedEvent args)
    {
        if (_timing.ApplyingState ||
            ent.Comp.Target == null ||
            ent.Comp.Target != args.BlockingEntity)
        {
            return;
        }

        if (HasOwnedHolderHandBlocker(ent.Owner, args.BlockingEntity))
            return;

        ReleaseHolderContribution(ent.Owner, args.BlockingEntity, clearIfEmpty: true);
    }
}
