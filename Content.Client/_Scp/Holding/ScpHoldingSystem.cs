using Content.Client.Hands.Systems;
using Content.Client.Inventory;
using Content.Shared._Scp.Holding.Components;
using Content.Shared._Scp.Holding.Systems;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Inventory.VirtualItem;
using Robust.Client.Physics;
using Robust.Client.Player;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.Client._Scp.Holding;

public sealed partial class ScpHoldingSystem : SharedScpHoldingSystem
{
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly Robust.Client.Physics.PhysicsSystem _physics = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly VirtualItemSystem _virtualItem = default!;

    private EntityUid? _trackedHolderTarget;

    private EntityQuery<HandsComponent> _handsQuery;
    private EntityQuery<ScpHoldHandBlockerComponent> _blockerQuery;
    private EntityQuery<ActiveScpHolderComponent> _activeHolderQuery;
    private EntityQuery<VirtualItemComponent> _virtualItemQuery;

    public override void Initialize()
    {
        base.Initialize();

        _handsQuery = GetEntityQuery<HandsComponent>();
        _blockerQuery = GetEntityQuery<ScpHoldHandBlockerComponent>();
        _activeHolderQuery = GetEntityQuery<ActiveScpHolderComponent>();
        _virtualItemQuery = GetEntityQuery<VirtualItemComponent>();

        SubscribeLocalEvent<ActiveScpHoldableComponent, AfterAutoHandleStateEvent>(OnHeldAfterState);
        SubscribeLocalEvent<ActiveScpHolderComponent, AfterAutoHandleStateEvent>(OnHolderAfterState);
        SubscribeLocalEvent<ScpHoldHandBlockerComponent, ComponentStartup>(OnBlockerStartup);
        SubscribeLocalEvent<ScpHoldHandBlockerComponent, GotEquippedHandEvent>(OnBlockerEquipped);
        SubscribeLocalEvent<ActiveScpHoldableComponent, UpdateIsPredictedEvent>(OnUpdateHeldPredicted);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_player.LocalEntity is not { Valid: true } local)
        {
            UpdateTrackedLocalHeldTarget(null);
            return;
        }

        if (!_activeHolderQuery.TryComp(local, out var localHolder))
        {
            UpdateTrackedLocalHeldTarget(null);
            return;
        }

        ReconcileLocalHolderState((local, localHolder));
    }

    private void OnHeldAfterState(Entity<ActiveScpHoldableComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        ReconcileHeldAfterState(ent);
    }

    private void OnHolderAfterState(Entity<ActiveScpHolderComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (_player.LocalEntity != ent.Owner)
            return;

        ReconcileLocalHolderState(ent);
    }

    private void OnBlockerStartup(Entity<ScpHoldHandBlockerComponent> ent, ref ComponentStartup args)
    {
        if (!_timing.ApplyingState)
            return;

        ReconcileLocalHolderBlocker(ent.Owner);
    }

    private void OnBlockerEquipped(Entity<ScpHoldHandBlockerComponent> ent, ref GotEquippedHandEvent args)
    {
        if (!_timing.ApplyingState)
            return;

        ReconcileLocalHolderBlocker(ent.Owner, args.User);
    }

    private void OnUpdateHeldPredicted(Entity<ActiveScpHoldableComponent> ent, ref UpdateIsPredictedEvent args)
    {
        if (_player.LocalEntity is not { Valid: true } local)
            return;

        if (ent.Owner == local)
        {
            args.IsPredicted = true;
            return;
        }

        if (_activeHolderQuery.TryComp(local, out var localHolder))
        {
            if (localHolder.Target == ent.Owner)
            {
                args.IsPredicted = true;
                return;
            }
        }

        foreach (var holder in ent.Comp.Holders)
        {
            if (holder != local)
                continue;

            args.IsPredicted = true;
            return;
        }

        if (ent.Comp.Holders.Count > 0)
            args.BlockPrediction = true;
    }

    private void ReconcileHeldAfterState(Entity<ActiveScpHoldableComponent> held)
    {
        _physics.UpdateIsPredicted(held);

        if (HasComp<ActiveStateScpHoldableFullHoldComponent>(held.Owner))
            SyncPlaceholderHands(held);
    }

    protected override void UpdateHeldStates()
    {
        var query = EntityQueryEnumerator<ActiveScpHoldableComponent, PhysicsComponent>();
        while (query.MoveNext(out var uid, out var held, out var physics))
        {
            if (!physics.Predict)
                continue;

            _physics.UpdateIsPredicted(uid);
            UpdateHeld((uid, held));
        }
    }

    protected override void OnHeldStateShutdown(Entity<ActiveScpHoldableComponent> held)
    {
        _physics.UpdateIsPredicted(held);
    }

    private void ReconcileLocalHolderBlocker(EntityUid blocker, EntityUid? holderUid = null)
    {
        holderUid ??= _player.LocalEntity;

        if (holderUid is not { Valid: true } holder)
            return;

        if (!_activeHolderQuery.TryComp(holder, out var activeHolder) ||
            activeHolder.Target == null)
        {
            return;
        }

        if (!_virtualItemQuery.TryComp(blocker, out var virtualItem))
            return;

        if (virtualItem.BlockingEntity != activeHolder.Target.Value)
            return;

        if (!_handsQuery.TryComp(holder, out var hands) ||
            !_hands.IsHolding((holder, hands), blocker))
        {
            return;
        }

        ReconcileLocalHolderState((holder, activeHolder));
    }

    private void ReconcileLocalHolderState(Entity<ActiveScpHolderComponent> holder)
    {
        UpdateTrackedLocalHeldTarget(holder, holder.Comp.Target);
        ReconcileLocalHolderBlockerSteadyState(holder);
    }

    private void ReconcileLocalHolderBlockerSteadyState(Entity<ActiveScpHolderComponent> holder)
    {
        if (holder.Comp.Target == null ||
            !_handsQuery.TryComp(holder.Owner, out var hands))
        {
            return;
        }

        var target = holder.Comp.Target.Value;
        EntityUid? authoritativeBlocker = null;
        EntityUid? predictedBlocker = null;

        foreach (var heldItem in _hands.EnumerateHeld((holder.Owner, hands)))
        {
            if (!_virtualItemQuery.TryComp(heldItem, out var virtualItem) ||
                virtualItem.BlockingEntity != target)
            {
                continue;
            }

            if (!IsClientSide(heldItem))
            {
                authoritativeBlocker ??= heldItem;
                continue;
            }

            if (!_blockerQuery.HasComp(heldItem))
                continue;

            if (predictedBlocker == null)
            {
                predictedBlocker = heldItem;
                continue;
            }

            QueueDel(heldItem);
        }

        if (authoritativeBlocker != null)
        {
            if (predictedBlocker != null)
                QueueDel(predictedBlocker.Value);
            return;
        }

        if (_timing.ApplyingState ||
            predictedBlocker != null ||
            !_hands.TryGetEmptyHand((holder.Owner, hands), out var emptyHand) ||
            !_virtualItem.TrySpawnVirtualItem(target, holder.Owner, out var spawnedVirtualItem))
        {
            return;
        }

        EnsureComp<ScpHoldHandBlockerComponent>(spawnedVirtualItem.Value);
        _hands.DoPickup(holder.Owner, emptyHand, spawnedVirtualItem.Value, hands);
    }

    private void UpdateTrackedLocalHeldTarget(EntityUid? currentTarget, EntityUid? previousTarget = null)
    {
        if (_trackedHolderTarget == currentTarget)
            return;

        previousTarget ??= _trackedHolderTarget;

        if (previousTarget != null)
            _physics.UpdateIsPredicted(previousTarget.Value);

        _trackedHolderTarget = currentTarget;

        if (_trackedHolderTarget != null)
            _physics.UpdateIsPredicted(_trackedHolderTarget.Value);
    }

    private void UpdateTrackedLocalHeldTarget(EntityUid holderUid, EntityUid? currentTarget, EntityUid? previousTarget = null)
    {
        if (_player.LocalEntity != holderUid)
            return;

        UpdateTrackedLocalHeldTarget(currentTarget, previousTarget);
    }
}
