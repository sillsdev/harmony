using SIL.Harmony.Db;

namespace SIL.Harmony;

public class ModelSnapshot
{
    public ModelSnapshot(ICollection<SimpleSnapshot> snapshots)
    {
        var lastSnapshot = snapshots.MaxBy(s => (s.HybridDateTime.DateTime, s.HybridDateTime.Counter, s.CommitId));
        LastChange = lastSnapshot?.HybridDateTime.DateTime;
        LastCommitId = lastSnapshot?.CommitId;
        LastCommitHash = lastSnapshot?.CommitHash;
        Snapshots = snapshots.ToDictionary(s => s.EntityId);
    }

    public DateTimeOffset? LastChange { get; }
    public Guid? LastCommitId { get; }
    public string? LastCommitHash { get; }
    /// <summary>
    /// key is the entity id
    /// </summary>
    public Dictionary<Guid, SimpleSnapshot> Snapshots { get; }
}
