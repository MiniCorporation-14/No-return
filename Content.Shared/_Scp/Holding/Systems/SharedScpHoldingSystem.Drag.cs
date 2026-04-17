using System.Numerics;
using Content.Shared._Scp.Holding.Components;
using Content.Shared.Interaction;
using Robust.Shared.Physics.Components;

namespace Content.Shared._Scp.Holding.Systems;

public abstract partial class SharedScpHoldingSystem
{
    /*
     * Drag-local dependencies, soft-drag movement, and helper calculations.
     */

    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private void UpdateSoftDrag(Entity<ActiveScpHoldableComponent> held, ScpHoldableComponent holdable, float maintenanceRange, float desiredDistance)
    {
        if (held.Comp.PrimaryHolder == null)
            return;

        var primaryHolder = held.Comp.PrimaryHolder.Value;
        if (!_activeHolderQuery.TryComp(primaryHolder, out var holder))
            return;

        if (holder.Target != held.Owner)
            return;

        if (!_container.IsInSameOrNoContainer(primaryHolder, held.Owner))
            return;

        if (!_interaction.InRangeUnobstructed(primaryHolder, held.Owner, maintenanceRange))
            return;

        if (!_physicsQuery.TryComp(held.Owner, out var heldPhysics))
            return;

        var holderCoords = _transform.GetMapCoordinates(primaryHolder);
        var heldCoords = _transform.GetMapCoordinates(held.Owner);

        if (holderCoords.MapId != heldCoords.MapId)
            return;

        var offset = heldCoords.Position - holderCoords.Position;
        var distance = offset.Length();
        var holderVelocity = _physicsQuery.TryComp(primaryHolder, out var holderPhysics)
            ? holderPhysics.LinearVelocity
            : Vector2.Zero;
        var velocityDirectionThresholdSquared = holdable.SoftDragVelocityDirectionThreshold * holdable.SoftDragVelocityDirectionThreshold;
        var direction = GetSoftDragDirection(primaryHolder, holdable, holderVelocity, offset, distance, velocityDirectionThresholdSquared);
        var desiredPosition = holderCoords.Position + direction * desiredDistance;
        var correction = desiredPosition - heldCoords.Position;
        var correctionDistance = correction.Length();

        Vector2 desiredVelocity;
        if (correctionDistance <= holdable.SoftDragSettleTolerance)
        {
            desiredVelocity = holderVelocity.LengthSquared() > velocityDirectionThresholdSquared
                ? holderVelocity
                : Vector2.Zero;
        }
        else
        {
            var correctionDirection = correction / correctionDistance;
            var correctionSpeed = Math.Min(correctionDistance / GetSoftDragCatchUpTime(holdable), holdable.SoftDragMaximumCorrectionSpeed);
            desiredVelocity = holderVelocity + correctionDirection * correctionSpeed;

            var relativeVelocity = heldPhysics.LinearVelocity - holderVelocity;
            var awaySpeed = MathF.Max(0f, -Vector2.Dot(relativeVelocity, correctionDirection));
            if (awaySpeed > 0f)
                desiredVelocity += correctionDirection * awaySpeed * holdable.SoftDragAwayVelocityStrength;
        }

        ApplyHeldVelocity(held.Owner, desiredVelocity, heldPhysics, holdable);
    }

    private static float GetDesiredSoftDragDistance(ScpHoldableComponent holdable)
    {
        return GetBaseSoftDragDistance(holdable);
    }

    private static float GetHoldMaintenanceRange(ScpHoldableComponent holdable, float desiredSoftDragDistance)
    {
        return MathF.Max(MathF.Max(holdable.HoldRange, SharedInteractionSystem.InteractionRange), desiredSoftDragDistance + holdable.SoftDragSnapTolerance);
    }

    private static float GetBaseSoftDragDistance(ScpHoldableComponent holdable)
    {
        return Math.Clamp(holdable.HoldRange * holdable.SoftDragDistanceFactor, holdable.SoftDragMinimumDistance, holdable.SoftDragMaximumDistance);
    }

    private float GetSoftDragCatchUpTime(ScpHoldableComponent holdable)
    {
        return MathF.Max((float)_timing.TickPeriod.TotalSeconds, holdable.SoftDragCatchUpTime);
    }

    private Vector2 GetSoftDragDirection(EntityUid holderUid, ScpHoldableComponent holdable, Vector2 holderVelocity, Vector2 offset, float distance, float velocityDirectionThresholdSquared)
    {
        if (distance > holdable.SoftDragSnapTolerance)
            return offset / distance;

        if (holderVelocity.LengthSquared() > velocityDirectionThresholdSquared)
            return -Vector2.Normalize(holderVelocity);

        return Transform(holderUid).LocalRotation.ToWorldVec();
    }

    private void ApplyHeldVelocity(EntityUid uid, Vector2 desiredVelocity, PhysicsComponent physics, ScpHoldableComponent holdable)
    {
        if (Vector2.DistanceSquared(physics.LinearVelocity, desiredVelocity) > holdable.SoftDragVelocityTolerance * holdable.SoftDragVelocityTolerance)
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
}
