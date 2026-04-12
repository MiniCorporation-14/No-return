using Content.Shared._Scp.Knowledge;
using Robust.Shared.Prototypes;

namespace Content.Shared._Scp.Knowledge.Components;

[RegisterComponent]
public sealed partial class ScpKnowledgeComponent : Component
{
    [DataField]
    public HashSet<ProtoId<ScpKnowledgePrototype>> KnownKnowledge = [];

    [DataField]
    public Dictionary<ProtoId<ScpKnowledgePrototype>, int> Progress = new();

    [DataField]
    public Dictionary<ProtoId<ScpKnowledgePrototype>, ScpKnowledgeExposureFlags> ExposureFlags = new();

    [NonSerialized]
    public HashSet<ScpKnowledgeSourceRecord> ProcessedSources = [];

    [NonSerialized]
    public HashSet<ScpKnowledgeMessageRecord> ProcessedMessages = [];
}
