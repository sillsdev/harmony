using SIL.Harmony.Changes;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Resource;

/// <summary>
/// used when a resource is uploaded to the remote server, stores the remote url in the resource entity
/// </summary>
public class RemoteResourceUploadedChange<TMetadata>(Guid entityId, string remoteId)
    : EditChange<RemoteResource<TMetadata>>(entityId), IPolyType
    where TMetadata : class
{
    public string RemoteId { get; set; } = remoteId;
    public static string TypeName => "uploaded:RemoteResource";

    public override ValueTask ApplyChange(RemoteResource<TMetadata> entity, IChangeContext context)
    {
        entity.RemoteId = RemoteId;
        return ValueTask.CompletedTask;
    }
}



