using System.Diagnostics.CodeAnalysis;

namespace SIL.Harmony.Resource;

public class HarmonyResource<TMetadata> where TMetadata : class
{

    public HarmonyResource()
    {
        
    }

    [SetsRequiredMembers]
    public HarmonyResource(LocalResource? localResource, RemoteResource<TMetadata>? remoteResource)
    {
        Id = localResource?.Id ?? remoteResource?.Id ?? throw new ArgumentNullException("Either localResource or remoteResource must be provided");
        RemoteId = remoteResource?.RemoteId;
        LocalPath = localResource?.LocalPath;
        Metadata = remoteResource?.Metadata;
    }
    public required Guid Id { get; init; }
    public string? RemoteId { get; init; }
    public string? LocalPath { get; init; }
    public TMetadata? Metadata { get; init; }
    [MemberNotNullWhen(true, nameof(LocalPath))]
    public bool Local => !string.IsNullOrEmpty(LocalPath);
    [MemberNotNullWhen(true, nameof(RemoteId))]
    public bool Remote => !string.IsNullOrEmpty(RemoteId);
}
