using SIL.Harmony.Refs.Entities;

namespace SIL.Harmony.Refs;

internal static class TagTipResolver
{
    public static async Task<Commit> ResolveTagTip(DataModel dataModel, Guid tagId)
    {
        var tag = await dataModel.GetLatest<Tag>(tagId)
                  ?? throw new InvalidOperationException($"Tag {tagId} was not found.");
        return await dataModel.GetCommit(tag.TargetCommitId);
    }
}

