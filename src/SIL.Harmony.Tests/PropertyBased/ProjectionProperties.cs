using CsCheck;
using static SIL.Harmony.Tests.PropertyBased.HarmonyEngineHarness;

namespace SIL.Harmony.Tests.PropertyBased;

/// <summary>
/// Property-based tests for Harmony's incremental rollback-and-replay projection engine.
/// Each property drives real <see cref="DataModel"/> instances over generated schedules that
/// inject stragglers, ties, cascades, duplicates and batching (see <see cref="Generators"/>).
/// Every property runs against two generators: <b>Tier 1</b> (create-or-edit text only) and
/// <b>Tier 2</b> (creates, deletes, notes, definitions, reference cascades, and ordering).
///
/// Oracles (see PropertyBased/README.md for the full rationale):
///  - <b>Replica convergence</b> (primary, fully independent): the same commit set delivered
///    in two independent arrival orders must project identically.
///  - <b>Incremental == from-scratch</b> (secondary): the incrementally-rolled-back projection
///    must equal <c>RegenerateSnapshots()</c>, which bypasses the rollback-specific machinery.
///
/// A failure that shrinks to a genuine engine defect is a real finding — capture the CsCheck
/// seed it prints and the minimized schedule; do not weaken the property to make it pass.
/// Raise CsCheck_Iter / CsCheck_Time in CI; failures replay via CsCheck_Seed=&lt;seed&gt;.
/// </summary>
public class ProjectionProperties
{
    private static void AssertSameProjection(Projection a, Projection b, string because)
    {
        a.Entities.Should().BeEquivalentTo(b.Entities, because);
        a.LastCommitHash.Should().Be(b.LastCommitHash, because);
    }

    // ---- Oracle bodies, parameterized by generator ---------------------------------------

    /// <summary>
    /// P-converge (doc P1 / P-master): the projection is a pure function of the change SET.
    /// Two fresh replicas fed the same commits in independently shuffled/batched orders must
    /// converge to identical entity content and the same canonical commit-hash chain. Primary,
    /// order-independent oracle: any order-dependent rollback or tiebreak bug diverges here.
    /// </summary>
    private static Task ConvergesAcrossArrivalOrders(Gen<Schedule> schedules)
    {
        var output = TestContext.Current.TestOutputHelper;
        return schedules.SampleAsync(async schedule =>
        {
            await using var replicaA = await NewEngine();
            await using var replicaB = await NewEngine();

            await Feed(replicaA.DataModel, schedule.ArrivalsA);
            await Feed(replicaB.DataModel, schedule.ArrivalsB);

            AssertSameProjection(
                await Read(replicaA.DataModel),
                await Read(replicaB.DataModel),
                "two replicas receiving the same commit set in different arrival orders must converge");
        }, print: s => ReproCode.Emit(output, s, ReproTemplate.Converge));
    }

    /// <summary>
    /// P-regenerate (doc P-master, secondary oracle): the state built incrementally by the
    /// rollback engine must equal a full from-scratch replay via <c>RegenerateSnapshots()</c>,
    /// which rebuilds without any rollback machinery.
    /// </summary>
    private static Task IncrementalEqualsFromScratch(Gen<Schedule> schedules)
    {
        var output = TestContext.Current.TestOutputHelper;
        return schedules.SampleAsync(async schedule =>
        {
            await using var engine = await NewEngine();

            await Feed(engine.DataModel, schedule.ArrivalsA);
            var incremental = await Read(engine.DataModel);

            await engine.DataModel.RegenerateSnapshots();
            var fromScratch = await Read(engine.DataModel);

            AssertSameProjection(
                incremental,
                fromScratch,
                "the incrementally rolled-back projection must equal a from-scratch replay of the same commits");
        }, print: s => ReproCode.Emit(output, s, ReproTemplate.IncrementalVsFromScratch));
    }

    /// <summary>
    /// P-dup (doc §5.4): re-ingesting commits already in the log is idempotent. Re-sending the
    /// entire commit set as one batch must not change the projection or the commit-hash chain.
    /// </summary>
    private static Task ReingestIsIdempotent(Gen<Schedule> schedules)
    {
        var output = TestContext.Current.TestOutputHelper;
        return schedules.SampleAsync(async schedule =>
        {
            await using var engine = await NewEngine();

            await Feed(engine.DataModel, schedule.ArrivalsA);
            var before = await Read(engine.DataModel);

            await Feed(engine.DataModel, schedule.Commits);
            var after = await Read(engine.DataModel);

            AssertSameProjection(before, after, "re-ingesting already-present commits must leave the projection unchanged");
        }, print: s => ReproCode.Emit(output, s, ReproTemplate.ReingestIdempotent));
    }

    /// <summary>
    /// R-determinism (doc §5.6): replay is reproducible. Feeding the identical schedule to two
    /// fresh engines yields identical projected content and commit-hash chains.
    /// </summary>
    private static Task ReplayIsDeterministic(Gen<Schedule> schedules)
    {
        var output = TestContext.Current.TestOutputHelper;
        return schedules.SampleAsync(async schedule =>
        {
            await using var first = await NewEngine();
            await using var second = await NewEngine();

            await Feed(first.DataModel, schedule.ArrivalsA);
            await Feed(second.DataModel, schedule.ArrivalsA);

            AssertSameProjection(
                await Read(first.DataModel),
                await Read(second.DataModel),
                "feeding the identical schedule twice must produce identical projections");
        }, print: s => ReproCode.Emit(output, s, ReproTemplate.ReplayDeterministic));
    }

    // ---- Tier 1: create-or-edit text only ------------------------------------------------

    [Fact]
    public Task Tier1_Projection_IsIndependentOfArrivalOrder() => ConvergesAcrossArrivalOrders(Generators.Tier1);

    [Fact]
    public Task Tier1_IncrementalProjection_EqualsFromScratchReplay() => IncrementalEqualsFromScratch(Generators.Tier1);

    [Fact]
    public Task Tier1_ReingestingExistingCommits_IsIdempotent() => ReingestIsIdempotent(Generators.Tier1);

    [Fact]
    public Task Tier1_Replay_IsDeterministic() => ReplayIsDeterministic(Generators.Tier1);

    // ---- Tier 2: creates, deletes, definitions, references, ordering ---------------------

    [Fact]
    public Task Tier2_Projection_IsIndependentOfArrivalOrder() => ConvergesAcrossArrivalOrders(Generators.Tier2);

    [Fact]
    public Task Tier2_IncrementalProjection_EqualsFromScratchReplay() => IncrementalEqualsFromScratch(Generators.Tier2);

    [Fact]
    public Task Tier2_ReingestingExistingCommits_IsIdempotent() => ReingestIsIdempotent(Generators.Tier2);

    [Fact]
    public Task Tier2_Replay_IsDeterministic() => ReplayIsDeterministic(Generators.Tier2);
}
