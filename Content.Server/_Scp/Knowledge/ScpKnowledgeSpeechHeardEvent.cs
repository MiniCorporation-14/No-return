namespace Content.Server._Scp.Knowledge;

[ByRefEvent]
public readonly record struct ScpKnowledgeSpeechHeardEvent(
    EntityUid Source,
    string NormalizedMessage,
    EntityUid[] Listeners);
