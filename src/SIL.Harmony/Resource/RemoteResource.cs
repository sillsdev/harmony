using System.Text.Json;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Resource;

/// <summary>
/// Marker type for apps that do not need synced resource metadata.
/// </summary>
public sealed class NoMetadata;

/// <summary>
/// represents a remote binary resource (e.g. image, video, audio, etc.)
/// </summary>
public class RemoteResource<TMetadata> : IObjectBase<RemoteResource<TMetadata>>
    where TMetadata : class
{
    public static string TypeName => "RemoteResource";

    public Guid Id { get; init; }
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>
    /// will be null when the resource has not been uploaded yet
    /// </summary>
    public string? RemoteId { get; set; }
    public TMetadata? Metadata { get; set; }

    public Guid[] GetReferences()
    {
        return [];
    }

    public void RemoveReference(Guid id, CommitBase commit)
    {
    }

    public IObjectBase Copy()
    {
        return new RemoteResource<TMetadata>
        {
            Id = Id,
            RemoteId = RemoteId,
            DeletedAt = DeletedAt,
            Metadata = CloneMetadata(Metadata)
        };
    }

    private static TMetadata? CloneMetadata(TMetadata? metadata)
    {
        if (metadata is null) return null;
        return JsonSerializer.Deserialize<TMetadata>(JsonSerializer.Serialize(metadata));
    }
}



