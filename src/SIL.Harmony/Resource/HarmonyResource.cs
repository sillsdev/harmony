using System.Diagnostics.CodeAnalysis;

namespace SIL.Harmony.Resource;

public class HarmonyResource
{
    public required Guid Id { get; init; }
    public string? RemoteId { get; init; }
    public string? LocalPath { get; init; }
    [MemberNotNullWhen(true, nameof(LocalPath))]
    public bool Local => !string.IsNullOrEmpty(LocalPath);
    [MemberNotNullWhen(true, nameof(RemoteId))]
    public bool Remote => !string.IsNullOrEmpty(RemoteId);
}
