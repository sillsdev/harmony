using SIL.Harmony.Changes;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Resource;

public class CreateRemoteResourcePendingUploadChange(Guid entityId)
    : CreateChange<RemoteResource>(entityId), IPolyType
{
    public override ValueTask<RemoteResource> NewEntity(Commit commit, IChangeContext context)
    {
        return ValueTask.FromResult(new RemoteResource
        {
            Id = EntityId
        });
    }

    public static string TypeName => "create:pendingUpload";
}
