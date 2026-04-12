using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Scp.Knowledge;

public enum ScpKnowledgeAcquisitionChannel : byte
{
    Listen,
    Read,
    Examine,
    Other,
}

[Flags]
[Serializable, NetSerializable]
public enum ScpKnowledgeExposureFlags : byte
{
    None = 0,
    Text = 1 << 0,
    Examine = 1 << 1,
}

public readonly record struct ScpKnowledgeSourceRecord(
    EntityUid Source,
    ProtoId<ScpKnowledgePrototype> KnowledgeId,
    ScpKnowledgeAcquisitionChannel Channel);

public readonly record struct ScpKnowledgeMessageRecord(
    EntityUid Source,
    ProtoId<ScpKnowledgePrototype> KnowledgeId,
    ScpKnowledgeAcquisitionChannel Channel,
    string NormalizedMessage);

[ByRefEvent]
public readonly record struct ScpKnowledgeUnlockedEvent(
    ProtoId<ScpKnowledgePrototype> KnowledgeId,
    ScpKnowledgeAcquisitionChannel Channel,
    EntityUid? Source = null);

[Serializable, NetSerializable]
public readonly struct ScpKnowledgeStateEntry(
    string knowledgeId,
    bool known,
    int progress,
    ScpKnowledgeExposureFlags exposureFlags)
{
    public readonly string KnowledgeId = knowledgeId;
    public readonly bool Known = known;
    public readonly int Progress = progress;
    public readonly ScpKnowledgeExposureFlags ExposureFlags = exposureFlags;
}

[Serializable, NetSerializable]
public sealed class ScpKnowledgeStateSyncEvent(NetEntity entity, ScpKnowledgeStateEntry[] entries) : EntityEventArgs
{
    public readonly NetEntity Entity = entity;
    public readonly ScpKnowledgeStateEntry[] Entries = entries;
}
