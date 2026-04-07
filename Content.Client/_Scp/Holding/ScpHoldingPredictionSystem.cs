using Content.Shared._Scp.Holding;
using Robust.Client.Physics;
using Robust.Client.Player;

namespace Content.Client._Scp.Holding;

public sealed class ScpHoldingPredictionSystem : EntitySystem
{
    [Dependency] private readonly SharedScpHoldingSystem _holding = default!;
    [Dependency] private readonly Robust.Client.Physics.PhysicsSystem _physics = default!;
    [Dependency] private readonly IPlayerManager _player = default!;

    private EntityQuery<ScpHolderComponent> _holderQuery;

    public override void Initialize()
    {
        base.Initialize();

        _holderQuery = GetEntityQuery<ScpHolderComponent>();

        SubscribeLocalEvent<ScpHeldComponent, AfterAutoHandleStateEvent>(OnHeldAfterState);
        SubscribeLocalEvent<ScpHolderComponent, AfterAutoHandleStateEvent>(OnHolderAfterState);
        SubscribeLocalEvent<ScpHeldComponent, UpdateIsPredictedEvent>(OnUpdateHeldPredicted);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<ScpHeldComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            _physics.UpdateIsPredicted(uid);
        }

        if (_player.LocalEntity is not { Valid: true } local ||
            !_holderQuery.TryComp(local, out var localHolder))
        {
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
        _holding.RefreshHolderState(ent);
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
}
