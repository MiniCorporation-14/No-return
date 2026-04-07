using System;
using Content.Shared._Scp.Holding;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory.VirtualItem;
using Robust.Client.Physics;
using Robust.Client.Player;
using Robust.Shared.Timing;

namespace Content.Client._Scp.Holding;

public sealed class ScpHoldingPredictionSystem : EntitySystem
{
    [Dependency] private readonly SharedScpHoldingSystem _holding = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly Robust.Client.Physics.PhysicsSystem _physics = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedVirtualItemSystem _virtualItem = default!;

    private static readonly TimeSpan BlockerRespawnSuppressionDuration = TimeSpan.FromSeconds(0.5);

    private EntityUid? _suppressedHolder;
    private EntityUid? _suppressedTarget;
    private TimeSpan _suppressedUntil;

    private EntityQuery<HandsComponent> _handsQuery;
    private EntityQuery<ScpHolderComponent> _holderQuery;

    public override void Initialize()
    {
        base.Initialize();

        _handsQuery = GetEntityQuery<HandsComponent>();
        _holderQuery = GetEntityQuery<ScpHolderComponent>();

        SubscribeLocalEvent<ScpHeldComponent, AfterAutoHandleStateEvent>(OnHeldAfterState);
        SubscribeLocalEvent<ScpHoldHandBlockerComponent, GotUnequippedHandEvent>(OnBlockerUnequipped);
        SubscribeLocalEvent<ScpHolderComponent, AfterAutoHandleStateEvent>(OnHolderAfterState);
        SubscribeLocalEvent<ScpHeldComponent, UpdateIsPredictedEvent>(OnUpdateHeldPredicted);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime >= _suppressedUntil)
            ClearBlockerRespawnSuppression();

        var query = EntityQueryEnumerator<ScpHeldComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            _physics.UpdateIsPredicted(uid);
        }

        if (_player.LocalEntity is not { Valid: true } local)
        {
            ClearBlockerRespawnSuppression();
            return;
        }

        if (ShouldSuppressBlockerRespawn(local, _suppressedTarget))
            DeleteSuppressedBlockers(local, _suppressedTarget!.Value);

        if (!_holderQuery.TryComp(local, out var localHolder))
            return;

        if (ShouldSuppressBlockerRespawn(local, localHolder.Target))
        {
            DeleteSuppressedBlockers(local, localHolder.Target!.Value);
            return;
        }

        _holding.RefreshHolderState((local, localHolder));
    }

    private void OnHeldAfterState(Entity<ScpHeldComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        _holding.RefreshHeldState(ent);
    }

    private void OnHolderAfterState(Entity<ScpHolderComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (ShouldSuppressBlockerRespawn(ent.Owner, ent.Comp.Target))
        {
            DeleteSuppressedBlockers(ent.Owner, ent.Comp.Target!.Value);
            return;
        }

        _holding.RefreshHolderState(ent);
    }

    private void OnBlockerUnequipped(Entity<ScpHoldHandBlockerComponent> ent, ref GotUnequippedHandEvent args)
    {
        if (_player.LocalEntity != args.User)
        {
            return;
        }

        if (_holderQuery.TryComp(args.User, out var holder) &&
            holder.Target == ent.Comp.Target)
        {
            return;
        }

        SuppressBlockerRespawn(args.User, ent.Comp.Target);
    }

    private void OnUpdateHeldPredicted(Entity<ScpHeldComponent> ent, ref UpdateIsPredictedEvent args)
    {
        if (_player.LocalEntity is not { Valid: true } local)
            return;

        if (ent.Owner == local)
        {
            args.IsPredicted = true;
            return;
        }

        if (_holderQuery.TryComp(local, out var localHolder) && localHolder.Target == ent.Owner)
        {
            args.IsPredicted = true;
            return;
        }

        for (var i = 0; i < ent.Comp.Holders.Count; i++)
        {
            if (ent.Comp.Holders[i] != local)
                continue;

            args.IsPredicted = true;
            return;
        }

        if (ent.Comp.Holders.Count > 0)
            args.BlockPrediction = true;
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

    private void DeleteSuppressedBlockers(EntityUid holder, EntityUid target)
    {
        if (!_handsQuery.TryComp(holder, out var hands))
            return;

        foreach (var heldItem in _hands.EnumerateHeld((holder, hands)))
        {
            if (!TryComp<VirtualItemComponent>(heldItem, out var virtualItem) ||
                !TryComp<ScpHoldHandBlockerComponent>(heldItem, out var blocker) ||
                virtualItem.BlockingEntity != target ||
                blocker.Target != target)
            {
                continue;
            }

            _virtualItem.DeleteVirtualItem((heldItem, virtualItem), holder);
        }
    }
}
