using Content.Shared.ActionBlocker;
using Content.Shared.Alert;
using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Movement.Systems;
using Content.Shared.StatusEffectNew;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Shared._Scp.Holding;

public sealed partial class SharedScpHoldingSystem : EntitySystem
{
    /*
     * Core lifecycle, dependencies, constants, and runtime caches.
     */
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movement = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;

    private const string GrabbedStatusEffect = "StatusEffectScpHeld";
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<ScpHeldComponent> _heldQuery;
    private EntityQuery<ScpHoldComponent> _holdQuery;
    private EntityQuery<ScpHolderComponent> _holderQuery;

    public override void Initialize()
    {
        base.Initialize();

        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _heldQuery = GetEntityQuery<ScpHeldComponent>();
        _holdQuery = GetEntityQuery<ScpHoldComponent>();
        _holderQuery = GetEntityQuery<ScpHolderComponent>();
        InitializeActionQueries();
        InitializeHandQueries();
        InitializeStateQueries();
        SubscribeHoldingEvents();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var immuneQuery = EntityQueryEnumerator<ScpHoldImmuneComponent>();
        while (immuneQuery.MoveNext(out var uid, out var immune))
        {
            if (_timing.CurTime >= immune.ExpiresAt)
                RemComp<ScpHoldImmuneComponent>(uid);
        }

        var heldQuery = EntityQueryEnumerator<ScpHeldComponent>();
        while (heldQuery.MoveNext(out var uid, out var held))
        {
            if (_net.IsClient &&
                (!_physicsQuery.TryComp(uid, out var physics) || !physics.Predict))
            {
                continue;
            }

            UpdateHeld((uid, held));
        }
    }
}
