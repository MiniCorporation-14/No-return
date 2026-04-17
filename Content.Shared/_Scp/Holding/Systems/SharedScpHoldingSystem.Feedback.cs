using Content.Shared._Scp.Holding.Components;
using Content.Shared.Coordinates;
using Content.Shared.Popups;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Shared._Scp.Holding.Systems;

public abstract partial class SharedScpHoldingSystem
{
    /*
     * Feedback-local dependencies plus popup/audio helpers.
     */

    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    private void ShowBreakoutAttemptFeedback(Entity<ScpHeldComponent> held)
    {
        if (!CanShowBreakoutAttemptFeedback())
            return;

        if (!TryComp<ScpHoldableComponent>(held.Owner, out var holdable))
            return;

        foreach (var holderUid in held.Comp.Holders)
        {
            if (!_holderQuery.TryComp(holderUid, out var holder))
                continue;

            if (holder.Target != held.Owner)
                continue;

            SpawnBreakoutAttemptEffect(holderUid, holdable.BreakoutAttemptEffect);
        }

        PlayBreakoutAttemptSound(held.Owner, holdable.BreakoutAttemptSound);
    }

    private void PopupHolder(EntityUid holder, string key, params (string, object)[] args)
    {
        if (!ShouldShowHoldPopups)
            return;

        _popup.PopupEntity(Loc.GetString(key, args), holder, holder);
    }

    private void PopupTarget(EntityUid target, string key, params (string, object)[] args)
    {
        if (!ShouldShowHoldPopups)
            return;

        _popup.PopupEntity(Loc.GetString(key, args), target, target);
    }

    private void SpawnBreakoutAttemptEffect(EntityUid holderUid, EntProtoId? effect)
    {
        if (effect == null)
            return;

        if (ShouldUsePredictedBreakoutFeedback)
        {
            PredictedSpawnAttachedTo(effect.Value, holderUid.ToCoordinates());
            return;
        }

        SpawnAttachedTo(effect.Value, holderUid.ToCoordinates());
    }

    private void PlayBreakoutAttemptSound(EntityUid targetUid, SoundSpecifier? sound)
    {
        if (sound == null)
            return;

        if (ShouldUsePredictedBreakoutFeedback)
        {
            _audio.PlayPredicted(sound, targetUid, targetUid);
            return;
        }

        _audio.PlayPvs(sound, targetUid);
    }

    protected virtual bool ShouldShowHoldPopups => false;

    protected virtual bool ShouldUsePredictedBreakoutFeedback => false;

    protected virtual bool CanShowBreakoutAttemptFeedback() => true;
}
