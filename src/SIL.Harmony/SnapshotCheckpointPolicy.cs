using SIL.Harmony.Db;

namespace SIL.Harmony;

/// <summary>
/// Decides, for a replay of one batch, whether a superseded snapshot must still be persisted, and tracks which of the
/// batch's commits a later replay can resume from as a result. Has mutable state, one instance per replay.
/// </summary>
/// <remarks>
/// A superseded snapshot is only worth persisting if it is its entity's state at a checkpoint: a commit a later replay
/// resumes from. Dropping one leaves a hole in the entity's history, so the commits that hole spans stop being
/// checkpoints. Keeping every superseded snapshot that straddles a boundary between two checkpoint intervals is what
/// guarantees a checkpoint at least every <c>maxChangesBetweenCheckpoints</c> changes.
/// </remarks>
internal sealed class SnapshotCheckpointPolicy
{
    private readonly int _maxChangesBetweenCheckpoints;
    private readonly Commit[] _commits;
    /// <summary>each commit's position in the batch, so a snapshot's commit tells us where in the batch it was made</summary>
    private readonly Dictionary<Guid, int> _positionOfCommit;
    /// <summary>running totals, so [0] is 0 and there is one more entry than there are commits</summary>
    private readonly long[] _changesBeforeCommit;
    /// <summary>every commit is safe to resume from until a dropped snapshot says otherwise</summary>
    private readonly bool[] _isCheckpoint;

    /// <param name="commits">the batch being replayed, in replay order</param>
    /// <param name="maxChangesBetweenCheckpoints">force a safe commit at least this many changes apart</param>
    internal SnapshotCheckpointPolicy(IEnumerable<Commit> commits, int maxChangesBetweenCheckpoints)
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

    /// <summary>
    /// Whether <paramref name="superseded"/> must still be persisted now that <paramref name="newer"/> is its entity's
    /// newest snapshot. Answering no drops it, so the commits its entity's state is then missing for stop being checkpoints.
    /// </summary>
    internal bool MustKeep(ObjectSnapshot superseded, ObjectSnapshot newer)
    {
        if (superseded.CommitId == newer.CommitId)
        {
            // we (can) only keep 1 snapshot per entity per commit, so the new one replaces it and no history is lost
            return false;
        }

        if (superseded.IsRoot) return true; // always keep root snapshots

        if (StraddlesCheckpointBoundary(superseded, newer)) return true;

        // the entity's state is no longer stored from the superseded commit up to (not including) the new one
        ClearCheckpoints(superseded.CommitId, upTo: newer.CommitId);
        return false;
    }

    /// <summary>
    /// Each batch commit paired with whether a replay can resume from it. Only valid once the replay is done:
    /// until then we don't know which snapshots get dropped.
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
