using System.Numerics;
using Content.Shared.Interaction;
using Robust.Shared.Physics.Components;

namespace Content.Shared._Scp.Holding;

public sealed partial class SharedScpHoldingSystem
{
    /*
     * Drag-local dependencies, soft-drag movement, and helper calculations.
     */
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private const float SoftDragDistanceFactor = 0.3f;
    private const float SoftDragMinimumDistance = 0.4f;
    private const float SoftDragMaximumDistance = 0.6f;
    private const float SoftDragSnapTolerance = 0.03f;
    private const float SoftDragSettleTolerance = 0.08f;
    private const float SoftDragVelocityDirectionThreshold = 0.05f;
    private const float SoftDragCatchUpTime = 0.05f;
    private const float SoftDragMaximumCorrectionSpeed = 6f;
    private const float SoftDragAwayVelocityStrength = 0.6f;
    private const float SoftDragVelocityTolerance = 0.05f;

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
        return MathF.Max((float)_timing.TickPeriod.TotalSeconds, SoftDragCatchUpTime);
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
}
