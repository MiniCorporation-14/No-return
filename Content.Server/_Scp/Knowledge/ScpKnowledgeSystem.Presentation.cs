using Content.Shared._Scp.Knowledge;
using Content.Shared._Scp.Knowledge.Components;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Paper;
using Content.Shared.Popups;
using Content.Shared.UserInterface;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server._Scp.Knowledge;

public sealed partial class ScpKnowledgeSystem
{
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;

    private const string KnowledgeHighlightColor = "#8b0000";
    private readonly Dictionary<ProtoId<ScpKnowledgePrototype>, List<CachedHighlightPhrase>> _highlightPhrasesByKnowledge = new();
    private readonly List<TextHighlightRange> _highlightRanges = new();

    public string HighlightUnknownKnowledgeText(EntityUid viewer, string text, EntityUid? source = null)
    {
        return BuildHighlightedText(viewer, text, escapeText: false, source);
    }

    public string HighlightWrappedChatMessage(EntityUid viewer, string message, string wrappedMessage, EntityUid? source = null)
    {
        if (message.Length == 0 || wrappedMessage.Length == 0)
            return wrappedMessage;

        var escapedMessage = FormattedMessage.EscapeText(message);
        var highlightedEscapedMessage = BuildHighlightedText(viewer, message, escapeText: true, source);
        if (!string.Equals(highlightedEscapedMessage, escapedMessage, StringComparison.Ordinal))
            return ReplaceFirstOccurrence(wrappedMessage, escapedMessage, highlightedEscapedMessage);

        var highlightedMessage = BuildHighlightedText(viewer, message, escapeText: false, source);
        if (!string.Equals(highlightedMessage, message, StringComparison.Ordinal))
            return ReplaceFirstOccurrence(wrappedMessage, message, highlightedMessage);

        return wrappedMessage;
    }

    private void OnPaperUiOpened(Entity<PaperComponent> ent, ref AfterActivatableUIOpenEvent args)
    {
        if (ent.Comp.Content.Length == 0)
            return;

        var highlightedText = HighlightUnknownPaperKnowledgeText(args.User, ent);
        if (string.Equals(highlightedText, ent.Comp.Content, StringComparison.Ordinal))
            return;

        _ui.ServerSendUiMessage(
            ent.Owner,
            PaperComponent.PaperUiKey.Key,
            new PaperComponent.PaperKnowledgeHighlightMessage(ent.Comp.Content, highlightedText),
            args.User);
    }

    private void ShowKnowledgeUnlockedFeedback(
        EntityUid holderUid,
        ScpKnowledgeAcquisitionComponent acquisition,
        ScpKnowledgePrototype knowledge)
    {
        var popupTarget = holderUid;

        if (TryComp<MindComponent>(holderUid, out var mind) && mind.CurrentEntity is { } currentEntity)
            popupTarget = currentEntity;

        var popup = Loc.GetString(
            "scp-knowledge-unlocked-popup",
            ("knowledge", Loc.GetString(knowledge.DisplayName)));

        _popup.PopupEntity(popup, popupTarget, popupTarget, PopupType.Medium);

        if (acquisition.UnlockSound != null)
        {
            if (TryComp<ActorComponent>(popupTarget, out var actor))
                _audio.PlayGlobal(acquisition.UnlockSound, actor.PlayerSession);
            else
                _audio.PlayGlobal(acquisition.UnlockSound, popupTarget);
        }
    }

    private string HighlightUnknownPaperKnowledgeText(EntityUid viewer, Entity<PaperComponent> paper)
    {
        return BuildHighlightedText(
            viewer,
            paper.Comp.Content,
            escapeText: false,
            source: null,
            matchFilter: (knowledgeId, start, length) => DoesPaperRangeProvideKnowledge(paper.Comp, knowledgeId, start, length));
    }

    private string BuildHighlightedText(
        EntityUid viewer,
        string text,
        bool escapeText,
        EntityUid? source,
        Func<ProtoId<ScpKnowledgePrototype>, int, int, bool>? matchFilter = null)
    {
        var renderedText = escapeText ? FormattedMessage.EscapeText(text) : text;
        if (renderedText.Length == 0)
            return renderedText;

        if (!TryGetKnowledgeState(viewer, out _, out var knowledgeState))
            return renderedText;

        ScpKnowledgeComponent? sourceKnowledgeState = null;
        if (source != null && !TryGetKnowledgeState(source.Value, out _, out sourceKnowledgeState))
            return renderedText;

        var normalizedMessage = ScpKnowledgeText.NormalizeRecognitionText(text);
        if (normalizedMessage.Length == 0)
            return renderedText;

        CollectKnowledgeIdsFromText(normalizedMessage);
        if (_matchedKnowledgeBuffer.Count == 0)
            return renderedText;

        _highlightRanges.Clear();

        foreach (var knowledgeId in _matchedKnowledgeBuffer)
        {
            var knowledge = _prototype.Index(knowledgeId);
            if (IsKnowledgeKnown(knowledgeState, knowledgeId, knowledge))
                continue;

            if (sourceKnowledgeState != null && !IsKnowledgeKnown(sourceKnowledgeState, knowledgeId, knowledge))
                continue;

            if (!_highlightPhrasesByKnowledge.TryGetValue(knowledgeId, out var phrases))
                continue;

            for (var i = 0; i < phrases.Count; i++)
            {
                var phrase = escapeText ? phrases[i].EscapedPhrase : phrases[i].Phrase;
                AddHighlightRanges(
                    renderedText,
                    phrase,
                    matchFilter == null ? null : (start, length) => matchFilter(knowledgeId, start, length));
            }
        }

        if (_highlightRanges.Count == 0)
            return renderedText;

        _highlightRanges.Sort(static (left, right) =>
        {
            var compare = left.Start.CompareTo(right.Start);
            return compare != 0 ? compare : right.End.CompareTo(left.End);
        });

        return RenderHighlightedText(renderedText);
    }

    private void CacheKnowledgeHighlightPhrases(ScpKnowledgePrototype knowledge)
    {
        if (!_highlightPhrasesByKnowledge.TryGetValue(knowledge.ID, out var phrases))
        {
            phrases = new List<CachedHighlightPhrase>();
            _highlightPhrasesByKnowledge[knowledge.ID] = phrases;
        }

        var seenPhrases = new List<string>();
        foreach (var phraseId in knowledge.RecognitionPhrases)
        {
            foreach (var variant in ScpKnowledgeText.GetRecognitionPhraseVariants(Loc.GetString(phraseId)))
            {
                CacheKnowledgeHighlightPhrase(phrases, seenPhrases, variant);
            }
        }
    }

    private static void CacheKnowledgeHighlightPhrase(
        List<CachedHighlightPhrase> phrases,
        List<string> seenPhrases,
        string phrase)
    {
        if (phrase.Length == 0 || ContainsPhrase(seenPhrases, phrase))
            return;

        seenPhrases.Add(phrase);
        phrases.Add(new CachedHighlightPhrase(
            phrase,
            FormattedMessage.EscapeText(phrase)));
    }

    private static bool ContainsPhrase(List<string> phrases, string phrase)
    {
        for (var i = 0; i < phrases.Count; i++)
        {
            if (string.Equals(phrases[i], phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void AddHighlightRanges(string text, string phrase, Func<int, int, bool>? matchFilter = null)
    {
        if (phrase.Length == 0)
            return;

        var startIndex = 0;
        while (startIndex < text.Length)
        {
            startIndex = text.IndexOf(phrase, startIndex, StringComparison.OrdinalIgnoreCase);
            if (startIndex == -1)
                return;

            if (IsBoundedMatch(text, startIndex, phrase.Length) &&
                (matchFilter == null || matchFilter(startIndex, phrase.Length)))
            {
                _highlightRanges.Add(new TextHighlightRange(startIndex, startIndex + phrase.Length));
            }

            startIndex += phrase.Length;
        }
    }

    private bool HasPaperKnowledgePhraseMatch(PaperComponent paper, ProtoId<ScpKnowledgePrototype> knowledgeId)
    {
        if (!_highlightPhrasesByKnowledge.TryGetValue(knowledgeId, out var phrases))
            return false;

        for (var i = 0; i < phrases.Count; i++)
        {
            if (HasMatchedTextRange(
                    paper.Content,
                    phrases[i].Phrase,
                    (start, length) => DoesPaperRangeProvideKnowledge(paper, knowledgeId, start, length)))
            {
                return true;
            }
        }

        return false;
    }

    private bool DoesPaperRangeProvideKnowledge(PaperComponent paper, ProtoId<ScpKnowledgePrototype> knowledgeId, int start, int length)
    {
        if (length <= 0)
            return false;

        var authorRanges = paper.KnowledgeAuthorRanges;
        if (authorRanges.Count == 0)
            return true;

        var end = start + length;
        var coveredUntil = start;
        for (var i = 0; i < authorRanges.Count; i++)
        {
            var range = authorRanges[i];
            if (range.End <= start)
                continue;

            if (range.Start >= end)
                break;

            if (range.Start > coveredUntil)
                return false;

            if (range.Author == null)
            {
                coveredUntil = Math.Max(coveredUntil, Math.Min(range.End, end));
                continue;
            }

            if (!HasKnowledge(range.Author.Value, knowledgeId))
                return false;

            coveredUntil = Math.Max(coveredUntil, Math.Min(range.End, end));
        }

        return coveredUntil >= end;
    }

    private static bool HasMatchedTextRange(string text, string phrase, Func<int, int, bool>? matchFilter = null)
    {
        if (phrase.Length == 0)
            return false;

        var startIndex = 0;
        while (startIndex < text.Length)
        {
            startIndex = text.IndexOf(phrase, startIndex, StringComparison.OrdinalIgnoreCase);
            if (startIndex == -1)
                return false;

            if (IsBoundedMatch(text, startIndex, phrase.Length) &&
                (matchFilter == null || matchFilter(startIndex, phrase.Length)))
            {
                return true;
            }

            startIndex += phrase.Length;
        }

        return false;
    }

    private string RenderHighlightedText(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length + _highlightRanges.Count * 32);
        var currentIndex = 0;
        var currentRange = _highlightRanges[0];

        for (var i = 1; i < _highlightRanges.Count; i++)
        {
            var nextRange = _highlightRanges[i];
            if (nextRange.Start <= currentRange.End)
            {
                currentRange = new TextHighlightRange(currentRange.Start, Math.Max(currentRange.End, nextRange.End));
                continue;
            }

            AppendHighlightedRange(builder, text, ref currentIndex, currentRange);
            currentRange = nextRange;
        }

        AppendHighlightedRange(builder, text, ref currentIndex, currentRange);
        if (currentIndex < text.Length)
            builder.Append(text, currentIndex, text.Length - currentIndex);

        return builder.ToString();
    }

    private static void AppendHighlightedRange(
        System.Text.StringBuilder builder,
        string text,
        ref int currentIndex,
        TextHighlightRange range)
    {
        if (range.Start > currentIndex)
            builder.Append(text, currentIndex, range.Start - currentIndex);

        builder.Append("[color=");
        builder.Append(KnowledgeHighlightColor);
        builder.Append(']');
        builder.Append(text, range.Start, range.End - range.Start);
        builder.Append("[/color]");
        currentIndex = range.End;
    }

    private static string ReplaceFirstOccurrence(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (index == -1)
            return text;

        return string.Concat(
            text.AsSpan(0, index),
            newValue,
            text.AsSpan(index + oldValue.Length));
    }

    private static bool IsBoundedMatch(string text, int start, int length)
    {
        var leftBounded = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
        var end = start + length;
        var rightBounded = end >= text.Length || !char.IsLetterOrDigit(text[end]);
        return leftBounded && rightBounded;
    }

    private readonly record struct CachedHighlightPhrase(string Phrase, string EscapedPhrase);
    private readonly record struct TextHighlightRange(int Start, int End);
}
