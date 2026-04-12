namespace Content.Shared._Scp.Knowledge;

public static class ScpKnowledgeLogic
{
    public static bool RequiresTextAndExamine(ScpKnowledgePrototype knowledge)
    {
        return knowledge.Kind == ScpKnowledgeKind.Entity;
    }

    public static bool IsTextChannel(ScpKnowledgeAcquisitionChannel channel)
    {
        return channel is ScpKnowledgeAcquisitionChannel.Listen or ScpKnowledgeAcquisitionChannel.Read;
    }

    public static ScpKnowledgeExposureFlags GetExposureFlags(ScpKnowledgeAcquisitionChannel channel)
    {
        return channel switch
        {
            ScpKnowledgeAcquisitionChannel.Listen => ScpKnowledgeExposureFlags.Text,
            ScpKnowledgeAcquisitionChannel.Read => ScpKnowledgeExposureFlags.Text,
            ScpKnowledgeAcquisitionChannel.Examine => ScpKnowledgeExposureFlags.Examine,
            _ => ScpKnowledgeExposureFlags.None,
        };
    }

    public static int GetExposureProgress(ScpKnowledgeExposureFlags flags)
    {
        var progress = 0;

        if ((flags & ScpKnowledgeExposureFlags.Text) != 0)
            progress += 1;

        if ((flags & ScpKnowledgeExposureFlags.Examine) != 0)
            progress += 1;

        return progress;
    }
}
