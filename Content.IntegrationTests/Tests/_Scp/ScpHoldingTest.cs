#nullable enable
using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Shared.Alert;
using Content.Server.Body.Systems;
using Content.Shared._Scp.Holding;
using Content.Shared.Actions;
using Content.Shared.Actions.Components;
using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Components;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.StatusEffectNew;
using Robust.Server.Console;
using Robust.Client.Physics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Scp;

[TestFixture]
public sealed class ScpHoldingTest
{
    private const string HolderPrototype = "ScpHoldingTestHolder";
    private static readonly FieldInfo SoftEscapeAvailableAtField =
        typeof(ScpHeldComponent).GetField(nameof(ScpHeldComponent.SoftEscapeAvailableAt))!;

    [TestPrototypes]
    private const string Prototypes = """
- type: entity
  id: ScpHoldingTestHolder
  parent: MobHuman
  components:
  - type: ScpHold
""";

    [Test]
    public async Task SoftHoldBreakoutByMovementAndActionRespectsCooldown()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var alerts = server.System<AlertsSystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        var statusEffects = server.System<StatusEffectsSystem>();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var actions = server.System<SharedActionsSystem>();
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
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
                Assert.That(held.BreakoutActionEntity, Is.Not.Null);
                Assert.That(statusEffects.HasStatusEffect(target, "StatusEffectScpHeld"), Is.True);
                Assert.That(alerts.IsShowingAlert(target, "ScpHoldGrabbed"), Is.True);
            });
        });

        await server.WaitPost(() => RaiseMoveInput(entMan, target));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ScpHeldComponent>(target), Is.False);
        });

        await server.WaitPost(() =>
        {
            StartHold(entMan, holding, holder, target);
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            SetSoftEscapeAvailableAt(held, timing.CurTime + TimeSpan.FromSeconds(1));
        });

        await server.WaitPost(() => RaiseMoveInput(entMan, target));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ScpHeldComponent>(target), Is.True);
        });

        await server.WaitPost(() =>
        {
            var alert = proto.Index<AlertPrototype>("ScpHoldGrabbed");
            Assert.That(alerts.ActivateAlert(target, alert), Is.True);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ScpHeldComponent>(target), Is.True);
        });

        await server.WaitPost(() =>
        {
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            var action = actions.GetAction(held.BreakoutActionEntity);
            var targetActions = entMan.GetComponent<ActionsComponent>(target);

            Assert.That(action, Is.Not.Null);
            actions.PerformAction((target, targetActions), action!.Value);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ScpHeldComponent>(target), Is.True);
        });

        await server.WaitPost(() =>
        {
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            SetSoftEscapeAvailableAt(held, timing.CurTime);
            var alert = proto.Index<AlertPrototype>("ScpHoldGrabbed");
            Assert.That(alerts.ActivateAlert(target, alert), Is.True);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ScpHeldComponent>(target), Is.False);
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
            var held = sEntMan.GetComponent<ScpHeldComponent>(target);
            var holderState = sEntMan.GetComponent<ScpHolderComponent>(holder);
            var holderSpeed = sEntMan.GetComponent<MovementSpeedModifierComponent>(holder);
            var holderHands = sEntMan.GetComponent<HandsComponent>(holder);
            var puller = sEntMan.GetComponent<PullerComponent>(holder);
            var pullable = sEntMan.GetComponent<PullableComponent>(target);
            var move = new UpdateCanMoveEvent(target);
            var distance = GetDistance(sTransform, holder, target);
            var contacts = sPhysics.GetContactingEntities(holder);

            sEntMan.EventBus.RaiseLocalEvent(target, move);

            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.PrimaryHolder, Is.EqualTo(holder));
                Assert.That(held.Holders, Has.Count.EqualTo(1));
                Assert.That(move.Cancelled, Is.False);
                Assert.That(distance, Is.GreaterThan(0.18f));
                Assert.That(distance, Is.LessThan(0.7f));
                Assert.That(contacts, Does.Not.Contain(target));
                Assert.That(holderState.SlowdownEnabled, Is.True);
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

            var held = cEntMan.GetComponent<ScpHeldComponent>(clientTarget);
            var holderState = cEntMan.GetComponent<ScpHolderComponent>(clientHolder);
            var holderSpeed = cEntMan.GetComponent<MovementSpeedModifierComponent>(clientHolder);
            var holderHands = cEntMan.GetComponent<HandsComponent>(clientHolder);
            var puller = cEntMan.GetComponent<PullerComponent>(clientHolder);
            var pullable = cEntMan.GetComponent<PullableComponent>(clientTarget);
            var distance = GetDistance(cTransform, clientHolder, clientTarget);
            var contacts = cPhysics.GetContactingEntities(clientHolder);

            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.PrimaryHolder, Is.EqualTo(clientHolder));
                Assert.That(held.Holders, Has.Count.EqualTo(1));
                Assert.That(distance, Is.GreaterThan(0.18f));
                Assert.That(distance, Is.LessThan(0.55f));
                Assert.That(contacts, Does.Not.Contain(clientTarget));
                Assert.That(holderState.SlowdownEnabled, Is.True);
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
            Assert.That(sEntMan.HasComponent<ScpHeldComponent>(target), Is.True);
        });

        await client.WaitAssertion(() =>
        {
            Assert.That(cEntMan.HasComponent<ScpHeldComponent>(clientTarget), Is.True);
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
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            var hands = entMan.GetComponent<HandsComponent>(target);
            var holderOnePuller = entMan.GetComponent<PullerComponent>(holderOne);
            var holderTwoPuller = entMan.GetComponent<PullerComponent>(holderTwo);
            var pullable = entMan.GetComponent<PullableComponent>(target);
            var move = new UpdateCanMoveEvent(target);
            entMan.EventBus.RaiseLocalEvent(target, move);

            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.True);
                Assert.That(held.RequiredHolderCount, Is.EqualTo(2));
                Assert.That(held.Holders, Has.Count.EqualTo(2));
                Assert.That(move.Cancelled, Is.True);
                Assert.That(CountBlockingVirtualHands(entMan, handsSystem, target, hands), Is.EqualTo(hands.SortedHands.Count));
                Assert.That(holderOnePuller.Pulling, Is.Null);
                Assert.That(holderTwoPuller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
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
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(held.RequiredHolderCount, Is.EqualTo(3));
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(2));
            });
        });

        await server.WaitPost(() =>
        {
            StartHold(entMan, holding, holderThree, target);
        });

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            var hands = entMan.GetComponent<HandsComponent>(target);

            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.True);
                Assert.That(held.RequiredHolderCount, Is.EqualTo(3));
                Assert.That(hands.SortedHands.Count, Is.EqualTo(3));
                Assert.That(CountBlockingVirtualHands(entMan, handsSystem, target, hands), Is.EqualTo(3));
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
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            var hands = entMan.GetComponent<HandsComponent>(target);

            Assert.Multiple(() =>
            {
                Assert.That(held.RequiredHolderCount, Is.EqualTo(2));
                Assert.That(held.FullHold, Is.True);
                Assert.That(hands.SortedHands.Count, Is.EqualTo(2));
                Assert.That(CountBlockingVirtualHands(entMan, handsSystem, target, hands), Is.EqualTo(2));
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
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.True);
                Assert.That(held.BreakoutDoAfterId, Is.Null);
            });
        });

        await server.WaitRunTicks(GetTickCount(timing, TimeSpan.FromSeconds(10)));

        await server.WaitPost(() => RaiseMoveInput(entMan, target));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            Assert.That(held.BreakoutDoAfterId, Is.Not.Null);
        });

        await server.WaitRunTicks(GetTickCount(timing, TimeSpan.FromSeconds(5)));

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<ScpHeldComponent>(target), Is.False);
                Assert.That(entMan.HasComponent<ScpHoldImmuneComponent>(target), Is.True);
            });
        });

        await server.WaitPost(() =>
        {
            var holdComp = entMan.GetComponent<ScpHoldComponent>(holderOne);
            Assert.That(holding.TryToggleHold((holderOne, holdComp), target), Is.False);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ScpHeldComponent>(target), Is.False);
        });

        await server.WaitRunTicks(GetTickCount(timing, TimeSpan.FromSeconds(5)));

        await server.WaitPost(() =>
        {
            var holdComp = entMan.GetComponent<ScpHoldComponent>(holderOne);
            Assert.That(holding.TryToggleHold((holderOne, holdComp), target), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ScpHeldComponent>(target), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FullBreakoutByActionStartsAndCompletes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Fresh = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var actions = server.System<SharedActionsSystem>();
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
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            var action = actions.GetAction(held.BreakoutActionEntity);
            var targetActions = entMan.GetComponent<ActionsComponent>(target);

            Assert.That(action, Is.Not.Null);
            actions.PerformAction((target, targetActions), action!.Value);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            Assert.That(held.BreakoutDoAfterId, Is.Not.Null);
        });

        await server.WaitRunTicks(GetTickCount(timing, TimeSpan.FromSeconds(5)));

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ScpHeldComponent>(target), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClientHoldActionPredictsSoftHoldBeforeServerAck()
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
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            sEntMan.AddComponent<ScpHoldComponent>(serverPlayer);
            target = sEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.1f, 0f)));
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        EntityUid holdAction = default;
        await server.WaitAssertion(() =>
        {
            var hold = sEntMan.GetComponent<ScpHoldComponent>(serverPlayer);
            Assert.That(hold.ActionEntity, Is.Not.Null);
            holdAction = hold.ActionEntity!.Value;
        });

        var clientPlayer = EntityUid.Invalid;
        var clientTarget = EntityUid.Invalid;
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            clientTarget = ToClientEntity(sEntMan, cEntMan, target);

            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ScpHoldComponent>(clientPlayer), Is.True);
                Assert.That(cEntMan.EntityExists(clientTarget), Is.True);
            });
        });

        var holdActionNet = sEntMan.GetNetEntity(holdAction);
        var targetNet = sEntMan.GetNetEntity(target);

        await client.WaitPost(() =>
        {
            cEntMan.RaisePredictiveEvent(new RequestPerformActionEvent(holdActionNet, targetNet));

            var held = cEntMan.GetComponent<ScpHeldComponent>(clientTarget);
            var holderHands = cEntMan.GetComponent<HandsComponent>(clientPlayer);
            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
                Assert.That(cEntMan.HasComponent<ScpHolderComponent>(clientPlayer), Is.True);
                Assert.That(CountHolderHandBlockers(cEntMan, cHandsSystem, clientPlayer, clientTarget, holderHands), Is.EqualTo(1));
                Assert.That(CountHolderTargetVirtualItems(cEntMan, cHandsSystem, clientPlayer, clientTarget, holderHands), Is.EqualTo(1));
                Assert.That(cStatusEffects.HasStatusEffect(clientTarget, "StatusEffectScpHeld"), Is.True);
            });
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(sEntMan.HasComponent<ScpHeldComponent>(target), Is.False);
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
            var held = sEntMan.GetComponent<ScpHeldComponent>(target);
            var holderHands = sEntMan.GetComponent<HandsComponent>(serverPlayer);
            var puller = sEntMan.GetComponent<PullerComponent>(serverPlayer);
            var pullable = sEntMan.GetComponent<PullableComponent>(target);
            var distance = GetDistance(sTransform, serverPlayer, target);
            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
                Assert.That(held.PrimaryHolder, Is.EqualTo(serverPlayer));
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
            var held = cEntMan.GetComponent<ScpHeldComponent>(clientTarget);
            var holderHands = cEntMan.GetComponent<HandsComponent>(clientPlayer);
            var puller = cEntMan.GetComponent<PullerComponent>(clientPlayer);
            var pullable = cEntMan.GetComponent<PullableComponent>(clientTarget);
            var distance = GetDistance(cTransform, clientPlayer, clientTarget);
            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
                Assert.That(held.PrimaryHolder, Is.EqualTo(clientPlayer));
                Assert.That(distance, Is.GreaterThan(0.18f));
                Assert.That(distance, Is.LessThan(0.55f));
                Assert.That(puller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
                Assert.That(CountHolderHandBlockers(cEntMan, cHandsSystem, clientPlayer, clientTarget, holderHands), Is.EqualTo(1));
                Assert.That(CountHolderTargetVirtualItems(cEntMan, cHandsSystem, clientPlayer, clientTarget, holderHands), Is.EqualTo(1));
                Assert.That(cStatusEffects.HasStatusEffect(clientTarget, "StatusEffectScpHeld"), Is.True);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClientSecondHoldActionPredictsFullHoldBeforeServerAck()
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
        var cHandsSystem = client.System<SharedHandsSystem>();
        var holding = server.System<SharedScpHoldingSystem>();
        var map = await pair.CreateTestMap();

        var serverPlayer = pair.Player!.AttachedEntity!.Value;
        EntityUid holderOne = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            sTransform.SetCoordinates(serverPlayer, map.GridCoords);
            sEntMan.AddComponent<ScpHoldComponent>(serverPlayer);

            holderOne = sEntMan.SpawnEntity(HolderPrototype, map.GridCoords);
            target = sEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0f)));
            StartHold(sEntMan, holding, holderOne, target);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        EntityUid holdAction = default;
        await server.WaitAssertion(() =>
        {
            var hold = sEntMan.GetComponent<ScpHoldComponent>(serverPlayer);
            Assert.That(hold.ActionEntity, Is.Not.Null);
            holdAction = hold.ActionEntity!.Value;

            var held = sEntMan.GetComponent<ScpHeldComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
            });
        });

        var clientPlayer = EntityUid.Invalid;
        var clientTarget = EntityUid.Invalid;
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            clientTarget = ToClientEntity(sEntMan, cEntMan, target);

            var held = cEntMan.GetComponent<ScpHeldComponent>(clientTarget);
            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
            });
        });

        var holdActionNet = sEntMan.GetNetEntity(holdAction);
        var targetNet = sEntMan.GetNetEntity(target);

        await client.WaitPost(() =>
        {
            cEntMan.RaisePredictiveEvent(new RequestPerformActionEvent(holdActionNet, targetNet));

            var held = cEntMan.GetComponent<ScpHeldComponent>(clientTarget);
            var hands = cEntMan.GetComponent<HandsComponent>(clientTarget);

            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.True);
                Assert.That(held.Holders, Has.Count.EqualTo(2));
                Assert.That(CountBlockingVirtualHands(cEntMan, cHandsSystem, clientTarget, hands), Is.EqualTo(hands.SortedHands.Count));
            });
        });

        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ScpHeldComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
            });
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ScpHeldComponent>(target);
            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.True);
                Assert.That(held.Holders, Has.Count.EqualTo(2));
            });
        });

        await client.WaitAssertion(() =>
        {
            var held = cEntMan.GetComponent<ScpHeldComponent>(clientTarget);
            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.True);
                Assert.That(held.Holders, Has.Count.EqualTo(2));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClientPrimaryReassignmentKeepsCustomDragAndReconcilesCleanly()
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
            sEntMan.AddComponent<ScpHoldComponent>(serverPlayer);

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
            var held = sEntMan.GetComponent<ScpHeldComponent>(target);
            var serverPlayerPuller = sEntMan.GetComponent<PullerComponent>(serverPlayer);
            var holderTwoPuller = sEntMan.GetComponent<PullerComponent>(holderTwo);
            var pullable = sEntMan.GetComponent<PullableComponent>(target);
            var distance = GetDistance(sTransform, serverPlayer, target);
            var contacts = sPhysics.GetContactingEntities(serverPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(2));
                Assert.That(held.PrimaryHolder, Is.EqualTo(serverPlayer));
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

            var held = cEntMan.GetComponent<ScpHeldComponent>(clientTarget);
            var playerPuller = cEntMan.GetComponent<PullerComponent>(clientPlayer);
            var holderTwoPuller = cEntMan.GetComponent<PullerComponent>(clientHolderTwo);
            var pullable = cEntMan.GetComponent<PullableComponent>(clientTarget);
            var distance = GetDistance(cTransform, clientPlayer, clientTarget);
            var contacts = cPhysics.GetContactingEntities(clientPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(2));
                Assert.That(held.PrimaryHolder, Is.EqualTo(clientPlayer));
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
            var holdComp = sEntMan.GetComponent<ScpHoldComponent>(serverPlayer);
            Assert.That(holding.TryToggleHold((serverPlayer, holdComp), target), Is.True);
        });

        await pair.RunTicksSync(5);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ScpHeldComponent>(target);
            var pullable = sEntMan.GetComponent<PullableComponent>(target);
            var holderTwoPuller = sEntMan.GetComponent<PullerComponent>(holderTwo);
            var distance = GetDistance(sTransform, holderTwo, target);
            var contacts = sPhysics.GetContactingEntities(holderTwo);

            Assert.Multiple(() =>
            {
                Assert.That(held.Holders, Has.Count.EqualTo(1));
                Assert.That(held.PrimaryHolder, Is.EqualTo(holderTwo));
                Assert.That(distance, Is.GreaterThan(0.18f));
                Assert.That(distance, Is.LessThan(0.55f));
                Assert.That(contacts, Does.Not.Contain(target));
                Assert.That(holderTwoPuller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
            });
        });

        await client.WaitAssertion(() =>
        {
            var held = cEntMan.GetComponent<ScpHeldComponent>(clientTarget);
            var pullable = cEntMan.GetComponent<PullableComponent>(clientTarget);
            var holderTwoPuller = cEntMan.GetComponent<PullerComponent>(clientHolderTwo);
            var distance = GetDistance(cTransform, clientHolderTwo, clientTarget);
            var contacts = cPhysics.GetContactingEntities(clientHolderTwo);

            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
                Assert.That(held.PrimaryHolder, Is.EqualTo(clientHolderTwo));
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
    public async Task ClientFullBreakoutActionPredictsDoAfterAndReconciles()
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

        EntityUid breakoutAction = default;
        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ScpHeldComponent>(serverPlayer);
            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.True);
                Assert.That(held.BreakoutActionEntity, Is.Not.Null);
            });

            breakoutAction = held.BreakoutActionEntity!.Value;
        });

        var clientPlayer = EntityUid.Invalid;
        await client.WaitAssertion(() =>
        {
            clientPlayer = client.AttachedEntity!.Value;
            var held = cEntMan.GetComponent<ScpHeldComponent>(clientPlayer);
            Assert.That(held.FullHold, Is.True);
        });

        var breakoutActionNet = sEntMan.GetNetEntity(breakoutAction);

        await client.WaitPost(() =>
        {
            cEntMan.RaisePredictiveEvent(new RequestPerformActionEvent(breakoutActionNet));

            var held = cEntMan.GetComponent<ScpHeldComponent>(clientPlayer);
            Assert.Multiple(() =>
            {
                Assert.That(held.BreakoutDoAfterId, Is.Not.Null);
                Assert.That(cEntMan.HasComponent<ScpHoldImmuneComponent>(clientPlayer), Is.False);
            });
        });

        await server.WaitAssertion(() =>
        {
            var held = sEntMan.GetComponent<ScpHeldComponent>(serverPlayer);
            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.True);
                Assert.That(held.BreakoutDoAfterId, Is.Null);
            });
        });

        await pair.RunTicksSync(GetTickCount(timing, TimeSpan.FromSeconds(5)) + 5);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(sEntMan.HasComponent<ScpHeldComponent>(serverPlayer), Is.False);
                Assert.That(sEntMan.HasComponent<ScpHoldImmuneComponent>(serverPlayer), Is.True);
            });
        });

        await client.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ScpHeldComponent>(clientPlayer), Is.False);
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
                Assert.That(cEntMan.HasComponent<ScpHeldComponent>(clientPlayer), Is.True);
                Assert.That(cAlerts.IsShowingAlert(clientPlayer, "ScpHoldGrabbed"), Is.True);
            });
        });

        await client.WaitPost(() =>
        {
            cEntMan.RaisePredictiveEvent(new ClickAlertEvent("ScpHoldGrabbed"));

            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ScpHeldComponent>(clientPlayer), Is.False);
                Assert.That(cAlerts.IsShowingAlert(clientPlayer, "ScpHoldGrabbed"), Is.False);
            });
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(sEntMan.HasComponent<ScpHeldComponent>(serverPlayer), Is.True);
            Assert.That(sAlerts.IsShowingAlert(serverPlayer, "ScpHoldGrabbed"), Is.True);
        });

        await pair.RunTicksSync(10);
        await pair.SyncTicks(targetDelta: 1);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEntMan.HasComponent<ScpHeldComponent>(serverPlayer), Is.False);
            Assert.That(sAlerts.IsShowingAlert(serverPlayer, "ScpHoldGrabbed"), Is.False);
        });

        await client.WaitAssertion(() =>
        {
            Assert.That(cEntMan.HasComponent<ScpHeldComponent>(clientPlayer), Is.False);
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
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            var holderOnePuller = entMan.GetComponent<PullerComponent>(holderOne);
            var holderTwoPuller = entMan.GetComponent<PullerComponent>(holderTwo);
            var pullable = entMan.GetComponent<PullableComponent>(target);

            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.PrimaryHolder, Is.EqualTo(holderOne));
                Assert.That(holderOnePuller.Pulling, Is.Null);
                Assert.That(holderTwoPuller.Pulling, Is.Null);
                Assert.That(pullable.Puller, Is.Null);
            });
        });

        await server.WaitPost(() =>
        {
            var holderComp = entMan.GetComponent<ScpHoldComponent>(holderOne);
            Assert.That(holding.TryToggleHold((holderOne, holderComp), target), Is.True);
        });
        await server.WaitRunTicks(4);

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ScpHeldComponent>(target);
            var holderOnePuller = entMan.GetComponent<PullerComponent>(holderOne);
            var holderTwoPuller = entMan.GetComponent<PullerComponent>(holderTwo);
            var pullable = entMan.GetComponent<PullableComponent>(target);

            Assert.Multiple(() =>
            {
                Assert.That(held.FullHold, Is.False);
                Assert.That(held.Holders, Has.Count.EqualTo(1));
                Assert.That(held.PrimaryHolder, Is.EqualTo(holderTwo));
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
            Assert.That(entMan.HasComponent<ScpHeldComponent>(target), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    private static int CountBlockingVirtualHands(IEntityManager entMan, SharedHandsSystem handsSystem, EntityUid uid, HandsComponent hands)
    {
        return handsSystem.EnumerateHeld((uid, hands)).Count(item =>
            entMan.TryGetComponent(item, out VirtualItemComponent? virtualItem) &&
            virtualItem.BlockingEntity == uid &&
            entMan.HasComponent<UnremoveableComponent>(item));
    }

    private static int CountHolderHandBlockers(IEntityManager entMan, SharedHandsSystem handsSystem, EntityUid holder, EntityUid target, HandsComponent hands)
    {
        return handsSystem.EnumerateHeld((holder, hands)).Count(item =>
            entMan.TryGetComponent(item, out VirtualItemComponent? virtualItem) &&
            entMan.TryGetComponent(item, out ScpHoldHandBlockerComponent? blocker) &&
            blocker.Target == target &&
            virtualItem.BlockingEntity == target &&
            entMan.HasComponent<UnremoveableComponent>(item));
    }

    private static int CountHolderTargetVirtualItems(IEntityManager entMan, SharedHandsSystem handsSystem, EntityUid holder, EntityUid target, HandsComponent hands)
    {
        return handsSystem.EnumerateHeld((holder, hands)).Count(item =>
            entMan.TryGetComponent(item, out VirtualItemComponent? virtualItem) &&
            virtualItem.BlockingEntity == target);
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
        return Math.Max(1, (int) Math.Ceiling(duration.TotalSeconds / timing.TickPeriod.TotalSeconds) + 1);
    }

    private static void SetSoftEscapeAvailableAt(ScpHeldComponent held, TimeSpan value)
    {
        SoftEscapeAvailableAtField.SetValue(held, value);
    }

    private static void RaiseMoveInput(IEntityManager entMan, EntityUid uid)
    {
        var mover = entMan.GetComponent<InputMoverComponent>(uid);
        var move = new MoveInputEvent((uid, mover), MoveButtons.None, Direction.East, true);
        entMan.EventBus.RaiseLocalEvent(uid, ref move);
    }

    private static void StartHold(IEntityManager entMan, SharedScpHoldingSystem holding, EntityUid holder, EntityUid target)
    {
        var holdComp = entMan.GetComponent<ScpHoldComponent>(holder);
        Assert.That(holding.TryToggleHold((holder, holdComp), target), Is.True);
    }

    private static EntityUid ToClientEntity(IEntityManager serverEntMan, IEntityManager clientEntMan, EntityUid serverEntity)
    {
        return clientEntMan.GetEntity(serverEntMan.GetNetEntity(serverEntity));
    }
}
