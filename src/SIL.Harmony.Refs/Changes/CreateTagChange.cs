using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Refs.Entities;

namespace SIL.Harmony.Refs.Changes;

public class CreateTagChange(Guid entityId, string name, Guid targetCommitId) : CreateChange<Tag>(entityId), IPolyType
{
    public string Name { get; } = name;
    public Guid TargetCommitId { get; } = targetCommitId;

    public override ValueTask<Tag> NewEntity(Commit commit, IChangeContext context)
    {
        return ValueTask.FromResult(new Tag
        {
            Id = EntityId,
            Name = Name,
            TargetCommitId = TargetCommitId
        });
    }

    public static string TypeName => "create:tag";
}
