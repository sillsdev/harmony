using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony.Prototype;

// PROTOTYPE — throwaway, answers sillsdev/harmony#115. Not production code.
public static class PrototypeResumeRangeQueries
{
    /// <summary>
    /// The write side, as a pure function: given how many commits the batch had and the holes its pruning opened,
    /// the runs of positions that stay safe. Holes are half open <c>[From, To)</c>, runs are closed, both in 1 based
    /// batch positions.
    /// </summary>
    /// <remarks>
    /// This is the whole of the interval work, done once over data the replay already holds, which is why the read
    /// path needs no interval query at all.
    /// </remarks>
    internal static List<(int From, int To)> SafeRuns(int commitCount, IEnumerable<(int From, int To)> holes)
    {
        var unsafeAt = new bool[commitCount + 2];
        foreach (var (from, to) in holes)
        {
            for (var position = from; position < to; position++) unsafeAt[position] = true;
        }

        var runs = new List<(int From, int To)>();
        var runStart = 0;
        for (var position = 1; position <= commitCount + 1; position++)
        {
            var safe = position <= commitCount && !unsafeAt[position];
            if (safe && runStart == 0) runStart = position;
            if (!safe && runStart != 0)
            {
                runs.Add((runStart, position - 1));
                runStart = 0;
            }
        }

        return runs;
    }

    /// <summary>
    /// The read side. One index read on the ranges, then at most one more on the commits, and no correlated
    /// subquery anywhere - which is the whole point of storing runs rather than holes.
    /// </summary>
    /// <param name="before">the position being resumed to, or null for the newest position in history</param>
    /// <param name="inclusive">whether <paramref name="before"/> itself may be the answer</param>
    public static async Task<Commit?> FindNewestResumePoint(
        IQueryable<Commit> commits,
        IQueryable<ResumeRange> ranges,
        Commit? before,
        bool inclusive)
    {
        //seek 1: the newest range that starts at a position we are allowed to return. Ranges are disjoint and
        //ordered, so this is the only candidate: every later range starts past the bound, and every earlier range
        //ends before this one starts. It always contains at least one allowed position, its own start.
        var range = await NewestRangeStartingWithinBound(ranges, before, inclusive).FirstOrDefaultAsync();
        if (range is null) return null;

        //the bound is at or after the whole range, so the range's own end is the answer: a primary key lookup
        if (before is null || !EndsAtOrAfter(range, before))
            return await commits.FirstOrDefaultAsync(c => c.Id == range.ToCommitId);

        //the bound falls inside the range, so every position up to it is safe and the answer needs no search at
        //all: either the bound itself, or the commit before it, which is >= the range's start because seek 1
        //only accepted a range starting strictly before the bound.
        if (inclusive) return before;
        return await commits.WhereBefore(before).DefaultOrderDescending().FirstOrDefaultAsync();
    }

    /// <remarks>
    /// Inclusivity has no part in this: it is settled by seek 1, which only accepts a range starting at an allowed
    /// position. A range ending exactly at the bound still means the bound is inside it, and an exclusive lookup
    /// then wants the commit before the bound rather than the range's end, which is the bound itself.
    /// </remarks>
    private static bool EndsAtOrAfter(ResumeRange range, Commit before)
    {
        return (range.ToDateTime, range.ToCounter, range.ToCommitId).CompareTo(before.CompareKey) >= 0;
    }

    internal static IQueryable<ResumeRange> NewestRangeStartingWithinBound(IQueryable<ResumeRange> ranges, Commit? before, bool inclusive)
    {
        if (before is not null)
        {
            ranges = ranges.Where(r => r.FromDateTime < before.HybridDateTime.DateTime
                                       || (r.FromDateTime == before.HybridDateTime.DateTime && r.FromCounter < before.HybridDateTime.Counter)
                                       || (r.FromDateTime == before.HybridDateTime.DateTime && r.FromCounter == before.HybridDateTime.Counter && r.FromCommitId < before.Id)
                                       || (inclusive && r.FromCommitId == before.Id));
        }

        return ranges
            .OrderByDescending(r => r.FromDateTime)
            .ThenByDescending(r => r.FromCounter)
            .ThenByDescending(r => r.FromCommitId);
    }

    /// <summary>
    /// Not used by the read path: kept only so the report can compare its plan. Bounding the commit search on both
    /// sides looks like it should be a range seek and is not, because SQLite will not turn three ANDed lexicographic
    /// OR chains into one. Deciding between the range's end and the bound's predecessor in memory avoids it.
    /// </summary>
    internal static IQueryable<Commit> CommitsWithin(IQueryable<Commit> commits, ResumeRange range, Commit? before, bool inclusive)
    {
        commits = commits.Where(c => c.HybridDateTime.DateTime > range.FromDateTime
                                     || (c.HybridDateTime.DateTime == range.FromDateTime && c.HybridDateTime.Counter > range.FromCounter)
                                     || (c.HybridDateTime.DateTime == range.FromDateTime && c.HybridDateTime.Counter == range.FromCounter && c.Id >= range.FromCommitId));
        //and above by the range's end, since positions past it are not safe
        commits = commits.Where(c => c.HybridDateTime.DateTime < range.ToDateTime
                                     || (c.HybridDateTime.DateTime == range.ToDateTime && c.HybridDateTime.Counter < range.ToCounter)
                                     || (c.HybridDateTime.DateTime == range.ToDateTime && c.HybridDateTime.Counter == range.ToCounter && c.Id <= range.ToCommitId));
        if (before is not null) commits = commits.WhereBefore(before, inclusive);
        return commits.DefaultOrderDescending();
    }
}
