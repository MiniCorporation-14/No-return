using Content.Shared._Scp.Holding.Components;
using Content.Shared._Scp.Holding.Systems;
using Content.Shared.Movement.Pulling.Components;

#pragma warning disable IDE0130
namespace Content.Shared.Movement.Pulling.Systems;

public sealed partial class PullingSystem
{
    [Dependency] private readonly SharedScpHoldingSystem _scpHolding = default!;

    private EntityQuery<PullableComponent> _pullableQuery;
    private EntityQuery<ScpHolderComponent> _scpHolderConfigQuery;
    private EntityQuery<ScpHoldableComponent> _scpHoldableQuery;
    private EntityQuery<ActiveScpHolderComponent> _scpActiveHolderQuery;

    private void InitializeScpHolding()
    {
        _pullableQuery = GetEntityQuery<PullableComponent>();
        _scpHolderConfigQuery = GetEntityQuery<ScpHolderComponent>();
        _scpHoldableQuery = GetEntityQuery<ScpHoldableComponent>();
        _scpActiveHolderQuery = GetEntityQuery<ActiveScpHolderComponent>();
    }

    private bool TryRedirectPullToScpHold(EntityUid pullerUid, EntityUid pullableUid,
        PullerComponent pullerComp, PullableComponent pullableComp, out bool result)
    {
        result = false;

        if (!_scpHolderConfigQuery.TryComp(pullerUid, out var holdComp) ||
            !_scpHoldableQuery.HasComp(pullableUid))
        {
            return false;
        }

        var holder = (pullerUid, holdComp);

        if (_scpActiveHolderQuery.TryComp(pullerUid, out var activeHolder) &&
            activeHolder.Target != null)
        {
            result = _scpHolding.TryToggleHold(holder, pullableUid);
            return true;
        }

        if (!_scpHolding.CanToggleHold(holder,
                pullableUid,
                ignoreHandAvailability: pullerComp.Pulling != null,
                checkAttempt: true))
            return true;

        if (pullerComp.Pulling is { } currentPullUid &&
            _pullableQuery.TryComp(currentPullUid, out var currentPull) &&
            !TryStopPull(currentPullUid, currentPull, pullerUid))
        {
            return true;
        }

        if (pullableComp.Puller != null &&
            !TryStopPull(pullableUid, pullableComp, pullableComp.Puller))
        {
            return true;
        }

        result = _scpHolding.TryToggleHold(holder, pullableUid, attemptChecked: true);
        return true;
    }
}
