using System.Text.Json.Serialization;
using SIL.Harmony.Core;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;

namespace SIL.Harmony;

public class Commit : CommitBase<IChange>
{
    [JsonConstructor]
    protected Commit(Guid id, string hash, string parentHash, HybridDateTime hybridDateTime) : base(id,
        hybridDateTime)
    {
        Hash = hash;
        ParentHash = parentHash;
    }

    internal Commit(Guid id) : base(id)
    {
        Hash = GenerateHash(NullParentHash);
        ParentHash = NullParentHash;
    }

    public void SetParentHash(string parentHash)
    {
        Hash = GenerateHash(parentHash);
        ParentHash = parentHash;
    }
    internal Commit() : this(Guid.NewGuid())
    {

    }

    [JsonIgnore]
    public List<ObjectSnapshot> Snapshots { get; init; } = [];

    [JsonIgnore]
    public string Hash { get; private set; }

    [JsonIgnore]
    public string ParentHash { get; private set; }
}
