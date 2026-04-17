using Content.Shared.Hands;
using Content.Shared._Scp.Holding.Components;
using Content.Shared._Scp.Holding.Systems;

namespace Content.Server._Scp.Holding;

public sealed class ScpHoldingSystem : SharedScpHoldingSystem
{
    protected override bool ShouldShowHoldPopups => true;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ScpHoldComponent, ComponentShutdown>(OnHoldShutdown);
        SubscribeLocalEvent<ScpHeldComponent, HandCountChangedEvent>(OnHandCountChanged);
    }

    protected override void OnHeldStateShutdown(Entity<ScpHeldComponent> held)
    {
        foreach (var holderUid in held.Comp.Holders)
        {
            if (TryComp<ScpHolderComponent>(holderUid, out _))
                RemComp<ScpHolderComponent>(holderUid);
        }
    }

    private void OnHoldShutdown(Entity<ScpHoldComponent> ent, ref ComponentShutdown args)
    {
        if (!TryComp<ScpHolderComponent>(ent.Owner, out var holder))
            return;

        if (holder.Target == null)
            return;

        ReleaseHolderContribution(ent.Owner, holder.Target.Value, clearIfEmpty: true);
    }

    private void OnHandCountChanged(Entity<ScpHeldComponent> ent, ref HandCountChangedEvent args)
    {
        SyncHeldState(ent);
    }
}
