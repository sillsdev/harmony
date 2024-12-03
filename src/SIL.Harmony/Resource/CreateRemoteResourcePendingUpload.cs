using SIL.Harmony.Changes;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Resource;

public class CreateRemoteResourcePendingUploadChange: CreateChange<RemoteResource>, IPolyType
{
    public CreateRemoteResourcePendingUploadChange(Guid resourceId) : base(resourceId)
    {
    }

    public override ValueTask<RemoteResource> NewEntity(Commit commit, ChangeContext context)
    {
        return ValueTask.FromResult(new RemoteResource
        {
            Id = EntityId
        });
    }

    public static string TypeName => "create:pendingUpload";
}
