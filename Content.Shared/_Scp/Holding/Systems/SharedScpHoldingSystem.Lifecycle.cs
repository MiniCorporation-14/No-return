using Content.Shared._Scp.Holding.Components;
using Content.Shared.Alert;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Robust.Shared.Prototypes;

namespace Content.Shared._Scp.Holding.Systems;

public abstract partial class SharedScpHoldingSystem
{
    /*
     * Held/holder lifecycle wiring and state refresh reactions.
     */

    private static readonly ProtoId<AlertPrototype> HeldAlert = "ScpHoldGrabbed";

    private void InitializeLifecycleEvents()
    {
        SubscribeLocalEvent<ActiveScpHoldableComponent, ComponentStartup>(OnHeldStartup);
        SubscribeLocalEvent<ActiveScpHoldableComponent, ComponentShutdown>(OnHeldShutdown);
        SubscribeLocalEvent<ActiveScpHoldableComponent, ComponentRemove>(OnHeldRemove);
        SubscribeLocalEvent<ActiveStateScpHoldableFullHoldComponent, ComponentStartup>(OnFullHeldStartup);
        SubscribeLocalEvent<ActiveStateScpHoldableFullHoldComponent, ComponentRemove>(OnFullHeldRemove);
        SubscribeLocalEvent<ActiveStateScpHoldableFullHoldComponent, UpdateCanMoveEvent>(OnFullHeldUpdateCanMove);
        SubscribeLocalEvent<ActiveScpHolderComponent, ComponentStartup>(OnHolderStartup);
        SubscribeLocalEvent<ActiveScpHolderComponent, ComponentShutdown>(OnHolderShutdown);
        SubscribeLocalEvent<ActiveStateScpHolderSlowdownComponent, ComponentRemove>(OnHolderSlowdownRemove);
        SubscribeLocalEvent<ActiveStateScpHolderSlowdownComponent, AfterAutoHandleStateEvent>(OnHolderSlowdownAfterState);
        SubscribeLocalEvent<ActiveStateScpHolderSlowdownComponent, RefreshMovementSpeedModifiersEvent>(OnHolderSlowdownRefreshMoveSpeed);
    }

    private void OnHeldStartup(Entity<ActiveScpHoldableComponent> ent, ref ComponentStartup args)
    {
        _alerts.ShowAlert(ent.Owner, HeldAlert);
        _statusEffects.TrySetStatusEffectDuration(ent, GrabbedStatusEffect);
        ValidateAllActions(ent.Owner);
    }

    private void OnHeldShutdown(Entity<ActiveScpHoldableComponent> ent, ref ComponentShutdown args)
    {
        _alerts.ClearAlert(ent.Owner, HeldAlert);
        _statusEffects.TryRemoveStatusEffect(ent, GrabbedStatusEffect);
        OnHeldStateShutdown(ent);
    }

    private void OnHeldRemove(Entity<ActiveScpHoldableComponent> ent, ref ComponentRemove args)
    {
        ValidateAllActions(ent.Owner);
    }

    private static void OnFullHeldUpdateCanMove(Entity<ActiveStateScpHoldableFullHoldComponent> ent, ref UpdateCanMoveEvent args)
    {
        args.Cancel();
    }

    private void OnFullHeldStartup(Entity<ActiveStateScpHoldableFullHoldComponent> ent, ref ComponentStartup args)
    {
        if (_activeHoldableQuery.TryComp(ent.Owner, out var held))
            SyncPlaceholderHands((ent.Owner, held));

        ZeroHeldVelocity(ent.Owner);
        _actionBlocker.UpdateCanMove(ent.Owner);
    }

    private void OnFullHeldRemove(Entity<ActiveStateScpHoldableFullHoldComponent> ent, ref ComponentRemove args)
    {
        DeleteHeldHandBlockers(ent.Owner);
        _actionBlocker.UpdateCanMove(ent.Owner);
    }

    private void OnHolderStartup(Entity<ActiveScpHolderComponent> ent, ref ComponentStartup args)
    {
        if (_timing.ApplyingState)
            return;

        SyncHolderState(ent);
    }

    private void OnHolderShutdown(Entity<ActiveScpHolderComponent> ent, ref ComponentShutdown args)
    {
        ent.Comp.Target = null;
        DeleteHolderHandBlockers(ent.Owner);

        if (!_timing.ApplyingState)
            RemComp<ActiveStateScpHolderSlowdownComponent>(ent.Owner);
    }

    private void OnHolderSlowdownRemove(Entity<ActiveStateScpHolderSlowdownComponent> ent, ref ComponentRemove args)
    {
        _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnHolderSlowdownAfterState(Entity<ActiveStateScpHolderSlowdownComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnHolderSlowdownRefreshMoveSpeed(Entity<ActiveStateScpHolderSlowdownComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        args.ModifySpeed(ent.Comp.WalkModifier, ent.Comp.SprintModifier);
    }
}
