using SIL.Harmony.Changes;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Resource;

public class CreateRemoteResourcePendingUploadChange<TMetadata>(Guid entityId, TMetadata? metadata = null)
    : CreateChange<RemoteResource<TMetadata>>(entityId), IPolyType
    where TMetadata : class
{
    public TMetadata? Metadata { get; set; } = metadata;

    public override ValueTask<RemoteResource<TMetadata>> NewEntity(Commit commit, IChangeContext context)
    {
        return ValueTask.FromResult(new RemoteResource<TMetadata>
        {
            Id = EntityId,
            Metadata = Metadata
        });
    }

    public static string TypeName => "create:pendingUpload";
}
