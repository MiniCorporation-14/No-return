using System.Diagnostics.CodeAnalysis;
using Content.Server.Chat.Systems;
using Content.Server.Examine;
using Content.Server.Mind;
using Content.Server.Popups;
using Content.Shared._Scp.Knowledge;
using Content.Shared._Scp.Knowledge.Components;
using Content.Shared.Examine;
using Content.Shared.Mind;
using Content.Shared.Paper;
using Content.Shared.UserInterface;
using Content.Shared.Verbs;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server._Scp.Knowledge;

public sealed partial class ScpKnowledgeSystem : EntitySystem
{
    [Dependency] private readonly ExamineSystem _examine = default!;
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;

    private readonly Dictionary<EntProtoId, List<ProtoId<ScpKnowledgePrototype>>> _knowledgeByEntityPrototype = new();
    private readonly List<CachedKnowledgePhrase> _knowledgePhrases = [];
    private readonly HashSet<ProtoId<ScpKnowledgePrototype>> _matchedKnowledgeBuffer = [];

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<ScpKnowledgePaperReadEvent>(OnPaperRead);
        SubscribeLocalEvent<PaperComponent, AfterActivatableUIOpenEvent>(OnPaperUiOpened);
        SubscribeLocalEvent<MetaDataComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<MetaDataComponent, GetVerbsEvent<ExamineVerb>>(OnGetExamineVerbs);
        SubscribeLocalEvent<ScpKnowledgeSpeechHeardEvent>(OnSpeechHeard);
        SubscribeLocalEvent<RadioSpokeEvent>(OnRadioSpoke);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);

        RebuildCaches();
    }

    public bool TryGetKnowledgeState(
        EntityUid uid,
        [NotNullWhen(true)] out EntityUid? holderUid,
        [NotNullWhen(true)] out ScpKnowledgeComponent? knowledge)
    {
        if (TryComp(uid, out knowledge))
        {
            holderUid = uid;
            return true;
        }

        if (TryComp<MindComponent>(uid, out var mind) &&
            mind.CurrentEntity is { } currentEntity &&
            TryComp(currentEntity, out knowledge))
        {
            holderUid = currentEntity;
            return true;
        }

        if (!_mind.TryGetMind(uid, out var resolvedMindUid, out _))
        {
            holderUid = null;
            knowledge = null;
            return false;
        }

        if (TryComp(resolvedMindUid, out knowledge))
        {
            holderUid = resolvedMindUid;
            return true;
        }

        if (TryComp<MindComponent>(resolvedMindUid, out var resolvedMind) &&
            resolvedMind.CurrentEntity is { } resolvedCurrentEntity &&
            TryComp(resolvedCurrentEntity, out knowledge))
        {
            holderUid = resolvedCurrentEntity;
            return true;
        }

        holderUid = null;
        knowledge = null;
        return false;
    }

    public bool HasKnowledge(EntityUid uid, ProtoId<ScpKnowledgePrototype> knowledgeId)
    {
        if (!TryGetKnowledgeState(uid, out _, out var knowledgeState))
            return false;

        var knowledge = _prototype.Index(knowledgeId);
        return IsKnowledgeKnown(knowledgeState, knowledgeId, knowledge);
    }

    public bool TryGetKnowledgeProgress(EntityUid uid, ProtoId<ScpKnowledgePrototype> knowledgeId, out int progress)
    {
        progress = 0;

        if (!TryGetKnowledgeState(uid, out _, out var knowledge))
            return false;

        var knowledgePrototype = _prototype.Index(knowledgeId);
        progress = GetKnowledgeProgress(knowledge, knowledgeId, knowledgePrototype);
        return progress > 0;
    }

    public bool TryGrantKnowledgeProgress(
        EntityUid uid,
        ProtoId<ScpKnowledgePrototype> knowledgeId,
        int progress,
        ScpKnowledgeAcquisitionChannel channel = ScpKnowledgeAcquisitionChannel.Other,
        EntityUid? source = null,
        string? normalizedMessage = null)
    {
        return TryGrantKnowledgeProgress(uid, knowledgeId, progress, channel, source, true, normalizedMessage);
    }

    public bool TryGetUnknownExamineMessage(
        EntityUid examiner,
        EntityUid target,
        [NotNullWhen(true)] out FormattedMessage? message)
    {
        message = null;

        if (!TryGetKnowledgeIdsForEntity(target, out var knowledgeIds))
            return false;

        var requiresKnowledge = false;

        foreach (var knowledgeId in knowledgeIds)
        {
            var knowledge = _prototype.Index(knowledgeId);
            if (!knowledge.HideIdentityUntilKnown)
                continue;

            requiresKnowledge = true;
            if (HasKnowledge(examiner, knowledgeId))
                return false;
        }

        if (!requiresKnowledge)
            return false;

        message = new FormattedMessage();
        message.AddText(Loc.GetString("scp-knowledge-unknown-examine"));
        return true;
    }

    public bool TryGetKnownExamineNameOverride(
        EntityUid examiner,
        EntityUid target,
        [NotNullWhen(true)] out string? nameOverride)
    {
        nameOverride = null;

        if (!TryGetKnowledgeIdsForEntity(target, out var knowledgeIds))
            return false;

        foreach (var knowledgeId in knowledgeIds)
        {
            var knowledge = _prototype.Index(knowledgeId);
            if (!knowledge.HideIdentityUntilKnown || !HasKnowledge(examiner, knowledgeId))
                continue;

            nameOverride = Loc.GetString(knowledge.DisplayName);
            return true;
        }

        return false;
    }

    public bool TryGrantExamineKnowledge(EntityUid examiner, EntityUid target)
    {
        if (!TryGetKnowledgeIdsForEntity(target, out var knowledgeIds))
            return false;

        var changed = false;

        foreach (var knowledgeId in knowledgeIds)
        {
            var knowledge = _prototype.Index(knowledgeId);
            if (knowledge.ExamineProgress <= 0)
                continue;

            changed |= TryGrantKnowledgeProgress(
                examiner,
                knowledgeId,
                knowledge.ExamineProgress,
                ScpKnowledgeAcquisitionChannel.Examine,
                target);
        }

        return changed;
    }

    private bool TryGrantKnowledgeProgress(
        EntityUid uid,
        ProtoId<ScpKnowledgePrototype> knowledgeId,
        int progress,
        ScpKnowledgeAcquisitionChannel channel,
        EntityUid? source,
        bool showUnlockFeedback,
        string? normalizedMessage)
    {
        if (progress <= 0)
            return false;

        if (!TryGetKnowledgeState(uid, out var holderUid, out var knowledgeState))
            return false;

        if (!TryComp<ScpKnowledgeAcquisitionComponent>(holderUid.Value, out var acquisition))
            return false;

        var knowledge = _prototype.Index(knowledgeId);

        if (!CanAcquireKnowledge(acquisition, knowledge, channel))
            return false;

        if (IsKnowledgeKnown(knowledgeState, knowledgeId, knowledge))
            return false;

        if (source != null &&
            channel is ScpKnowledgeAcquisitionChannel.Read or ScpKnowledgeAcquisitionChannel.Examine &&
            !knowledgeState.ProcessedSources.Add(new ScpKnowledgeSourceRecord(source.Value, knowledgeId, channel)))
        {
            return false;
        }

        if (source != null &&
            channel == ScpKnowledgeAcquisitionChannel.Listen &&
            !string.IsNullOrEmpty(normalizedMessage) &&
            !knowledgeState.ProcessedMessages.Add(new ScpKnowledgeMessageRecord(
                source.Value,
                knowledgeId,
                channel,
                normalizedMessage)))
        {
            return false;
        }

        var currentProgress = GetKnowledgeProgress(knowledgeState, knowledgeId, knowledge);
        var currentExposureFlags = GetExposureFlags(knowledgeState, knowledgeId);
        var updatedExposureFlags = currentExposureFlags;

        if (ScpKnowledgeLogic.RequiresTextAndExamine(knowledge) &&
            channel != ScpKnowledgeAcquisitionChannel.Other)
        {
            updatedExposureFlags |= ScpKnowledgeLogic.GetExposureFlags(channel);
            progress = ScpKnowledgeLogic.GetExposureProgress(updatedExposureFlags);
        }

        var clampedProgress = ScpKnowledgeLogic.RequiresTextAndExamine(knowledge) &&
                              channel != ScpKnowledgeAcquisitionChannel.Other
            ? Math.Min(knowledge.RequiredProgress, Math.Max(currentProgress, progress))
            : Math.Min(knowledge.RequiredProgress, currentProgress + progress);

        if (clampedProgress <= currentProgress &&
            updatedExposureFlags == currentExposureFlags)
        {
            return false;
        }

        if (updatedExposureFlags == ScpKnowledgeExposureFlags.None)
            knowledgeState.ExposureFlags.Remove(knowledgeId);
        else
            knowledgeState.ExposureFlags[knowledgeId] = updatedExposureFlags;

        if (clampedProgress < knowledge.RequiredProgress)
        {
            knowledgeState.Progress[knowledgeId] = clampedProgress;
            SyncKnowledgeState(holderUid.Value, knowledgeState);
            return true;
        }

        knowledgeState.Progress.Remove(knowledgeId);
        knowledgeState.ExposureFlags.Remove(knowledgeId);
        knowledgeState.KnownKnowledge.Add(knowledgeId);

        SyncKnowledgeState(holderUid.Value, knowledgeState);

        if (showUnlockFeedback)
        {
            var unlockEvent = new ScpKnowledgeUnlockedEvent(knowledgeId, channel, source);
            RaiseLocalEvent(holderUid.Value, ref unlockEvent, true);
            ShowKnowledgeUnlockedFeedback(holderUid.Value, acquisition, knowledge);
        }

        return true;
    }

    private bool CanAcquireKnowledge(
        ScpKnowledgeAcquisitionComponent acquisition,
        ScpKnowledgePrototype knowledge,
        ScpKnowledgeAcquisitionChannel channel)
    {
        return channel switch
        {
            ScpKnowledgeAcquisitionChannel.Listen => acquisition.CanLearnByListen && knowledge.AllowListen,
            ScpKnowledgeAcquisitionChannel.Read => acquisition.CanLearnByRead && knowledge.AllowRead,
            ScpKnowledgeAcquisitionChannel.Examine => acquisition.CanLearnByExamine && knowledge.AllowExamine,
            ScpKnowledgeAcquisitionChannel.Other => acquisition.CanLearnByOther,
            _ => false,
        };
    }

    private void RebuildCaches()
    {
        _knowledgeByEntityPrototype.Clear();
        _knowledgePhrases.Clear();
        _highlightPhrasesByKnowledge.Clear();

        foreach (var knowledge in _prototype.EnumeratePrototypes<ScpKnowledgePrototype>())
        {
            CacheKnowledgePhrases(knowledge);
            CacheKnowledgeEntities(knowledge);
            CacheKnowledgeHighlightPhrases(knowledge);
        }
    }

    private void CacheKnowledgePhrases(ScpKnowledgePrototype knowledge)
    {
        foreach (var phraseId in knowledge.RecognitionPhrases)
        {
            foreach (var variant in ScpKnowledgeText.GetRecognitionPhraseVariants(Loc.GetString(phraseId)))
            {
                var normalized = ScpKnowledgeText.NormalizeRecognitionText(variant);
                if (normalized.Length == 0)
                    continue;

                _knowledgePhrases.Add(new CachedKnowledgePhrase(
                    ScpKnowledgeText.WrapForPhraseSearch(normalized),
                    knowledge.ID));
            }
        }
    }

    private void CacheKnowledgeEntities(ScpKnowledgePrototype knowledge)
    {
        foreach (var entityPrototype in knowledge.EntityPrototypes)
        {
            if (!_knowledgeByEntityPrototype.TryGetValue(entityPrototype, out var knowledgeIds))
            {
                knowledgeIds = [];
                _knowledgeByEntityPrototype[entityPrototype] = knowledgeIds;
            }

            knowledgeIds.Add(knowledge.ID);
        }
    }

    private bool TryGetKnowledgeIdsForEntity(
        EntityUid target,
        [NotNullWhen(true)] out List<ProtoId<ScpKnowledgePrototype>>? knowledgeIds)
    {
        knowledgeIds = null;
        var prototypeId = Prototype(target)?.ID;
        return prototypeId != null && _knowledgeByEntityPrototype.TryGetValue(prototypeId, out knowledgeIds);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<ScpKnowledgePrototype>())
            return;

        RebuildCaches();
    }

    private void OnPlayerAttached(PlayerAttachedEvent args)
    {
        SyncKnowledgeState(args.Entity);
    }

    private static ScpKnowledgeExposureFlags GetExposureFlags(
        ScpKnowledgeComponent knowledgeState,
        ProtoId<ScpKnowledgePrototype> knowledgeId)
    {
        return knowledgeState.ExposureFlags.GetValueOrDefault(knowledgeId, ScpKnowledgeExposureFlags.None);
    }

    private static bool IsKnowledgeKnown(
        ScpKnowledgeComponent knowledgeState,
        ProtoId<ScpKnowledgePrototype> knowledgeId,
        ScpKnowledgePrototype knowledge)
    {
        return knowledgeState.KnownKnowledge.Contains(knowledgeId) ||
               GetKnowledgeProgress(knowledgeState, knowledgeId, knowledge) >= knowledge.RequiredProgress;
    }

    private static int GetKnowledgeProgress(
        ScpKnowledgeComponent knowledgeState,
        ProtoId<ScpKnowledgePrototype> knowledgeId,
        ScpKnowledgePrototype knowledge)
    {
        if (knowledgeState.KnownKnowledge.Contains(knowledgeId))
            return knowledge.RequiredProgress;

        knowledgeState.Progress.TryGetValue(knowledgeId, out var progress);

        if (!ScpKnowledgeLogic.RequiresTextAndExamine(knowledge))
            return progress;

        var exposureProgress = ScpKnowledgeLogic.GetExposureProgress(GetExposureFlags(knowledgeState, knowledgeId));
        return Math.Max(progress, exposureProgress);
    }

    private void SyncKnowledgeState(EntityUid uid)
    {
        if (!TryGetKnowledgeState(uid, out var holderUid, out var knowledgeState))
        {
            if (TryComp<ActorComponent>(uid, out var actor))
                RaiseNetworkEvent(new ScpKnowledgeStateSyncEvent(GetNetEntity(uid), []), actor.PlayerSession);

            return;
        }

        SyncKnowledgeState(holderUid.Value, knowledgeState);
    }

    private void SyncKnowledgeState(EntityUid holderUid, ScpKnowledgeComponent knowledgeState)
    {
        if (!TryComp<ActorComponent>(holderUid, out var actor))
            return;

        var entries = BuildKnowledgeStateEntries(knowledgeState);
        RaiseNetworkEvent(new ScpKnowledgeStateSyncEvent(GetNetEntity(holderUid), entries), actor.PlayerSession);
    }

    private ScpKnowledgeStateEntry[] BuildKnowledgeStateEntries(ScpKnowledgeComponent knowledgeState)
    {
        var knownIds = new HashSet<string>();
        foreach (var knowledgeId in knowledgeState.KnownKnowledge)
        {
            knownIds.Add(knowledgeId);
        }

        foreach (var knowledgeId in knowledgeState.Progress.Keys)
        {
            knownIds.Add(knowledgeId);
        }

        foreach (var knowledgeId in knowledgeState.ExposureFlags.Keys)
        {
            knownIds.Add(knowledgeId);
        }

        var entries = new ScpKnowledgeStateEntry[knownIds.Count];
        var index = 0;

        foreach (var knowledgeId in knownIds)
        {
            var prototypeId = new ProtoId<ScpKnowledgePrototype>(knowledgeId);
            var knowledge = _prototype.Index(prototypeId);
            entries[index] = new ScpKnowledgeStateEntry(
                knowledgeId,
                knowledgeState.KnownKnowledge.Contains(prototypeId),
                GetKnowledgeProgress(knowledgeState, prototypeId, knowledge),
                GetExposureFlags(knowledgeState, prototypeId));
            index++;
        }

        return entries;
    }

    private readonly record struct CachedKnowledgePhrase(string WrappedPhrase, ProtoId<ScpKnowledgePrototype> KnowledgeId);
}
