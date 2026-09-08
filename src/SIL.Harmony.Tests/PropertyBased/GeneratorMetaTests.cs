using CsCheck;
using FluentAssertions.Execution;

namespace SIL.Harmony.Tests.PropertyBased;

/// <summary>
/// Meta-tests over the generators themselves (no engine involved). They prove the generated
/// schedules actually contain author-time ties, stragglers, cascades, duplicates, and
/// genesis-depth rollbacks. If any of these stopped appearing, the property suite would still
/// go green while testing nothing — these tests fail loudly instead.
/// </summary>
public class GeneratorMetaTests
{
    // Fixed, generous sample budget so every (even rare) axis appears with overwhelming
    // probability. Independent of CsCheck_Iter so CI tuning of the real properties doesn't
    // make this meta-check flaky.
    private const int Iterations = 5000;

    [Fact]
    public void Tier1_Generator_Produces_AllRollbackPhenomena() => AssertProducesAllPhenomena(Generators.Tier1);

    [Fact]
    public void Tier2_Generator_Produces_AllRollbackPhenomena() => AssertProducesAllPhenomena(Generators.Tier2);

    private static void AssertProducesAllPhenomena(Gen<Schedule> schedules)
    {
        var sawTie = false;
        var sawStraggler = false;
        var sawCascade = false;
        var sawDuplicate = false;
        var sawGenesisRebuild = false;

        schedules.Sample(schedule =>
        {
            sawTie |= ScheduleAnalysis.HasAuthorTimeTie(schedule);
            sawStraggler |= ScheduleAnalysis.StragglerCount(schedule.ArrivalsA) > 0;
            sawCascade |= ScheduleAnalysis.MaxCascade(schedule.ArrivalsA) >= 2;
            sawDuplicate |= ScheduleAnalysis.HasDuplicates(schedule.ArrivalsA)
                            || ScheduleAnalysis.HasDuplicates(schedule.ArrivalsB);
            sawGenesisRebuild |= ScheduleAnalysis.ForcesGenesisRebuild(schedule, schedule.ArrivalsA);
            return true;
        }, iter: Iterations);

        using var _ = new AssertionScope();
        sawTie.Should().BeTrue("the small author-time range must produce commits with equal author times (tiebreak path)");
        sawStraggler.Should().BeTrue("arrival order independent of author time must produce stragglers (rollback path)");
        sawCascade.Should().BeTrue("batches must sometimes contain multiple stragglers (cascading rollback)");
        sawDuplicate.Should().BeTrue("the generator must re-send some commits (idempotency/dedup path)");
        sawGenesisRebuild.Should().BeTrue("some straggler must arrive earlier than everything already folded (genesis-depth rollback)");
    }
}
