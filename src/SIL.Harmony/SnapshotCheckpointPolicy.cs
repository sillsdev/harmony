namespace SIL.Harmony;

/// <summary>
/// Decides which snapshots a replay of one batch must keep, and tracks which of its commits
/// a later replay can resume from as a result. Has mutable state, one instance per replay.
/// </summary>
internal sealed class SnapshotCheckpointPolicy
{
    private readonly int _maxChangesBetweenCheckpoints;
    // _changesBeforeCommit[commitIndex] is the number of changes in the commits before it, so it starts at 0
    private readonly long[] _changesBeforeCommit;

    /// <summary>every commit is safe to resume from until a dropped snapshot says otherwise</summary>
    private readonly bool[] _isCheckpoint;

    /// <param name="changesPerCommit">each commit's change count, in the batch's commit order</param>
    /// <param name="maxChangesBetweenCheckpoints">force a safe commit at least this many changes apart</param>
    internal SnapshotCheckpointPolicy(IEnumerable<int> changesPerCommit, int maxChangesBetweenCheckpoints)
    {
        if (maxChangesBetweenCheckpoints < 1) throw new ArgumentOutOfRangeException(nameof(maxChangesBetweenCheckpoints));
        _maxChangesBetweenCheckpoints = maxChangesBetweenCheckpoints;
        var changesBefore = new List<long> { 0 }; // there are 0 changes before the first commit
        var running = 0L;
        foreach (var changes in changesPerCommit)
        {
            running += changes;
            changesBefore.Add(running);
        }
        _changesBeforeCommit = [.. changesBefore];
        _isCheckpoint = new bool[_changesBeforeCommit.Length - 1];

        // we consider every commit a checkpoint until we learn otherwise
        Array.Fill(_isCheckpoint, true);
    }

    /// <summary>
    /// Whether an entity's snapshot at commit <paramref name="snapshotCommitIndex"/> must be kept when its next one is at
    /// <paramref name="nextSnapshotCommitIndex"/> (batch indexes). Keeping the ones that straddle a boundary is what ensures we have a
    /// checkpoint AT LEAST every maxChangesBetweenCheckpoints changes.
    /// </summary>
    internal bool MustKeepSnapshot(int snapshotCommitIndex, int nextSnapshotCommitIndex)
    {
        return CheckpointInterval(snapshotCommitIndex) < CheckpointInterval(nextSnapshotCommitIndex);
    }

    /// <summary>
    /// Records that a snapshot was dropped at <paramref name="snapshotCommitIndex"/>, and that the next snapshot for that entity is at <paramref name="nextSnapshotCommitIndex"/>.
    /// So, everything between those two commits is no longer safe to resume from, and we mark them as such.
    /// </summary>
    internal void SnapshotDropped(int snapshotCommitIndex, int nextSnapshotCommitIndex)
    {
        for (var commitIndex = snapshotCommitIndex; commitIndex < nextSnapshotCommitIndex; commitIndex++)
        {
            _isCheckpoint[commitIndex] = false;
        }
    }

    // which multiple of maxChanges the commit falls in; two commits straddle a boundary iff these differ
    private long CheckpointInterval(int commitIndex) => _changesBeforeCommit[commitIndex] / _maxChangesBetweenCheckpoints;

    /// <summary>Whether the batch's commit at <paramref name="commitIndex"/> is safe to resume a replay from.</summary>
    internal bool IsCheckpoint(int commitIndex) => _isCheckpoint[commitIndex];

    /// <summary>
    /// Writes the outcome onto <paramref name="batchCommits"/>, which must be the same commits in the same order
    /// the policy was built from. Only valid once the replay is done: until then we don't know which snapshots get dropped.
    /// </summary>
    internal void PopulateCheckpoints(Commit[] batchCommits)
    {
        if (batchCommits.Length != _isCheckpoint.Length)
            throw new ArgumentException("commits must be the batch this policy was built from", nameof(batchCommits));
        for (var commitIndex = 0; commitIndex < batchCommits.Length; commitIndex++)
        {
            batchCommits[commitIndex].IsSnapshotCheckpoint = _isCheckpoint[commitIndex];
        }
    }
}
