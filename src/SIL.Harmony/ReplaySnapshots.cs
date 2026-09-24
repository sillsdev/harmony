using SIL.Harmony.Db;

namespace SIL.Harmony;

/// <summary>
/// The snapshots a replay of one batch produces. Holds each entity's newest snapshot, decides which superseded ones
/// must still be persisted, and tracks which of the batch's commits a later replay can resume from as a result.
/// Has mutable state, one instance per replay.
/// </summary>
/// <remarks>
/// A superseded snapshot is only worth persisting if it is its entity's state at a checkpoint: a commit a later replay
/// resumes from. Dropping one leaves a hole in the entity's history, so the commits that hole spans stop being
/// checkpoints. Keeping every superseded snapshot that straddles a boundary between two checkpoint intervals is what
/// guarantees a checkpoint at least every <c>maxChangesBetweenCheckpoints</c> changes.
/// </remarks>
internal sealed class ReplaySnapshots
{
    private readonly int _maxChangesBetweenCheckpoints;
    private readonly Commit[] _commits;
    /// <summary>each commit's position in the batch, so a snapshot's commit tells us where in the batch it was made</summary>
    private readonly Dictionary<Guid, int> _positionOfCommit;
    /// <summary>running totals, so [0] is 0 and there is one more entry than there are commits</summary>
    private readonly long[] _changesBeforeCommit;
    /// <summary>every commit is safe to resume from until a dropped snapshot says otherwise</summary>
    private readonly bool[] _isCheckpoint;

    /// <summary>each entity's newest snapshot so far in this replay</summary>
    private readonly Dictionary<Guid, ObjectSnapshot> _latestSnapshots = [];
    /// <summary>superseded snapshots that must still be persisted: roots, and the ones checkpoints depend on</summary>
    private readonly List<ObjectSnapshot> _keptSupersededSnapshots = [];

    /// <param name="commits">the batch being replayed, in replay order</param>
    /// <param name="maxChangesBetweenCheckpoints">force a safe commit at least this many changes apart</param>
    internal ReplaySnapshots(IEnumerable<Commit> commits, int maxChangesBetweenCheckpoints)
    {
        if (maxChangesBetweenCheckpoints < 1) throw new ArgumentOutOfRangeException(nameof(maxChangesBetweenCheckpoints));
        _maxChangesBetweenCheckpoints = maxChangesBetweenCheckpoints;
        _commits = [.. commits];
        _positionOfCommit = new Dictionary<Guid, int>(_commits.Length);
        _changesBeforeCommit = new long[_commits.Length + 1];
        for (var position = 0; position < _commits.Length; position++)
        {
            _positionOfCommit[_commits[position].Id] = position;
            _changesBeforeCommit[position + 1] = _changesBeforeCommit[position] + _commits[position].ChangeEntities.Count;
        }

        _isCheckpoint = new bool[_commits.Length];
        Array.Fill(_isCheckpoint, true);
    }

    /// <summary>the entity's newest snapshot in this replay, or null if the replay hasn't touched it yet</summary>
    internal ObjectSnapshot? Latest(Guid entityId) => _latestSnapshots.GetValueOrDefault(entityId);

    /// <summary>whether this replay has made a snapshot of the entity</summary>
    internal bool Contains(Guid entityId) => _latestSnapshots.ContainsKey(entityId);

    /// <summary>the newest snapshot of every entity this replay has touched</summary>
    internal IReadOnlyCollection<ObjectSnapshot> LatestSnapshots => _latestSnapshots.Values;

    /// <summary>
    /// Makes <paramref name="snapshot"/> its entity's newest, and decides the fate of the one it supersedes.
    /// </summary>
    internal void Add(ObjectSnapshot snapshot)
    {
        var superseded = Latest(snapshot.EntityId);
        _latestSnapshots[snapshot.EntityId] = snapshot;
        if (superseded is null) return;

        if (superseded.CommitId == snapshot.CommitId)
        {
            // we (can) only keep 1 snapshot per entity per commit, so the new one replaces it and no history is lost
            return;
        }

        if (superseded.IsRoot || StraddlesCheckpointBoundary(superseded, snapshot))
        {
            _keptSupersededSnapshots.Add(superseded);
        }
        else
        {
            // the entity's state is no longer stored from the superseded commit up to (not including) the new one
            ClearCheckpoints(superseded.CommitId, upTo: snapshot.CommitId);
        }
    }

    /// <summary>every snapshot the replay must persist: the kept superseded ones and each entity's newest</summary>
    internal IReadOnlyList<ObjectSnapshot> SnapshotsToPersist() => [.. _keptSupersededSnapshots, .. _latestSnapshots.Values];

    /// <summary>
    /// Each batch commit paired with whether a replay can resume from it. Only valid once every snapshot has been added:
    /// until then we don't know which ones get dropped.
    /// </summary>
    internal CheckpointFlag[] CheckpointFlags()
    {
        var flags = new CheckpointFlag[_commits.Length];
        for (var position = 0; position < _commits.Length; position++)
        {
            flags[position] = new CheckpointFlag(_commits[position], _isCheckpoint[position]);
        }

        return flags;
    }

    /// <summary>
    /// Whether a checkpoint boundary falls between the commits of <paramref name="older"/> and <paramref name="newer"/>,
    /// which makes <paramref name="older"/> the entity's state at a checkpoint.
    /// </summary>
    private bool StraddlesCheckpointBoundary(ObjectSnapshot older, ObjectSnapshot newer) =>
        CheckpointInterval(older.CommitId) < CheckpointInterval(newer.CommitId);

    // which multiple of maxChanges the commit falls in; two commits straddle a boundary iff these differ
    private long CheckpointInterval(Guid commitId) => _changesBeforeCommit[PositionOf(commitId)] / _maxChangesBetweenCheckpoints;

    private void ClearCheckpoints(Guid fromCommitId, Guid upTo)
    {
        for (var position = PositionOf(fromCommitId); position < PositionOf(upTo); position++)
        {
            _isCheckpoint[position] = false;
        }
    }

    private int PositionOf(Guid commitId) => _positionOfCommit[commitId];
}

/// <param name="IsCheckpoint">whether a replay can resume from <paramref name="Commit"/></param>
internal readonly record struct CheckpointFlag(Commit Commit, bool IsCheckpoint);
