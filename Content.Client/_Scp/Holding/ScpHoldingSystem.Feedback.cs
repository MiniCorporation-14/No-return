using Content.Shared._Scp.Holding.Components;
using Content.Shared.Coordinates;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;

namespace Content.Client._Scp.Holding;

public sealed partial class ScpHoldingSystem
{
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    protected override void Popup(EntityUid target, string key, params (string, object)[] args)
    {
    }

    protected override void ShowBreakoutAttemptFeedback(Entity<ActiveScpHoldableComponent> held)
    {
        if (!_timing.IsFirstTimePredicted)
            return;

        if (!TryComp<ScpHoldableComponent>(held, out var holdable))
            return;

        foreach (var holderUid in held.Comp.Holders)
        {
            if (!TryComp<ActiveScpHolderComponent>(holderUid, out var holder))
                continue;

            if (holder.Target != held.Owner)
                continue;

            SpawnBreakoutAttemptEffect(holderUid, holdable.BreakoutAttemptEffect);
        }

        PlayBreakoutAttemptSound(held.Owner, holdable.BreakoutAttemptSound);
    }

    private void SpawnBreakoutAttemptEffect(EntityUid holderUid, EntProtoId? effect)
    {
        if (effect == null)
            return;

        PredictedSpawnAttachedTo(effect.Value, holderUid.ToCoordinates());
    }

    private void PlayBreakoutAttemptSound(EntityUid targetUid, SoundSpecifier? sound)
    {
        if (sound == null)
            return;

        _audio.PlayPredicted(sound, targetUid, targetUid);
    }
}
