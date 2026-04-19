using Content.Shared.Coordinates;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Spawners;

namespace Content.Shared._Scp.Other.WorldAlert;

public sealed class WorldAlertSystem : EntitySystem
{
    private const float DefaultLifetimeSeconds = 1f;

    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly INetManager _net = default!;

    public bool TrySpawnAlert(EntityUid target, WorldAlertSettings settings, EntityUid? soundReceiver = null)
    {
        if (settings.Prototype == null)
            return false;

        var alert = PredictedSpawnAttachedTo(settings.Prototype, target.ToCoordinates());
        EnsureTimedDespawn(alert, settings.Lifetime);

        soundReceiver ??= target;
        if (settings.DirectSound && _net.IsServer)
            _audio.PlayEntity(settings.Sound, target, soundReceiver.Value);
        else
            _audio.PlayPredicted(settings.Sound, target, soundReceiver.Value);

        return true;
    }

    private void EnsureTimedDespawn(EntityUid uid, TimeSpan? lifetime)
    {
        if (HasComp<TimedDespawnComponent>(uid))
            return;

        var despawn = EnsureComp<TimedDespawnComponent>(uid);
        despawn.Lifetime = lifetime.HasValue
            ? (float) lifetime.Value.TotalSeconds
            : DefaultLifetimeSeconds;
    }
}
