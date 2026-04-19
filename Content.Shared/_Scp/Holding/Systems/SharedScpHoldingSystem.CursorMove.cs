using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Shared._Scp.Holding.Components;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;

namespace Content.Shared._Scp.Holding.Systems;

public abstract partial class SharedScpHoldingSystem
{
    /*
     * Cursor-move input validation, per-holder cursor intent, and cursor-based movement helpers.
     */

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

        if (!CanMoveHeldToCursor(holderUid, cursorCoords, out _, out var clampedCoords))
            return false;

        SetHolderCursorMoveState((holderUid, activeHolder), clampedCoords, active: true);
        return true;
    }

    private bool CanMoveHeldToCursor(
        EntityUid holderUid,
        EntityCoordinates cursorCoords,
        [NotNullWhen(true)] out Entity<ActiveScpHoldableComponent>? held,
        out EntityCoordinates clampedCoords,
        bool quiet = false)
    {
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

    private bool TryGetHolderCursorDesiredVelocity(
        Entity<ActiveScpHolderComponent> holder,
        Entity<ActiveScpHoldableComponent> held,
        ScpHoldableComponent holdable,
        float maintenanceRange,
        PhysicsComponent heldPhysics,
        out Vector2 desiredVelocity)
    {
        desiredVelocity = Vector2.Zero;

        if (!TryGetValidatedHolderCursorMoveState(holder, held, maintenanceRange, out var targetCoordinates))
            return false;

        var targetCoords = _transform.ToMapCoordinates(targetCoordinates);
        var heldCoords = _transform.GetMapCoordinates(held);

        if (targetCoords.MapId != heldCoords.MapId)
        {
            ClearHolderCursorMoveState(holder);
            return false;
        }

        var correction = targetCoords.Position - heldCoords.Position;
        var correctionDistance = correction.Length();

        if (!holder.Comp.CursorMoveActive && correctionDistance > holdable.SoftDragSettleTolerance)
        {
            holder.Comp.CursorMoveActive = true;
            Dirty(holder);
        }

        if (correctionDistance <= holdable.SoftDragSettleTolerance)
        {
            if (holder.Comp.CursorMoveActive)
            {
                holder.Comp.CursorMoveActive = false;
                Dirty(holder);
            }

            return true;
        }

        var correctionDirection = correction / correctionDistance;
        var correctionSpeed = Math.Min(
            correctionDistance / GetSoftDragCatchUpTime(holdable),
            holdable.SoftDragMaximumCorrectionSpeed);

        desiredVelocity = correctionDirection * correctionSpeed;

        var relativeVelocity = heldPhysics.LinearVelocity;
        var awaySpeed = MathF.Max(0f, -Vector2.Dot(relativeVelocity, correctionDirection));
        if (awaySpeed > 0f)
            desiredVelocity += correctionDirection * awaySpeed * holdable.SoftDragAwayVelocityStrength;

        return true;
    }

    private bool TryGetValidatedHolderCursorMoveState(
        Entity<ActiveScpHolderComponent> holder,
        Entity<ActiveScpHoldableComponent> held,
        float maintenanceRange,
        out EntityCoordinates targetCoordinates)
    {
        targetCoordinates = EntityCoordinates.Invalid;

        if (holder.Comp.Target != held)
        {
            ClearHolderCursorMoveState(holder);
            return false;
        }

        if (!held.Comp.Holders.Contains(holder.Owner))
        {
            ClearHolderCursorMoveState(holder);
            return false;
        }

        if (!holder.Comp.CursorTargetCoordinates.IsValid(EntityManager))
            return false;

        if (!_container.IsInSameOrNoContainer(holder.Owner, held.Owner))
        {
            ClearHolderCursorMoveState(holder);
            return false;
        }

        var holderCoords = _transform.GetMapCoordinates(holder.Owner);
        var targetCoords = _transform.ToMapCoordinates(holder.Comp.CursorTargetCoordinates);

        if (holderCoords.MapId != targetCoords.MapId)
        {
            ClearHolderCursorMoveState(holder);
            return false;
        }

        if ((targetCoords.Position - holderCoords.Position).LengthSquared() > maintenanceRange * maintenanceRange)
        {
            ClearHolderCursorMoveState(holder);
            return false;
        }

        targetCoordinates = holder.Comp.CursorTargetCoordinates;
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

    private void SetHolderCursorMoveState(
        Entity<ActiveScpHolderComponent> holder,
        EntityCoordinates targetCoordinates,
        bool active)
    {
        if (holder.Comp.CursorTargetCoordinates == targetCoordinates
            && holder.Comp.CursorMoveActive == active)
        {
            return;
        }

        holder.Comp.CursorTargetCoordinates = targetCoordinates;
        holder.Comp.CursorMoveActive = active;
        Dirty(holder);
    }

    private void ClearHolderCursorMoveState(EntityUid holderUid)
    {
        if (_activeHolderQuery.TryComp(holderUid, out var holder))
            ClearHolderCursorMoveState((holderUid, holder));
    }

    private void ClearHolderCursorMoveState(Entity<ActiveScpHolderComponent> holder)
    {
        if (holder.Comp.CursorTargetCoordinates == EntityCoordinates.Invalid
            && !holder.Comp.CursorMoveActive)
        {
            return;
        }

        holder.Comp.CursorTargetCoordinates = EntityCoordinates.Invalid;
        holder.Comp.CursorMoveActive = false;
        Dirty(holder);
    }

    private void ClearHeldCursorMoveStates(Entity<ActiveScpHoldableComponent> held)
    {
        foreach (var holderUid in held.Comp.Holders)
        {
            ClearHolderCursorMoveState(holderUid);
        }
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

        ClearHolderCursorMoveState(ent);
    }
}
