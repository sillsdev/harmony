using SIL.Harmony.Entities;

namespace SIL.Harmony.Resource;

/// <summary>
/// represents a remote binary resource (e.g. image, video, audio, etc.)
/// </summary>
public class RemoteResource: IObjectBase<RemoteResource>
{
    public Guid Id { get; init; }
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>
    /// will be null when the resource has not been uploaded yet
    /// </summary>
    public string? RemoteId { get; set; }
    public Guid[] GetReferences()
    {
        return [];
    }

    public void RemoveReference(Guid id, CommitBase commit)
    {
    }

    public IObjectBase Copy()
    {
        return new RemoteResource
        {
            Id = Id,
            RemoteId = RemoteId,
            DeletedAt = DeletedAt
        };
    }
}
