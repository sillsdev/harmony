using System.Text.Json;

namespace SIL.Harmony.Changes;

/// <summary>
/// An <see cref="IChange"/> whose <c>$type</c> was not registered on this client, or whose JSON this client can't read.
/// Preserves the original JSON so it can round-trip and be applied once the type is known.
/// </summary>
public sealed class OpaqueChange : IChange
{
    public required string TypeName { get; init; }
    public required JsonElement RawJson { get; init; }

    public Guid EntityId { get; set; }

    /// <summary>why a registered type fell back to opaque; null for an unregistered type</summary>
    internal JsonException? ReadError { get; init; }

    public Type? EntityType => null;

    public ValueTask ApplyChange(IObjectBase entity, IChangeContext context) => default;

    public ValueTask<IObjectBase> NewEntity(Commit commit, IChangeContext context) =>
        throw new NotSupportedException(
            $"Opaque change '{TypeName}' cannot create entities on this client. CommitId: {commit.Id}, EntityId: {EntityId}");

    public bool SupportsApplyChange() => false;
    public bool SupportsNewEntity() => false;
}
