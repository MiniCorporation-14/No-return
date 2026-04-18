#nullable enable
using System.Collections.Generic;
using System.Numerics;
using Content.IntegrationTests.Tests.Movement;
using Content.Server._Scp.Holding;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory.VirtualItem;
using Content.Server.Movement.Components;
using Content.Shared._Scp.Holding.Components;
using Content.Shared.Input;
using Content.Shared.Interaction;
using Robust.Server.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;

namespace Content.IntegrationTests.Tests._Scp;

[TestFixture]
public sealed class ScpHoldingCursorMoveTest : MovementTest
{
    private const float PositionTolerance = 0.15f;

    private IServerConsoleHost _consoleHost = default!;
    private SharedHandsSystem _hands = default!;
    private ScpHoldingSystem _holding = default!;
    private readonly List<EntityUid> _spawnedServerHolders = [];

    [SetUp]
    public override async Task Setup()
    {
        await base.Setup();

        _consoleHost = Server.ResolveDependency<IServerConsoleHost>();
        _hands = Server.System<SharedHandsSystem>();
        _holding = Server.System<ScpHoldingSystem>();

        await Server.WaitPost(() =>
        {
            SEntMan.EnsureComponent<ScpHolderComponent>(SPlayer);
        });

        await RunTicks(1);
    }

    [TearDown]
    public async Task TearDownScpHolding()
    {
        await Server.WaitPost(() =>
        {
            ReleaseHoldIfActive(SPlayer);

            foreach (var holderUid in _spawnedServerHolders)
            {
                ReleaseHoldIfActive(holderUid);
            }

            foreach (var holderUid in _spawnedServerHolders)
            {
                if (SEntMan.EntityExists(holderUid))
                    SEntMan.DeleteEntity(holderUid);
            }
        });

        await RunTicks(5);
        _spawnedServerHolders.Clear();
    }

    [Test]
    public async Task SoftHoldCursorMoveUsesClampAndBridge()
    {
        await SpawnTarget("MobHuman");
        await PrepareTargetForHolding(STarget!.Value);
        await StartPlayerHold();

        var holdable = SEntMan.GetComponent<ScpHoldableComponent>(STarget.Value);
        var maintenanceRange = GetMaintenanceRange(holdable);
        var playerPosition = Transform.GetWorldPosition(SPlayer);
        var farTarget = SEntMan.GetCoordinates(PlayerCoords).Offset(new Vector2(5f, 0f));

        await PressKey(ContentKeyFunctions.MovePulledObject, coordinates: SEntMan.GetNetCoordinates(farTarget));
        await RunTicks(20);

        await Server.WaitAssertion(() =>
        {
            var heldPosition = Transform.GetWorldPosition(STarget.Value);
            Assert.Multiple(() =>
            {
                Assert.That(heldPosition.X, Is.EqualTo(playerPosition.X + maintenanceRange).Within(PositionTolerance));
                Assert.That(SEntMan.HasComponent<ActiveScpHoldableComponent>(STarget.Value), Is.True);
                Assert.That(SEntMan.HasComponent<ActiveStateScpHoldableFullHoldComponent>(STarget.Value), Is.False);
                Assert.That(SEntMan.HasComponent<ActiveStateScpHoldableCursorMoveComponent>(STarget.Value), Is.True);
                Assert.That(SEntMan.HasComponent<PullMovingComponent>(STarget.Value), Is.False);
            });
        });
    }

    [Test]
    public async Task MultiHolderCursorMoveUsesLastValidCommand()
    {
        await SpawnTarget("MobHuman");
        await PrepareTargetForHolding(STarget!.Value);
        await AddHand(Target!.Value);

        var secondHolder = await SpawnHolder(1.8f);
        await StartPlayerHold();
        await StartServerHold(secondHolder, STarget.Value);

        await Server.WaitPost(() =>
        {
            var held = SEntMan.GetComponent<ActiveScpHoldableComponent>(STarget.Value);
            Assert.Multiple(() =>
            {
                Assert.That(held.Holders, Has.Count.EqualTo(2));
                Assert.That(held.Holders[0], Is.EqualTo(SPlayer));
                Assert.That(held.Holders[1], Is.EqualTo(secondHolder));
                Assert.That(SEntMan.HasComponent<ActiveStateScpHoldableFullHoldComponent>(STarget.Value), Is.False);
            });
        });

        var firstPoint = SEntMan.GetCoordinates(PlayerCoords).Offset(new Vector2(0.5f, 0f));
        var secondPoint = SEntMan.GetCoordinates(PlayerCoords).Offset(new Vector2(1.5f, 0f));

        await Server.WaitPost(() =>
        {
            Assert.That(_holding.TryMoveHeldToCursor(SPlayer, firstPoint), Is.True);
        });
        await RunTicks(5);

        await Server.WaitPost(() =>
        {
            Assert.That(_holding.TryMoveHeldToCursor(secondHolder, secondPoint), Is.True);
        });
        await RunTicks(20);

        await Server.WaitAssertion(() =>
        {
            var heldPosition = Transform.GetWorldPosition(STarget.Value);
            var cursorMove = SEntMan.GetComponent<ActiveStateScpHoldableCursorMoveComponent>(STarget.Value);

            Assert.Multiple(() =>
            {
                Assert.That(heldPosition.X, Is.EqualTo(2.0f).Within(PositionTolerance));
                Assert.That(cursorMove.Holder, Is.EqualTo(secondHolder));
                Assert.That(SEntMan.HasComponent<ActiveStateScpHoldableFullHoldComponent>(STarget.Value), Is.False);
            });
        });
    }

    [Test]
    public async Task FullHoldIgnoresMovePulledObject()
    {
        await SpawnTarget("MobHuman");
        await PrepareTargetForHolding(STarget!.Value);

        var secondHolder = await SpawnHolder(1.8f);
        await StartPlayerHold();
        await StartServerHold(secondHolder, STarget.Value);

        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.HasComponent<ActiveStateScpHoldableFullHoldComponent>(STarget!.Value), Is.True);
        });

        var initialPosition = Transform.GetWorldPosition(STarget.Value);
        var farTarget = SEntMan.GetCoordinates(PlayerCoords).Offset(new Vector2(5f, 0f));

        await PressKey(ContentKeyFunctions.MovePulledObject, coordinates: SEntMan.GetNetCoordinates(farTarget));
        await RunTicks(20);

        await Server.WaitAssertion(() =>
        {
            var heldPosition = Transform.GetWorldPosition(STarget.Value);
            Assert.Multiple(() =>
            {
                Assert.That(Vector2.Distance(heldPosition, initialPosition), Is.LessThanOrEqualTo(0.05f));
                Assert.That(SEntMan.HasComponent<ActiveStateScpHoldableFullHoldComponent>(STarget.Value), Is.True);
                Assert.That(SEntMan.HasComponent<ActiveStateScpHoldableCursorMoveComponent>(STarget.Value), Is.False);
                Assert.That(SEntMan.HasComponent<PullMovingComponent>(STarget.Value), Is.False);
            });
        });
    }

    [Test]
    public async Task HolderMovementInvalidatesCursorMoveAndReturnsToSoftDrag()
    {
        await SpawnTarget("MobHuman");
        await PrepareTargetForHolding(STarget!.Value);
        await StartPlayerHold();

        var parkedTarget = SEntMan.GetCoordinates(PlayerCoords).Offset(new Vector2(1.5f, 0f));
        await PressKey(ContentKeyFunctions.MovePulledObject, coordinates: SEntMan.GetNetCoordinates(parkedTarget));
        await RunTicks(20);

        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.HasComponent<ActiveStateScpHoldableCursorMoveComponent>(STarget!.Value), Is.True);
            Assert.That(Transform.GetWorldPosition(STarget.Value).X, Is.EqualTo(2.0f).Within(PositionTolerance));
        });

        var parkedX = Transform.GetWorldPosition(STarget.Value).X;
        var maintenanceRange = GetMaintenanceRange(SEntMan.GetComponent<ScpHoldableComponent>(STarget.Value));

        await Move(DirectionFlag.West, 1f);
        await RunTicks(10);

        await Server.WaitAssertion(() =>
        {
            var heldPosition = Transform.GetWorldPosition(STarget!.Value);
            var playerPosition = Transform.GetWorldPosition(SPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(SEntMan.HasComponent<ActiveStateScpHoldableCursorMoveComponent>(STarget.Value), Is.False);
                Assert.That(heldPosition.X, Is.LessThan(parkedX - 0.25f));
                Assert.That((heldPosition - playerPosition).Length(), Is.LessThanOrEqualTo(maintenanceRange + 0.2f));
            });
        });
    }

    [Test]
    public async Task ClientCursorMoveWaitsForServerAndBecomesAuthoritative()
    {
        await SpawnTarget("MobHuman");
        await PrepareTargetForHolding(STarget!.Value);
        await StartPlayerHold();
        await RunTicks(10);

        await Client.WaitAssertion(() =>
        {
            Assert.That(CTarget, Is.Not.Null);
            Assert.That(CEntMan.HasComponent<ActiveScpHoldableComponent>(CTarget!.Value), Is.True);
        });

        var parkedTarget = SEntMan.GetCoordinates(PlayerCoords).Offset(new Vector2(1.5f, 0f));
        var parkedTargetNetCoords = SEntMan.GetNetCoordinates(parkedTarget);

        var initialServerPosition = Vector2.Zero;
        var initialClientPosition = Vector2.Zero;

        await Server.WaitPost(() =>
        {
            initialServerPosition = Transform.GetWorldPosition(STarget!.Value);
        });

        await Client.WaitPost(() =>
        {
            initialClientPosition = CEntMan.System<SharedTransformSystem>().GetWorldPosition(CTarget!.Value);
        });

        await SetKey(ContentKeyFunctions.MovePulledObject, BoundKeyState.Down, coordinates: parkedTargetNetCoords);
        await Client.WaitRunTicks(1);

        var firstTickClientPosition = Vector2.Zero;
        var authoritativeServerPosition = Vector2.Zero;
        var hasCursorMoveAfterFirstTick = false;

        await Client.WaitPost(() =>
        {
            firstTickClientPosition = CEntMan.System<SharedTransformSystem>().GetWorldPosition(CTarget!.Value);
            hasCursorMoveAfterFirstTick = CEntMan.HasComponent<ActiveStateScpHoldableCursorMoveComponent>(CTarget.Value);
        });

        await Server.WaitPost(() =>
        {
            authoritativeServerPosition = Transform.GetWorldPosition(STarget!.Value);
        });

        Assert.Multiple(() =>
        {
            Assert.That(hasCursorMoveAfterFirstTick, Is.False,
                $"Client created cursor-move state before the server processed input. initialClientX={initialClientPosition.X:F4}; firstTickClientX={firstTickClientPosition.X:F4}; initialServerX={initialServerPosition.X:F4}; serverX={authoritativeServerPosition.X:F4}");
            Assert.That(firstTickClientPosition.X, Is.EqualTo(initialClientPosition.X).Within(0.01f),
                $"Client moved the held target before the server processed input. initialClientX={initialClientPosition.X:F4}; firstTickClientX={firstTickClientPosition.X:F4}; initialServerX={initialServerPosition.X:F4}; serverX={authoritativeServerPosition.X:F4}");
            Assert.That(authoritativeServerPosition.X, Is.EqualTo(initialServerPosition.X).Within(0.001f),
                $"Server must remain unchanged until it processes the input. initialClientX={initialClientPosition.X:F4}; firstTickClientX={firstTickClientPosition.X:F4}; initialServerX={initialServerPosition.X:F4}; serverX={authoritativeServerPosition.X:F4}");
        });

        await RunTicks(10);

        await Client.WaitAssertion(() =>
        {
            var clientPosition = CEntMan.System<SharedTransformSystem>().GetWorldPosition(CTarget!.Value);
            var clientPhysicsPredict = CEntMan.GetComponent<PhysicsComponent>(CTarget.Value).Predict;

            Assert.Multiple(() =>
            {
                Assert.That(CEntMan.HasComponent<ActiveStateScpHoldableCursorMoveComponent>(CTarget.Value), Is.True);
                Assert.That(clientPhysicsPredict, Is.False,
                    $"Held target must stay authoritative like PullMoving. initialClientX={initialClientPosition.X:F4}; currentClientX={clientPosition.X:F4}");
                Assert.That(clientPosition.X, Is.GreaterThan(initialClientPosition.X + 0.1f),
                    $"Held target did not start moving after the server processed cursor input. initialClientX={initialClientPosition.X:F4}; currentClientX={clientPosition.X:F4}");
            });
        });

        await SetKey(ContentKeyFunctions.MovePulledObject, BoundKeyState.Up, coordinates: parkedTargetNetCoords);
        await RunTicks(1);
    }

    [Test]
    public async Task ClientCursorMoveDoesNotSnapToCursorPoint()
    {
        await Client.WaitPost(() =>
        {
            Client.CfgMan.SetCVar("net.fakelagmin", 0.35f);
            Client.CfgMan.SetCVar("net.fakelagrand", 0f);
        });

        try
        {
            await SpawnTarget("MobHuman");
            await PrepareTargetForHolding(STarget!.Value);
            await StartPlayerHold();

            await Client.WaitAssertion(() =>
            {
                Assert.That(CTarget, Is.Not.Null);
                Assert.That(CEntMan.HasComponent<ActiveScpHoldableComponent>(CTarget!.Value), Is.True);
            });

            var parkedTarget = SEntMan.GetCoordinates(PlayerCoords).Offset(new Vector2(1.5f, 0f));
            var parkedTargetNetCoords = SEntMan.GetNetCoordinates(parkedTarget);
            var sampledPositions = new List<float>();

            await Client.WaitPost(() =>
            {
                sampledPositions.Add(CEntMan.System<SharedTransformSystem>().GetWorldPosition(CTarget!.Value).X);
            });

            await SetKey(ContentKeyFunctions.MovePulledObject, BoundKeyState.Down, coordinates: parkedTargetNetCoords);

            for (var i = 0; i < 12; i++)
            {
                await RunTicks(1);
                await Client.WaitPost(() =>
                {
                    sampledPositions.Add(CEntMan.System<SharedTransformSystem>().GetWorldPosition(CTarget!.Value).X);
                });
            }

            var maxDelta = 0f;
            for (var i = 1; i < sampledPositions.Count; i++)
            {
                maxDelta = MathF.Max(maxDelta, MathF.Abs(sampledPositions[i] - sampledPositions[i - 1]));
            }

            Assert.That(maxDelta, Is.LessThanOrEqualTo(0.35f),
                $"Held target snapped too far in a single client tick. positions=[{string.Join(", ", sampledPositions.ConvertAll(x => x.ToString("F4")))}]");

            await SetKey(ContentKeyFunctions.MovePulledObject, BoundKeyState.Up, coordinates: parkedTargetNetCoords);
            await RunTicks(1);
        }
        finally
        {
            await Client.WaitPost(() =>
            {
                Client.CfgMan.SetCVar("net.fakelagmin", 0f);
                Client.CfgMan.SetCVar("net.fakelagrand", 0f);
            });
        }
    }

    [Test]
    public async Task HolderHandsRequiredTwoDoesNotImmediatelyReleaseHold()
    {
        await SpawnTarget("MobHuman");
        await PrepareTargetForHolding(STarget!.Value);

        await Server.WaitPost(() =>
        {
            var holdable = SEntMan.GetComponent<ScpHoldableComponent>(STarget.Value);
            holdable.HolderHandsRequired = 2;
            SEntMan.Dirty(STarget.Value, holdable);
        });

        await RunTicks(1);
        await StartServerHold(SPlayer, STarget.Value);

        await Server.WaitAssertion(() =>
        {
            var activeHolder = SEntMan.GetComponent<ActiveScpHolderComponent>(SPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(activeHolder.Target, Is.EqualTo(STarget));
                Assert.That(SEntMan.HasComponent<ActiveScpHoldableComponent>(STarget.Value), Is.True);
                Assert.That(CountHolderBlockers(SPlayer, STarget.Value), Is.EqualTo(2));
                Assert.That(_hands.CountFreeHands(SPlayer), Is.EqualTo(0));
            });
        });
    }

    private async Task PrepareTargetForHolding(EntityUid targetUid)
    {
        await Server.WaitPost(() =>
        {
            SEntMan.EnsureComponent<ScpHoldableComponent>(targetUid);
        });

        await RunTicks(1);
    }

    private async Task<EntityUid> SpawnHolder(float playerOffsetX)
    {
        var coords = SEntMan.GetCoordinates(PlayerCoords).Offset(new Vector2(playerOffsetX, 0f));
        var holder = await SpawnEntity("MobHuman", coords);

        await Server.WaitPost(() =>
        {
            SEntMan.EnsureComponent<ScpHolderComponent>(holder);
        });

        await RunTicks(1);
        _spawnedServerHolders.Add(holder);
        return holder;
    }

    private async Task AddHand(NetEntity target)
    {
        await Server.WaitPost(() =>
        {
            _consoleHost.ExecuteCommand(null, $"addhand {target}");
        });

        await RunTicks(1);
    }

    private async Task StartPlayerHold()
    {
        await PressKey(ContentKeyFunctions.TryPullObject);
        await RunTicks(5);

        await Server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(SEntMan.HasComponent<ActiveScpHolderComponent>(SPlayer), Is.True);
                Assert.That(SEntMan.GetComponent<ActiveScpHolderComponent>(SPlayer).Target, Is.EqualTo(STarget));
                Assert.That(SEntMan.HasComponent<ActiveScpHoldableComponent>(STarget!.Value), Is.True);
            });
        });
    }

    private async Task StartServerHold(EntityUid holderUid, EntityUid targetUid)
    {
        await Server.WaitPost(() =>
        {
            var holder = SEntMan.GetComponent<ScpHolderComponent>(holderUid);
            Assert.That(_holding.TryToggleHold((holderUid, holder), targetUid), Is.True);
        });

        await RunTicks(5);
    }

    private static float GetMaintenanceRange(ScpHoldableComponent holdable)
    {
        var desiredSoftDragDistance = Math.Clamp(
            holdable.HoldRange * holdable.SoftDragDistanceFactor,
            holdable.SoftDragMinimumDistance,
            holdable.SoftDragMaximumDistance);

        return MathF.Max(
            MathF.Max(holdable.HoldRange, SharedInteractionSystem.InteractionRange),
            desiredSoftDragDistance + holdable.SoftDragSnapTolerance);
    }

    private void ReleaseHoldIfActive(EntityUid holderUid)
    {
        if (!SEntMan.EntityExists(holderUid) ||
            !SEntMan.TryGetComponent(holderUid, out ScpHolderComponent? holder) ||
            !SEntMan.TryGetComponent(holderUid, out ActiveScpHolderComponent? activeHolder) ||
            activeHolder.Target == null)
        {
            return;
        }

        _holding.TryReleaseHold((holderUid, holder), activeHolder.Target.Value);
    }

    private int CountHolderBlockers(EntityUid holderUid, EntityUid targetUid)
    {
        var blockerCount = 0;
        foreach (var heldItem in _hands.EnumerateHeld(holderUid))
        {
            if (SEntMan.HasComponent<ScpHoldHandBlockerComponent>(heldItem) &&
                SEntMan.TryGetComponent<VirtualItemComponent>(heldItem, out var virtualItem) &&
                virtualItem.BlockingEntity == targetUid)
            {
                blockerCount++;
            }
        }

        return blockerCount;
    }
}
