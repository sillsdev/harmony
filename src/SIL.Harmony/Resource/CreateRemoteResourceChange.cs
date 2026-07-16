using SIL.Harmony.Changes;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Resource;

public class CreateRemoteResourceChange<TMetadata>(Guid entityId, string remoteId, TMetadata? metadata = null)
    : CreateChange<RemoteResource<TMetadata>>(entityId), IPolyType
    where TMetadata : class
{
    public string RemoteId { get; set; } = remoteId;
    public TMetadata? Metadata { get; set; } = metadata;

    public override ValueTask<RemoteResource<TMetadata>> NewEntity(Commit commit, IChangeContext context)
    {
        return ValueTask.FromResult(new RemoteResource<TMetadata>
        {
            Id = EntityId,
            RemoteId = RemoteId,
            Metadata = Metadata
        });
    }

    public static string TypeName => "create:remote-resource";
}



