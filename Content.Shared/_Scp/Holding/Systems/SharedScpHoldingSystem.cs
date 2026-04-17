using Content.Shared._Scp.Holding.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.Alert;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Movement.Systems;
using Content.Shared.StatusEffectNew;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Shared._Scp.Holding.Systems;

public abstract partial class SharedScpHoldingSystem : EntitySystem
{
    /*
     * Core lifecycle, dependencies, constants, and runtime caches.
     */

    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movement = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private static readonly EntProtoId GrabbedStatusEffect = "StatusEffectScpHeld";
    private readonly Dictionary<EntityUid, DoAfterId> _breakoutDoAfterIds = [];

    private EntityQuery<ScpBreakoutAttemptComponent> _breakoutAttemptQuery;
    private EntityQuery<ActiveStateScpHoldableFullHoldComponent> _activeHoldableFullHoldStateQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<ActiveScpHoldableComponent> _activeHoldableQuery;
    private EntityQuery<ScpHolderComponent> _holderConfigQuery;
    private EntityQuery<ActiveScpHolderComponent> _activeHolderQuery;
    private EntityQuery<ActiveStateScpHolderSlowdownComponent> _activeHolderSlowdownStateQuery;

    public override void Initialize()
    {
        base.Initialize();

        _breakoutAttemptQuery = GetEntityQuery<ScpBreakoutAttemptComponent>();
        _activeHoldableFullHoldStateQuery = GetEntityQuery<ActiveStateScpHoldableFullHoldComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _activeHoldableQuery = GetEntityQuery<ActiveScpHoldableComponent>();
        _holderConfigQuery = GetEntityQuery<ScpHolderComponent>();
        _activeHolderQuery = GetEntityQuery<ActiveScpHolderComponent>();
        _activeHolderSlowdownStateQuery = GetEntityQuery<ActiveStateScpHolderSlowdownComponent>();

        InitializeHoldQueries();
        InitializeHandQueries();
        InitializeStateQueries();
        SubscribeHoldingEvents();
        InitializeRestrictions();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _breakoutDoAfterIds.Clear();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var immuneQuery = EntityQueryEnumerator<ScpHoldImmuneComponent>();
        while (immuneQuery.MoveNext(out var uid, out var immune))
        {
            if (_timing.CurTime >= immune.ExpiresAt)
                RemCompDeferred<ScpHoldImmuneComponent>(uid);
        }

        var heldQuery = EntityQueryEnumerator<ActiveScpHoldableComponent>();
        while (heldQuery.MoveNext(out var uid, out var held))
        {
            if (!ShouldUpdateHeld(uid, held))
                continue;

            UpdateHeld((uid, held));
        }
    }

    protected virtual bool ShouldUpdateHeld(EntityUid uid, ActiveScpHoldableComponent held)
    {
        return true;
    }

    protected virtual void OnHeldStateRefreshed(Entity<ActiveScpHoldableComponent> held)
    {
    }

    protected virtual void OnHeldStateShutdown(Entity<ActiveScpHoldableComponent> held)
    {
    }

    protected virtual void OnHolderStateRefreshed(Entity<ActiveScpHolderComponent> holder)
    {
    }

    protected virtual void OnHolderStateShutdown(EntityUid holderUid, EntityUid? target)
    {
    }
}
