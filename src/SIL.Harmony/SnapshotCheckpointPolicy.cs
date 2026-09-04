namespace SIL.Harmony;

/// <summary>
/// Picks which commits of a replayed batch become checkpoints, and which snapshots that choice forces the replay to keep.
/// </summary>
/// <remarks>
/// A replay resumes at a checkpoint by seeding every entity from its newest snapshot at or before it, so that snapshot
/// has to be the entity's state there. Dropping a snapshot leaves a gap from it up to the entity's next snapshot, and any
/// checkpoint inside that gap would seed the entity from before an edit nothing is going to re-apply. Hence the two halves
/// here: choose the checkpoints first, then keep whatever snapshots they need.
///
/// Density is the only dial. Storage scales with it; rollback distance and the cost of reading state at an old commit
/// scale inversely. A commit's flag may only ever be cleared, never set outside a window being replayed, otherwise it
/// claims safety at a position an earlier replay already left a gap in.
/// </remarks>
/// <param name="Interval">every Nth commit of a batch is a checkpoint</param>
internal sealed record SnapshotCheckpointPolicy(int Interval)
{
    internal static SnapshotCheckpointPolicy Default { get; } = new(8);

    /// <param name="commitIndex">1 based position in the batch</param>
    /// <param name="commitCount">size of the batch; its last commit is always a checkpoint, since every entity keeps the snapshot of its last touch</param>
    internal bool IsCheckpoint(int commitIndex, int commitCount)
    {
        return commitIndex % Interval == 0 || commitIndex == commitCount;
    }

    /// <summary>
    /// Whether the snapshot an entity got at <paramref name="commitIndex"/> has to be kept, given that the entity's next
    /// snapshot in the batch is at <paramref name="nextCommitIndex"/>.
    /// </summary>
    internal bool MustKeepSnapshot(int commitIndex, int nextCommitIndex)
    {
        return NextCheckpointAtOrAfter(commitIndex) < nextCommitIndex;
    }

    private int NextCheckpointAtOrAfter(int commitIndex)
    {
        return (commitIndex + Interval - 1) / Interval * Interval;
    }
}
