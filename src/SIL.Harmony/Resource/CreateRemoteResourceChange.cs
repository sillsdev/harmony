using SIL.Harmony.Changes;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Resource;

public class CreateRemoteResourceChange(Guid entityId, string remoteId) : CreateChange<RemoteResource>(entityId), IPolyType
{
    public string RemoteId { get; set; } = remoteId;
    public override ValueTask<RemoteResource> NewEntity(Commit commit, IChangeContext context)
    {
        return ValueTask.FromResult(new RemoteResource
        {
            Id = EntityId,
            RemoteId = RemoteId
        });
    }

    public static string TypeName => "create:remote-resource";
}
