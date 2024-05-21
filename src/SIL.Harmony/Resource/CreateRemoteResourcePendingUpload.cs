using SIL.Harmony.Changes;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Resource;

public class CreateRemoteResourcePendingUploadChange: CreateChange<RemoteResource>, IPolyType
{
    public CreateRemoteResourcePendingUploadChange(Guid resourceId) : base(resourceId)
    {
    }

    public override ValueTask<IObjectBase> NewEntity(Commit commit, ChangeContext context)
    {
        return ValueTask.FromResult<IObjectBase>(new RemoteResource
        {
            Id = EntityId
        });
    }

    public static string TypeName => "create:pendingUpload";
}
