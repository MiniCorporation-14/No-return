using Content.Shared._Scp.Holding.Components;
using Content.Shared.Actions.Events;
using Content.Shared.CombatMode;

namespace Content.Shared._Scp.Holding.Systems;

public abstract partial class SharedScpHoldingSystem
{
    [Dependency] private readonly SharedCombatModeSystem _combatMode = default!;

    private void OnHoldRestrictedActionAttempt(Entity<ScpHoldRestrictedComponent> ent, ref ActionAttemptEvent args)
    {
        if (args.Cancelled || !IsHeldAtStage(args.User, ent.Comp.Stage))
            return;

        args.Cancelled = true;
        _popup.PopupClient(Loc.GetString("scp-hold-action-restricted"), args.User, args.User);
    }

    private void OnHeldCombatModeChanged(Entity<ScpHeldComponent> ent, ref CombatModeChangedEvent args)
    {
        if (!args.IsInCombatMode)
            return;

        EnsureCombatModeDisabled(ent.Owner);
    }

    public bool IsHeldAtStage(EntityUid uid, ScpHoldStage stage)
    {
        return _heldQuery.TryComp(uid, out var held) && IsHeldAtStage((uid, held), stage);
    }

    private bool IsHeldAtStage(Entity<ScpHeldComponent> held, ScpHoldStage stage)
    {
        return stage switch
        {
            ScpHoldStage.Soft => true,
            ScpHoldStage.Full => _fullHeldQuery.HasComp(held.Owner),
            _ => false,
        };
    }

    private void EnsureCombatModeDisabled(EntityUid uid)
    {
        if (!IsHeldAtStage(uid, ScpHoldStage.Soft) ||
            !TryComp<CombatModeComponent>(uid, out var combatMode) ||
            !combatMode.IsInCombatMode)
        {
            return;
        }

        _combatMode.SetInCombatMode(uid, false, combatMode);
    }
}
