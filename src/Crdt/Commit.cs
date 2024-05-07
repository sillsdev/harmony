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


    public Commit(Guid id) : base(id)
    {
    }

    public Commit()
    {
    }

    [JsonIgnore]
    public List<ObjectSnapshot> Snapshots { get; init; } = [];
}
