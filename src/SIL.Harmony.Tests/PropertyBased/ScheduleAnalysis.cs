namespace SIL.Harmony.Tests.PropertyBased;

/// <summary>
/// Pure (engine-free) analyses of a generated <see cref="Schedule"/>. Used by the generator
/// meta-tests to prove the generator actually produces the phenomena the properties exist to
/// stress. Per the handoff doc §7: "if the generator doesn't produce these, the suite is
/// testing nothing." Ordering here mirrors <c>CommitBase.CompareKey</c> exactly.
/// </summary>
internal static class ScheduleAnalysis
{
    private static readonly IComparer<(DateTimeOffset, long, Guid)> KeyComparer =
        Comparer<(DateTimeOffset, long, Guid)>.Default;

    private static int CompareKeys(CommitSpec a, CommitSpec b) => KeyComparer.Compare(a.CompareKey, b.CompareKey);

    /// <summary>True if two commits share an author time (same DateTime + Counter) — the tiebreak path.</summary>
    public static bool HasAuthorTimeTie(Schedule schedule) =>
        schedule.Commits
            .GroupBy(c => (c.Time.DateTime, c.Time.Counter))
            .Any(g => g.Count() > 1);

    /// <summary>
    /// A "straggler" is a commit that arrives with a canonical key EARLIER than the maximum
    /// key already delivered — i.e. it must be slotted into the past, forcing a rollback.
    /// Returns the total number of stragglers across the whole delivery plan.
    /// </summary>
    public static int StragglerCount(IReadOnlyList<Arrival> arrivals)
    {
        var count = 0;
        CommitSpec? maxSeen = null;
        foreach (var commit in arrivals.SelectMany(a => a.Batch))
        {
            if (maxSeen is not null && CompareKeys(commit, maxSeen) < 0) count++;
            if (maxSeen is null || CompareKeys(commit, maxSeen) > 0) maxSeen = commit;
        }
        return count;
    }

    /// <summary>
    /// The largest number of stragglers contained in a single batch, measured against the state
    /// BEFORE that batch. A value ≥ 2 is a "cascade": one ingest that must roll back for several
    /// stragglers at once (and, correctly, to the earliest of them — not process each alone).
    /// </summary>
    public static int MaxCascade(IReadOnlyList<Arrival> arrivals)
    {
        var maxCascade = 0;
        CommitSpec? maxSeen = null;
        foreach (var arrival in arrivals)
        {
            var stragglersThisBatch = 0;
            CommitSpec? maxInBatch = null;
            foreach (var commit in arrival.Batch)
            {
                if (maxSeen is not null && CompareKeys(commit, maxSeen) < 0) stragglersThisBatch++;
                if (maxInBatch is null || CompareKeys(commit, maxInBatch) > 0) maxInBatch = commit;
            }
            maxCascade = Math.Max(maxCascade, stragglersThisBatch);
            if (maxInBatch is not null && (maxSeen is null || CompareKeys(maxInBatch, maxSeen) > 0)) maxSeen = maxInBatch;
        }
        return maxCascade;
    }

    /// <summary>True if any commit id is delivered in more than one batch (a duplicate re-send).</summary>
    public static bool HasDuplicates(IReadOnlyList<Arrival> arrivals)
    {
        var total = arrivals.Sum(a => a.Batch.Count);
        var distinct = arrivals.SelectMany(a => a.Batch).Select(c => c.Id).Distinct().Count();
        return total > distinct;
    }

    /// <summary>
    /// True if the globally-earliest commit (the new canonical genesis) is delivered after the
    /// very first batch — arriving as a straggler earlier than everything already folded, which
    /// forces a rollback all the way toward genesis.
    /// </summary>
    public static bool ForcesGenesisRebuild(Schedule schedule, IReadOnlyList<Arrival> arrivals)
    {
        if (arrivals.Count == 0) return false;
        var globalMin = schedule.Commits.Aggregate((a, b) => CompareKeys(a, b) <= 0 ? a : b);
        var firstBatchIds = arrivals[0].Batch.Select(c => c.Id).ToHashSet();
        if (firstBatchIds.Contains(globalMin.Id)) return false;
        // It forces a genesis rebuild only if something with a larger key was already folded first.
        return arrivals[0].Batch.Any(c => CompareKeys(c, globalMin) > 0);
    }
}
