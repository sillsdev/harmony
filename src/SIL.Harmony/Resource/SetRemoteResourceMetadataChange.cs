using SIL.Harmony.Changes;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Resource;

public class SetRemoteResourceMetadataChange<TMetadata>(Guid entityId, TMetadata metadata)
    : EditChange<RemoteResource<TMetadata>>(entityId), IPolyType
    where TMetadata : class
{
    public TMetadata Metadata { get; } = metadata;
    public static string TypeName => "set:remote-resource-metadata";

    public override ValueTask ApplyChange(RemoteResource<TMetadata> entity, IChangeContext context)
    {
        entity.Metadata = Metadata;
        return ValueTask.CompletedTask;
    }
}
