using System.Numerics;
using Content.Shared.ActionBlocker;
using Content.Shared.Alert;
using Content.Shared.Actions;
using Content.Shared.Actions.Components;
using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.StatusEffectNew;
using Content.Shared.StatusEffectNew.Components;
using Robust.Shared.GameStates;
using Robust.Shared.Containers;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Shared._Scp.Holding;

public sealed class SharedScpHoldingSystem : EntitySystem
{
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movement = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedVirtualItemSystem _virtualItem = default!;

    private const string GrabbedStatusEffect = "StatusEffectScpHeld";
    private const float SoftDragDistanceFactor = 0.3f;
    private const float SoftDragMinimumDistance = 0.18f;
    private const float SoftDragMaximumDistance = 0.3f;
    private const float SoftDragSnapTolerance = 0.03f;
    private const float SoftDragSettleTolerance = 0.08f;
    private const float SoftDragVelocityDirectionThreshold = 0.05f;
    private const float SoftDragCatchUpTime = 0.05f;
    private const float SoftDragMaximumCorrectionSpeed = 6f;
    private const float SoftDragAwayVelocityStrength = 0.6f;
    private const float SoftDragVelocityTolerance = 0.05f;

    private readonly List<EntityUid> _holdersToRemove = new();
    private readonly List<Entity<VirtualItemComponent>> _virtualBlockersToDelete = new();

    private EntityQuery<BodyComponent> _bodyQuery;
    private EntityQuery<HandsComponent> _handsQuery;
    private EntityQuery<InputMoverComponent> _moverQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<ScpHeldComponent> _heldQuery;
    private EntityQuery<ScpHoldComponent> _holdQuery;
    private EntityQuery<ScpHolderComponent> _holderQuery;

    public override void Initialize()
    {
        base.Initialize();

        _bodyQuery = GetEntityQuery<BodyComponent>();
        _handsQuery = GetEntityQuery<HandsComponent>();
        _moverQuery = GetEntityQuery<InputMoverComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _heldQuery = GetEntityQuery<ScpHeldComponent>();
        _holdQuery = GetEntityQuery<ScpHoldComponent>();
        _holderQuery = GetEntityQuery<ScpHolderComponent>();

        SubscribeLocalEvent<ScpHoldComponent, ComponentStartup>(OnHoldStartup);
        SubscribeLocalEvent<ScpHoldComponent, ComponentShutdown>(OnHoldShutdown);
        SubscribeLocalEvent<ScpHoldComponent, ScpHoldActionEvent>(OnHoldAction);

        SubscribeLocalEvent<ScpHeldComponent, ComponentStartup>(OnHeldStartup);
        SubscribeLocalEvent<ScpHeldComponent, ComponentShutdown>(OnHeldShutdown);
        SubscribeLocalEvent<ScpHeldComponent, ScpHoldBreakoutActionEvent>(OnBreakoutAction);
        SubscribeLocalEvent<ScpHeldComponent, ScpHoldBreakoutAlertEvent>(OnBreakoutAlert);
        SubscribeLocalEvent<ScpHeldComponent, ScpHoldBreakoutDoAfterEvent>(OnBreakoutDoAfter);
        SubscribeLocalEvent<ScpHeldComponent, MoveInputEvent>(OnHeldMoveInput);
        SubscribeLocalEvent<ScpHeldComponent, HandCountChangedEvent>(OnHandCountChanged);
        SubscribeLocalEvent<ScpHeldComponent, UpdateCanMoveEvent>(OnHeldUpdateCanMove);
        SubscribeLocalEvent<ScpHeldComponent, PreventCollideEvent>(OnHeldPreventCollide);

        SubscribeLocalEvent<ScpHolderComponent, ComponentStartup>(OnHolderStartup);
        SubscribeLocalEvent<ScpHolderComponent, ComponentShutdown>(OnHolderShutdown);
        SubscribeLocalEvent<ScpHolderComponent, RefreshMovementSpeedModifiersEvent>(OnHolderRefreshMoveSpeed);
        SubscribeLocalEvent<ScpHolderComponent, DidEquipHandEvent>(OnHolderHandsModified);
        SubscribeLocalEvent<ScpHolderComponent, PreventCollideEvent>(OnHolderPreventCollide);
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

    private void OnHoldStartup(Entity<ScpHoldComponent> ent, ref ComponentStartup args)
    {
        _actions.AddAction(ent.Owner, ref ent.Comp.ActionEntity, ent.Comp.Action);
    }

    private void OnHoldShutdown(Entity<ScpHoldComponent> ent, ref ComponentShutdown args)
    {
        _actions.RemoveAction(ent.Comp.ActionEntity);

        if (_net.IsClient ||
            TerminatingOrDeleted(ent.Owner) ||
            !_holderQuery.TryComp(ent.Owner, out var holder) ||
            holder.Target == null ||
            TerminatingOrDeleted(holder.Target.Value))
        {
            return;
        }

        ReleaseHolderContribution(ent.Owner, holder.Target.Value, clearIfEmpty: true);
    }

    private void OnHoldAction(Entity<ScpHoldComponent> ent, ref ScpHoldActionEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = TryToggleHold(ent, args.Target);
    }

    private void OnHeldStartup(Entity<ScpHeldComponent> ent, ref ComponentStartup args)
    {
        _actions.AddAction(ent.Owner, ref ent.Comp.BreakoutActionEntity, ent.Comp.BreakoutAction);
        RefreshHeldState(ent);
    }

    private void OnHeldShutdown(Entity<ScpHeldComponent> ent, ref ComponentShutdown args)
    {
        _actions.RemoveAction(ent.Comp.BreakoutActionEntity);
        _alerts.ClearAlert(ent.Owner, "ScpHoldGrabbed");
        _statusEffects.TryRemoveStatusEffect(ent.Owner, GrabbedStatusEffect);
        _virtualItem.DeleteInHandsMatching(ent.Owner, ent.Owner);

        if (!_timing.ApplyingState)
            CancelBreakoutDoAfter(ent);

        if (!_net.IsClient)
        {
            for (var i = 0; i < ent.Comp.Holders.Count; i++)
            {
                var holderUid = ent.Comp.Holders[i];
                if (!TerminatingOrDeleted(holderUid) && _holderQuery.HasComp(holderUid))
                    RemComp<ScpHolderComponent>(holderUid);
            }
        }

        _actionBlocker.UpdateCanMove(ent.Owner);

        if (_net.IsClient)
            _physics.UpdateIsPredicted(ent.Owner);
    }

    private void OnBreakoutAction(Entity<ScpHeldComponent> ent, ref ScpHoldBreakoutActionEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = TryBreakOut(ent, viaMovement: false);
    }

    private void OnBreakoutAlert(Entity<ScpHeldComponent> ent, ref ScpHoldBreakoutAlertEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        TryBreakOut(ent, viaMovement: false);
    }

    private void OnBreakoutDoAfter(Entity<ScpHeldComponent> ent, ref ScpHoldBreakoutDoAfterEvent args)
    {
        SetBreakoutDoAfterId(ent, null);

        if (args.Handled)
            return;

        args.Handled = true;

        if (args.Cancelled)
        {
            PopupTarget(ent.Owner, "scp-hold-breakout-interrupted");
            return;
        }

        ClearHoldState(ent, applyImmunity: true);
    }

    private void OnHeldMoveInput(Entity<ScpHeldComponent> ent, ref MoveInputEvent args)
    {
        if (!args.State)
            return;

        TryBreakOut(ent, viaMovement: true);
    }

    private void OnHandCountChanged(Entity<ScpHeldComponent> ent, ref HandCountChangedEvent args)
    {
        if (_net.IsClient)
            return;

        SyncHeldState(ent);
    }

    private void OnHeldUpdateCanMove(Entity<ScpHeldComponent> ent, ref UpdateCanMoveEvent args)
    {
        if (ent.Comp.LifeStage > ComponentLifeStage.Running)
            return;

        if (ent.Comp.FullHold)
            args.Cancel();
    }

    private void OnHeldPreventCollide(Entity<ScpHeldComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled)
            return;

        if (_holderQuery.TryComp(args.OtherEntity, out var holder) &&
            holder.Target == ent.Owner)
        {
            args.Cancelled = true;
        }
    }

    private void OnHolderStartup(Entity<ScpHolderComponent> ent, ref ComponentStartup args)
    {
        RefreshHolderState(ent);
    }

    private void OnHolderShutdown(Entity<ScpHolderComponent> ent, ref ComponentShutdown args)
    {
        ent.Comp.Target = null;
        ent.Comp.SlowdownEnabled = false;
        DeleteHolderHandBlockers(ent.Owner);
        _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnHolderRefreshMoveSpeed(Entity<ScpHolderComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (!ent.Comp.SlowdownEnabled)
            return;

        args.ModifySpeed(ent.Comp.WalkModifier, ent.Comp.SprintModifier);
    }

    private void OnHolderHandsModified(Entity<ScpHolderComponent> ent, ref DidEquipHandEvent args)
    {
        if (ent.Comp.LifeStage > ComponentLifeStage.Running ||
            TerminatingOrDeleted(ent.Owner) ||
            ent.Comp.Target == null ||
            TerminatingOrDeleted(ent.Comp.Target.Value))
        {
            return;
        }

        RefreshHolderState(ent);
    }

    private void OnHolderPreventCollide(Entity<ScpHolderComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled ||
            ent.Comp.Target == null ||
            ent.Comp.Target != args.OtherEntity)
        {
            return;
        }

        args.Cancelled = true;
    }

    public bool TryToggleHold(Entity<ScpHoldComponent> holder, EntityUid target)
    {
        if (_holderQuery.TryComp(holder.Owner, out var activeHolder) && activeHolder.Target != null)
        {
            if (activeHolder.Target.Value == target)
            {
                ReleaseHolderContribution(holder.Owner, target, clearIfEmpty: true);
                return true;
            }

            PopupHolder(holder.Owner, "scp-hold-already-holding-other");
            return false;
        }

        if (!CanToggleHold(holder, target))
            return false;

        var held = EnsureHeldState(target, holder.Comp);
        AddHolderContribution(holder.Owner, held);
        SyncHeldState(held);
        return true;
    }

    public bool CanToggleHold(Entity<ScpHoldComponent> holder, EntityUid target, bool quiet = false)
    {
        if (!Exists(target) || holder.Owner == target)
            return false;

        if (!_moverQuery.HasComp(holder.Owner) ||
            !_moverQuery.HasComp(target) ||
            !_physicsQuery.TryComp(target, out var targetPhysics) ||
            targetPhysics.BodyType == BodyType.Static)
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-invalid", ("target", target));
            return false;
        }

        if (!_container.IsInSameOrNoContainer(holder.Owner, target))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-invalid", ("target", target));
            return false;
        }

        if (TryComp<ScpHoldImmuneComponent>(target, out _))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-immune", ("target", target));
            return false;
        }

        if (!HasAvailableHolderHand(holder.Owner))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-holder-no-free-hand", ("target", target));
            return false;
        }

        var range = holder.Comp.HoldRange;
        if (_heldQuery.TryComp(target, out var held))
        {
            range = held.HoldRange;

            if (held.FullHold && held.Holders.Count >= held.RequiredHolderCount)
            {
                if (!quiet)
                    PopupHolder(holder.Owner, "scp-hold-target-fully-held", ("target", target));
                return false;
            }
        }

        if (!_interaction.InRangeUnobstructed(holder.Owner, target, range))
        {
            if (!quiet)
                PopupHolder(holder.Owner, "scp-hold-target-too-far", ("target", target));
            return false;
        }

        return true;
    }

    public bool TryBreakOut(Entity<ScpHeldComponent> held, bool viaMovement)
    {
        return held.Comp.FullHold
            ? TryStartFullBreakout(held, viaMovement)
            : TrySoftBreakOut(held, viaMovement);
    }

    public void RefreshHeldState(Entity<ScpHeldComponent> held)
    {
        _alerts.ShowAlert(held.Owner, "ScpHoldGrabbed");
        SyncHeldStatusEffect(held.Owner);
        SyncPlaceholderHands(held);
        _actionBlocker.UpdateCanMove(held.Owner);

        if (_net.IsClient)
            _physics.UpdateIsPredicted(held.Owner);
    }

    public void RefreshHolderState(Entity<ScpHolderComponent> holder)
    {
        SyncHolderHandBlocker(holder);
        _movement.RefreshMovementSpeedModifiers(holder.Owner);
    }

    private bool TrySoftBreakOut(Entity<ScpHeldComponent> held, bool viaMovement)
    {
        if (_timing.CurTime < held.Comp.SoftEscapeAvailableAt)
            return false;

        if (!viaMovement)
            PopupTarget(held.Owner, "scp-hold-breakout-start");

        ClearHoldState(held, applyImmunity: false);
        return true;
    }

    private bool TryStartFullBreakout(Entity<ScpHeldComponent> held, bool viaMovement)
    {
        if (held.Comp.FullHoldStartedAt == null ||
            _timing.CurTime < held.Comp.FullHoldStartedAt.Value + held.Comp.FullHoldDelay)
        {
            if (!viaMovement)
                PopupTarget(held.Owner, "scp-hold-breakout-too-early");
            return false;
        }

        if (held.Comp.BreakoutDoAfterId != null)
            return true;

        var doAfter = new DoAfterArgs(EntityManager, held.Owner, held.Comp.FullBreakoutDuration,
            new ScpHoldBreakoutDoAfterEvent(), held.Owner, target: held.Owner)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = false,
            Hidden = false,
        };

        if (!_doAfter.TryStartDoAfter(doAfter, out var id))
            return false;

        SetBreakoutDoAfterId(held, id.Value.Index);
        PopupTarget(held.Owner, "scp-hold-breakout-start");
        return true;
    }

    private void UpdateHeld(Entity<ScpHeldComponent> held)
    {
        if (!EnsurePrimaryHolder(held))
        {
            ClearHoldState(held, applyImmunity: false);
            return;
        }

        var desiredSoftDragDistance = GetDesiredSoftDragDistance(held);
        var maintenanceRange = GetHoldMaintenanceRange(held.Comp.HoldRange, desiredSoftDragDistance);

        if (!held.Comp.FullHold)
            UpdateSoftDrag(held, maintenanceRange, desiredSoftDragDistance);
        else
            ZeroHeldVelocity(held.Owner);

        _holdersToRemove.Clear();

        for (var i = 0; i < held.Comp.Holders.Count; i++)
        {
            var holderUid = held.Comp.Holders[i];

            if (!Exists(holderUid) ||
                !_holdQuery.HasComp(holderUid) ||
                !_holderQuery.TryComp(holderUid, out var holder) ||
                holder.Target != held.Owner ||
                !_container.IsInSameOrNoContainer(holderUid, held.Owner) ||
                !_interaction.InRangeUnobstructed(holderUid, held.Owner, maintenanceRange))
            {
                _holdersToRemove.Add(holderUid);
            }
        }

        for (var i = 0; i < _holdersToRemove.Count; i++)
        {
            ReleaseHolderContribution(_holdersToRemove[i], held.Owner, clearIfEmpty: false);

            if (!_heldQuery.TryComp(held.Owner, out _))
                return;
        }

        if (_heldQuery.TryComp(held.Owner, out var refreshed))
            SyncHeldState((held.Owner, refreshed));
    }

    private Entity<ScpHeldComponent> EnsureHeldState(EntityUid target, ScpHoldComponent config)
    {
        var created = !_heldQuery.TryComp(target, out var held);
        held ??= EnsureComp<ScpHeldComponent>(target);

        if (created)
            CopyConfig(config, held);

        held.RequiredHolderCount = GetRequiredHolderCount(target);
        return (target, held);
    }

    private void AddHolderContribution(EntityUid holderUid, Entity<ScpHeldComponent> held)
    {
        if (!held.Comp.Holders.Contains(holderUid))
            held.Comp.Holders.Add(holderUid);

        var holder = EnsureComp<ScpHolderComponent>(holderUid);
        holder.Target = held.Owner;
        holder.SlowdownEnabled = false;
        holder.WalkModifier = held.Comp.WalkModifier;
        holder.SprintModifier = held.Comp.SprintModifier;
        Dirty(holderUid, holder);
        RefreshHolderState((holderUid, holder));
    }

    private void ReleaseHolderContribution(EntityUid holderUid, EntityUid targetUid, bool clearIfEmpty)
    {
        if (!_heldQuery.TryComp(targetUid, out var held))
            return;

        for (var i = held.Holders.Count - 1; i >= 0; i--)
        {
            if (held.Holders[i] == holderUid)
                held.Holders.RemoveAt(i);
        }

        if (_holderQuery.HasComp(holderUid))
            RemComp<ScpHolderComponent>(holderUid);

        if (held.PrimaryHolder == holderUid)
            held.PrimaryHolder = null;

        if (held.Holders.Count == 0)
        {
            if (clearIfEmpty)
                ClearHoldState((targetUid, held), applyImmunity: false);
            return;
        }

        SyncHeldState((targetUid, held));
    }

    private void SyncHeldState(Entity<ScpHeldComponent> held)
    {
        if (!_heldQuery.TryComp(held.Owner, out var heldComp))
            return;

        held.Comp = heldComp;
        held.Comp.RequiredHolderCount = GetRequiredHolderCount(held.Owner);

        if (held.Comp.Holders.Count == 0)
        {
            ClearHoldState(held, applyImmunity: false);
            return;
        }

        if (!EnsurePrimaryHolder(held))
        {
            ClearHoldState(held, applyImmunity: false);
            return;
        }

        if (held.Comp.Holders.Count >= held.Comp.RequiredHolderCount)
        {
            EnterFullHold(held);
            return;
        }

        ExitFullHold(held);
        var desiredSoftDragDistance = GetDesiredSoftDragDistance(held);
        var maintenanceRange = GetHoldMaintenanceRange(held.Comp.HoldRange, desiredSoftDragDistance);
        UpdateSoftDrag(held, maintenanceRange, desiredSoftDragDistance);
        UpdateHolderSlowdowns(held);
        SyncPlaceholderHands(held);
        Dirty(held);
    }

    private void EnterFullHold(Entity<ScpHeldComponent> held)
    {
        if (!held.Comp.FullHold)
        {
            held.Comp.FullHold = true;
            held.Comp.FullHoldStartedAt = _timing.CurTime;
        }

        UpdateHolderSlowdowns(held);
        SyncPlaceholderHands(held);
        ZeroHeldVelocity(held.Owner);
        _actionBlocker.UpdateCanMove(held.Owner);
        Dirty(held);
    }

    private void ExitFullHold(Entity<ScpHeldComponent> held)
    {
        CancelBreakoutDoAfter(held);

        if (!held.Comp.FullHold && held.Comp.FullHoldStartedAt == null)
            return;

        held.Comp.FullHold = false;
        held.Comp.FullHoldStartedAt = null;
        SyncPlaceholderHands(held);
        _actionBlocker.UpdateCanMove(held.Owner);
        Dirty(held);
    }

    private bool EnsurePrimaryHolder(Entity<ScpHeldComponent> held)
    {
        if (held.Comp.PrimaryHolder != null &&
            _holderQuery.TryComp(held.Comp.PrimaryHolder.Value, out var activeHolder) &&
            activeHolder.Target == held.Owner &&
            held.Comp.Holders.Contains(held.Comp.PrimaryHolder.Value))
        {
            return true;
        }

        held.Comp.PrimaryHolder = null;

        for (var i = 0; i < held.Comp.Holders.Count; i++)
        {
            var holderUid = held.Comp.Holders[i];

            if (!_holderQuery.TryComp(holderUid, out var holder) ||
                holder.Target != held.Owner)
            {
                continue;
            }

            held.Comp.PrimaryHolder = holderUid;
            return true;
        }

        return false;
    }

    private void UpdateSoftDrag(Entity<ScpHeldComponent> held, float maintenanceRange, float desiredDistance)
    {
        if (held.Comp.PrimaryHolder == null)
            return;

        var primaryHolder = held.Comp.PrimaryHolder.Value;
        if (!_holderQuery.TryComp(primaryHolder, out var holder) ||
            holder.Target != held.Owner ||
            !_container.IsInSameOrNoContainer(primaryHolder, held.Owner) ||
            !_interaction.InRangeUnobstructed(primaryHolder, held.Owner, maintenanceRange) ||
            !_physicsQuery.TryComp(held.Owner, out var heldPhysics))
        {
            return;
        }

        var holderCoords = _transform.GetMapCoordinates(primaryHolder);
        var heldCoords = _transform.GetMapCoordinates(held.Owner);

        if (holderCoords.MapId != heldCoords.MapId)
            return;

        var offset = heldCoords.Position - holderCoords.Position;
        var distance = offset.Length();
        var holderVelocity = _physicsQuery.TryComp(primaryHolder, out var holderPhysics)
            ? holderPhysics.LinearVelocity
            : Vector2.Zero;
        var holderSpeed = holderVelocity.Length();

        var direction = GetSoftDragDirection(primaryHolder, holderVelocity, offset, distance);
        var desiredPosition = holderCoords.Position + direction * desiredDistance;
        var correction = desiredPosition - heldCoords.Position;
        var correctionDistance = correction.Length();

        Vector2 desiredVelocity;
        if (correctionDistance <= SoftDragSettleTolerance)
        {
            desiredVelocity = holderVelocity.LengthSquared() > SoftDragVelocityDirectionThreshold * SoftDragVelocityDirectionThreshold
                ? holderVelocity
                : Vector2.Zero;
        }
        else
        {
            var correctionDirection = correction / correctionDistance;
            var correctionSpeed = Math.Min(correctionDistance / GetSoftDragCatchUpTime(), SoftDragMaximumCorrectionSpeed);
            desiredVelocity = holderVelocity + correctionDirection * correctionSpeed;

            var relativeVelocity = heldPhysics.LinearVelocity - holderVelocity;
            var awaySpeed = MathF.Max(0f, -Vector2.Dot(relativeVelocity, correctionDirection));
            if (awaySpeed > 0f)
                desiredVelocity += correctionDirection * awaySpeed * SoftDragAwayVelocityStrength;
        }

        ApplyHeldVelocity(held.Owner, desiredVelocity, heldPhysics);
    }

    private void ClearHoldState(Entity<ScpHeldComponent> held, bool applyImmunity)
    {
        if (_heldQuery.TryComp(held.Owner, out var refreshed))
            held = (held.Owner, refreshed);

        CancelBreakoutDoAfter(held);
        _virtualItem.DeleteInHandsMatching(held.Owner, held.Owner);
        _actionBlocker.UpdateCanMove(held.Owner);

        for (var i = 0; i < held.Comp.Holders.Count; i++)
        {
            var holderUid = held.Comp.Holders[i];
            if (_holderQuery.HasComp(holderUid))
                RemComp<ScpHolderComponent>(holderUid);
        }

        held.Comp.Holders.Clear();
        held.Comp.PrimaryHolder = null;

        if (applyImmunity)
        {
            var immune = EnsureComp<ScpHoldImmuneComponent>(held.Owner);
            immune.ExpiresAt = _timing.CurTime + held.Comp.PostBreakoutImmunity;
            Dirty(held.Owner, immune);
        }

        RemComp<ScpHeldComponent>(held.Owner);
    }

    private void UpdateHolderSlowdowns(Entity<ScpHeldComponent> held)
    {
        for (var i = 0; i < held.Comp.Holders.Count; i++)
        {
            var holderUid = held.Comp.Holders[i];
            if (!_holderQuery.TryComp(holderUid, out var holder))
                continue;

            SetHolderSlowdown((holderUid, holder), true, held.Comp.WalkModifier, held.Comp.SprintModifier);
        }
    }

    private void SetHolderSlowdown(Entity<ScpHolderComponent> holder, bool enabled, float walkModifier, float sprintModifier)
    {
        if (holder.Comp.SlowdownEnabled == enabled &&
            MathHelper.CloseTo(holder.Comp.WalkModifier, walkModifier) &&
            MathHelper.CloseTo(holder.Comp.SprintModifier, sprintModifier))
        {
            return;
        }

        holder.Comp.SlowdownEnabled = enabled;
        holder.Comp.WalkModifier = walkModifier;
        holder.Comp.SprintModifier = sprintModifier;
        Dirty(holder);
        _movement.RefreshMovementSpeedModifiers(holder.Owner);
    }

    private void SyncPlaceholderHands(Entity<ScpHeldComponent> held)
    {
        _virtualItem.DeleteInHandsMatching(held.Owner, held.Owner);

        if (!held.Comp.FullHold || !_handsQuery.TryComp(held.Owner, out var hands))
            return;

        foreach (var hand in _hands.EnumerateHands((held.Owner, hands)))
        {
            if (!_hands.TryGetHeldItem((held.Owner, hands), hand, out var heldItem))
                continue;

            if (HasComp<UnremoveableComponent>(heldItem.Value))
                continue;

            _hands.DoDrop((held.Owner, hands), hand, doDropInteraction: true);
        }

        while (_virtualItem.TrySpawnVirtualItemInHand(held.Owner, held.Owner, out var virtualItem, silent: true))
        {
            EnsureComp<UnremoveableComponent>(virtualItem.Value);
        }
    }

    private void SyncHeldStatusEffect(EntityUid target)
    {
        if (_statusEffects.HasStatusEffect(target, GrabbedStatusEffect) ||
            !_statusEffects.CanAddStatusEffect(target, GrabbedStatusEffect))
        {
            return;
        }

        EnsureComp<StatusEffectContainerComponent>(target);
        PredictedTrySpawnInContainer(GrabbedStatusEffect, target, StatusEffectContainerComponent.ContainerId, out _);
    }

    private void SyncHolderHandBlocker(Entity<ScpHolderComponent> holder)
    {
        _virtualBlockersToDelete.Clear();
        EntityUid? validBlocker = null;
        var target = holder.Comp.Target;

        foreach (var heldItem in _hands.EnumerateHeld(holder.Owner))
        {
            if (!TryComp<VirtualItemComponent>(heldItem, out var virtualItem))
            {
                continue;
            }

            var matchesCurrentTarget = holder.Comp.LifeStage <= ComponentLifeStage.Running &&
                target != null &&
                virtualItem.BlockingEntity == target.Value;

            if (matchesCurrentTarget)
            {
                if (validBlocker == null)
                {
                    validBlocker = heldItem;
                    EnsureComp<UnremoveableComponent>(heldItem);
                    var blocker = EnsureComp<ScpHoldHandBlockerComponent>(heldItem);
                    var currentTarget = target!.Value;
                    if (blocker.Target != currentTarget)
                    {
                        blocker.Target = currentTarget;
                        Dirty(heldItem, blocker);
                    }
                    continue;
                }
            }

            if (TryComp<ScpHoldHandBlockerComponent>(heldItem, out _) || matchesCurrentTarget)
                _virtualBlockersToDelete.Add((heldItem, virtualItem));
        }

        for (var i = 0; i < _virtualBlockersToDelete.Count; i++)
        {
            _virtualItem.DeleteVirtualItem(_virtualBlockersToDelete[i], holder.Owner);
        }

        if (holder.Comp.LifeStage > ComponentLifeStage.Running ||
            holder.Comp.Target == null ||
            validBlocker != null)
        {
            return;
        }

        if (!_handsQuery.TryComp(holder.Owner, out var hands) ||
            !_hands.TryGetEmptyHand((holder.Owner, hands), out _))
        {
            return;
        }

        if (!_virtualItem.TrySpawnVirtualItemInHand(holder.Comp.Target.Value, holder.Owner, out var spawnedVirtualItem, silent: true))
            return;

        EnsureComp<UnremoveableComponent>(spawnedVirtualItem.Value);
        var blockerComp = EnsureComp<ScpHoldHandBlockerComponent>(spawnedVirtualItem.Value);
        blockerComp.Target = holder.Comp.Target.Value;
        Dirty(spawnedVirtualItem.Value, blockerComp);
    }

    private bool HasAvailableHolderHand(EntityUid holderUid)
    {
        return _handsQuery.TryComp(holderUid, out var hands) &&
               _hands.TryGetEmptyHand((holderUid, hands), out _);
    }

    private void DeleteHolderHandBlockers(EntityUid holderUid)
    {
        _virtualBlockersToDelete.Clear();

        foreach (var heldItem in _hands.EnumerateHeld(holderUid))
        {
            if (TryComp<ScpHoldHandBlockerComponent>(heldItem, out _) &&
                TryComp<VirtualItemComponent>(heldItem, out var virtualItem))
            {
                _virtualBlockersToDelete.Add((heldItem, virtualItem));
            }
        }

        for (var i = 0; i < _virtualBlockersToDelete.Count; i++)
        {
            _virtualItem.DeleteVirtualItem(_virtualBlockersToDelete[i], holderUid);
        }
    }

    private int GetRequiredHolderCount(EntityUid target)
    {
        if (_bodyQuery.TryComp(target, out var body))
        {
            var handCount = 0;
            foreach (var _ in _body.GetBodyChildrenOfType(target, BodyPartType.Hand, body))
            {
                handCount++;
            }

            if (handCount > 0)
                return handCount;
        }

        return 2;
    }

    private void CopyConfig(ScpHoldComponent source, ScpHeldComponent target)
    {
        target.SoftEscapeCooldown = source.SoftEscapeCooldown;
        target.FullHoldDelay = source.FullHoldDelay;
        target.FullBreakoutDuration = source.FullBreakoutDuration;
        target.PostBreakoutImmunity = source.PostBreakoutImmunity;
        target.HoldRange = source.HoldRange;
        target.WalkModifier = source.WalkModifier;
        target.SprintModifier = source.SprintModifier;
        target.SoftEscapeAvailableAt = _timing.CurTime;
        target.FullHoldStartedAt = null;
    }

    private float GetDesiredSoftDragDistance(Entity<ScpHeldComponent> held)
    {
        return GetBaseSoftDragDistance(held.Comp.HoldRange);
    }

    private static float GetHoldMaintenanceRange(float configuredRange, float desiredSoftDragDistance)
    {
        return MathF.Max(MathF.Max(configuredRange, SharedInteractionSystem.InteractionRange), desiredSoftDragDistance + SoftDragSnapTolerance);
    }

    private static float GetBaseSoftDragDistance(float holdRange)
    {
        return Math.Clamp(holdRange * SoftDragDistanceFactor, SoftDragMinimumDistance, SoftDragMaximumDistance);
    }

    private float GetSoftDragCatchUpTime()
    {
        return MathF.Max((float) _timing.TickPeriod.TotalSeconds, SoftDragCatchUpTime);
    }

    private Vector2 GetSoftDragDirection(EntityUid holderUid, Vector2 holderVelocity, Vector2 offset, float distance)
    {
        if (distance > SoftDragSnapTolerance)
            return offset / distance;

        if (holderVelocity.LengthSquared() > SoftDragVelocityDirectionThreshold * SoftDragVelocityDirectionThreshold)
            return -Vector2.Normalize(holderVelocity);

        return Transform(holderUid).LocalRotation.ToWorldVec();
    }

    private void ApplyHeldVelocity(EntityUid uid, Vector2 desiredVelocity, PhysicsComponent physics)
    {
        if (Vector2.DistanceSquared(physics.LinearVelocity, desiredVelocity) > SoftDragVelocityTolerance * SoftDragVelocityTolerance)
            _physics.SetLinearVelocity(uid, desiredVelocity, body: physics);

        if (!MathHelper.CloseTo(physics.AngularVelocity, 0f))
            _physics.SetAngularVelocity(uid, 0f, body: physics);
    }

    private void ZeroHeldVelocity(EntityUid uid)
    {
        if (!_physicsQuery.TryComp(uid, out var physics))
            return;

        if (physics.LinearVelocity == Vector2.Zero && MathHelper.CloseTo(physics.AngularVelocity, 0f))
            return;

        _physics.SetLinearVelocity(uid, Vector2.Zero, body: physics);
        _physics.SetAngularVelocity(uid, 0f, body: physics);
    }

    private void CancelBreakoutDoAfter(Entity<ScpHeldComponent> held)
    {
        if (held.Comp.BreakoutDoAfterId == null)
            return;

        _doAfter.Cancel(held.Owner, held.Comp.BreakoutDoAfterId.Value);
        SetBreakoutDoAfterId(held, null);
    }

    private void SetBreakoutDoAfterId(Entity<ScpHeldComponent> held, ushort? breakoutDoAfterId)
    {
        if (held.Comp.BreakoutDoAfterId == breakoutDoAfterId)
            return;

        held.Comp.BreakoutDoAfterId = breakoutDoAfterId;
        Dirty(held);
    }

    private void PopupHolder(EntityUid holder, string key, params (string, object)[] args)
    {
        if (_net.IsClient)
            return;

        _popup.PopupEntity(Loc.GetString(key, args), holder, holder);
    }

    private void PopupTarget(EntityUid target, string key, params (string, object)[] args)
    {
        if (_net.IsClient)
            return;

        _popup.PopupEntity(Loc.GetString(key, args), target, target);
    }
}
