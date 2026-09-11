namespace SIL.Harmony;

/// <summary>
/// Decides which snapshots a replay of one batch must keep (the retention floor) and, from the holes the dropped ones
/// leave, which of the batch's commits are checkpoints (safe to resume from).
/// </summary>
/// <remarks>
/// A replay resumes at a commit by seeding every entity from its newest snapshot at or before it, so that snapshot has
/// to be the entity's state there. Dropping a snapshot leaves a hole from its commit up to the entity's next snapshot;
/// resuming inside that hole seeds the entity from before an edit nothing is going to re-apply, so the whole hole is
/// unsafe to resume at.
///
/// <see cref="MustKeepSnapshot"/> is the retention floor, applied during the replay: keep a snapshot whenever dropping
/// it would let a hole span a floor boundary, which guarantees a safe commit at least every <c>maxChanges</c> changes.
/// <see cref="DiscoverCheckpoints"/> is discovery, applied after: every commit is safe unless a hole covers it, so the floor's
/// guaranteed commits fall out as a subset and every other commit that came out safe is kept too.
///
/// Boundaries count changes, not commits, because replay cost is per change: one commit of 1000 changes costs as much
/// to replay as 1000 single-change commits. Storage trades against replay cost through <c>maxChanges</c> alone.
/// </remarks>
internal sealed class SnapshotCheckpointPolicy
{
    private readonly int _maxChanges;
    // prefix sums of change counts in commit order; _changesUpTo[i] is the changes in the batch's first i commits
    private readonly long[] _changesUpTo;

    /// <param name="changesPerCommit">each commit's change count, in the batch's commit order</param>
    /// <param name="maxChanges">force a safe commit at least this many changes apart</param>
    internal SnapshotCheckpointPolicy(IEnumerable<int> changesPerCommit, int maxChanges)
    {
        if (maxChanges < 1) throw new ArgumentOutOfRangeException(nameof(maxChanges));
        _maxChanges = maxChanges;
        var changesUpTo = new List<long> { 0 };
        var running = 0L;
        foreach (var changes in changesPerCommit)
        {
            running += changes;
            changesUpTo.Add(running);
        }
        _changesUpTo = [.. changesUpTo];
    }

    private int CommitCount => _changesUpTo.Length - 1;

    /// <summary>
    /// Whether the pruner must keep the snapshot an entity got at commit <paramref name="from"/>, given that its next
    /// snapshot in the batch is at commit <paramref name="to"/> (both 1-based positions). Keep it exactly when a floor
    /// boundary falls in the hole <c>[from, to)</c> that dropping it would open, i.e. when the two positions sit in
    /// different floor intervals.
    /// </summary>
    internal bool MustKeepSnapshot(int from, int to)
    {
        return FloorInterval(from) < FloorInterval(to);
    }

    // position is 1-based (ChangeContext.CommitIndex); _changesUpTo[position - 1] is the changes before it. A floor
    // boundary lies between two positions iff they fall in different intervals of maxChanges.
    private long FloorInterval(int position) => _changesUpTo[position - 1] / _maxChanges;

    /// <summary>
    /// The checkpoint flag for every commit in the batch, in commit order: assume each is safe to resume from, then
    /// clear the ones a dropped snapshot's <paramref name="holes"/> cover. Holes are half-open <c>[from, to)</c> ranges
    /// of 1-based positions.
    /// </summary>
    internal bool[] DiscoverCheckpoints(IEnumerable<(int From, int ToExclusive)> holes)
    {
        var isCheckpoint = new bool[CommitCount];
        Array.Fill(isCheckpoint, true);
        foreach (var (from, toExclusive) in holes)
        {
            for (var position = from; position < toExclusive; position++)
            {
                isCheckpoint[position - 1] = false; // positions are 1-based
            }
        }

        return isCheckpoint;
    }
}
