#nullable enable
using System.Numerics;
using Content.Client.Actions;
using Content.Client.Gameplay;
using Content.Client.IoC;
using Content.Client.Parallax.Managers;
using Content.IntegrationTests._Sunrise;
using Content.Server.Mind;
using Content.Shared._Scp.Holding.Components;
using Content.Shared._Scp.Holding.Systems;
using Content.Shared.Actions.Components;
using Content.Shared.CombatMode;
using Content.Shared.Input;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Players;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Weapons.Melee;
using Robust.Client.Audio.Midi;
using Robust.Client.GameObjects;
using Robust.Client.Input;
using Robust.Client.State;
using Robust.Server.Player;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._Scp;

[TestFixture]
public sealed class ScpHoldingTwoClientCombatTest
{
    [Test]
    public async Task TwoClients_TargetClientHeldAfterOwnCombatToggle_DisablesCombatModeForTargetClient()
    {
        using var server = new RobustIntegrationTest.ServerIntegrationInstance(CreateServerOptions());
        using var targetClient = new RobustIntegrationTest.ClientIntegrationInstance(CreateClientOptions());
        using var holderClient = new RobustIntegrationTest.ClientIntegrationInstance(CreateClientOptions());

        await Task.WhenAll(server.WaitIdleAsync(), targetClient.WaitIdleAsync(), holderClient.WaitIdleAsync());

        await targetClient.Connect(server);
        await holderClient.Connect(server);
        await RunTicks(server, targetClient, holderClient, 10);

        var sEntMan = server.EntMan;
        var targetEntMan = targetClient.EntMan;
        var holderEntMan = holderClient.EntMan;
        var targetTiming = targetClient.ResolveDependency<IGameTiming>();
        var holderTiming = holderClient.ResolveDependency<IGameTiming>();
        var targetActions = targetClient.System<ActionsSystem>();
        var sTransform = server.System<SharedTransformSystem>();
        var sCombatMode = server.System<SharedCombatModeSystem>();
        var sPlayerMan = server.ResolveDependency<IPlayerManager>();
        var targetState = targetClient.ResolveDependency<IStateManager>();
        var holderState = holderClient.ResolveDependency<IStateManager>();

        Assert.That(targetClient.User, Is.Not.Null);
        Assert.That(holderClient.User, Is.Not.Null);

        var targetSession = sPlayerMan.GetSessionById(targetClient.User!.Value);
        var holderSession = sPlayerMan.GetSessionById(holderClient.User!.Value);

        EntityUid sTarget = default;
        EntityUid sHolder = default;
        EntityUid sCombatAction = default;

        await targetClient.WaitPost(() => targetState.RequestStateChange<GameplayState>());
        await holderClient.WaitPost(() => holderState.RequestStateChange<GameplayState>());
        await RunTicks(server, targetClient, holderClient, 20);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(targetSession.AttachedEntity, Is.Not.Null);
                Assert.That(holderSession.AttachedEntity, Is.Not.Null);
            });

            sTarget = targetSession.AttachedEntity!.Value;
            sHolder = holderSession.AttachedEntity!.Value;
            sEntMan.EnsureComponent<ScpHolderComponent>(sHolder);

            var targetCoords = sEntMan.GetComponent<TransformComponent>(sTarget).Coordinates;
            sTransform.SetCoordinates(sHolder, new EntityCoordinates(targetCoords.EntityId, targetCoords.Position + new Vector2(0.1f, 0f)));

            sCombatAction = GetCombatToggleAction(sEntMan, sTarget);
        });
        await RunTicks(server, targetClient, holderClient, 10);

        EntityUid cTargetOnTargetClient = default;
        EntityUid cCombatActionOnTargetClient = default;
        EntityUid cHolderOnTargetClient = default;
        EntityUid cTargetOnHolderClient = default;
        EntityUid cHolderOnHolderClient = default;

        await targetClient.WaitAssertion(() =>
        {
            cTargetOnTargetClient = ToClientEntity(sEntMan, targetEntMan, sTarget);
            cCombatActionOnTargetClient = ToClientEntity(sEntMan, targetEntMan, sCombatAction);
            cHolderOnTargetClient = ToClientEntity(sEntMan, targetEntMan, sHolder);

            Assert.Multiple(() =>
            {
                Assert.That(targetClient.AttachedEntity, Is.EqualTo(cTargetOnTargetClient));
                Assert.That(targetEntMan.EntityExists(cTargetOnTargetClient), Is.True);
                Assert.That(targetEntMan.EntityExists(cCombatActionOnTargetClient), Is.True);
                Assert.That(targetEntMan.EntityExists(cHolderOnTargetClient), Is.True);
            });
        });

        await holderClient.WaitAssertion(() =>
        {
            cTargetOnHolderClient = ToClientEntity(sEntMan, holderEntMan, sTarget);
            cHolderOnHolderClient = ToClientEntity(sEntMan, holderEntMan, sHolder);

            Assert.Multiple(() =>
            {
                Assert.That(holderClient.AttachedEntity, Is.EqualTo(cHolderOnHolderClient));
                Assert.That(holderEntMan.EntityExists(cTargetOnHolderClient), Is.True);
            });
        });

        await targetClient.WaitPost(() =>
        {
            var action = targetEntMan.GetComponent<ActionComponent>(cCombatActionOnTargetClient);
            targetActions.TriggerAction((cCombatActionOnTargetClient, action));
        });
        await RunTicks(server, targetClient, holderClient, 10);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(IsInCombatMode(sEntMan, sTarget), Is.True);
                Assert.That(IsActionToggled(sEntMan, sCombatAction), Is.True);
            });
        });

        await targetClient.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(IsInCombatMode(targetEntMan, cTargetOnTargetClient), Is.True);
                Assert.That(IsActionToggled(targetEntMan, cCombatActionOnTargetClient), Is.True);
            });
        });

        await SendClientPullInput(holderClient, holderEntMan, holderTiming, cTargetOnHolderClient, BoundKeyState.Down);
        await RunTicks(server, targetClient, holderClient, 2);
        await SendClientPullInput(holderClient, holderEntMan, holderTiming, cTargetOnHolderClient, BoundKeyState.Up);
        await RunTicks(server, targetClient, holderClient, 15);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(sTarget), Is.True);
                Assert.That(IsInCombatMode(sEntMan, sTarget), Is.False);
                Assert.That(IsActionToggled(sEntMan, sCombatAction), Is.False);
                Assert.That(IsActionEnabled(sEntMan, sCombatAction), Is.False);
            });
        });

        await RunTicks(server, targetClient, holderClient, 1);

        await targetClient.WaitPost(() =>
        {
            cCombatActionOnTargetClient = ToClientEntity(sEntMan, targetEntMan, sCombatAction);

            Assert.Multiple(() =>
            {
                Assert.That(IsInCombatMode(targetEntMan, cTargetOnTargetClient), Is.False);
                Assert.That(IsActionToggled(targetEntMan, cCombatActionOnTargetClient), Is.False);
                Assert.That(IsActionEnabled(targetEntMan, cCombatActionOnTargetClient), Is.False);
            });
        });

        EntityUid sWeapon = default;
        TimeSpan weaponCooldownBeforeAttack = default;

        await server.WaitPost(() =>
        {
            var meleeSystem = server.System<Content.Server.Weapons.Melee.MeleeWeaponSystem>();
            Assert.That(meleeSystem.TryGetWeapon(sTarget, out sWeapon, out var melee), Is.True);
            Assert.That(melee, Is.Not.Null);
            weaponCooldownBeforeAttack = melee!.NextAttack;
        });

        await targetClient.WaitPost(() =>
        {
            var cWeaponOnTargetClient = ToClientEntity(sEntMan, targetEntMan, sWeapon);
            var targetCoords = targetEntMan.GetComponent<TransformComponent>(cHolderOnTargetClient).Coordinates;

            targetEntMan.RaisePredictiveEvent(new LightAttackEvent(
                targetEntMan.GetNetEntity(cHolderOnTargetClient),
                targetEntMan.GetNetEntity(cWeaponOnTargetClient),
                targetEntMan.GetNetCoordinates(targetCoords)));
        });
        await RunTicks(server, targetClient, holderClient, 3);

        await server.WaitAssertion(() =>
        {
            var melee = sEntMan.GetComponent<MeleeWeaponComponent>(sWeapon);

            Assert.Multiple(() =>
            {
                Assert.That(IsInCombatMode(sEntMan, sTarget), Is.False);
                Assert.That(melee.NextAttack, Is.EqualTo(weaponCooldownBeforeAttack));
            });
        });

        await holderClient.WaitAssertion(() =>
        {
            var cCombatActionOnHolderClient = ToClientEntity(sEntMan, holderEntMan, sCombatAction);

            Assert.Multiple(() =>
            {
                Assert.That(holderEntMan.HasComponent<ActiveScpHoldableComponent>(cTargetOnHolderClient), Is.True);
                Assert.That(IsInCombatMode(holderEntMan, cTargetOnHolderClient), Is.False);
                Assert.That(IsActionToggled(holderEntMan, cCombatActionOnHolderClient), Is.False);
                Assert.That(IsActionEnabled(holderEntMan, cCombatActionOnHolderClient), Is.False);
            });
        });

        await targetClient.WaitAssertion(() =>
        {
            cCombatActionOnTargetClient = ToClientEntity(sEntMan, targetEntMan, sCombatAction);

            Assert.Multiple(() =>
            {
                Assert.That(targetEntMan.HasComponent<ActiveScpHoldableComponent>(cTargetOnTargetClient), Is.True);
                Assert.That(IsInCombatMode(targetEntMan, cTargetOnTargetClient), Is.False);
                Assert.That(IsActionToggled(targetEntMan, cCombatActionOnTargetClient), Is.False);
                Assert.That(IsActionEnabled(targetEntMan, cCombatActionOnTargetClient), Is.False);
            });
        });
    }

    [Test]
    public async Task VisitingHeldTargetAfterBreakout_ReEnablesCombatAction()
    {
        using var server = new RobustIntegrationTest.ServerIntegrationInstance(CreateServerOptions());
        using var client = new RobustIntegrationTest.ClientIntegrationInstance(CreateClientOptions());

        await Task.WhenAll(server.WaitIdleAsync(), client.WaitIdleAsync());

        await client.Connect(server);
        await RunTicks(server, client, 10);

        var sEntMan = server.EntMan;
        var cEntMan = client.EntMan;
        var sHolding = server.System<SharedScpHoldingSystem>();
        var sMind = server.System<MindSystem>();
        var sPlayerMan = server.ResolveDependency<IPlayerManager>();
        var cState = client.ResolveDependency<IStateManager>();
        var cActions = client.System<ActionsSystem>();

        Assert.That(client.User, Is.Not.Null);
        var session = sPlayerMan.GetSessionById(client.User!.Value);

        EntityUid sHolder = default;
        EntityUid sTarget = default;
        EntityUid sCombatAction = default;
        EntityUid sMindId = default;

        await client.WaitPost(() => cState.RequestStateChange<GameplayState>());
        await RunTicks(server, client, 20);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(session.AttachedEntity, Is.Not.Null);
                Assert.That(session.ContentData()?.Mind, Is.Not.Null);
            });

            sHolder = session.AttachedEntity!.Value;
            sMindId = session.ContentData()!.Mind!.Value;

            sEntMan.EnsureComponent<ScpHolderComponent>(sHolder);

            var holderCoords = sEntMan.GetComponent<TransformComponent>(sHolder).Coordinates;
            sTarget = sEntMan.SpawnEntity("MobHuman", holderCoords.Offset(new Vector2(0.1f, 0f)));
            sCombatAction = GetCombatToggleAction(sEntMan, sTarget);

            var holder = sEntMan.GetComponent<ScpHolderComponent>(sHolder);
            Assert.That(sHolding.TryToggleHold((sHolder, holder), sTarget), Is.True);
        });
        await RunTicks(server, client, 10);

        EntityUid cTarget = default;
        EntityUid cCombatAction = default;

        await client.WaitAssertion(() =>
        {
            cTarget = ToClientEntity(sEntMan, cEntMan, sTarget);
            cCombatAction = ToClientEntity(sEntMan, cEntMan, sCombatAction);

            Assert.Multiple(() =>
            {
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(cTarget), Is.True);
                Assert.That(IsActionEnabled(cEntMan, cCombatAction), Is.False);
            });
        });

        await server.WaitPost(() => sMind.Visit(sMindId, sTarget));
        await RunTicks(server, client, 10);

        await client.WaitAssertion(() =>
        {
            cTarget = ToClientEntity(sEntMan, cEntMan, sTarget);
            cCombatAction = ToClientEntity(sEntMan, cEntMan, sCombatAction);

            Assert.Multiple(() =>
            {
                Assert.That(client.AttachedEntity, Is.EqualTo(cTarget));
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(cTarget), Is.True);
                Assert.That(IsActionEnabled(cEntMan, cCombatAction), Is.False);
            });
        });

        await server.WaitPost(() => RaiseMoveInput(sEntMan, sTarget));
        await RunTicks(server, client, 10);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(sEntMan.HasComponent<ActiveScpHoldableComponent>(sTarget), Is.False);
                Assert.That(IsActionEnabled(sEntMan, sCombatAction), Is.True);
            });
        });

        await client.WaitAssertion(() =>
        {
            cTarget = ToClientEntity(sEntMan, cEntMan, sTarget);
            cCombatAction = ToClientEntity(sEntMan, cEntMan, sCombatAction);

            Assert.Multiple(() =>
            {
                Assert.That(client.AttachedEntity, Is.EqualTo(cTarget));
                Assert.That(cEntMan.HasComponent<ActiveScpHoldableComponent>(cTarget), Is.False);
                Assert.That(IsActionEnabled(cEntMan, cCombatAction), Is.True);
            });
        });

        await client.WaitPost(() =>
        {
            var action = cEntMan.GetComponent<ActionComponent>(cCombatAction);
            cActions.TriggerAction((cCombatAction, action));
        });
        await RunTicks(server, client, 10);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(IsInCombatMode(sEntMan, sTarget), Is.True);
                Assert.That(IsActionToggled(sEntMan, sCombatAction), Is.True);
            });
        });

        await client.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(IsInCombatMode(cEntMan, cTarget), Is.True);
                Assert.That(IsActionToggled(cEntMan, cCombatAction), Is.True);
            });
        });
    }

    private static RobustIntegrationTest.ServerIntegrationOptions CreateServerOptions()
    {
        return new RobustIntegrationTest.ServerIntegrationOptions
        {
            Pool = false,
            ContentStart = true,
            LoadTestAssembly = false,
            ContentAssemblies =
            [
                typeof(Shared.Entry.EntryPoint).Assembly,
                typeof(Server.Entry.EntryPoint).Assembly
            ],
            Options = new()
            {
                LoadConfigAndUserData = false,
            },
        };
    }

    private static RobustIntegrationTest.ClientIntegrationOptions CreateClientOptions()
    {
        var opts = new RobustIntegrationTest.ClientIntegrationOptions
        {
            Pool = false,
            ContentStart = true,
            LoadTestAssembly = false,
            ContentAssemblies =
            [
                typeof(Shared.Entry.EntryPoint).Assembly,
                typeof(Client.Entry.EntryPoint).Assembly
            ],
            Options = new()
            {
                LoadConfigAndUserData = false,
            },
        };

        opts.InitIoC = () =>
        {
            IoCManager.Register<IMidiManager, DummyMidiManager>(true);
        };

        opts.BeforeStart += () =>
        {
            IoCManager.Resolve<IModLoader>().SetModuleBaseCallbacks(new ClientModuleTestingCallbacks
            {
                ClientBeforeIoC = () => IoCManager.Register<IParallaxManager, DummyParallaxManager>(true)
            });
        };

        return opts;
    }

    private static async Task RunTicks(
        RobustIntegrationTest.ServerIntegrationInstance server,
        RobustIntegrationTest.ClientIntegrationInstance targetClient,
        RobustIntegrationTest.ClientIntegrationInstance holderClient,
        int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            await server.WaitRunTicks(1);
            await targetClient.WaitRunTicks(1);
            await holderClient.WaitRunTicks(1);
        }
    }

    private static async Task RunTicks(
        RobustIntegrationTest.ServerIntegrationInstance server,
        RobustIntegrationTest.ClientIntegrationInstance client,
        int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            await server.WaitRunTicks(1);
            await client.WaitRunTicks(1);
        }
    }

    private static EntityUid GetCombatToggleAction(IEntityManager entMan, EntityUid uid)
    {
        var combat = entMan.GetComponent<CombatModeComponent>(uid);

        Assert.That(combat.CombatToggleActionEntity, Is.Not.Null);
        return combat.CombatToggleActionEntity!.Value;
    }

    private static bool IsInCombatMode(IEntityManager entMan, EntityUid uid)
    {
        return entMan.GetComponent<CombatModeComponent>(uid).IsInCombatMode;
    }

    private static bool IsActionToggled(IEntityManager entMan, EntityUid uid)
    {
        return entMan.GetComponent<ActionComponent>(uid).Toggled;
    }

    private static bool IsActionEnabled(IEntityManager entMan, EntityUid uid)
    {
        return entMan.GetComponent<ActionComponent>(uid).Enabled;
    }

    private static EntityUid ToClientEntity(IEntityManager serverEntMan, IEntityManager clientEntMan, EntityUid serverEntity)
    {
        return clientEntMan.GetEntity(serverEntMan.GetNetEntity(serverEntity));
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

    private static void RaiseMoveInput(IEntityManager entMan, EntityUid uid)
    {
        var mover = entMan.GetComponent<InputMoverComponent>(uid);
        var move = new MoveInputEvent((uid, mover), MoveButtons.None, Direction.East, true);
        entMan.EventBus.RaiseLocalEvent(uid, ref move);
    }
}
