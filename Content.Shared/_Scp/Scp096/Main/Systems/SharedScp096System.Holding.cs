using System.Numerics;
using Content.Shared._Scp.Holding;
using Content.Shared._Scp.Holding.Components;
using Content.Shared._Scp.Holding.Systems;
using Content.Shared._Scp.Scp096.Main.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Shared._Scp.Scp096.Main.Systems;

public abstract partial class SharedScp096System
{
    [Dependency] private readonly SharedScpHoldingSystem _holding = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private void InitializeHolding()
    {
        SubscribeLocalEvent<Scp096Component, ScpHoldAttemptEvent>(OnHoldAttempt);
        SubscribeLocalEvent<Scp096Component, ScpHoldBreakoutEvent>(OnHoldBreakout);
    }

    private void OnHoldAttempt(Entity<Scp096Component> ent, ref ScpHoldAttemptEvent args)
    {
        if (IsInHoldRestrictedState(ent.Owner))
            args.Cancelled = true;
    }

    private void OnHoldBreakout(Entity<Scp096Component> ent, ref ScpHoldBreakoutEvent args)
    {
        if (_timing.ApplyingState)
            return;

        if (!args.WasFullHold && !IsInHoldRestrictedState(ent.Owner))
            return;

        if (!TryComp<ActiveScpHoldableComponent>(ent.Owner, out var held))
            return;

        var scpPosition = _transform.GetWorldPosition(ent.Owner);
        foreach (var holderUid in held.Holders)
        {
            ApplyHoldBreakoutEffects(ent, holderUid, scpPosition);
        }
    }

    protected bool IsInHoldRestrictedState(EntityUid uid)
    {
        return HasComp<ActiveScp096HeatingUpComponent>(uid)
            || HasComp<ActiveScp096RageComponent>(uid)
            || HasComp<ActiveScp096WithoutFaceComponent>(uid);
    }

    protected void TryBreakOutOfHold(EntityUid uid)
    {
        _holding.TryForceBreakOut((uid, (ActiveScpHoldableComponent?) null));
    }

    private void ApplyHoldBreakoutEffects(Entity<Scp096Component> ent, EntityUid holderUid, Vector2 scpPosition)
    {
        _damageable.TryChangeDamage(holderUid, ent.Comp.HoldBreakoutDamage, origin: ent.Owner);
        _stun.TryUpdateParalyzeDuration(holderUid, ent.Comp.HoldBreakoutParalyzeTime);

        var direction = _transform.GetWorldPosition(holderUid) - scpPosition;
        direction = direction.LengthSquared() < 0.001f
            ? Vector2.UnitY
            : Vector2.Normalize(direction);

        _physics.ApplyLinearImpulse(holderUid, direction * ent.Comp.HoldBreakoutImpulse);
    }
}
