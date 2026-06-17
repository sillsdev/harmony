using SIL.Harmony.Changes;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Resource;

public class DeleteRemoteResourceChange<TMetadata>(Guid entityId) : EditChange<RemoteResource<TMetadata>>(entityId), IPolyType
    where TMetadata : class
{
    public static string TypeName => "delete:RemoteResource";

    public override ValueTask ApplyChange(RemoteResource<TMetadata> entity, IChangeContext context)
    {
        context.Adapt(entity).DeletedAt = context.Commit.DateTime;
        return ValueTask.CompletedTask;
    }
}
