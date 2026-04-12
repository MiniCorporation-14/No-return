#pragma warning disable IDE0130 // Namespace does not match folder structure
using Content.Server._Scp.Knowledge;
using Content.Shared._Scp.Helpers;
using Content.Shared.Chat;
using Content.Shared._Scp.Knowledge;

namespace Content.Server.Chat.Systems;

public sealed partial class ChatSystem
{
    [Dependency] private readonly ScpKnowledgeSystem _knowledge = default!;

    private void RaiseScpKnowledgeSpeakEvent(EntityUid source, string message, ChatTransmitRange range)
    {
        using var listeners = ListPool<EntityUid>.Rent();

        foreach (var (session, data) in GetRecipients(source, VoiceRange))
        {
            if (session.AttachedEntity is not { Valid: true } attachedEntity)
                continue;

            if (MessageRangeCheck(session, data, range) == MessageRangeCheckResult.Disallowed)
                continue;

            listeners.Value.Add(attachedEntity);
        }

        RaiseScpKnowledgeSpeechEvent(source, message, listeners.Value);
    }

    private void RaiseScpKnowledgeWhisperEvent(
        EntityUid source,
        List<EntityUid> clearListeners,
        List<EntityUid> obfuscatedListeners,
        string clearMessage,
        string obfuscatedMessage)
    {
        RaiseScpKnowledgeSpeechEvent(source, clearMessage, clearListeners);
        RaiseScpKnowledgeSpeechEvent(source, obfuscatedMessage, obfuscatedListeners);
    }

    private void RaiseScpKnowledgeSpeechEvent(EntityUid source, string heardMessage, List<EntityUid> listeners)
    {
        if (listeners.Count == 0)
            return;

        var normalizedMessage = ScpKnowledgeText.NormalizeRecognitionText(heardMessage);
        if (normalizedMessage.Length == 0)
            return;

        var ev = new ScpKnowledgeSpeechHeardEvent(source, normalizedMessage, listeners.ToArray());
        RaiseLocalEvent(ref ev);
    }

    private string GetScpKnowledgeWrappedMessage(EntityUid listener, EntityUid source, string message, string wrappedMessage)
    {
        return _knowledge.HighlightWrappedChatMessage(listener, message, wrappedMessage, source);
    }
}
