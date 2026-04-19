using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Shared._Scp.Holding.Components;
using Robust.Shared.Map;

namespace Content.Shared._Scp.Holding.Systems;

public abstract partial class SharedScpHoldingSystem
{
    /*
     * Cursor-move input validation, runtime state, and movement update helpers.
     */

    private EntityQuery<ActiveStateScpHoldableCursorMoveComponent> _activeHoldableCursorMoveQuery;

    private void InitializeCursorMoveQueries()
    {
        _activeHoldableCursorMoveQuery = GetEntityQuery<ActiveStateScpHoldableCursorMoveComponent>();
    }

    private void InitializeCursorMoveEvents()
    {
        SubscribeLocalEvent<ActiveScpHolderComponent, MoveEvent>(OnHolderMove);
    }

    public bool TryMoveHeldToCursor(EntityUid holderUid, EntityCoordinates cursorCoords)
    {
        if (!_activeHolderQuery.TryComp(holderUid, out var activeHolder))
            return false;

        if (activeHolder.Target == null)
            return false;

        if (!CanMoveHeldToCursor(holderUid, cursorCoords, out var held, out var clampedCoords))
            return false;

        SetCursorMoveState(held.Value, holderUid, clampedCoords, active: true);
        return true;
    }

    private bool CanMoveHeldToCursor(
        EntityUid holderUid,
        EntityCoordinates cursorCoords,
        [NotNullWhen(true)] out Entity<ActiveScpHoldableComponent>? held,
        out EntityCoordinates clampedCoords,
        bool quiet = false)
    {
        _ = quiet;
        held = null;
        clampedCoords = EntityCoordinates.Invalid;

        if (!_activeHolderQuery.TryComp(holderUid, out var holder))
            return false;

        if (holder.Target is not { } heldUid)
            return false;

        if (!_activeHoldableQuery.TryComp(heldUid, out var heldComponent))
            return false;

        if (!heldComponent.Holders.Contains(holderUid))
            return false;

        if (_activeHoldableFullHoldStateQuery.HasComp(heldUid))
            return false;

        held = (heldUid, heldComponent);

        if (!TryGetHeldHoldable(held.Value, out var holdable))
            return false;

        if (!_container.IsInSameOrNoContainer(holderUid, heldUid))
            return false;

        var desiredSoftDragDistance = GetDesiredSoftDragDistance(holdable);
        var maintenanceRange = GetHoldMaintenanceRange(holdable, desiredSoftDragDistance);

        if (!_interaction.InRangeUnobstructed(holderUid, heldUid, maintenanceRange))
            return false;

        return TryClampHeldCursorMoveTargetCoordinates(holderUid, cursorCoords, maintenanceRange, out clampedCoords);
    }

    private bool TryGetValidatedCursorMoveState(
        Entity<ActiveScpHoldableComponent> held,
        ScpHoldableComponent holdable,
        [NotNullWhen(true)] out ActiveStateScpHoldableCursorMoveComponent? cursorMove,
        out EntityUid holderUid)
    {
        cursorMove = null;
        holderUid = default;

        if (!_activeHoldableCursorMoveQuery.TryComp(held, out var moveState))
            return false;

        if (!moveState.TargetCoordinates.IsValid(EntityManager))
        {
            ClearCursorMoveState(held);
            return false;
        }

        if (_activeHoldableFullHoldStateQuery.HasComp(held))
        {
            ClearCursorMoveState(held);
            return false;
        }

        if (!_activeHolderQuery.TryComp(moveState.Holder, out var holder))
        {
            ClearCursorMoveState(held);
            return false;
        }

        if (holder.Target != held)
        {
            ClearCursorMoveState(held);
            return false;
        }

        if (!held.Comp.Holders.Contains(moveState.Holder))
        {
            ClearCursorMoveState(held);
            return false;
        }

        if (!_container.IsInSameOrNoContainer(moveState.Holder, held.Owner))
        {
            ClearCursorMoveState(held);
            return false;
        }

        var desiredSoftDragDistance = GetDesiredSoftDragDistance(holdable);
        var maintenanceRange = GetHoldMaintenanceRange(holdable, desiredSoftDragDistance);
        var holderCoords = _transform.GetMapCoordinates(moveState.Holder);
        var targetCoords = _transform.ToMapCoordinates(moveState.TargetCoordinates);

        if (holderCoords.MapId != targetCoords.MapId)
        {
            ClearCursorMoveState(held);
            return false;
        }

        if ((targetCoords.Position - holderCoords.Position).LengthSquared() > maintenanceRange * maintenanceRange)
        {
            ClearCursorMoveState(held);
            return false;
        }

        cursorMove = moveState;
        holderUid = moveState.Holder;
        return true;
    }

    private bool TryClampHeldCursorMoveTargetCoordinates(
        EntityUid holderUid,
        EntityCoordinates cursorCoords,
        float maintenanceRange,
        out EntityCoordinates clampedCoords)
    {
        clampedCoords = EntityCoordinates.Invalid;

        if (!cursorCoords.IsValid(EntityManager))
            return false;

        var holderCoords = _transform.GetMapCoordinates(holderUid);
        var cursorMapCoords = _transform.ToMapCoordinates(cursorCoords);

        if (holderCoords.MapId != cursorMapCoords.MapId)
            return false;

        var offset = cursorMapCoords.Position - holderCoords.Position;
        var distance = offset.Length();
        var clampedPosition = cursorMapCoords.Position;

        if (distance > maintenanceRange && distance > 0f)
            clampedPosition = holderCoords.Position + offset / distance * maintenanceRange;

        clampedCoords = _transform.ToCoordinates(new MapCoordinates(clampedPosition, holderCoords.MapId));
        return clampedCoords.IsValid(EntityManager);
    }

    private void SetCursorMoveState(
        Entity<ActiveScpHoldableComponent> held,
        EntityUid holderUid,
        EntityCoordinates targetCoordinates,
        bool active)
    {
        var cursorMove = EnsureComp<ActiveStateScpHoldableCursorMoveComponent>(held);
        if (cursorMove.Holder == holderUid &&
            cursorMove.TargetCoordinates == targetCoordinates &&
            cursorMove.Active == active)
        {
            return;
        }

        cursorMove.Holder = holderUid;
        cursorMove.TargetCoordinates = targetCoordinates;
        cursorMove.Active = active;
        Dirty(held, cursorMove);
    }

    private void ClearCursorMoveState(EntityUid heldUid)
    {
        if (_activeHoldableCursorMoveQuery.HasComp(heldUid))
            RemComp<ActiveStateScpHoldableCursorMoveComponent>(heldUid);
    }

    private void UpdateCursorMoveDrag(
        Entity<ActiveScpHoldableComponent> held,
        ScpHoldableComponent holdable,
        EntityUid holderUid,
        ActiveStateScpHoldableCursorMoveComponent cursorMove)
    {
        if (!_physicsQuery.TryComp(held, out var heldPhysics))
            return;

        if (!_container.IsInSameOrNoContainer(holderUid, held.Owner))
        {
            ClearCursorMoveState(held);
            return;
        }

        var targetCoords = _transform.ToMapCoordinates(cursorMove.TargetCoordinates);
        var heldCoords = _transform.GetMapCoordinates(held);

        if (targetCoords.MapId != heldCoords.MapId)
        {
            ClearCursorMoveState(held);
            return;
        }

        var correction = targetCoords.Position - heldCoords.Position;
        var correctionDistance = correction.Length();

        if (!cursorMove.Active && correctionDistance > holdable.SoftDragSettleTolerance)
        {
            cursorMove.Active = true;
            Dirty(held, cursorMove);
        }

        Vector2 desiredVelocity;
        if (correctionDistance <= holdable.SoftDragSettleTolerance)
        {
            if (cursorMove.Active)
            {
                cursorMove.Active = false;
                Dirty(held, cursorMove);
            }

            desiredVelocity = Vector2.Zero;
        }
        else
        {
            var correctionDirection = correction / correctionDistance;
            var correctionSpeed = Math.Min(
                correctionDistance / GetSoftDragCatchUpTime(holdable),
                holdable.SoftDragMaximumCorrectionSpeed);

            desiredVelocity = correctionDirection * correctionSpeed;

            var relativeVelocity = heldPhysics.LinearVelocity;
            var awaySpeed = MathF.Max(0f, -Vector2.Dot(relativeVelocity, correctionDirection));
            if (awaySpeed > 0f)
                desiredVelocity += correctionDirection * awaySpeed * holdable.SoftDragAwayVelocityStrength;
        }

        ApplyHeldVelocity(held, desiredVelocity, heldPhysics, holdable);
    }

    private void OnHolderMove(Entity<ActiveScpHolderComponent> ent, ref MoveEvent args)
    {
        if (_timing.ApplyingState)
            return;

        if (ent.Comp.Target == null)
            return;

        if (args.NewPosition.EntityId == args.OldPosition.EntityId &&
            (args.NewPosition.Position - args.OldPosition.Position).LengthSquared() <= 0f)
        {
            return;
        }

        ClearCursorMoveState(ent.Comp.Target.Value);
    }
}
