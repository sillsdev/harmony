using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crdt.Core;

public class ServerCommit : CommitBase<ServerJsonChange>
{
    [JsonConstructor]
    protected ServerCommit(Guid id, string hash, string parentHash, HybridDateTime hybridDateTime) : base(id,
        hash,
        parentHash,
        hybridDateTime)
    {
    }

    public ServerCommit(Guid id) : base(id)
    {
    }

    public Guid ProjectId { get; set; }
}

/// <summary>
/// a generic IChange implementation that can be used to deserialize any JSON change, used for the sync server so it doesn't need to know the specific change types
/// </summary>
public class ServerJsonChange
{
    [JsonPropertyName(CrdtConstants.ChangeDiscriminatorProperty), JsonPropertyOrder(1)]
    public required string Type { get; set; }

    [JsonExtensionData, JsonPropertyOrder(2)]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public static implicit operator ServerJsonChange(JsonElement e) =>
        e.Deserialize<ServerJsonChange>() ??
        throw new SerializationException("Failed to deserialize JSON change");
}
