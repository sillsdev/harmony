using System.Text.Json.Serialization;
using Crdt.Changes;
using Crdt.Core;
using Crdt.Db;

namespace Crdt;

public class Commit : CommitBase<IChange>
{
    [JsonConstructor]
    protected Commit(Guid id, string hash, string parentHash, HybridDateTime hybridDateTime) : base(id,
        hash,
        parentHash,
        hybridDateTime)
    {
    }

    internal Commit(Guid id) : base(id)
    {
    }

    internal Commit() : this(Guid.NewGuid())
    {

    }

    [JsonIgnore]
    public List<ObjectSnapshot> Snapshots { get; init; } = [];
}
