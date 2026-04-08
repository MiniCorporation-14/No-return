using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Components;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.StatusEffectNew.Components;

namespace Content.Shared._Scp.Holding;

public sealed partial class SharedScpHoldingSystem
{
    /*
     * Hand-local dependencies, caches, placeholders, virtual blockers, and held-status visuals.
     */
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedVirtualItemSystem _virtualItem = default!;

    private readonly List<EntityUid> _placeholderIcons = [];
    private readonly List<Entity<VirtualItemComponent>> _virtualBlockersToDelete = [];

    private EntityQuery<HandsComponent> _handsQuery;

    private void InitializeHandQueries()
    {
        _handsQuery = GetEntityQuery<HandsComponent>();
    }

    private void SyncPlaceholderHands(Entity<ScpHeldComponent> held)
    {
        DeleteHeldHandBlockers(held.Owner);

        if (!held.Comp.FullHold || !_handsQuery.TryComp(held.Owner, out var hands))
            return;

        foreach (var hand in _hands.EnumerateHands((held.Owner, hands)))
        {
            if (!_hands.TryGetHeldItem((held.Owner, hands), hand, out var heldItem))
                continue;

            if (HasComp<UnremoveableComponent>(heldItem.Value))
                continue;

            _hands.DoDrop((held.Owner, hands), hand, doDropInteraction: true);
        }

        _placeholderIcons.Clear();
        foreach (var holderUid in held.Comp.Holders)
        {
            if (_holderQuery.TryComp(holderUid, out var holder) && holder.Target == held.Owner)
                _placeholderIcons.Add(holderUid);
        }

        if (_placeholderIcons.Count == 0)
            return;

        var iconIndex = 0;
        while (_hands.TryGetEmptyHand((held.Owner, hands), out var emptyHand))
        {
            var holderUid = _placeholderIcons[iconIndex % _placeholderIcons.Count];
            if (!_virtualItem.TrySpawnVirtualItemInHand(holderUid, held.Owner, out var virtualItem, empty: emptyHand, silent: true))
                break;

            EnsureComp<UnremoveableComponent>(virtualItem.Value);
            var blocker = EnsureComp<ScpHeldHandBlockerComponent>(virtualItem.Value);

            if (blocker.Target != held.Owner || blocker.Holder != holderUid)
            {
                blocker.Target = held.Owner;
                blocker.Holder = holderUid;
                Dirty(virtualItem.Value, blocker);
            }

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

    private void SyncHolderHandBlocker(Entity<ScpHolderComponent> holder)
    {
        _virtualBlockersToDelete.Clear();
        EntityUid? validBlocker = null;
        var target = holder.Comp.Target;

        foreach (var heldItem in _hands.EnumerateHeld(holder.Owner))
        {
            if (!TryComp<VirtualItemComponent>(heldItem, out var virtualItem))
            {
                continue;
            }

            var matchesCurrentTarget = holder.Comp.LifeStage <= ComponentLifeStage.Running &&
                target != null &&
                virtualItem.BlockingEntity == target.Value;

            if (matchesCurrentTarget)
            {
                if (validBlocker == null)
                {
                    validBlocker = heldItem;
                    RemComp<UnremoveableComponent>(heldItem);
                    var blocker = EnsureComp<ScpHoldHandBlockerComponent>(heldItem);
                    var currentTarget = target!.Value;
                    if (blocker.Target != currentTarget)
                    {
                        blocker.Target = currentTarget;
                        Dirty(heldItem, blocker);
                    }
                    continue;
                }
            }

            if (TryComp<ScpHoldHandBlockerComponent>(heldItem, out _) || matchesCurrentTarget)
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

        var blockerComp = EnsureComp<ScpHoldHandBlockerComponent>(spawnedVirtualItem.Value);
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
            if (TryComp<ScpHoldHandBlockerComponent>(heldItem, out _) &&
                TryComp<VirtualItemComponent>(heldItem, out var virtualItem))
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
            if (TryComp<ScpHeldHandBlockerComponent>(heldItem, out _) &&
                TryComp<VirtualItemComponent>(heldItem, out var virtualItem))
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
