using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Refs.Entities;

namespace SIL.Harmony.Refs.Changes;

public class CreateBranchChange(Guid entityId, string name) : CreateChange<Branch>(entityId), IPolyType
{
    public string Name { get; } = name;

    public override ValueTask<Branch> NewEntity(Commit commit, IChangeContext context)
    {
        return ValueTask.FromResult(new Branch
        {
            Id = EntityId,
            Name = Name
        });
    }

    public static string TypeName => "create:branch";
}
