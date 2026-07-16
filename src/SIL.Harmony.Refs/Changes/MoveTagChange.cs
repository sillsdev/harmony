using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Refs.Entities;

namespace SIL.Harmony.Refs.Changes;

public class MoveTagChange(Guid entityId, Guid targetCommitId) : EditChange<Tag>(entityId), IPolyType
{
    public Guid TargetCommitId { get; } = targetCommitId;

    public override ValueTask ApplyChange(Tag entity, IChangeContext context)
    {
        entity.TargetCommitId = TargetCommitId;
        return ValueTask.CompletedTask;
    }

    public static string TypeName => "move:tag";
}
