using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;

namespace SIL.Harmony;

public class Commit : CommitBase<IChange>
{
    [JsonConstructor]
    [SetsRequiredMembers]
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

    /// <summary>
    /// Snapshots are complete as of this commit: every entity's newest snapshot at or before it is that entity's state after it,
    /// except for changes this client could not apply (see <see cref="Config.UnknownChangeHandling"/>), which only <see cref="DataModel.RegenerateSnapshots"/> folds in.
    /// A commit that arrives out of order rolls snapshots back to the newest checkpoint before it and replays from there.
    /// Local bookkeeping, never synced.
    /// </summary>
    [JsonIgnore]
    public bool IsSnapshotCheckpoint { get; internal set; }
}
