using Content.Shared.Coordinates;
using Content.Shared.Popups;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;

namespace Content.Shared._Scp.Holding;

public abstract partial class SharedScpHoldingSystem
{
    /*
     * Feedback-local dependencies, breakout do-after tracking, and popup/audio helpers.
     */

    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    private const string BreakoutAttemptEffect = "WhistleExclamation";
    private static readonly SoundSpecifier BreakoutAttemptSound =
        new SoundCollectionSpecifier("storageRustle",
            AudioParams.Default.WithVolume(-8f).WithMaxDistance(4f).WithVariation(0.15f));

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
        DirtyHeldField(held, nameof(ScpHeldComponent.BreakoutDoAfterId));
    }

    private void ShowBreakoutAttemptFeedback(Entity<ScpHeldComponent> held)
    {
        if (_net.IsClient && !_timing.IsFirstTimePredicted)
            return;

        foreach (var holderUid in held.Comp.Holders)
        {
            if (!_holderQuery.TryComp(holderUid, out var holder))
                continue;

            if (holder.Target != held.Owner)
                continue;

            SpawnBreakoutAttemptEffect(holderUid);
        }

        PlayBreakoutAttemptSound(held.Owner);
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

    private void SpawnBreakoutAttemptEffect(EntityUid holderUid)
    {
        if (_net.IsClient)
        {
            PredictedSpawnAttachedTo(BreakoutAttemptEffect, holderUid.ToCoordinates());
            return;
        }

        SpawnAttachedTo(BreakoutAttemptEffect, holderUid.ToCoordinates());
    }

    private void PlayBreakoutAttemptSound(EntityUid targetUid)
    {
        if (_net.IsClient)
        {
            _audio.PlayPredicted(BreakoutAttemptSound, targetUid, targetUid);
            return;
        }

        _audio.PlayPvs(BreakoutAttemptSound, targetUid);
    }
}
