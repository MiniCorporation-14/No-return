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

public sealed class ScpHoldingSystem : SharedScpHoldingSystem
{
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly Robust.Client.Physics.PhysicsSystem _physics = default!;
    [Dependency] private readonly VirtualItemSystem _virtualItem = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private static readonly TimeSpan BlockerRespawnSuppressionDuration = TimeSpan.FromSeconds(0.5f);

    private EntityUid? _suppressedHolder;
    private EntityUid? _suppressedTarget;
    private EntityUid? _trackedHolderTarget;
    private TimeSpan _suppressedUntil;

    private EntityQuery<HandsComponent> _handsQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<ScpHoldHandBlockerComponent> _blockerQuery;
    private EntityQuery<ActiveScpHolderComponent> _activeHolderQuery;
    private EntityQuery<VirtualItemComponent> _virtualItemQuery;

    public override void Initialize()
    {
        base.Initialize();

        _handsQuery = GetEntityQuery<HandsComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _blockerQuery = GetEntityQuery<ScpHoldHandBlockerComponent>();
        _activeHolderQuery = GetEntityQuery<ActiveScpHolderComponent>();
        _virtualItemQuery = GetEntityQuery<VirtualItemComponent>();

        SubscribeLocalEvent<ActiveScpHoldableComponent, AfterAutoHandleStateEvent>(OnHeldAfterState);
        SubscribeLocalEvent<ScpHoldHandBlockerComponent, GotUnequippedHandEvent>(OnBlockerUnequipped);
        SubscribeLocalEvent<ActiveScpHoldableComponent, UpdateIsPredictedEvent>(OnUpdateHeldPredicted);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime >= _suppressedUntil)
            ClearBlockerRespawnSuppression();

        if (_player.LocalEntity is not { Valid: true } local)
        {
            UpdateTrackedLocalHeldTarget(null);
            ClearBlockerRespawnSuppression();
            return;
        }

        if (ShouldSuppressBlockerRespawn(local, _suppressedTarget))
            DeleteSuppressedBlockers(local, _suppressedTarget!.Value);

        if (!_activeHolderQuery.TryComp(local, out var localHolder))
        {
            UpdateTrackedLocalHeldTarget(null);
            return;
        }

        if (ShouldSuppressBlockerRespawn(local, localHolder.Target))
        {
            DeleteSuppressedBlockers(local, localHolder.Target!.Value);
            UpdateTrackedLocalHeldTarget(localHolder.Target);
            return;
        }

        SyncHolderState((local, localHolder));
    }

    private void OnHeldAfterState(Entity<ActiveScpHoldableComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        ReconcileHeldAfterState(ent);
    }

    private void OnBlockerUnequipped(Entity<ScpHoldHandBlockerComponent> ent, ref GotUnequippedHandEvent args)
    {
        if (_player.LocalEntity != args.User)
            return;

        if (_activeHolderQuery.TryComp(args.User, out var holder))
        {
            if (holder.Target == ent.Comp.Target)
                return;
        }

        SuppressBlockerRespawn(args.User, ent.Comp.Target);
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

    protected override bool ShouldUsePredictedBreakoutFeedback => true;

    protected override bool ShouldUpdateHeld(EntityUid uid, ActiveScpHoldableComponent held)
    {
        return _physicsQuery.TryComp(uid, out var physics) && physics.Predict;
    }

    protected override bool CanShowBreakoutAttemptFeedback()
    {
        return _timing.IsFirstTimePredicted;
    }

    protected override void OnHeldStateRefreshed(Entity<ActiveScpHoldableComponent> held)
    {
        _physics.UpdateIsPredicted(held);
    }

    protected override void OnHeldStateShutdown(Entity<ActiveScpHoldableComponent> held)
    {
        _physics.UpdateIsPredicted(held);
    }

    protected override void OnHolderStateRefreshed(Entity<ActiveScpHolderComponent> holder)
    {
        UpdateTrackedLocalHeldTarget(holder, holder.Comp.Target);
    }

    protected override void OnHolderStateShutdown(EntityUid holderUid, EntityUid? target)
    {
        UpdateTrackedLocalHeldTarget(holderUid, null, target);
    }

    private void SuppressBlockerRespawn(EntityUid holder, EntityUid target)
    {
        _suppressedHolder = holder;
        _suppressedTarget = target;
        _suppressedUntil = _timing.CurTime + BlockerRespawnSuppressionDuration;
    }

    private void ClearBlockerRespawnSuppression()
    {
        _suppressedHolder = null;
        _suppressedTarget = null;
        _suppressedUntil = TimeSpan.Zero;
    }

    private bool ShouldSuppressBlockerRespawn(EntityUid holder, EntityUid? target)
    {
        return _suppressedHolder == holder &&
               _suppressedTarget != null &&
               target == _suppressedTarget &&
               _timing.CurTime < _suppressedUntil;
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

    private void DeleteSuppressedBlockers(EntityUid holder, EntityUid target)
    {
        if (!_handsQuery.TryComp(holder, out var hands))
            return;

        foreach (var heldItem in _hands.EnumerateHeld((holder, hands)))
        {
            if (!_virtualItemQuery.TryComp(heldItem, out var virtualItem))
                continue;

            if (!_blockerQuery.TryComp(heldItem, out var blocker))
                continue;

            if (virtualItem.BlockingEntity != target)
                continue;

            if (blocker.Target != target)
                continue;

            _virtualItem.DeleteVirtualItem((heldItem, virtualItem), holder);
        }
    }
}
