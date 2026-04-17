using Content.Shared._Scp.Holding.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Components;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.StatusEffectNew.Components;

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

        var heldHands = new Entity<HandsComponent>(held.Owner, hands).AsNullable();
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

            if (!TrySyncHeldHandBlocker((heldItem, blocker), virtualItem, held.Owner))
            {
                _virtualBlockersToDelete.Add((heldItem, virtualItem));
            }
        }

        foreach (var virtualItem in _virtualBlockersToDelete)
        {
            _virtualItem.DeleteVirtualItem(virtualItem, held.Owner);
        }
    }

    private bool TrySyncHeldHandBlocker(
        Entity<ScpHeldHandBlockerComponent> blocker,
        VirtualItemComponent virtualItem,
        EntityUid heldUid)
    {
        if (!_placeholderIcons.Contains(virtualItem.BlockingEntity))
            return false;

        var dirtyTarget = blocker.Comp.Target != heldUid;
        if (dirtyTarget)
        {
            blocker.Comp.Target = heldUid;
            DirtyField(blocker.Owner, blocker.Comp, nameof(ScpHeldHandBlockerComponent.Target));
        }

        var dirtyHolder = blocker.Comp.Holder != virtualItem.BlockingEntity;
        if (dirtyHolder)
        {
            blocker.Comp.Holder = virtualItem.BlockingEntity;
            DirtyField(blocker.Owner, blocker.Comp, nameof(ScpHeldHandBlockerComponent.Holder));
        }

        return true;
    }

    private void EnsureHeldHandBlockers(Entity<HandsComponent?> held)
    {
        var iconIndex = 0;
        while (_hands.TryGetEmptyHand(held, out var emptyHand))
        {
            var holderUid = _placeholderIcons[iconIndex % _placeholderIcons.Count];
            if (!_virtualItem.TrySpawnVirtualItemInHand(holderUid, held.Owner, out var virtualItem, empty: emptyHand, silent: true))
                break;

            EnsureComp<UnremoveableComponent>(virtualItem.Value);
            var blocker = EnsureComp<ScpHeldHandBlockerComponent>(virtualItem.Value);
            blocker.Target = held.Owner;
            blocker.Holder = holderUid;
            Dirty(virtualItem.Value, blocker);

            iconIndex++;
        }
    }

    private void SyncHeldStatusEffect(EntityUid target)
    {
        if (_statusEffects.HasStatusEffect(target, GrabbedStatusEffect) ||
            !_statusEffects.CanAddStatusEffect(target, GrabbedStatusEffect))
        {
            return;
        }

        EnsureComp<StatusEffectContainerComponent>(target);
        PredictedTrySpawnInContainer(GrabbedStatusEffect, target, StatusEffectContainerComponent.ContainerId, out _);
    }

    private void SyncHolderHandBlocker(Entity<ActiveScpHolderComponent> holder)
    {
        _virtualBlockersToDelete.Clear();
        EntityUid? validBlocker = null;
        var target = holder.Comp.Target;

        foreach (var heldItem in _hands.EnumerateHeld(holder.Owner))
        {
            if (!_virtualItemQuery.TryComp(heldItem, out var virtualItem))
                continue;

            var matchesCurrentTarget = holder.Comp.LifeStage <= ComponentLifeStage.Running &&
                target != null &&
                virtualItem.BlockingEntity == target.Value;

            if (matchesCurrentTarget)
            {
                if (validBlocker == null)
                {
                    validBlocker = heldItem;
                    RemComp<UnremoveableComponent>(heldItem);
                    var existingBlockerCreated = !_holdHandBlockerQuery.TryComp(heldItem, out var blocker);
                    blocker ??= EnsureComp<ScpHoldHandBlockerComponent>(heldItem);
                    var currentTarget = target!.Value;
                    if (blocker.Target != currentTarget)
                    {
                        blocker.Target = currentTarget;
                        DirtyField(heldItem, blocker, nameof(ScpHoldHandBlockerComponent.Target));
                    }

                    if (existingBlockerCreated)
                        Dirty(heldItem, blocker);

                    continue;
                }
            }

            if (_holdHandBlockerQuery.HasComp(heldItem) || matchesCurrentTarget)
                _virtualBlockersToDelete.Add((heldItem, virtualItem));
        }

        foreach (var virtualItem in _virtualBlockersToDelete)
        {
            _virtualItem.DeleteVirtualItem(virtualItem, holder.Owner);
        }

        if (holder.Comp.LifeStage > ComponentLifeStage.Running ||
            holder.Comp.Target == null ||
            validBlocker != null)
        {
            return;
        }

        if (!_handsQuery.TryComp(holder.Owner, out var hands) ||
            !_hands.TryGetEmptyHand((holder.Owner, hands), out _))
        {
            return;
        }

        if (!_virtualItem.TrySpawnVirtualItemInHand(holder.Comp.Target.Value, holder.Owner, out var spawnedVirtualItem, silent: true))
            return;

        _holdHandBlockerQuery.TryComp(spawnedVirtualItem.Value, out var blockerComp);
        blockerComp ??= EnsureComp<ScpHoldHandBlockerComponent>(spawnedVirtualItem.Value);
        blockerComp.Target = holder.Comp.Target.Value;
        Dirty(spawnedVirtualItem.Value, blockerComp);
    }

    private bool HasAvailableHolderHand(EntityUid holderUid)
    {
        return _handsQuery.TryComp(holderUid, out var hands) &&
               _hands.TryGetEmptyHand((holderUid, hands), out _);
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
            _virtualItem.DeleteVirtualItem(virtualItem, holderUid);
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
}
