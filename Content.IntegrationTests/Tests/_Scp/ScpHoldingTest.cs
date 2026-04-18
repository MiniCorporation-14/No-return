#nullable enable
using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.IntegrationTests.Tests.Helpers;
using Content.Shared.Alert;
using Content.Server.Body.Systems;
using Content.Shared._Scp.Holding;
using Content.Shared._Scp.Holding.Components;
using Content.Shared._Scp.Holding.Systems;
using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.Damage.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Components;
using Content.Shared.Input;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Movement.Systems;
using Content.Shared.StatusEffectNew;
using Content.Shared.StatusEffectNew.Components;
using Content.Shared.Throwing;
using Robust.Client.Input;
using Robust.Server.Console;
using Robust.Client.Physics;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.UnitTesting;
using Content.Shared.Stunnable;
using Content.Shared.Whitelist;

namespace Content.IntegrationTests.Tests._Scp;

[TestFixture]
public sealed class ScpHoldingTest
{
    private const string HolderPrototype = "ScpHoldingTestHolder";
    private const string HoldableWhitelistedHolderPrototype = "ScpHoldingTestHolderHoldableWhitelisted";
    private const string HoldableBlacklistedHolderPrototype = "ScpHoldingTestHolderHoldableBlacklisted";
    private const string TestListenerComponentName = "TestListener";
    private static readonly ProtoId<AlertPrototype> GrabbedAlertId = "ScpHoldGrabbed";
    private static readonly FieldInfo ActiveScpHolderTargetField =
        typeof(ActiveScpHolderComponent).GetField(nameof(ActiveScpHolderComponent.Target))!;
    private static readonly FieldInfo SoftEscapeAvailableAtField =
        typeof(ActiveScpHoldableComponent).GetField(nameof(ActiveScpHoldableComponent.SoftEscapeAvailableAt))!;

    private static EntityWhitelist CreateComponentWhitelist(params string[] components)
    {
        return new EntityWhitelist
        {
            Components = components,
        };
    }

    [TestPrototypes]
    private const string Prototypes = """
- type: entity
  id: ScpHoldingTestHolder
  parent: MobHuman
  components:
  - type: ScpHolder
- type: entity
  id: ScpHoldingTestHolderHoldableWhitelisted
  parent: ScpHoldingTestHolder
  components:
  - type: ScpHolder
    holdableWhitelist:
      components:
      - TestListener
- type: entity
  id: ScpHoldingTestHolderHoldableBlacklisted
  parent: ScpHoldingTestHolder
  components:
  - type: ScpHolder
    holdableBlacklist:
      components:
      - TestListener
""";

    [Test]
    public void ActiveScpHoldableStateDoesNotStorePrimaryHolder()
    {
        Assert.That(typeof(ActiveScpHoldableComponent).GetField("PrimaryHolder"), Is.Null);
    }

    [Test]
    public async Task HoldAppliesStatusEffectImmediately()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var statusEffects = server.System<StatusEffectsSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        await server.WaitPost(() =>
        {
            var holder = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            var target = entMan.SpawnEntity("MobHuman", map.GridCoords);
            StartHold(entMan, holding, holder, target);

            Assert.That(statusEffects.TryGetStatusEffect(target, "StatusEffectScpHeld", out var effect), Is.True);
            Assert.That(effect, Is.Not.Null);
            Assert.That(entMan.GetComponent<StatusEffectComponent>(effect!.Value).Applied, Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SyncHolderState_DoesNotAdoptForeignVirtualItemWithSameBlockingEntity()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var handsSystem = server.System<SharedHandsSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var virtualItem = server.System<SharedVirtualItemSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holder = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holder = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.1f, 0f)));

            var holderState = entMan.EnsureComponent<ActiveScpHolderComponent>(holder);
            SetHolderTarget(holderState, target);

            Assert.That(virtualItem.TrySpawnVirtualItemInHand(target, holder, out _), Is.True);

            holding.SyncHolderState((holder, holderState));

            var hands = entMan.GetComponent<HandsComponent>(holder);
            Assert.Multiple(() =>
            {
                Assert.That(CountHolderHandBlockers(entMan, handsSystem, holder, target, hands), Is.EqualTo(1));
                Assert.That(CountHolderTargetVirtualItems(entMan, handsSystem, holder, target, hands), Is.EqualTo(2));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SyncHolderState_DeletesDuplicateTaggedHolderBlockers()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var handsSystem = server.System<SharedHandsSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var virtualItem = server.System<SharedVirtualItemSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holder = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holder = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.1f, 0f)));

            var holderState = entMan.EnsureComponent<ActiveScpHolderComponent>(holder);
            SetHolderTarget(holderState, target);

            Assert.That(virtualItem.TrySpawnVirtualItemInHand(target, holder, out var blocker1), Is.True);
            Assert.That(virtualItem.TrySpawnVirtualItemInHand(target, holder, out var blocker2), Is.True);

            entMan.EnsureComponent<ScpHoldHandBlockerComponent>(blocker1!.Value);
            entMan.EnsureComponent<ScpHoldHandBlockerComponent>(blocker2!.Value);

            holding.SyncHolderState((holder, holderState));
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var hands = entMan.GetComponent<HandsComponent>(holder);

            Assert.Multiple(() =>
            {
                Assert.That(CountHolderHandBlockers(entMan, handsSystem, holder, target, hands), Is.EqualTo(1));
                Assert.That(CountHolderTargetVirtualItems(entMan, handsSystem, holder, target, hands), Is.EqualTo(1));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SoftHoldBreakoutByMovementAndAlertRespectsCooldown()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var alerts = server.System<AlertsSystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        var statusEffects = server.System<StatusEffectsSystem>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holder = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holder = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords);
            StartHold(entMan, holding, holder, target);
        });

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ActiveScpHoldableComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(entMan, target), Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
                Assert.That(statusEffects.HasStatusEffect(target, "StatusEffectScpHeld"), Is.True);
                Assert.That(alerts.IsShowingAlert(target, "ScpHoldGrabbed"), Is.True);
            });
        });

        await server.WaitPost(() => RaiseMoveInput(entMan, target));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
        });

        await server.WaitPost(() =>
        {
            StartHold(entMan, holding, holder, target);
            var held = entMan.GetComponent<ActiveScpHoldableComponent>(target);
            SetSoftEscapeAvailableAt(held, timing.CurTime + TimeSpan.FromSeconds(1));
        });

        await server.WaitPost(() => RaiseMoveInput(entMan, target));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.True);
        });

        await server.WaitPost(() =>
        {
            var alert = proto.Index(GrabbedAlertId);
            Assert.That(alerts.ActivateAlert(target, alert), Is.True);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.True);
        });

        await server.WaitPost(() =>
        {
            var held = entMan.GetComponent<ActiveScpHoldableComponent>(target);
            SetSoftEscapeAvailableAt(held, timing.CurTime);
            var alert = proto.Index(GrabbedAlertId);
            Assert.That(alerts.ActivateAlert(target, alert), Is.True);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SoftHoldUsesCustomDragAndLeavesVanillaPullIdle()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var sPhysics = server.System<SharedPhysicsSystem>();
        var cPhysics = client.System<PhysicsSystem>();
        var sTransform = server.System<SharedTransformSystem>();
        var cTransform = client.System<SharedTransformSystem>();
        var sAlerts = server.System<AlertsSystem>();
        var cAlerts = client.System<AlertsSystem>();
        var sStatusEffects = server.System<StatusEffectsSystem>();
        var cStatusEffects = client.System<StatusEffectsSystem>();
        var sHandsSystem = server.System<SharedHandsSystem>();
        var cHandsSystem = client.System<SharedHandsSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holder = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holder = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = sEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.1f, 0f)));
            StartHold(sEntMan, holding, holder, target);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ActiveScpHoldableComponent>(target);
            var holderState = sEntMan.GetComponent<ActiveScpHolderComponent>(holder);
            var holderSpeed = sEntMan.GetComponent<MovementSpeedModifierComponent>(holder);
            var holderHands = sEntMan.GetComponent<HandsComponent>(holder);
            var puller = sEntMan.GetComponent<PullerComponent>(holder);
            var pullable = sEntMan.GetComponent<PullableComponent>(target);
            var move = new UpdateCanMoveEvent(target);
            var collide = new AttemptMobCollideEvent();
            var targetCollide = new AttemptMobTargetCollideEvent();
            var distance = GetDistance(sTransform, holder, target);
            var contacts = sPhysics.GetContactingEntities(holder);

            sEntMan.EventBus.RaiseLocalEvent(target, move);
            sEntMan.EventBus.RaiseLocalEvent(target, ref collide);
            sEntMan.EventBus.RaiseLocalEvent(target, ref targetCollide);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(sEntMan, target), Is.False);
                Assert.That(held.Holders, Is.EqualTo(new[] { holder }));
                Assert.That(move.Cancelled, Is.False);
                Assert.That(collide.Cancelled, Is.True);
                Assert.That(targetCollide.Cancelled, Is.True);
                Assert.That(distance, Is.GreaterThan(0.18f));
                Assert.That(distance, Is.LessThan(0.7f));
                Assert.That(contacts, Does.Not.Contain(target));
                Assert.That(HasHolderSlowdown(sEntMan, holder), Is.True);
                Assert.That(holderSpeed.WalkSpeedModifier, Is.LessThan(0.6f));
                Assert.That(holderSpeed.SprintSpeedModifier, Is.LessThan(0.6f));
                Assert.That(puller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
                Assert.That(CountHolderHandBlockers(sEntMan, sHandsSystem, holder, target, holderHands), Is.EqualTo(1));
                Assert.That(CountHolderTargetVirtualItems(sEntMan, sHandsSystem, holder, target, holderHands), Is.EqualTo(1));
                Assert.That(sStatusEffects.HasStatusEffect(target, "StatusEffectScpHeld"), Is.True);
                Assert.That(sAlerts.IsShowingAlert(target, "ScpHoldGrabbed"), Is.True);
            });
        });

        EntityUid clientHolder = default;
        EntityUid clientTarget = default;
        await client.WaitAssertion(() =>
        {
            clientHolder = ToClientEntity(sEntMan, cEntMan, holder);
            clientTarget = ToClientEntity(sEntMan, cEntMan, target);

            var held = cEntMan.GetComponent<ActiveScpHoldableComponent>(clientTarget);
            var holderState = cEntMan.GetComponent<ActiveScpHolderComponent>(clientHolder);
            var holderSpeed = cEntMan.GetComponent<MovementSpeedModifierComponent>(clientHolder);
            var holderHands = cEntMan.GetComponent<HandsComponent>(clientHolder);
            var puller = cEntMan.GetComponent<PullerComponent>(clientHolder);
            var pullable = cEntMan.GetComponent<PullableComponent>(clientTarget);
            var collide = new AttemptMobCollideEvent();
            var targetCollide = new AttemptMobTargetCollideEvent();
            var distance = GetDistance(cTransform, clientHolder, clientTarget);
            var contacts = cPhysics.GetContactingEntities(clientHolder);

            cEntMan.EventBus.RaiseLocalEvent(clientTarget, ref collide);
            cEntMan.EventBus.RaiseLocalEvent(clientTarget, ref targetCollide);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(cEntMan, clientTarget), Is.False);
                Assert.That(held.Holders, Is.EqualTo(new[] { clientHolder }));
                Assert.That(distance, Is.GreaterThan(0.18f));
                Assert.That(distance, Is.LessThan(0.55f));
                Assert.That(collide.Cancelled, Is.True);
                Assert.That(targetCollide.Cancelled, Is.True);
                Assert.That(contacts, Does.Not.Contain(clientTarget));
                Assert.That(HasHolderSlowdown(cEntMan, clientHolder), Is.True);
                Assert.That(holderSpeed.WalkSpeedModifier, Is.LessThan(0.6f));
                Assert.That(holderSpeed.SprintSpeedModifier, Is.LessThan(0.6f));
                Assert.That(puller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
                Assert.That(CountHolderHandBlockers(cEntMan, cHandsSystem, clientHolder, clientTarget, holderHands), Is.EqualTo(1));
                Assert.That(CountHolderTargetVirtualItems(cEntMan, cHandsSystem, clientHolder, clientTarget, holderHands), Is.EqualTo(1));
                Assert.That(cStatusEffects.HasStatusEffect(clientTarget, "StatusEffectScpHeld"), Is.True);
            });
        });

        var serverDistanceSamples = new float[24];
        var clientDistanceSamples = new float[24];
        await server.WaitPost(() =>
        {
            var holderPhysics = sEntMan.GetComponent<PhysicsComponent>(holder);
            sPhysics.SetLinearVelocity(holder, new Vector2(4f, 0f), body: holderPhysics);
        });

        for (var i = 0; i < 16; i++)
        {
            var sampleIndex = i;
            await pair.RunTicksSync(1);
            await pair.SyncTicks(targetDelta: 1);
            await server.WaitPost(() => serverDistanceSamples[sampleIndex] = GetDistance(sTransform, holder, target));
            await client.WaitPost(() => clientDistanceSamples[sampleIndex] = GetDistance(cTransform, clientHolder, clientTarget));
        }

        await server.WaitPost(() =>
        {
            var holderPhysics = sEntMan.GetComponent<PhysicsComponent>(holder);
            sPhysics.SetLinearVelocity(holder, Vector2.Zero, body: holderPhysics);
        });

        for (var i = 16; i < serverDistanceSamples.Length; i++)
        {
            var sampleIndex = i;
            await pair.RunTicksSync(1);
            await pair.SyncTicks(targetDelta: 1);
            await server.WaitPost(() => serverDistanceSamples[sampleIndex] = GetDistance(sTransform, holder, target));
            await client.WaitPost(() => clientDistanceSamples[sampleIndex] = GetDistance(cTransform, clientHolder, clientTarget));
        }

        Assert.Multiple(() =>
        {
            Assert.That(serverDistanceSamples.Max(), Is.LessThan(0.6f));
            Assert.That(serverDistanceSamples.Min(), Is.GreaterThan(0.16f));
            Assert.That(GetLargestDistanceStep(serverDistanceSamples), Is.LessThan(0.2f));
            Assert.That(serverDistanceSamples[^1], Is.GreaterThan(0.18f));
            Assert.That(serverDistanceSamples[^1], Is.LessThan(0.4f));

            Assert.That(clientDistanceSamples.Max(), Is.LessThan(0.6f));
            Assert.That(clientDistanceSamples.Min(), Is.GreaterThan(0.16f));
            Assert.That(GetLargestDistanceStep(clientDistanceSamples), Is.LessThan(0.2f));
            Assert.That(clientDistanceSamples[^1], Is.GreaterThan(0.18f));
            Assert.That(clientDistanceSamples[^1], Is.LessThan(0.4f));
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(target), Is.True);
        });

        await client.WaitAssertion(() =>
        {
            Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientTarget), Is.True);
        });

        await server.WaitPost(() =>
        {
            var targetCoords = sEntMan.GetComponent<TransformComponent>(target).Coordinates;
            sTransform.SetCoordinates(holder, targetCoords);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var distance = GetDistance(sTransform, holder, target);
            var contacts = sPhysics.GetContactingEntities(holder);

            Assert.Multiple(() =>
            {
                Assert.That(distance, Is.GreaterThan(0.16f));
                Assert.That(distance, Is.LessThan(0.55f));
                Assert.That(contacts, Does.Not.Contain(target));
            });
        });

        await client.WaitAssertion(() =>
        {
            var distance = GetDistance(cTransform, clientHolder, clientTarget);
            var contacts = cPhysics.GetContactingEntities(clientHolder);

            Assert.Multiple(() =>
            {
                Assert.That(distance, Is.GreaterThan(0.16f));
                Assert.That(distance, Is.LessThan(0.55f));
                Assert.That(contacts, Does.Not.Contain(clientTarget));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PlayerHolderSlowdownAppliesOnGrabAndClearsOnRelease()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var sTransform = server.System<SharedTransformSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid target = default;
        var serverBaseWalk = 1f;
        var serverBaseSprint = 1f;
        var clientBaseWalk = 1f;
        var clientBaseSprint = 1f;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            sEntMan.EnsureComponent<ScpHolderComponent>(serverPlayer);
            target = sEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.1f, 0f)));
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        var clientPlayer = EntityUid.Invalid;
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            _ = ToClientEntity(sEntMan, cEntMan, target);
        });

        await server.WaitAssertion(() =>
        {
            var speed = sEntMan.GetComponent<MovementSpeedModifierComponent>(serverPlayer);
            serverBaseWalk = speed.WalkSpeedModifier;
            serverBaseSprint = speed.SprintSpeedModifier;

            Assert.Multiple(() =>
            {
                Assert.That(HasHolderSlowdown(sEntMan, serverPlayer), Is.False);
                Assert.That(serverBaseWalk, Is.GreaterThan(0f));
                Assert.That(serverBaseSprint, Is.GreaterThan(0f));
            });
        });

        await client.WaitAssertion(() =>
        {
            var speed = cEntMan.GetComponent<MovementSpeedModifierComponent>(clientPlayer);
            clientBaseWalk = speed.WalkSpeedModifier;
            clientBaseSprint = speed.SprintSpeedModifier;

            Assert.Multiple(() =>
            {
                Assert.That(HasHolderSlowdown(cEntMan, clientPlayer), Is.False);
                Assert.That(clientBaseWalk, Is.GreaterThan(0f));
                Assert.That(clientBaseSprint, Is.GreaterThan(0f));
            });
        });

        await server.WaitPost(() => StartHold(sEntMan, holding, serverPlayer, target));

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var speed = sEntMan.GetComponent<MovementSpeedModifierComponent>(serverPlayer);
            Assert.Multiple(() =>
            {
                Assert.That(sEntMan.HasComponent<ActiveScpHolderComponent>(serverPlayer), Is.True);
                Assert.That(HasHolderSlowdown(sEntMan, serverPlayer), Is.True);
                Assert.That(speed.WalkSpeedModifier, Is.LessThan(serverBaseWalk * 0.75f));
                Assert.That(speed.SprintSpeedModifier, Is.LessThan(serverBaseSprint * 0.75f));
            });
        });

        await client.WaitAssertion(() =>
        {
            var speed = cEntMan.GetComponent<MovementSpeedModifierComponent>(clientPlayer);
            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHolderComponent>(clientPlayer), Is.True);
                Assert.That(HasHolderSlowdown(cEntMan, clientPlayer), Is.True);
                Assert.That(speed.WalkSpeedModifier, Is.LessThan(clientBaseWalk * 0.75f));
                Assert.That(speed.SprintSpeedModifier, Is.LessThan(clientBaseSprint * 0.75f));
            });
        });

        await server.WaitPost(() =>
        {
            var holdComp = sEntMan.GetComponent<ScpHolderComponent>(serverPlayer);
            Assert.That(holding.TryToggleHold((serverPlayer, holdComp), target), Is.True);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var speed = sEntMan.GetComponent<MovementSpeedModifierComponent>(serverPlayer);
            Assert.Multiple(() =>
            {
                Assert.That(sEntMan.HasComponent<ActiveScpHolderComponent>(serverPlayer), Is.False);
                Assert.That(HasHolderSlowdown(sEntMan, serverPlayer), Is.False);
                Assert.That(speed.WalkSpeedModifier, Is.EqualTo(serverBaseWalk).Within(0.001f));
                Assert.That(speed.SprintSpeedModifier, Is.EqualTo(serverBaseSprint).Within(0.001f));
            });
        });

        await client.WaitAssertion(() =>
        {
            var speed = cEntMan.GetComponent<MovementSpeedModifierComponent>(clientPlayer);
            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHolderComponent>(clientPlayer), Is.False);
                Assert.That(HasHolderSlowdown(cEntMan, clientPlayer), Is.False);
                Assert.That(speed.WalkSpeedModifier, Is.EqualTo(clientBaseWalk).Within(0.001f));
                Assert.That(speed.SprintSpeedModifier, Is.EqualTo(clientBaseSprint).Within(0.001f));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PullAttemptOnHoldableTargetRedirectsToHoldAndReplacesVanillaPull()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var pulling = server.System<PullingSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holder = default;
        EntityUid plainTarget = default;
        EntityUid holdTarget = default;

        await server.WaitPost(() =>
        {
            holder = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            plainTarget = entMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.1f, 0f)));
            holdTarget = entMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(-0.1f, 0f)));

            entMan.RemoveComponent<ScpHoldableComponent>(plainTarget);

            Assert.That(pulling.TryStartPull(holder, plainTarget), Is.True);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var holderPuller = entMan.GetComponent<PullerComponent>(holder);
            var plainPullable = entMan.GetComponent<PullableComponent>(plainTarget);

            Assert.Multiple(() =>
            {
                Assert.That(holderPuller.Pulling, Is.EqualTo(plainTarget));
                Assert.That(plainPullable.Puller, Is.EqualTo(holder));
                Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(holdTarget), Is.False);
            });
        });

        await server.WaitPost(() =>
        {
            Assert.That(pulling.TryStartPull(holder, holdTarget), Is.True);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var holderPuller = entMan.GetComponent<PullerComponent>(holder);
            var holderState = entMan.GetComponent<ActiveScpHolderComponent>(holder);
            var plainPullable = entMan.GetComponent<PullableComponent>(plainTarget);
            var holdPullable = entMan.GetComponent<PullableComponent>(holdTarget);

            Assert.Multiple(() =>
            {
                Assert.That(holderPuller.Pulling, Is.Null);
                Assert.That(holderState.Target, Is.EqualTo(holdTarget));
                Assert.That(plainPullable.Puller, Is.Null);
                Assert.That(holdPullable.Puller, Is.Null);
                Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(holdTarget), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SecondHolderEntersFullHoldAndFillsHands()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var holding = server.System<SharedScpHoldingSystem>();
        var handsSystem = server.System<SharedHandsSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holderOne = default;
        EntityUid holderTwo = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holderOne = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            holderTwo = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords);

            StartHold(entMan, holding, holderOne, target);
            StartHold(entMan, holding, holderTwo, target);
        });

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ActiveScpHoldableComponent>(target);
            var hands = entMan.GetComponent<HandsComponent>(target);
            var holderOnePuller = entMan.GetComponent<PullerComponent>(holderOne);
            var holderTwoPuller = entMan.GetComponent<PullerComponent>(holderTwo);
            var pullable = entMan.GetComponent<PullableComponent>(target);
            var move = new UpdateCanMoveEvent(target);
            entMan.EventBus.RaiseLocalEvent(target, move);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(entMan, target), Is.True);
                Assert.That(held.RequiredHolderCount, Is.EqualTo(2));
                Assert.That(held.Holders, Has.Count.EqualTo(2));
                Assert.That(move.Cancelled, Is.True);
                Assert.That(CountBlockingVirtualHands(entMan, handsSystem, target, hands, holderOne, holderTwo), Is.EqualTo(hands.SortedHands.Count));
                Assert.That(VictimHandsUseHolderIcons(entMan, handsSystem, target, hands, holderOne, holderTwo), Is.True);
                Assert.That(holderOnePuller.Pulling, Is.Null);
                Assert.That(holderTwoPuller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FullHoldVictimBlockersStayStableOnServerAndClient()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var cTiming = client.ResolveDependency<IGameTiming>();
        var sTransform = server.System<SharedTransformSystem>();
        var sHandsSystem = server.System<SharedHandsSystem>();
        var cHandsSystem = client.System<SharedHandsSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid holderOne = default;
        EntityUid holderTwo = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            holderOne = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords.Offset(new Vector2(0.5f, 0f)));
            holderTwo = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords.Offset(new Vector2(-0.5f, 0f)));
            StartHold(sEntMan, holding, holderOne, serverPlayer);
            StartHold(sEntMan, holding, holderTwo, serverPlayer);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        EntityUid[] initialServerBlockers = [];
        await server.WaitAssertion(() =>
        {
            var hands = sEntMan.GetComponent<HandsComponent>(serverPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(sEntMan, serverPlayer), Is.True);
                Assert.That(CountBlockingVirtualHands(sEntMan, sHandsSystem, serverPlayer, hands, holderOne, holderTwo), Is.EqualTo(hands.SortedHands.Count));
            });

            initialServerBlockers = GetHeldHandBlockers(sEntMan, sHandsSystem, serverPlayer, hands, holderOne, holderTwo);
            Assert.That(initialServerBlockers, Has.Length.EqualTo(hands.SortedHands.Count));
        });

        var clientPlayer = EntityUid.Invalid;
        EntityUid[] initialClientBlockers = [];
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            var hands = cEntMan.GetComponent<HandsComponent>(clientPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(cEntMan, clientPlayer), Is.True);
                Assert.That(CountBlockingVirtualHands(cEntMan, cHandsSystem, clientPlayer, hands, holderOne, holderTwo), Is.EqualTo(hands.SortedHands.Count));
            });

            initialClientBlockers = GetHeldHandBlockers(cEntMan, cHandsSystem, clientPlayer, hands, holderOne, holderTwo);
            Assert.That(initialClientBlockers, Has.Length.EqualTo(hands.SortedHands.Count));
        });

        await pair.RunTicksSync(8);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var hands = sEntMan.GetComponent<HandsComponent>(serverPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(sEntMan, serverPlayer), Is.True);
                Assert.That(GetHeldHandBlockers(sEntMan, sHandsSystem, serverPlayer, hands, holderOne, holderTwo), Is.EqualTo(initialServerBlockers));
            });
        });

        await client.WaitAssertion(() =>
        {
            var hands = cEntMan.GetComponent<HandsComponent>(clientPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(cEntMan, clientPlayer), Is.True);
                Assert.That(GetHeldHandBlockers(cEntMan, cHandsSystem, clientPlayer, hands, holderOne, holderTwo), Is.EqualTo(initialClientBlockers));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TargetWithoutScpHoldableCannotBeHeld()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holder = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holder = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords);
            entMan.RemoveComponent<ScpHoldableComponent>(target);
        });

        await server.WaitAssertion(() =>
        {
            var holdComp = entMan.GetComponent<ScpHolderComponent>(holder);

            Assert.Multiple(() =>
            {
                Assert.That(holding.CanToggleHold((holder, holdComp), target, quiet: true), Is.False);
                Assert.That(holding.TryToggleHold((holder, holdComp), target), Is.False);
                Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HolderAndHoldableFiltersUseCheckBoth()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        EntityUid successHolder = default;
        EntityUid blockedHolder = default;
        EntityUid blacklistHolder = default;
        EntityUid successTarget = default;
        EntityUid blockedTarget = default;
        EntityUid blacklistTarget = default;
        EntityUid holderBlacklistTarget = default;

        await server.WaitPost(() =>
        {
            successHolder = entMan.SpawnEntity(HoldableWhitelistedHolderPrototype, map.GridCoords);
            blockedHolder = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            blacklistHolder = entMan.SpawnEntity(HoldableBlacklistedHolderPrototype, map.GridCoords);
            successTarget = entMan.SpawnEntity("MobHuman", map.GridCoords);
            blockedTarget = entMan.SpawnEntity("MobHuman", map.GridCoords);
            blacklistTarget = entMan.SpawnEntity("MobHuman", map.GridCoords);
            holderBlacklistTarget = entMan.SpawnEntity("MobHuman", map.GridCoords);

            entMan.AddComponent<TestListenerComponent>(successHolder);
            entMan.AddComponent<TestListenerComponent>(blacklistHolder);
            entMan.AddComponent<TestListenerComponent>(successTarget);
            entMan.AddComponent<TestListenerComponent>(blacklistTarget);
            entMan.AddComponent<TestListenerComponent>(holderBlacklistTarget);

            var successTargetHoldable = entMan.GetComponent<ScpHoldableComponent>(successTarget);
            successTargetHoldable.HolderWhitelist = CreateComponentWhitelist(TestListenerComponentName);

            var holderBlacklistHoldable = entMan.GetComponent<ScpHoldableComponent>(holderBlacklistTarget);
            holderBlacklistHoldable.HolderBlacklist = CreateComponentWhitelist(TestListenerComponentName);
        });

        await server.WaitAssertion(() =>
        {
            var successHold = entMan.GetComponent<ScpHolderComponent>(successHolder);
            var blockedHold = entMan.GetComponent<ScpHolderComponent>(blockedHolder);
            var blacklistHold = entMan.GetComponent<ScpHolderComponent>(blacklistHolder);

            Assert.Multiple(() =>
            {
                Assert.That(holding.CanToggleHold((successHolder, successHold), blockedTarget, quiet: true), Is.False);
                Assert.That(holding.TryToggleHold((successHolder, successHold), blockedTarget), Is.False);

                Assert.That(holding.CanToggleHold((blockedHolder, blockedHold), successTarget, quiet: true), Is.False);
                Assert.That(holding.TryToggleHold((blockedHolder, blockedHold), successTarget), Is.False);

                Assert.That(holding.CanToggleHold((blacklistHolder, blacklistHold), blacklistTarget, quiet: true), Is.False);
                Assert.That(holding.TryToggleHold((blacklistHolder, blacklistHold), blacklistTarget), Is.False);

                Assert.That(holding.CanToggleHold((successHolder, successHold), holderBlacklistTarget, quiet: true), Is.False);
                Assert.That(holding.TryToggleHold((successHolder, successHold), holderBlacklistTarget), Is.False);

                Assert.That(holding.CanToggleHold((successHolder, successHold), successTarget, quiet: true), Is.True);
                Assert.That(holding.TryToggleHold((successHolder, successHold), successTarget), Is.True);

                Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(blockedTarget), Is.False);
                Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(successTarget), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HoldAttemptEventCanCancelGrab()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var holding = server.System<SharedScpHoldingSystem>();
        var attempts = server.System<ScpHoldAttemptListenerSystem>();
        _ = server.System<ScpHoldAttemptCancelSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holder = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holder = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords);
            entMan.AddComponent<TestListenerComponent>(holder);
            entMan.AddComponent<TestListenerComponent>(target);
            entMan.AddComponent<ScpHoldAttemptCancelTestComponent>(target);
        });

        await server.WaitAssertion(() =>
        {
            var holdComp = entMan.GetComponent<ScpHolderComponent>(holder);

            Assert.Multiple(() =>
            {
                Assert.That(holding.TryToggleHold((holder, holdComp), target), Is.False);
                Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
                Assert.That(attempts.Count(target), Is.EqualTo(1));
                Assert.That(attempts.Count(holder), Is.EqualTo(1));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BreakoutEventRaisedWhenTargetEscapes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var holding = server.System<SharedScpHoldingSystem>();
        var breakouts = server.System<ScpHoldBreakoutListenerSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holder = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holder = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords);
            entMan.AddComponent<TestListenerComponent>(target);
            StartHold(entMan, holding, holder, target);
        });

        await server.WaitPost(() => RaiseMoveInput(entMan, target));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var breakout = breakouts.GetEvents(target).Single();

            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
                Assert.That(breakouts.Count(target), Is.EqualTo(1));
                Assert.That(breakout.ViaMovement, Is.True);
                Assert.That(breakout.WasFullHold, Is.False);
                Assert.That(breakout.AppliedImmunity, Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MultiHandTargetNeedsMatchingHolderCountAndResyncsOnHandLoss()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var host = server.ResolveDependency<IServerConsoleHost>();
        var holding = server.System<SharedScpHoldingSystem>();
        var handsSystem = server.System<SharedHandsSystem>();
        var transform = server.System<SharedTransformSystem>();
        var bodySystem = server.System<BodySystem>();
        var map = await pair.CreateTestMap();

        EntityUid holderOne = default;
        EntityUid holderTwo = default;
        EntityUid holderThree = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holderOne = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            holderTwo = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            holderThree = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords);

            host.ExecuteCommand(null, $"addhand {entMan.GetNetEntity(target)}");
        });
        await server.WaitRunTicks(2);

        await server.WaitPost(() =>
        {
            StartHold(entMan, holding, holderOne, target);
            StartHold(entMan, holding, holderTwo, target);
        });

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ActiveScpHoldableComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(held.RequiredHolderCount, Is.EqualTo(3));
                Assert.That(HasFullHold(entMan, target), Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(2));
            });
        });

        await server.WaitPost(() =>
        {
            StartHold(entMan, holding, holderThree, target);
        });

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ActiveScpHoldableComponent>(target);
            var hands = entMan.GetComponent<HandsComponent>(target);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(entMan, target), Is.True);
                Assert.That(held.RequiredHolderCount, Is.EqualTo(3));
                Assert.That(hands.SortedHands.Count, Is.EqualTo(3));
                Assert.That(CountBlockingVirtualHands(entMan, handsSystem, target, hands, holderOne, holderTwo, holderThree), Is.EqualTo(3));
                Assert.That(VictimHandsUseHolderIcons(entMan, handsSystem, target, hands, holderOne, holderTwo, holderThree), Is.True);
            });
        });

        await server.WaitPost(() =>
        {
            var body = entMan.GetComponent<BodyComponent>(target);
            var removedHand = bodySystem.GetBodyChildrenOfType(target, BodyPartType.Hand, body).First().Id;
            transform.AttachToGridOrMap(removedHand);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ActiveScpHoldableComponent>(target);
            var hands = entMan.GetComponent<HandsComponent>(target);

            Assert.Multiple(() =>
            {
                Assert.That(held.RequiredHolderCount, Is.EqualTo(2));
                Assert.That(HasFullHold(entMan, target), Is.True);
                Assert.That(hands.SortedHands.Count, Is.EqualTo(2));
                Assert.That(CountBlockingVirtualHands(entMan, handsSystem, target, hands, holderOne, holderTwo, holderThree), Is.EqualTo(2));
                Assert.That(VictimHandsUseHolderIcons(entMan, handsSystem, target, hands, holderOne, holderTwo, holderThree), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FullBreakoutByMovementAppliesImmunity()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holderOne = default;
        EntityUid holderTwo = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holderOne = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            holderTwo = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords);

            StartHold(entMan, holding, holderOne, target);
            StartHold(entMan, holding, holderTwo, target);
        });

        await server.WaitPost(() => RaiseMoveInput(entMan, target));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ActiveScpHoldableComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(entMan, target), Is.True);
                Assert.That(HasBreakoutAttempt(entMan, target), Is.False);
            });
        });

        await server.WaitRunTicks(GetTickCount(timing, TimeSpan.FromSeconds(10)));

        await server.WaitPost(() => RaiseMoveInput(entMan, target));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ActiveScpHoldableComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(HasBreakoutAttempt(entMan, target), Is.True);
                Assert.That(CountAttachedPrototype(entMan, holderOne, "WhistleExclamation"), Is.EqualTo(1));
                Assert.That(CountAttachedPrototype(entMan, holderTwo, "WhistleExclamation"), Is.EqualTo(1));
            });
        });

        await server.WaitRunTicks(GetTickCount(timing, TimeSpan.FromSeconds(5)));

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
                Assert.That(entMan.HasComponent<ScpHoldImmuneComponent>(target), Is.True);
            });
        });

        await server.WaitPost(() =>
        {
            var holdComp = entMan.GetComponent<ScpHolderComponent>(holderOne);
            Assert.That(holding.TryToggleHold((holderOne, holdComp), target), Is.False);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
        });

        await server.WaitRunTicks(GetTickCount(timing, TimeSpan.FromSeconds(5)));

        await server.WaitPost(() =>
        {
            var holdComp = entMan.GetComponent<ScpHolderComponent>(holderOne);
            Assert.That(holding.TryToggleHold((holderOne, holdComp), target), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Scp096FullBreakoutAffectsAllHoldersAndThrowsOverlappingHoldersApart()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var holding = server.System<SharedScpHoldingSystem>();
        var physics = server.System<SharedPhysicsSystem>();
        var transform = server.System<SharedTransformSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holderOne = default;
        EntityUid holderTwo = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holderOne = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            holderTwo = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("Scp096", map.GridCoords);

            var holdable = entMan.GetComponent<ScpHoldableComponent>(target);
            holdable.FullHoldDelay = TimeSpan.Zero;
            holdable.FullBreakoutDuration = TimeSpan.Zero;

            StartHold(entMan, holding, holderOne, target);
            StartHold(entMan, holding, holderTwo, target);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(HasFullHold(entMan, target), Is.True);
        });

        await server.WaitPost(() =>
        {
            transform.SetCoordinates(holderOne, map.GridCoords);
            transform.SetCoordinates(holderTwo, map.GridCoords);
            transform.SetCoordinates(target, map.GridCoords);
            RaiseMoveInput(entMan, target);
        });
        await server.WaitRunTicks(4);

        await server.WaitAssertion(() =>
        {
            var holderOneDamage = entMan.GetComponent<DamageableComponent>(holderOne);
            var holderTwoDamage = entMan.GetComponent<DamageableComponent>(holderTwo);
            var holderOneVelocity = physics.GetMapLinearVelocity(holderOne);
            var holderTwoVelocity = physics.GetMapLinearVelocity(holderTwo);

            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
                Assert.That(entMan.HasComponent<ActiveStateScpHoldableFullHoldComponent>(target), Is.False);
                Assert.That(entMan.HasComponent<ActiveScpHolderComponent>(holderOne), Is.False);
                Assert.That(entMan.HasComponent<ActiveScpHolderComponent>(holderTwo), Is.False);
                Assert.That(entMan.HasComponent<StunnedComponent>(holderOne), Is.True);
                Assert.That(entMan.HasComponent<StunnedComponent>(holderTwo), Is.True);
                Assert.That(holderOneDamage.TotalDamage, Is.GreaterThan(FixedPoint2.Zero));
                Assert.That(holderTwoDamage.TotalDamage, Is.GreaterThan(FixedPoint2.Zero));
                Assert.That(MathF.Abs(holderOneVelocity.X), Is.GreaterThan(1f));
                Assert.That(MathF.Abs(holderTwoVelocity.X), Is.GreaterThan(1f));
                Assert.That(MathF.Sign(holderOneVelocity.X), Is.EqualTo(-MathF.Sign(holderTwoVelocity.X)));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FullBreakoutRestoresMovementOnServerAndClient()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var sTransform = server.System<SharedTransformSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid holderOne = default;
        EntityUid holderTwo = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            holderOne = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords.Offset(new Vector2(0.5f, 0f)));
            holderTwo = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords.Offset(new Vector2(-0.5f, 0f)));
            StartHold(sEntMan, holding, holderOne, serverPlayer);
            StartHold(sEntMan, holding, holderTwo, serverPlayer);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);
        await pair.RunTicksSync(GetTickCount(timing, TimeSpan.FromSeconds(10)));
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var mover = sEntMan.GetComponent<InputMoverComponent>(serverPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(sEntMan, serverPlayer), Is.True);
                Assert.That(mover.CanMove, Is.False);
            });
        });

        var clientPlayer = EntityUid.Invalid;
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            var mover = cEntMan.GetComponent<InputMoverComponent>(clientPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(cEntMan, clientPlayer), Is.True);
                Assert.That(mover.CanMove, Is.False);
            });
        });

        await server.WaitPost(() => RaiseMoveInput(sEntMan, serverPlayer));
        await pair.RunTicksSync(2);
        await pair.RunTicksSync(GetTickCount(timing, TimeSpan.FromSeconds(5)) + 2);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var mover = sEntMan.GetComponent<InputMoverComponent>(serverPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(serverPlayer), Is.False);
                Assert.That(sEntMan.HasComponent<ActiveStateScpHoldableFullHoldComponent>(serverPlayer), Is.False);
                Assert.That(mover.CanMove, Is.True);
            });
        });

        await client.WaitAssertion(() =>
        {
            var mover = cEntMan.GetComponent<InputMoverComponent>(clientPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientPlayer), Is.False);
                Assert.That(cEntMan.HasComponent<ActiveStateScpHoldableFullHoldComponent>(clientPlayer), Is.False);
                Assert.That(mover.CanMove, Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FullBreakoutByAlertStartsAndCompletes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var alerts = server.System<AlertsSystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holderOne = default;
        EntityUid holderTwo = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holderOne = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            holderTwo = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords);

            StartHold(entMan, holding, holderOne, target);
            StartHold(entMan, holding, holderTwo, target);
        });

        await server.WaitRunTicks(GetTickCount(timing, TimeSpan.FromSeconds(10)));

        await server.WaitPost(() =>
        {
            var alert = proto.Index(GrabbedAlertId);
            Assert.That(alerts.ActivateAlert(target, alert), Is.True);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ActiveScpHoldableComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(HasBreakoutAttempt(entMan, target), Is.True);
                Assert.That(CountAttachedPrototype(entMan, holderOne, "WhistleExclamation"), Is.EqualTo(1));
                Assert.That(CountAttachedPrototype(entMan, holderTwo, "WhistleExclamation"), Is.EqualTo(1));
            });
        });

        await server.WaitRunTicks(GetTickCount(timing, TimeSpan.FromSeconds(5)));

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DroppingHolderBlockerReleasesHold()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var handsSystem = server.System<SharedHandsSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holder = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holder = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.1f, 0f)));
            StartHold(entMan, holding, holder, target);
        });
        await server.WaitRunTicks(2);

        await server.WaitPost(() =>
        {
            var hands = entMan.GetComponent<HandsComponent>(holder);
            var blocker = FindHolderHandBlocker(entMan, handsSystem, holder, target, hands);

            Assert.That(blocker, Is.Not.EqualTo(EntityUid.Invalid));
            Assert.That(handsSystem.TryDrop((holder, hands), blocker), Is.False);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
            Assert.That(entMan.HasComponent<ActiveScpHolderComponent>(holder), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ThrowingHolderBlockerReleasesHold()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var handsSystem = server.System<SharedHandsSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holder = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holder = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.1f, 0f)));
            StartHold(entMan, holding, holder, target);
        });
        await server.WaitRunTicks(2);

        await server.WaitPost(() =>
        {
            var hands = entMan.GetComponent<HandsComponent>(holder);
            var blocker = FindHolderHandBlocker(entMan, handsSystem, holder, target, hands);
            var throwEvent = new BeforeThrowEvent(blocker, Vector2.UnitX, 5f, holder);

            Assert.That(blocker, Is.Not.EqualTo(EntityUid.Invalid));
            entMan.EventBus.RaiseLocalEvent(holder, ref throwEvent);
            Assert.That(throwEvent.Cancelled, Is.True);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
            Assert.That(entMan.HasComponent<ActiveScpHolderComponent>(holder), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GettingDroppedAttemptOnHolderBlockerReleasesHold()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var handsSystem = server.System<SharedHandsSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holder = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holder = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.1f, 0f)));
            StartHold(entMan, holding, holder, target);
        });
        await server.WaitRunTicks(2);

        await server.WaitPost(() =>
        {
            var hands = entMan.GetComponent<HandsComponent>(holder);
            var blocker = FindHolderHandBlocker(entMan, handsSystem, holder, target, hands);
            var dropAttempt = new GettingDroppedAttemptEvent(holder);

            Assert.That(blocker, Is.Not.EqualTo(EntityUid.Invalid));
            entMan.EventBus.RaiseLocalEvent(blocker, ref dropAttempt);
            Assert.That(dropAttempt.Cancelled, Is.True);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
            Assert.That(entMan.HasComponent<ActiveScpHolderComponent>(holder), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClientDroppingHolderBlockerReleasesWithoutRespawnFlicker()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var cTiming = client.ResolveDependency<IGameTiming>();
        var sTransform = server.System<SharedTransformSystem>();
        var sHandsSystem = server.System<SharedHandsSystem>();
        var cHandsSystem = client.System<SharedHandsSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            sEntMan.EnsureComponent<ScpHolderComponent>(serverPlayer);
            target = sEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.1f, 0f)));
            StartHold(sEntMan, holding, serverPlayer, target);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        var clientPlayer = EntityUid.Invalid;
        var clientTarget = EntityUid.Invalid;
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            clientTarget = ToClientEntity(sEntMan, cEntMan, target);
            var hands = cEntMan.GetComponent<HandsComponent>(clientPlayer);

                Assert.Multiple(() =>
                {
                    Assert.That(cEntMan.HasComponent<ActiveScpHolderComponent>(clientPlayer), Is.True);
                    Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientTarget), Is.True);
                    Assert.That(
                        CountHolderTargetVirtualItems(cEntMan, cHandsSystem, clientPlayer, clientTarget, hands),
                        Is.EqualTo(1),
                        DescribeHolderTargetVirtualItems(cEntMan, cHandsSystem, clientPlayer, clientTarget, hands));
                    Assert.That(CountPrototypeEntities(cEntMan, "clientsideclone"), Is.EqualTo(0));
                });
        });

        await client.WaitPost(() =>
        {
            var hands = cEntMan.GetComponent<HandsComponent>(clientPlayer);
            var blocker = FindHolderHandBlocker(cEntMan, cHandsSystem, clientPlayer, clientTarget, hands);

            Assert.That(blocker, Is.Not.EqualTo(EntityUid.Invalid));
            Assert.That(cHandsSystem.IsHolding((clientPlayer, hands), blocker, out var blockerHand), Is.True);

            if (hands.ActiveHandId != blockerHand)
                Assert.That(cHandsSystem.TrySetActiveHand((clientPlayer, hands), blockerHand), Is.True);
        });

        await PressClientDropKey(client, cEntMan, cTiming, clientTarget);

        await client.WaitAssertion(() =>
        {
            var hands = cEntMan.GetComponent<HandsComponent>(clientPlayer);
            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientTarget), Is.False);
                Assert.That(cEntMan.HasComponent<ActiveScpHolderComponent>(clientPlayer), Is.False);
                Assert.That(CountHolderTargetVirtualItems(cEntMan, cHandsSystem, clientPlayer, clientTarget, hands), Is.EqualTo(0));
                Assert.That(CountPrototypeEntities(cEntMan, "clientsideclone"), Is.EqualTo(0));
            });
        });

        var maxClientBlockers = 0;
        var holderRespawned = false;
        var heldRespawned = false;
        string? blockerTimeline = null;
        for (var i = 0; i < 12; i++)
        {
            await pair.RunTicksSync(1);
            await pair.SyncTicks(targetDelta: 1);
            await client.WaitPost(() =>
            {
                if (!cEntMan.TryGetComponent<HandsComponent>(clientPlayer, out var hands))
                    return;

                var blockerCount = CountHolderTargetVirtualItems(cEntMan, cHandsSystem, clientPlayer, clientTarget, hands);
                var hasHolder = cEntMan.HasComponent<ActiveScpHolderComponent>(clientPlayer);
                var hasHeld = cEntMan.HasComponent<ActiveScpHoldableComponent>(clientTarget);

                maxClientBlockers = Math.Max(maxClientBlockers, blockerCount);
                holderRespawned |= hasHolder;
                heldRespawned |= hasHeld;

                if (blockerCount > 0 || hasHolder || hasHeld)
                {
                    blockerTimeline =
                        $"tick={i}, holder={hasHolder}, held={hasHeld}, " +
                        $"items=[{DescribeHolderTargetVirtualItems(cEntMan, cHandsSystem, clientPlayer, clientTarget, hands)}]";
                }
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(maxClientBlockers, Is.EqualTo(0), blockerTimeline);
            Assert.That(holderRespawned, Is.False, blockerTimeline);
            Assert.That(heldRespawned, Is.False, blockerTimeline);
            Assert.That(CountPrototypeEntities(cEntMan, "clientsideclone"), Is.EqualTo(0));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClientPullAttemptPredictsSoftHoldBeforeServerAck()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var cTiming = client.ResolveDependency<IGameTiming>();
        var sPhysics = server.System<SharedPhysicsSystem>();
        var cPhysics = client.System<PhysicsSystem>();
        var sTransform = server.System<SharedTransformSystem>();
        var cTransform = client.System<SharedTransformSystem>();
        var sAlerts = server.System<AlertsSystem>();
        var cAlerts = client.System<AlertsSystem>();
        var sStatusEffects = server.System<StatusEffectsSystem>();
        var cStatusEffects = client.System<StatusEffectsSystem>();
        var sHandsSystem = server.System<SharedHandsSystem>();
        var cHandsSystem = client.System<SharedHandsSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            sEntMan.EnsureComponent<ScpHolderComponent>(serverPlayer);
            target = sEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.1f, 0f)));
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        var clientPlayer = EntityUid.Invalid;
        var clientTarget = EntityUid.Invalid;
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            clientTarget = ToClientEntity(sEntMan, cEntMan, target);

            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ScpHolderComponent>(clientPlayer), Is.True);
                Assert.That(cEntMan.EntityExists(clientTarget), Is.True);
            });
        });

        await PressClientPullKey(client, cEntMan, cTiming, clientTarget);

        await client.WaitAssertion(() =>
        {
            var held = cEntMan.GetComponent<ActiveScpHoldableComponent>(clientTarget);
            var holderHands = cEntMan.GetComponent<HandsComponent>(clientPlayer);
            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(cEntMan, clientTarget), Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
                Assert.That(cEntMan.HasComponent<ActiveScpHolderComponent>(clientPlayer), Is.True);
                Assert.That(CountHolderHandBlockers(cEntMan, cHandsSystem, clientPlayer, clientTarget, holderHands), Is.EqualTo(1));
                Assert.That(CountHolderTargetVirtualItems(cEntMan, cHandsSystem, clientPlayer, clientTarget, holderHands), Is.EqualTo(1));
                Assert.That(cStatusEffects.HasStatusEffect(clientTarget, "StatusEffectScpHeld"), Is.True);
            });
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
        });

        var maxPredictedClientBlockers = 1;
        for (var i = 0; i < 6; i++)
        {
            await pair.RunTicksSync(1);
            await pair.SyncTicks(targetDelta: 1);
            await client.WaitPost(() =>
            {
                if (!cEntMan.TryGetComponent<HandsComponent>(clientPlayer, out var holderHands))
                    return;

                maxPredictedClientBlockers = Math.Max(maxPredictedClientBlockers,
                    CountHolderTargetVirtualItems(cEntMan, cHandsSystem, clientPlayer, clientTarget, holderHands));
            });
        }

        Assert.That(maxPredictedClientBlockers, Is.EqualTo(1));

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ActiveScpHoldableComponent>(target);
            var holderHands = sEntMan.GetComponent<HandsComponent>(serverPlayer);
            var puller = sEntMan.GetComponent<PullerComponent>(serverPlayer);
            var pullable = sEntMan.GetComponent<PullableComponent>(target);
            var distance = GetDistance(sTransform, serverPlayer, target);
            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(sEntMan, target), Is.False);
                Assert.That(held.Holders, Is.EqualTo(new[] { serverPlayer }));
                Assert.That(distance, Is.GreaterThan(0.18f));
                Assert.That(distance, Is.LessThan(0.55f));
                Assert.That(puller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
                Assert.That(CountHolderHandBlockers(sEntMan, sHandsSystem, serverPlayer, target, holderHands), Is.EqualTo(1));
                Assert.That(CountHolderTargetVirtualItems(sEntMan, sHandsSystem, serverPlayer, target, holderHands), Is.EqualTo(1));
                Assert.That(sStatusEffects.HasStatusEffect(target, "StatusEffectScpHeld"), Is.True);
                Assert.That(sAlerts.IsShowingAlert(target, "ScpHoldGrabbed"), Is.True);
            });
        });

        await client.WaitAssertion(() =>
        {
            var held = cEntMan.GetComponent<ActiveScpHoldableComponent>(clientTarget);
            var holderHands = cEntMan.GetComponent<HandsComponent>(clientPlayer);
            var puller = cEntMan.GetComponent<PullerComponent>(clientPlayer);
            var pullable = cEntMan.GetComponent<PullableComponent>(clientTarget);
            var distance = GetDistance(cTransform, clientPlayer, clientTarget);
            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(cEntMan, clientTarget), Is.False);
                Assert.That(held.Holders, Is.EqualTo(new[] { clientPlayer }));
                Assert.That(distance, Is.GreaterThan(0.18f));
                Assert.That(distance, Is.LessThan(0.55f));
                Assert.That(puller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
                Assert.That(CountHolderHandBlockers(cEntMan, cHandsSystem, clientPlayer, clientTarget, holderHands), Is.EqualTo(1));
                Assert.That(CountHolderTargetVirtualItems(cEntMan, cHandsSystem, clientPlayer, clientTarget, holderHands), Is.EqualTo(1));
                Assert.That(cStatusEffects.HasStatusEffect(clientTarget, "StatusEffectScpHeld"), Is.True);
            });
        });

        var minClientBlockersAfterAck = int.MaxValue;
        var maxClientBlockersAfterAck = 0;
        string? blockerTimelineAfterAck = null;
        for (var i = 0; i < 12; i++)
        {
            await pair.RunTicksSync(1);
            await pair.SyncTicks(targetDelta: 1);
            await client.WaitPost(() =>
            {
                if (!cEntMan.TryGetComponent<HandsComponent>(clientPlayer, out var holderHands))
                    return;

                var blockerCount = CountHolderTargetVirtualItems(cEntMan, cHandsSystem, clientPlayer, clientTarget, holderHands);
                minClientBlockersAfterAck = Math.Min(minClientBlockersAfterAck, blockerCount);
                maxClientBlockersAfterAck = Math.Max(maxClientBlockersAfterAck, blockerCount);

                if (blockerCount != 1 ||
                    !cEntMan.HasComponent<ActiveScpHolderComponent>(clientPlayer) ||
                    !cEntMan.HasComponent<ActiveScpHoldableComponent>(clientTarget))
                {
                    var distance = GetDistance(cTransform, clientPlayer, clientTarget);
                    blockerTimelineAfterAck =
                        $"tick={i}, blockers={blockerCount}, distance={distance:0.000}, " +
                        $"hasHolder={cEntMan.HasComponent<ActiveScpHolderComponent>(clientPlayer)}, " +
                        $"hasHeld={cEntMan.HasComponent<ActiveScpHoldableComponent>(clientTarget)}, " +
                        $"items=[{DescribeHolderTargetVirtualItems(cEntMan, cHandsSystem, clientPlayer, clientTarget, holderHands)}]";
                }
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(minClientBlockersAfterAck, Is.EqualTo(1), blockerTimelineAfterAck);
            Assert.That(maxClientBlockersAfterAck, Is.EqualTo(1), blockerTimelineAfterAck);
        });

        await pair.CleanReturnAsync();
    }

    // Fire added start - compare real client pull path against direct server hold helper
    [Test]
    public async Task ClientPullHeldTargetWithCombatModeEnabled_DisablesCombatModeInPredictionAndAfterAck()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var cTiming = client.ResolveDependency<IGameTiming>();
        var sTransform = server.System<SharedTransformSystem>();
        var sCombatMode = server.System<Content.Shared.CombatMode.SharedCombatModeSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid target = default;
        EntityUid serverCombatAction = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            sEntMan.EnsureComponent<ScpHolderComponent>(serverPlayer);
            target = sEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.1f, 0f)));

            var combat = sEntMan.GetComponent<Content.Shared.CombatMode.CombatModeComponent>(target);
            sCombatMode.SetInCombatMode(target, true, combat);
            serverCombatAction = GetCombatToggleAction(sEntMan, target);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        EntityUid clientPlayer = default;
        EntityUid clientTarget = default;
        EntityUid clientCombatAction = default;
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            clientTarget = ToClientEntity(sEntMan, cEntMan, target);
            clientCombatAction = ToClientEntity(sEntMan, cEntMan, serverCombatAction);

            Assert.Multiple(() =>
            {
                Assert.That(IsInCombatMode(sEntMan, target), Is.True);
                Assert.That(IsActionToggled(sEntMan, serverCombatAction), Is.True);
                Assert.That(cEntMan.HasComponent<ScpHolderComponent>(clientPlayer), Is.True);
                Assert.That(IsInCombatMode(cEntMan, clientTarget), Is.True);
                Assert.That(IsActionToggled(cEntMan, clientCombatAction), Is.True);
            });
        });

        await PressClientPullKey(client, cEntMan, cTiming, clientTarget);

        await client.WaitAssertion(() =>
        {
            clientCombatAction = ToClientEntity(sEntMan, cEntMan, serverCombatAction);

            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientTarget), Is.True);
                Assert.That(cEntMan.HasComponent<ActiveScpHolderComponent>(clientPlayer), Is.True);
                Assert.That(IsInCombatMode(cEntMan, clientTarget), Is.False);
                Assert.That(IsActionToggled(cEntMan, clientCombatAction), Is.False);
            });
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(target), Is.True);
                Assert.That(IsInCombatMode(sEntMan, target), Is.False);
                Assert.That(IsActionToggled(sEntMan, serverCombatAction), Is.False);
            });
        });

        await client.WaitAssertion(() =>
        {
            clientCombatAction = ToClientEntity(sEntMan, cEntMan, serverCombatAction);

            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientTarget), Is.True);
                Assert.That(IsInCombatMode(cEntMan, clientTarget), Is.False);
                Assert.That(IsActionToggled(cEntMan, clientCombatAction), Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }
    // Fire added end

    [Test]
    public async Task ClientPullCooldownAndFullBreakoutPenaltyReplicate()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var sTiming = server.ResolveDependency<IGameTiming>();
        var cTiming = client.ResolveDependency<IGameTiming>();
        var sTransform = server.System<SharedTransformSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid firstTarget = default;
        EntityUid breakoutTarget = default;
        EntityUid holderTwo = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            sEntMan.EnsureComponent<ScpHolderComponent>(serverPlayer);
            firstTarget = sEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.4f, 0f)));
            breakoutTarget = sEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.4f, 0.6f)));
            holderTwo = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords.Offset(new Vector2(-0.4f, 0.6f)));
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        EntityUid clientPlayer = default;
        EntityUid clientFirstTarget = default;
        EntityUid clientBreakoutTarget = default;

        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            clientFirstTarget = ToClientEntity(sEntMan, cEntMan, firstTarget);
            clientBreakoutTarget = ToClientEntity(sEntMan, cEntMan, breakoutTarget);
        });

        await PressClientPullKey(client, cEntMan, cTiming, clientFirstTarget);
        await PressClientPullKey(client, cEntMan, cTiming, clientBreakoutTarget);

        await client.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientFirstTarget), Is.True);
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientBreakoutTarget), Is.False);
                Assert.That(GetHoldCooldownRemaining(cEntMan, clientPlayer, cTiming), Is.GreaterThan(TimeSpan.Zero));
            });
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(firstTarget), Is.True);
                Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(breakoutTarget), Is.False);
                Assert.That(GetHoldCooldownRemaining(sEntMan, serverPlayer, sTiming), Is.GreaterThan(TimeSpan.Zero));
            });
        });

        await client.WaitAssertion(() =>
        {
            Assert.That(GetHoldCooldownRemaining(cEntMan, clientPlayer, cTiming), Is.GreaterThan(TimeSpan.Zero));
        });

        await pair.RunTicksSync(GetTickCount(sTiming, TimeSpan.FromSeconds(1)) + 1);
        await pair.SyncTicks(targetDelta: 1);

        await PressClientPullKey(client, cEntMan, cTiming, clientFirstTarget);

        await client.WaitAssertion(() =>
        {
            Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientFirstTarget), Is.False);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(firstTarget), Is.False);
        });

        await pair.RunTicksSync(GetTickCount(sTiming, TimeSpan.FromSeconds(1)) + 1);
        await pair.SyncTicks(targetDelta: 1);

        await PressClientPullKey(client, cEntMan, cTiming, clientBreakoutTarget);

        await client.WaitAssertion(() =>
        {
            Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientBreakoutTarget), Is.True);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(breakoutTarget), Is.True);
        });

        await server.WaitPost(() =>
        {
            StartHold(sEntMan, holding, holderTwo, breakoutTarget);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);
        await pair.RunTicksSync(GetTickCount(sTiming, TimeSpan.FromSeconds(10)));
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitPost(() => RaiseMoveInput(sEntMan, breakoutTarget));
        await pair.RunTicksSync(2);
        await pair.SyncTicks(targetDelta: 1);
        await pair.RunTicksSync(GetTickCount(sTiming, TimeSpan.FromSeconds(5)) + 5);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(breakoutTarget), Is.False);
                Assert.That(GetHoldCooldownRemaining(sEntMan, serverPlayer, sTiming), Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1.5)));
                Assert.That(GetHoldCooldownRemaining(sEntMan, holderTwo, sTiming), Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1.5)));
            });
        });

        await client.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientBreakoutTarget), Is.False);
                Assert.That(GetHoldCooldownRemaining(cEntMan, clientPlayer, cTiming), Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1.5)));
            });
        });

        await PressClientPullKey(client, cEntMan, cTiming, clientFirstTarget);

        await client.WaitAssertion(() =>
        {
            Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientFirstTarget), Is.False);
        });

        await pair.RunTicksSync(5);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(firstTarget), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClientSecondPullPredictsFullHoldBeforeServerAck()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var cTiming = client.ResolveDependency<IGameTiming>();
        var sTransform = server.System<SharedTransformSystem>();
        var sHandsSystem = server.System<SharedHandsSystem>();
        var cHandsSystem = client.System<SharedHandsSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid holderOne = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            sEntMan.EnsureComponent<ScpHolderComponent>(serverPlayer);

            holderOne = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = sEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0f)));
            StartHold(sEntMan, holding, holderOne, target);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        var clientPlayer = EntityUid.Invalid;
        var clientTarget = EntityUid.Invalid;
        var clientHolderOne = EntityUid.Invalid;
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            clientTarget = ToClientEntity(sEntMan, cEntMan, target);
            clientHolderOne = ToClientEntity(sEntMan, cEntMan, holderOne);

            var held = cEntMan.GetComponent<ActiveScpHoldableComponent>(clientTarget);
            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(cEntMan, clientTarget), Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
            });
        });

        await PressClientPullKey(client, cEntMan, cTiming, clientTarget);

        await client.WaitAssertion(() =>
        {
            var held = cEntMan.GetComponent<ActiveScpHoldableComponent>(clientTarget);
            var hands = cEntMan.GetComponent<HandsComponent>(clientTarget);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(cEntMan, clientTarget), Is.True);
                Assert.That(held.Holders, Has.Count.EqualTo(2));
                Assert.That(CountBlockingVirtualHands(cEntMan, cHandsSystem, clientTarget, hands, clientHolderOne, clientPlayer), Is.EqualTo(hands.SortedHands.Count));
                Assert.That(VictimHandsUseHolderIcons(cEntMan, cHandsSystem, clientTarget, hands, clientHolderOne, clientPlayer), Is.True);
            });
        });

        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ActiveScpHoldableComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(sEntMan, target), Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
            });
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ActiveScpHoldableComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(sEntMan, target), Is.True);
                Assert.That(held.Holders, Has.Count.EqualTo(2));
            });
        });

        await client.WaitAssertion(() =>
        {
            var held = cEntMan.GetComponent<ActiveScpHoldableComponent>(clientTarget);
            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(cEntMan, clientTarget), Is.True);
                Assert.That(held.Holders, Has.Count.EqualTo(2));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClientAnchorReassignmentKeepsCustomDragAndReconcilesCleanly()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var sPhysics = server.System<SharedPhysicsSystem>();
        var cPhysics = client.System<PhysicsSystem>();
        var host = server.ResolveDependency<IServerConsoleHost>();
        var sTransform = server.System<SharedTransformSystem>();
        var cTransform = client.System<SharedTransformSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid holderTwo = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            sEntMan.EnsureComponent<ScpHolderComponent>(serverPlayer);

            target = sEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.95f, 0f)));
            host.ExecuteCommand(null, $"addhand {sEntMan.GetNetEntity(target)}");

            holderTwo = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords.Offset(new Vector2(-0.5f, 0f)));

            StartHold(sEntMan, holding, serverPlayer, target);
            StartHold(sEntMan, holding, holderTwo, target);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ActiveScpHoldableComponent>(target);
            var serverPlayerPuller = sEntMan.GetComponent<PullerComponent>(serverPlayer);
            var holderTwoPuller = sEntMan.GetComponent<PullerComponent>(holderTwo);
            var pullable = sEntMan.GetComponent<PullableComponent>(target);
            var distance = GetDistance(sTransform, serverPlayer, target);
            var contacts = sPhysics.GetContactingEntities(serverPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(sEntMan, target), Is.False);
                Assert.That(held.Holders, Is.EqualTo(new[] { serverPlayer, holderTwo }));
                Assert.That(distance, Is.GreaterThan(0.18f));
                Assert.That(distance, Is.LessThan(0.5f));
                Assert.That(contacts, Does.Not.Contain(target));
                Assert.That(serverPlayerPuller.Pulling, Is.Null);
                Assert.That(holderTwoPuller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
            });
        });

        var clientPlayer = EntityUid.Invalid;
        var clientTarget = EntityUid.Invalid;
        var clientHolderTwo = EntityUid.Invalid;
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            clientTarget = ToClientEntity(sEntMan, cEntMan, target);
            clientHolderTwo = ToClientEntity(sEntMan, cEntMan, holderTwo);

            var held = cEntMan.GetComponent<ActiveScpHoldableComponent>(clientTarget);
            var playerPuller = cEntMan.GetComponent<PullerComponent>(clientPlayer);
            var holderTwoPuller = cEntMan.GetComponent<PullerComponent>(clientHolderTwo);
            var pullable = cEntMan.GetComponent<PullableComponent>(clientTarget);
            var distance = GetDistance(cTransform, clientPlayer, clientTarget);
            var contacts = cPhysics.GetContactingEntities(clientPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(cEntMan, clientTarget), Is.False);
                Assert.That(held.Holders, Is.EqualTo(new[] { clientPlayer, clientHolderTwo }));
                Assert.That(distance, Is.GreaterThan(0.18f));
                Assert.That(distance, Is.LessThan(0.5f));
                Assert.That(contacts, Does.Not.Contain(clientTarget));
                Assert.That(playerPuller.Pulling, Is.Null);
                Assert.That(holderTwoPuller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
            });
        });

        await server.WaitPost(() =>
        {
            var holdComp = sEntMan.GetComponent<ScpHolderComponent>(serverPlayer);
            Assert.That(holding.TryToggleHold((serverPlayer, holdComp), target), Is.True);
        });

        await pair.RunTicksSync(5);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ActiveScpHoldableComponent>(target);
            var pullable = sEntMan.GetComponent<PullableComponent>(target);
            var holderTwoPuller = sEntMan.GetComponent<PullerComponent>(holderTwo);
            var distance = GetDistance(sTransform, holderTwo, target);
            var contacts = sPhysics.GetContactingEntities(holderTwo);

            Assert.Multiple(() =>
            {
                Assert.That(held.Holders, Is.EqualTo(new[] { holderTwo }));
                Assert.That(distance, Is.GreaterThan(0.18f));
                Assert.That(distance, Is.LessThan(0.55f));
                Assert.That(contacts, Does.Not.Contain(target));
                Assert.That(holderTwoPuller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
            });
        });

        await client.WaitAssertion(() =>
        {
            var held = cEntMan.GetComponent<ActiveScpHoldableComponent>(clientTarget);
            var pullable = cEntMan.GetComponent<PullableComponent>(clientTarget);
            var holderTwoPuller = cEntMan.GetComponent<PullerComponent>(clientHolderTwo);
            var distance = GetDistance(cTransform, clientHolderTwo, clientTarget);
            var contacts = cPhysics.GetContactingEntities(clientHolderTwo);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(cEntMan, clientTarget), Is.False);
                Assert.That(held.Holders, Is.EqualTo(new[] { clientHolderTwo }));
                Assert.That(distance, Is.GreaterThan(0.18f));
                Assert.That(distance, Is.LessThan(0.7f));
                Assert.That(contacts, Does.Not.Contain(clientTarget));
                Assert.That(holderTwoPuller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClientFullBreakoutAlertPredictsDoAfterAndReconciles()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var sTransform = server.System<SharedTransformSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid holderOne = default;
        EntityUid holderTwo = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            holderOne = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords.Offset(new Vector2(0.5f, 0f)));
            holderTwo = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords.Offset(new Vector2(-0.5f, 0f)));
            StartHold(sEntMan, holding, holderOne, serverPlayer);
            StartHold(sEntMan, holding, holderTwo, serverPlayer);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);
        await pair.RunTicksSync(GetTickCount(timing, TimeSpan.FromSeconds(10)));
        await pair.SyncTicks(targetDelta: 1);
        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ActiveScpHoldableComponent>(serverPlayer);
            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(sEntMan, serverPlayer), Is.True);
            });
        });

        var clientPlayer = EntityUid.Invalid;
        var clientHolderOne = EntityUid.Invalid;
        var clientHolderTwo = EntityUid.Invalid;
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            clientHolderOne = ToClientEntity(sEntMan, cEntMan, holderOne);
            clientHolderTwo = ToClientEntity(sEntMan, cEntMan, holderTwo);
            Assert.That(HasFullHold(cEntMan, clientPlayer), Is.True);
        });

        await client.WaitPost(() =>
        {
            cEntMan.RaisePredictiveEvent(new ClickAlertEvent("ScpHoldGrabbed"));

            var held = cEntMan.GetComponent<ActiveScpHoldableComponent>(clientPlayer);
            Assert.Multiple(() =>
            {
                Assert.That(HasBreakoutAttempt(cEntMan, clientPlayer), Is.True);
                Assert.That(cEntMan.HasComponent<ScpHoldImmuneComponent>(clientPlayer), Is.False);
                Assert.That(CountAttachedPrototype(cEntMan, clientHolderOne, "WhistleExclamation"), Is.EqualTo(1));
                Assert.That(CountAttachedPrototype(cEntMan, clientHolderTwo, "WhistleExclamation"), Is.EqualTo(1));
            });
        });

        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ActiveScpHoldableComponent>(serverPlayer);
            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(sEntMan, serverPlayer), Is.True);
                Assert.That(HasBreakoutAttempt(sEntMan, serverPlayer), Is.False);
            });
        });

        await pair.RunTicksSync(GetTickCount(timing, TimeSpan.FromSeconds(5)) + 5);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(serverPlayer), Is.False);
                Assert.That(sEntMan.HasComponent<ScpHoldImmuneComponent>(serverPlayer), Is.True);
            });
        });

        await client.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientPlayer), Is.False);
                Assert.That(cEntMan.HasComponent<ScpHoldImmuneComponent>(clientPlayer), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClientGrabbedAlertPredictsSoftBreakout()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var sTransform = server.System<SharedTransformSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var sAlerts = server.System<AlertsSystem>();
        var cAlerts = client.System<AlertsSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid holder = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            holder = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords.Offset(new Vector2(0.1f, 0f)));
            StartHold(sEntMan, holding, holder, serverPlayer);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        var clientPlayer = EntityUid.Invalid;
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;

            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientPlayer), Is.True);
                Assert.That(cAlerts.IsShowingAlert(clientPlayer, "ScpHoldGrabbed"), Is.True);
            });
        });

        await client.WaitPost(() =>
        {
            cEntMan.RaisePredictiveEvent(new ClickAlertEvent("ScpHoldGrabbed"));

            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientPlayer), Is.False);
                Assert.That(cAlerts.IsShowingAlert(clientPlayer, "ScpHoldGrabbed"), Is.False);
            });
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(serverPlayer), Is.True);
            Assert.That(sAlerts.IsShowingAlert(serverPlayer, "ScpHoldGrabbed"), Is.True);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(serverPlayer), Is.False);
            Assert.That(sAlerts.IsShowingAlert(serverPlayer, "ScpHoldGrabbed"), Is.False);
        });

        await client.WaitAssertion(() =>
        {
            Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientPlayer), Is.False);
            Assert.That(cAlerts.IsShowingAlert(clientPlayer, "ScpHoldGrabbed"), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ReleaseAndRangeLossReassignOrClearHold()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var host = server.ResolveDependency<IServerConsoleHost>();
        var holding = server.System<SharedScpHoldingSystem>();
        var transform = server.System<SharedTransformSystem>();
        var map = await pair.CreateTestMap();

        EntityUid holderOne = default;
        EntityUid holderTwo = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            holderOne = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            holderTwo = entMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = entMan.SpawnEntity("MobHuman", map.GridCoords);

            host.ExecuteCommand(null, $"addhand {entMan.GetNetEntity(target)}");
        });
        await server.WaitRunTicks(2);

        await server.WaitPost(() =>
        {
            StartHold(entMan, holding, holderOne, target);
            StartHold(entMan, holding, holderTwo, target);
        });

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ActiveScpHoldableComponent>(target);
            var holderOnePuller = entMan.GetComponent<PullerComponent>(holderOne);
            var holderTwoPuller = entMan.GetComponent<PullerComponent>(holderTwo);
            var pullable = entMan.GetComponent<PullableComponent>(target);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(entMan, target), Is.False);
                Assert.That(held.Holders, Is.EqualTo(new[] { holderOne, holderTwo }));
                Assert.That(holderOnePuller.Pulling, Is.Null);
                Assert.That(holderTwoPuller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
            });
        });

        await server.WaitPost(() =>
        {
            var holderComp = entMan.GetComponent<ScpHolderComponent>(holderOne);
            Assert.That(holding.TryToggleHold((holderOne, holderComp), target), Is.True);
        });
        await server.WaitRunTicks(4);

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ActiveScpHoldableComponent>(target);
            var holderOnePuller = entMan.GetComponent<PullerComponent>(holderOne);
            var holderTwoPuller = entMan.GetComponent<PullerComponent>(holderTwo);
            var pullable = entMan.GetComponent<PullableComponent>(target);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(entMan, target), Is.False);
                Assert.That(held.Holders, Is.EqualTo(new[] { holderTwo }));
                Assert.That(holderOnePuller.Pulling, Is.Null);
                Assert.That(holderTwoPuller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
            });
        });

        await server.WaitPost(() =>
        {
            transform.SetCoordinates(holderTwo, map.GridCoords.Offset(new Vector2(10f, 0f)));
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ActiveScpHoldableComponent>(target), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SoftHoldTargetTeleportClearsStateOnServerAndClient()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var holding = server.System<SharedScpHoldingSystem>();
        var sTransform = server.System<SharedTransformSystem>();
        var sAlerts = server.System<AlertsSystem>();
        var cAlerts = client.System<AlertsSystem>();
        var sStatusEffects = server.System<StatusEffectsSystem>();
        var cStatusEffects = client.System<StatusEffectsSystem>();
        var sHandsSystem = server.System<SharedHandsSystem>();
        var cHandsSystem = client.System<SharedHandsSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid holder = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            holder = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords.Offset(new Vector2(0.1f, 0f)));
            StartHold(sEntMan, holding, holder, serverPlayer);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ActiveScpHoldableComponent>(serverPlayer);
            var holderState = sEntMan.GetComponent<ActiveScpHolderComponent>(holder);
            var holderHands = sEntMan.GetComponent<HandsComponent>(holder);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(sEntMan, serverPlayer), Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
                Assert.That(holderState.Target, Is.EqualTo(serverPlayer));
                Assert.That(CountHolderHandBlockers(sEntMan, sHandsSystem, holder, serverPlayer, holderHands), Is.EqualTo(1));
                Assert.That(CountHolderTargetVirtualItems(sEntMan, sHandsSystem, holder, serverPlayer, holderHands), Is.EqualTo(1));
                Assert.That(sStatusEffects.HasStatusEffect(serverPlayer, "StatusEffectScpHeld"), Is.True);
                Assert.That(sAlerts.IsShowingAlert(serverPlayer, "ScpHoldGrabbed"), Is.True);
            });
        });

        var clientPlayer = EntityUid.Invalid;
        var clientHolder = EntityUid.Invalid;
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            clientHolder = ToClientEntity(sEntMan, cEntMan, holder);

            var held = cEntMan.GetComponent<ActiveScpHoldableComponent>(clientPlayer);
            var holderState = cEntMan.GetComponent<ActiveScpHolderComponent>(clientHolder);
            var holderHands = cEntMan.GetComponent<HandsComponent>(clientHolder);

            Assert.Multiple(() =>
            {
                Assert.That(HasFullHold(cEntMan, clientPlayer), Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
                Assert.That(holderState.Target, Is.EqualTo(clientPlayer));
                Assert.That(CountHolderHandBlockers(cEntMan, cHandsSystem, clientHolder, clientPlayer, holderHands), Is.EqualTo(1));
                Assert.That(CountHolderTargetVirtualItems(cEntMan, cHandsSystem, clientHolder, clientPlayer, holderHands), Is.EqualTo(1));
                Assert.That(cStatusEffects.HasStatusEffect(clientPlayer, "StatusEffectScpHeld"), Is.True);
                Assert.That(cAlerts.IsShowingAlert(clientPlayer, "ScpHoldGrabbed"), Is.True);
            });
        });

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords.Offset(new Vector2(10f, 0f)));
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var holderHands = sEntMan.GetComponent<HandsComponent>(holder);

            Assert.Multiple(() =>
            {
                Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(serverPlayer), Is.False);
                Assert.That(sEntMan.HasComponent<ScpHoldImmuneComponent>(serverPlayer), Is.False);
                Assert.That(sEntMan.HasComponent<ActiveScpHolderComponent>(holder), Is.False);
                Assert.That(CountHolderHandBlockers(sEntMan, sHandsSystem, holder, serverPlayer, holderHands), Is.EqualTo(0));
                Assert.That(CountHolderTargetVirtualItems(sEntMan, sHandsSystem, holder, serverPlayer, holderHands), Is.EqualTo(0));
                Assert.That(sStatusEffects.HasStatusEffect(serverPlayer, "StatusEffectScpHeld"), Is.False);
                Assert.That(sAlerts.IsShowingAlert(serverPlayer, "ScpHoldGrabbed"), Is.False);
            });
        });

        await client.WaitAssertion(() =>
        {
            var holderHands = cEntMan.GetComponent<HandsComponent>(clientHolder);

            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientPlayer), Is.False);
                Assert.That(cEntMan.HasComponent<ScpHoldImmuneComponent>(clientPlayer), Is.False);
                Assert.That(cEntMan.HasComponent<ActiveScpHolderComponent>(clientHolder), Is.False);
                Assert.That(CountHolderHandBlockers(cEntMan, cHandsSystem, clientHolder, clientPlayer, holderHands), Is.EqualTo(0));
                Assert.That(CountHolderTargetVirtualItems(cEntMan, cHandsSystem, clientHolder, clientPlayer, holderHands), Is.EqualTo(0));
                Assert.That(cStatusEffects.HasStatusEffect(clientPlayer, "StatusEffectScpHeld"), Is.False);
                Assert.That(cAlerts.IsShowingAlert(clientPlayer, "ScpHoldGrabbed"), Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    // Fire added start - verify hold disables combat mode consistently
    [Test]
    public async Task ConnectedTargetHeldWithCombatModeEnabled_DisablesCombatModeAndCombatAction()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Fresh = true,
            Connected = true,
            DummyTicker = false,
        });

        var server = pair.Server;
        var client = pair.Client;
        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var sTransform = server.System<SharedTransformSystem>();
        var sCombatMode = server.System<Content.Shared.CombatMode.SharedCombatModeSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid clientPlayer = default;
        EntityUid holder = default;
        EntityUid serverCombatAction = default;
        EntityUid clientCombatAction = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords.Offset(new Vector2(0.1f, 0f)));
            holder = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords);

            var combat = sEntMan.GetComponent<Content.Shared.CombatMode.CombatModeComponent>(serverPlayer);
            sCombatMode.SetInCombatMode(serverPlayer, true, combat);
            serverCombatAction = GetCombatToggleAction(sEntMan, serverPlayer);
        });

        await pair.RunTicksSync(5);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(IsInCombatMode(sEntMan, serverPlayer), Is.True);
                Assert.That(IsActionToggled(sEntMan, serverCombatAction), Is.True);
            });
        });

        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            clientCombatAction = ToClientEntity(sEntMan, cEntMan, serverCombatAction);

            Assert.Multiple(() =>
            {
                Assert.That(IsInCombatMode(cEntMan, clientPlayer), Is.True);
                Assert.That(IsActionToggled(cEntMan, clientCombatAction), Is.True);
            });
        });

        await server.WaitPost(() => StartHold(sEntMan, holding, holder, serverPlayer));
        await pair.RunTicksSync(5);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(serverPlayer), Is.True);
                Assert.That(IsInCombatMode(sEntMan, serverPlayer), Is.False);
                Assert.That(IsActionToggled(sEntMan, serverCombatAction), Is.False);
            });
        });

        await client.WaitAssertion(() =>
        {
            clientCombatAction = ToClientEntity(sEntMan, cEntMan, serverCombatAction);

            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(clientPlayer), Is.True);
                Assert.That(IsInCombatMode(cEntMan, clientPlayer), Is.False);
                Assert.That(IsActionToggled(cEntMan, clientCombatAction), Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }
    // Fire added end

    private static int CountBlockingVirtualHands(IEntityManager entMan, SharedHandsSystem handsSystem, EntityUid uid, HandsComponent hands, params EntityUid[] holders)
    {
        var expected = holders.ToHashSet();

        return handsSystem.EnumerateHeld((uid, hands)).Count(item =>
            entMan.TryGetComponent(item, out VirtualItemComponent? virtualItem) &&
            entMan.HasComponent<ScpHeldHandBlockerComponent>(item) &&
            expected.Contains(virtualItem.BlockingEntity) &&
            entMan.HasComponent<UnremoveableComponent>(item));
    }

    private static EntityUid[] GetHeldHandBlockers(IEntityManager entMan, SharedHandsSystem handsSystem, EntityUid uid, HandsComponent hands, params EntityUid[] holders)
    {
        var expected = holders.ToHashSet();

        return handsSystem.EnumerateHeld((uid, hands))
            .Where(item =>
                entMan.TryGetComponent(item, out VirtualItemComponent? virtualItem) &&
                entMan.HasComponent<ScpHeldHandBlockerComponent>(item) &&
                expected.Contains(virtualItem.BlockingEntity) &&
                entMan.HasComponent<UnremoveableComponent>(item))
            .Order()
            .ToArray();
    }

    private static bool HasFullHold(IEntityManager entMan, EntityUid uid)
    {
        return entMan.HasComponent<ActiveStateScpHoldableFullHoldComponent>(uid);
    }

    private static bool HasBreakoutAttempt(IEntityManager entMan, EntityUid uid)
    {
        return entMan.HasComponent<ScpBreakoutAttemptComponent>(uid);
    }

    private static bool HasHolderSlowdown(IEntityManager entMan, EntityUid uid)
    {
        return entMan.HasComponent<ActiveStateScpHolderSlowdownComponent>(uid);
    }

    // Fire added start - verify hold disables combat mode consistently
    private static EntityUid GetCombatToggleAction(IEntityManager entMan, EntityUid uid)
    {
        var combat = entMan.GetComponent<Content.Shared.CombatMode.CombatModeComponent>(uid);

        Assert.That(combat.CombatToggleActionEntity, Is.Not.Null);
        return combat.CombatToggleActionEntity!.Value;
    }

    private static bool IsInCombatMode(IEntityManager entMan, EntityUid uid)
    {
        return entMan.GetComponent<Content.Shared.CombatMode.CombatModeComponent>(uid).IsInCombatMode;
    }

    private static bool IsActionToggled(IEntityManager entMan, EntityUid uid)
    {
        return entMan.GetComponent<Content.Shared.Actions.Components.ActionComponent>(uid).Toggled;
    }
    // Fire added end

    private static bool VictimHandsUseHolderIcons(IEntityManager entMan, SharedHandsSystem handsSystem, EntityUid uid, HandsComponent hands, params EntityUid[] holders)
    {
        var expected = holders.ToHashSet();
        var count = 0;

        foreach (var item in handsSystem.EnumerateHeld((uid, hands)))
        {
            if (!entMan.TryGetComponent(item, out VirtualItemComponent? virtualItem) ||
                !entMan.HasComponent<ScpHeldHandBlockerComponent>(item) ||
                !entMan.HasComponent<UnremoveableComponent>(item) ||
                !expected.Contains(virtualItem.BlockingEntity))
            {
                return false;
            }

            count++;
        }

        return count == hands.SortedHands.Count;
    }

    private static int CountAttachedPrototype(IEntityManager entMan, EntityUid parent, string prototypeId)
    {
        var count = 0;
        var enumerator = entMan.GetComponent<TransformComponent>(parent).ChildEnumerator;

        while (enumerator.MoveNext(out var child))
        {
            if (entMan.TryGetComponent(child, out MetaDataComponent? metadata) &&
                !metadata.Deleted &&
                metadata.EntityPrototype?.ID == prototypeId)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountPrototypeEntities(IEntityManager entMan, string prototypeId)
    {
        var count = 0;
        var query = entMan.AllEntityQueryEnumerator<MetaDataComponent>();

        while (query.MoveNext(out _, out var metadata))
        {
            if (!metadata.Deleted &&
                metadata.EntityPrototype?.ID == prototypeId)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountHolderHandBlockers(IEntityManager entMan, SharedHandsSystem handsSystem, EntityUid holder, EntityUid target, HandsComponent hands)
    {
        return handsSystem.EnumerateHeld((holder, hands)).Count(item =>
            entMan.TryGetComponent(item, out VirtualItemComponent? virtualItem) &&
            entMan.HasComponent<ScpHoldHandBlockerComponent>(item) &&
            virtualItem.BlockingEntity == target);
    }

    private static int CountHolderTargetVirtualItems(IEntityManager entMan, SharedHandsSystem handsSystem, EntityUid holder, EntityUid target, HandsComponent hands)
    {
        return handsSystem.EnumerateHeld((holder, hands)).Count(item =>
            entMan.TryGetComponent(item, out VirtualItemComponent? virtualItem) &&
            virtualItem.BlockingEntity == target);
    }

    private static string DescribeHolderTargetVirtualItems(IEntityManager entMan, SharedHandsSystem handsSystem, EntityUid holder, EntityUid target, HandsComponent hands)
    {
        var items = handsSystem.EnumerateHeld((holder, hands))
            .Where(item =>
                entMan.TryGetComponent(item, out VirtualItemComponent? virtualItem) &&
                virtualItem.BlockingEntity == target)
            .Select(item =>
                $"{item}:client={entMan.GetComponent<MetaDataComponent>(item).NetEntity.IsClientSide()},marker={entMan.HasComponent<ScpHoldHandBlockerComponent>(item)}");

        return string.Join(", ", items);
    }

    private static EntityUid FindHolderHandBlocker(IEntityManager entMan, SharedHandsSystem handsSystem, EntityUid holder, EntityUid target, HandsComponent hands)
    {
        foreach (var item in handsSystem.EnumerateHeld((holder, hands)))
        {
            if (entMan.TryGetComponent(item, out VirtualItemComponent? virtualItem) &&
                entMan.HasComponent<ScpHoldHandBlockerComponent>(item) &&
                virtualItem.BlockingEntity == target)
            {
                return item;
            }
        }

        return EntityUid.Invalid;
    }

    private static float GetDistance(SharedTransformSystem transform, EntityUid first, EntityUid second)
    {
        return Vector2.Distance(
            transform.GetMapCoordinates(first).Position,
            transform.GetMapCoordinates(second).Position);
    }

    private static float GetLargestDistanceStep(float[] samples)
    {
        var largest = 0f;

        for (var i = 1; i < samples.Length; i++)
        {
            largest = Math.Max(largest, MathF.Abs(samples[i] - samples[i - 1]));
        }

        return largest;
    }

    private static int GetTickCount(IGameTiming timing, TimeSpan duration)
    {
        return Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds / timing.TickPeriod.TotalSeconds) + 1);
    }

    private static TimeSpan GetHoldCooldownRemaining(IEntityManager entMan, EntityUid holder, IGameTiming timing)
    {
        if (!entMan.TryGetComponent(holder, out ScpHolderComponent? holdComp) ||
            holdComp.HoldAvailableAt is not { } cooldownEnd ||
            cooldownEnd <= timing.CurTime)
        {
            return TimeSpan.Zero;
        }

        return cooldownEnd - timing.CurTime;
    }

    private static async Task PressClientPullKey(
        RobustIntegrationTest.ClientIntegrationInstance client,
        IEntityManager entMan,
        IGameTiming timing,
        EntityUid cursorEntity)
    {
        await SendClientPullInput(client, entMan, timing, cursorEntity, BoundKeyState.Down);
        await SendClientPullInput(client, entMan, timing, cursorEntity, BoundKeyState.Up);
    }

    private static async Task PressClientDropKey(
        RobustIntegrationTest.ClientIntegrationInstance client,
        IEntityManager entMan,
        IGameTiming timing,
        EntityUid cursorEntity)
    {
        await SendClientDropInput(client, entMan, timing, cursorEntity, BoundKeyState.Down);
        await SendClientDropInput(client, entMan, timing, cursorEntity, BoundKeyState.Up);
    }

    private static async Task SendClientPullInput(
        RobustIntegrationTest.ClientIntegrationInstance client,
        IEntityManager entMan,
        IGameTiming timing,
        EntityUid cursorEntity,
        BoundKeyState state)
    {
        var inputManager = client.ResolveDependency<IInputManager>();
        var funcId = inputManager.NetworkBindMap.KeyFunctionID(ContentKeyFunctions.TryPullObject);
        var transform = entMan.GetComponent<TransformComponent>(cursorEntity);
        var inputSystem = client.System<Robust.Client.GameObjects.InputSystem>();
        var message = new ClientFullInputCmdMessage(timing.CurTick, timing.TickFraction, funcId)
        {
            State = state,
            Coordinates = transform.Coordinates,
            Uid = cursorEntity,
        };

        await client.WaitPost(() => inputSystem.HandleInputCommand(client.Session!, ContentKeyFunctions.TryPullObject, message));
    }

    private static async Task SendClientDropInput(
        RobustIntegrationTest.ClientIntegrationInstance client,
        IEntityManager entMan,
        IGameTiming timing,
        EntityUid cursorEntity,
        BoundKeyState state)
    {
        var inputManager = client.ResolveDependency<IInputManager>();
        var funcId = inputManager.NetworkBindMap.KeyFunctionID(ContentKeyFunctions.Drop);
        var transform = entMan.GetComponent<TransformComponent>(cursorEntity);
        var inputSystem = client.System<Robust.Client.GameObjects.InputSystem>();
        var message = new ClientFullInputCmdMessage(timing.CurTick, timing.TickFraction, funcId)
        {
            State = state,
            Coordinates = transform.Coordinates,
            Uid = cursorEntity,
        };

        await client.WaitPost(() => inputSystem.HandleInputCommand(client.Session!, ContentKeyFunctions.Drop, message));
    }

    private static void SetSoftEscapeAvailableAt(ActiveScpHoldableComponent held, TimeSpan value)
    {
        SoftEscapeAvailableAtField.SetValue(held, value);
    }

    private static void SetHolderTarget(ActiveScpHolderComponent holder, EntityUid? value)
    {
        ActiveScpHolderTargetField.SetValue(holder, value);
    }

    private static void RaiseMoveInput(IEntityManager entMan, EntityUid uid)
    {
        var mover = entMan.GetComponent<InputMoverComponent>(uid);
        var move = new MoveInputEvent((uid, mover), MoveButtons.None, Direction.East, true);
        entMan.EventBus.RaiseLocalEvent(uid, ref move);
    }

    private static void StartHold(IEntityManager entMan, SharedScpHoldingSystem holding, EntityUid holder, EntityUid target)
    {
        var holdComp = entMan.GetComponent<ScpHolderComponent>(holder);
        Assert.That(holding.TryToggleHold((holder, holdComp), target), Is.True);
    }

    private static EntityUid ToClientEntity(IEntityManager serverEntMan, IEntityManager clientEntMan, EntityUid serverEntity)
    {
        return clientEntMan.GetEntity(serverEntMan.GetNetEntity(serverEntity));
    }
}

[RegisterComponent]
public sealed partial class ScpHoldAttemptCancelTestComponent : Component;

public sealed class ScpHoldAttemptListenerSystem : TestListenerSystem<ScpHoldAttemptEvent>;

public sealed class ScpHoldBreakoutListenerSystem : TestListenerSystem<ScpHoldBreakoutEvent>;

public sealed class ScpHoldAttemptCancelSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<ScpHoldAttemptCancelTestComponent, ScpHoldAttemptEvent>(OnAttempt);
    }

    private static void OnAttempt(Entity<ScpHoldAttemptCancelTestComponent> ent, ref ScpHoldAttemptEvent args)
    {
        args.Cancelled = true;
    }
}
