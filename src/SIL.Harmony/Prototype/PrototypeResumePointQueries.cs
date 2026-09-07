using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SIL.Harmony.Prototype;

// PROTOTYPE — throwaway, answers sillsdev/harmony#110. Not production code.
//
// Three ways to answer "the newest position a replay may resume from, at or before P".
// Reading them side by side IS the prototype; the gate is whether design 2 is worth its query.
public static class PrototypeResumePointQueries
{
    /// <summary>
    /// SHIPPED TODAY, for comparison. Safety is *claimed* up front on a flagged column, and the pruner is obliged
    /// to honour the claim — which is what rules 1, 3, 4 and 5 of the design doc exist to protect.
    /// </summary>
    public static IQueryable<Commit> FlagQueryable(IQueryable<Commit> commits, Commit? before)
    {
        var candidates = commits.Where(c => c.IsSnapshotCheckpoint);
        if (before is not null) candidates = candidates.WhereBefore(before, inclusive: true);
        return candidates.DefaultOrderDescending();
    }

    /// <summary>
    /// DESIGN 2: safety *derived* from recorded holes — the newest candidate position that no hole straddles.
    /// The pruner may drop whatever it likes so long as it records the hole, so a wrong pruning decision costs
    /// density rather than correctness. The price is this predicate, evaluated per candidate commit scanned.
    /// </summary>
    public static IQueryable<Commit> HoleQueryable(IQueryable<Commit> commits, IQueryable<SnapshotHole> holes, Commit? before)
    {
        var candidates = before is null ? commits : commits.WhereBefore(before, inclusive: true);
        return candidates
            .Where(c => !holes.Any(h =>
                // h.From <= c: the hole opens at or before this position
                (h.FromDateTime < c.HybridDateTime.DateTime
                 || (h.FromDateTime == c.HybridDateTime.DateTime && h.FromCounter < c.HybridDateTime.Counter)
                 || (h.FromDateTime == c.HybridDateTime.DateTime && h.FromCounter == c.HybridDateTime.Counter && h.FromCommitId <= c.Id))
                // c < h.To: and has not closed again by it
                && (c.HybridDateTime.DateTime < h.ToDateTime
                 || (c.HybridDateTime.DateTime == h.ToDateTime && c.HybridDateTime.Counter < h.ToCounter)
                 || (c.HybridDateTime.DateTime == h.ToDateTime && c.HybridDateTime.Counter == h.ToCounter && c.Id < h.ToCommitId))))
            .DefaultOrderDescending();
    }

    /// <summary>
    /// DESIGN 2b: the same, with the commit-id tiebreak dropped and both endpoints rounded *outward*, so a tie counts
    /// as inside the hole. Conservative — it can only call a safe position unsafe, never the reverse — and it halves
    /// the predicate.
    /// </summary>
    public static IQueryable<Commit> HoleQueryableConservative(IQueryable<Commit> commits, IQueryable<SnapshotHole> holes, Commit? before)
    {
        var candidates = before is null ? commits : commits.WhereBefore(before, inclusive: true);
        return candidates
            .Where(c => !holes.Any(h =>
                (h.FromDateTime < c.HybridDateTime.DateTime
                 || (h.FromDateTime == c.HybridDateTime.DateTime && h.FromCounter <= c.HybridDateTime.Counter))
                && (c.HybridDateTime.DateTime < h.ToDateTime
                 || (c.HybridDateTime.DateTime == h.ToDateTime && c.HybridDateTime.Counter <= h.ToCounter))))
            .DefaultOrderDescending();
    }

    /// <summary>
    /// DESIGN 2c: design 2 plus a guard on how far back a hole can possibly reach. No hole may span a position the
    /// pruner protected, so every hole is short; recording the widest hole's time span per database turns the
    /// subquery's open ended index seek into a bounded range.
    /// </summary>
    public static IQueryable<Commit> HoleQueryableBounded(IQueryable<Commit> commits, IQueryable<SnapshotHole> holes, Commit? before, double maxHoleSpanDays)
    {
        var candidates = before is null ? commits : commits.WhereBefore(before, inclusive: true);
        return candidates
            .Where(c => !holes.Any(h =>
                //the guard: anything opening earlier than this cannot still be open at c
                h.FromDateTime > c.HybridDateTime.DateTime.AddDays(-maxHoleSpanDays)
                && (h.FromDateTime < c.HybridDateTime.DateTime
                 || (h.FromDateTime == c.HybridDateTime.DateTime && h.FromCounter < c.HybridDateTime.Counter)
                 || (h.FromDateTime == c.HybridDateTime.DateTime && h.FromCounter == c.HybridDateTime.Counter && h.FromCommitId <= c.Id))
                && (c.HybridDateTime.DateTime < h.ToDateTime
                 || (c.HybridDateTime.DateTime == h.ToDateTime && c.HybridDateTime.Counter < h.ToCounter)
                 || (c.HybridDateTime.DateTime == h.ToDateTime && c.HybridDateTime.Counter == h.ToCounter && c.Id < h.ToCommitId))))
            .DefaultOrderDescending();
    }

    public static Task<Commit?> FromHolesBounded(IQueryable<Commit> commits, IQueryable<SnapshotHole> holes, Commit? before, double maxHoleSpanDays)
        => HoleQueryableBounded(commits, holes, before, maxHoleSpanDays).FirstOrDefaultAsync();

    public static Task<Commit?> FromHoles(IQueryable<Commit> commits, IQueryable<SnapshotHole> holes, Commit? before)
        => HoleQueryable(commits, holes, before).FirstOrDefaultAsync();

    public static Task<Commit?> FromHolesConservative(IQueryable<Commit> commits, IQueryable<SnapshotHole> holes, Commit? before)
        => HoleQueryableConservative(commits, holes, before).FirstOrDefaultAsync();

    /// <summary>
    /// DESIGN 3: safety still *claimed*, but recorded in its own table rather than as a column on Commit. Satisfies
    /// the modelling objection — Commit stays an atomic pointer holding changes — at exactly today's query cost.
    /// </summary>
    public static async Task<Commit?> FromResumePointTable(IQueryable<Commit> commits, IQueryable<PrototypeResumePoint> resumePoints, Commit? before)
    {
        var candidates = resumePoints;
        if (before is not null)
        {
            candidates = candidates.Where(r => r.DateTime < before.HybridDateTime.DateTime
                                               || (r.DateTime == before.HybridDateTime.DateTime && r.Counter < before.HybridDateTime.Counter)
                                               || (r.DateTime == before.HybridDateTime.DateTime && r.Counter == before.HybridDateTime.Counter && r.CommitId <= before.Id));
        }

        var newest = await candidates
            .OrderByDescending(r => r.DateTime).ThenByDescending(r => r.Counter).ThenByDescending(r => r.CommitId)
            .FirstOrDefaultAsync();
        return newest is null ? null : await commits.SingleAsync(c => c.Id == newest.CommitId);
    }
}

/// <summary>PROTOTYPE. Design 3's table: the positions a replay may resume from, chosen before the replay.</summary>
public class PrototypeResumePoint
{
    public Guid CommitId { get; init; }
    //denormalised so the seek matches today's IX_Commits_IsSnapshotCheckpoint_DateTime_Counter_Id exactly
    public required DateTimeOffset DateTime { get; init; }
    public required long Counter { get; init; }
}

public class PrototypeResumePointEntityConfig : IEntityTypeConfiguration<PrototypeResumePoint>
{
    public void Configure(EntityTypeBuilder<PrototypeResumePoint> builder)
    {
        builder.ToTable("PrototypeResumePoints");
        builder.HasKey(r => r.CommitId);
        builder.Property(r => r.DateTime).HasConversion(d => d.UtcDateTime, d => new DateTimeOffset(d.Ticks, TimeSpan.Zero));
        builder.HasIndex(r => new { r.DateTime, r.Counter, r.CommitId });
    }
}
