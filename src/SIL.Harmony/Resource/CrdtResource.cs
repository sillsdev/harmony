namespace SIL.Harmony.Resource;

public class CrdtResource
{
    public required Guid Id { get; init; }
    public string? RemoteId { get; init; }
    public string? LocalPath { get; init; }
    public bool Local => !string.IsNullOrEmpty(LocalPath);
    public bool Remote => !string.IsNullOrEmpty(RemoteId);
}