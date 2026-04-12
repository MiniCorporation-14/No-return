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

public sealed class ScpKnowledgeUnlockedEvent : EntityEventArgs
{
    public readonly ProtoId<ScpKnowledgePrototype> KnowledgeId;
    public readonly ScpKnowledgeAcquisitionChannel Channel;
    public readonly EntityUid? Source;

    public ScpKnowledgeUnlockedEvent(
        ProtoId<ScpKnowledgePrototype> knowledgeId,
        ScpKnowledgeAcquisitionChannel channel,
        EntityUid? source = null)
    {
        KnowledgeId = knowledgeId;
        Channel = channel;
        Source = source;
    }
}

[Serializable, NetSerializable]
public readonly struct ScpKnowledgeStateEntry
{
    public readonly string KnowledgeId;
    public readonly bool Known;
    public readonly int Progress;
    public readonly ScpKnowledgeExposureFlags ExposureFlags;

    public ScpKnowledgeStateEntry(
        string knowledgeId,
        bool known,
        int progress,
        ScpKnowledgeExposureFlags exposureFlags)
    {
        KnowledgeId = knowledgeId;
        Known = known;
        Progress = progress;
        ExposureFlags = exposureFlags;
    }
}

[Serializable, NetSerializable]
public sealed class ScpKnowledgeStateSyncEvent : EntityEventArgs
{
    public readonly NetEntity Entity;
    public readonly ScpKnowledgeStateEntry[] Entries;

    public ScpKnowledgeStateSyncEvent(NetEntity entity, ScpKnowledgeStateEntry[] entries)
    {
        Entity = entity;
        Entries = entries;
    }
}
