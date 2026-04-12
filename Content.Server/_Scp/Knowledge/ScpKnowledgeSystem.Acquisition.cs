using Content.Server.Chat.Systems;
using Content.Shared._Scp.Knowledge;
using Content.Shared._Scp.Knowledge.Components;
using Content.Shared.Examine;
using Content.Shared.Paper;
using Content.Shared.Verbs;
using Robust.Shared.Utility;

namespace Content.Server._Scp.Knowledge;

public sealed partial class ScpKnowledgeSystem
{
    private void OnSpeechHeard(ref ScpKnowledgeSpeechHeardEvent args)
    {
        if (args.NormalizedMessage.Length == 0)
            return;

        foreach (var listener in args.Listeners)
        {
            GrantKnowledgeFromText(listener, args.NormalizedMessage, args.Source, ScpKnowledgeAcquisitionChannel.Listen);
        }
    }

    private void OnRadioSpoke(RadioSpokeEvent args)
    {
        var normalizedMessage = ScpKnowledgeText.NormalizeRecognitionText(args.Message);
        if (normalizedMessage.Length == 0)
            return;

        foreach (var receiver in args.Receivers)
        {
            GrantKnowledgeFromText(receiver, normalizedMessage, args.Source, ScpKnowledgeAcquisitionChannel.Listen);
        }
    }

    private void OnPaperRead(ScpKnowledgePaperReadEvent args)
    {
        if (!TryComp<PaperComponent>(args.Paper, out var paper))
            return;

        var normalizedMessage = ScpKnowledgeText.NormalizeRecognitionText(args.Content);
        if (normalizedMessage.Length == 0)
            return;

        CollectKnowledgeIdsFromText(normalizedMessage);

        foreach (var knowledgeId in _matchedKnowledgeBuffer)
        {
            if (!HasPaperKnowledgePhraseMatch(paper, knowledgeId))
                continue;

            var knowledge = _prototype.Index(knowledgeId);
            if (knowledge.ReadProgress <= 0)
                continue;

            TryGrantKnowledgeProgress(
                args.User,
                knowledgeId,
                knowledge.ReadProgress,
                ScpKnowledgeAcquisitionChannel.Read,
                args.Paper);
        }
    }

    private void OnExamined(Entity<MetaDataComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        TryGrantExamineKnowledge(args.Examiner, ent.Owner);
    }

    private void OnGetExamineVerbs(Entity<MetaDataComponent> ent, ref GetVerbsEvent<ExamineVerb> args)
    {
        if (!TryGetKnowledgeIdsForEntity(ent.Owner, out var knowledgeIds))
            return;

        foreach (var knowledgeId in knowledgeIds)
        {
            if (!HasKnowledge(args.User, knowledgeId))
                continue;

            var knowledge = _prototype.Index(knowledgeId);
            if (knowledge.KnownExamineVerbText == null || knowledge.KnownExamineText == null)
                continue;

            var message = new FormattedMessage();
            message.AddText(Loc.GetString(knowledge.KnownExamineText.Value));

            _examine.AddDetailedExamineVerb(
                args,
                ent.Comp,
                message,
                Loc.GetString(knowledge.KnownExamineVerbText.Value));
        }
    }

    private void GrantKnowledgeFromText(
        EntityUid uid,
        string normalizedMessage,
        EntityUid source,
        ScpKnowledgeAcquisitionChannel channel)
    {
        CollectKnowledgeIdsFromText(normalizedMessage);

        ScpKnowledgeComponent? sourceKnowledgeState = null;
        if (channel == ScpKnowledgeAcquisitionChannel.Listen &&
            !TryGetKnowledgeState(source, out _, out sourceKnowledgeState))
        {
            return;
        }

        foreach (var knowledgeId in _matchedKnowledgeBuffer)
        {
            var knowledge = _prototype.Index(knowledgeId);
            if (channel == ScpKnowledgeAcquisitionChannel.Listen &&
                (sourceKnowledgeState == null || !IsKnowledgeKnown(sourceKnowledgeState, knowledgeId, knowledge)))
            {
                continue;
            }

            var progress = channel switch
            {
                ScpKnowledgeAcquisitionChannel.Listen => knowledge.ListenProgress,
                ScpKnowledgeAcquisitionChannel.Read => knowledge.ReadProgress,
                ScpKnowledgeAcquisitionChannel.Examine => knowledge.ExamineProgress,
                _ => 0,
            };

            if (progress <= 0)
                continue;

            TryGrantKnowledgeProgress(uid, knowledgeId, progress, channel, source, normalizedMessage);
        }
    }

    private void CollectKnowledgeIdsFromText(string normalizedMessage)
    {
        _matchedKnowledgeBuffer.Clear();

        var wrappedMessage = ScpKnowledgeText.WrapForPhraseSearch(normalizedMessage);
        foreach (var knowledgePhrase in _knowledgePhrases)
        {
            if (!wrappedMessage.Contains(knowledgePhrase.WrappedPhrase, StringComparison.Ordinal))
                continue;

            _matchedKnowledgeBuffer.Add(knowledgePhrase.KnowledgeId);
        }
    }
}
