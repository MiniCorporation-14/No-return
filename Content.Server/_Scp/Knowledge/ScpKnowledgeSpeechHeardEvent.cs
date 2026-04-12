namespace Content.Server._Scp.Knowledge;

public sealed class ScpKnowledgeSpeechHeardEvent : EntityEventArgs
{
    public readonly EntityUid Source;
    public readonly string NormalizedMessage;
    public readonly EntityUid[] Listeners;

    public ScpKnowledgeSpeechHeardEvent(EntityUid source, string normalizedMessage, EntityUid[] listeners)
    {
        Source = source;
        NormalizedMessage = normalizedMessage;
        Listeners = listeners;
    }
}
