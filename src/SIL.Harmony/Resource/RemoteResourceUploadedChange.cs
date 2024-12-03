using SIL.Harmony.Changes;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Resource;

/// <summary>
/// used when a resource is uploaded to the remote server, stores the remote url in the resource entity
/// </summary>
/// <param name="entityId"></param>
/// <param name="remoteId"></param>
public class RemoteResourceUploadedChange(Guid entityId, string remoteId) : EditChange<RemoteResource>(entityId), IPolyType
{
    public string RemoteId { get; set; } = remoteId;
    public static string TypeName => "uploaded:RemoteResource";

    public override ValueTask ApplyChange(RemoteResource entity, ChangeContext context)
    {
        entity.RemoteId = RemoteId;
        return ValueTask.CompletedTask;
    }
}
