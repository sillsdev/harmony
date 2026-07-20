using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Refs.Entities;

namespace SIL.Harmony.Refs.Changes;

/// <summary>
/// Main-line commit that incorporates a branch into main visibility and deletes the branch entity.
/// Does not rewrite branch commit payloads; materialization starts including those commits after this change.
/// </summary>
public class MergeBranchChange(Guid entityId) : EditChange<Branch>(entityId), IPolyType
{
    public override ValueTask ApplyChange(Branch entity, IChangeContext context)
    {
        entity.DeletedAt = context.Commit.DateTime;
        return ValueTask.CompletedTask;
    }

    public static string TypeName => "merge:branch";
}
