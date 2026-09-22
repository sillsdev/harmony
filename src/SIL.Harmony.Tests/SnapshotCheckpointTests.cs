using FluentAssertions.Equivalency;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class SnapshotCheckpointTests() : DataModelTestBase(configure: UseTestMaxChanges)
{
    // a low max so short test batches still cross several checkpoint boundaries; the plans below are one change per commit
    private const int TestMaxChanges = 8;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryCheckpointHoldsTheStateAReplayWouldResumeFrom(int seed)
    {
        var plan = PlanHistory(24, seed);
        var commits = await AddInOneBatch(this, plan);
        var checkpoints = await CheckpointsIn(commits);
        checkpoints.Should().HaveCountGreaterThan(1, "otherwise this only checks the end of the batch");
        checkpoints.Should().HaveCountLessThan(commits.Length, "only a hole unflags a commit, so this says something was dropped");
        var snapshots = await AllSnapshots();

        foreach (var checkpoint in checkpoints)
        {
            var checkpointState = StateAtOrBefore(snapshots, checkpoint);

            //Rebuild from scratch up to the target checkpoint
            await using var fromScratch = NewModel();
            var commitCount = Array.IndexOf(commits, checkpoint) + 1;
            var planUpToCheckpoint = plan.Take(commitCount);
            planUpToCheckpoint.Last().Change.Should().Be(checkpoint.ChangeEntities.Single().Change);
            await AddInOneBatch(fromScratch, planUpToCheckpoint);
            var expectedState = await CurrentState(fromScratch);

            checkpointState.Should().BeEquivalentTo(expectedState, RespectSnapshotTypes,
                $"snapshots have to be complete at the checkpoint {commitCount} commits in");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ALateCommitAtAnyPositionKeepsEveryEntitysState(int seed)
    {
        var plan = PlanHistory(20, seed);
        var commits = await AddInOneBatch(this, plan);
        var expected = await CurrentState(this);

        for (var index = 0; index < commits.Length; index++)
        {
            await using var fork = ForkDatabase();
            await fork.WriteNoOpCommitAfter(commits[index]);

            var state = await CurrentState(fork);
            state.Should().BeEquivalentTo(expected, RespectSnapshotTypes,
                $"a commit landing after commits[{index}] changes nothing itself");
        }
    }

    [Fact]
    public async Task ConsecutiveCheckpointsAreNeverMoreThanMaxChangesApart()
    {
        var wordId = Guid.NewGuid();
        // this plan only touches a single entity, so it never drops a snapshot
        var plan = Enumerable.Range(0, 24)
            .Select(i => new PlannedChange(NextDate(), SetWord(wordId, $"word {i}")))
            .ToArray();
        var commits = await AddInOneBatch(this, plan);
        var checkpoints = await CheckpointsIn(commits);

        checkpoints.Should().Contain(commits[^1], "the last commit is always safe to resume from");
        var positions = checkpoints.Select(c => Array.IndexOf(commits, c)).ToArray();
        foreach (var (previous, next) in positions.Zip(positions.Skip(1)))
        {
            (next - previous).Should().BeLessThanOrEqualTo(TestMaxChanges,
                "a late commit's resume point is never more than a checkpoint interval behind it");
        }
    }

    [Fact]
    public async Task DiscoveryFlagsEverySafeCommitNotJustTheCheckpointBoundaries()
    {
        //every commit creates a distinct word and never touches it again, so nothing is ever dropped and every commit is safe
        var plan = Enumerable.Range(0, 24)
            .Select(i => new PlannedChange(NextDate(), SetWord(Guid.NewGuid(), $"word {i}")))
            .ToArray();
        var commits = await AddInOneBatch(this, plan);

        (await CheckpointsIn(commits)).Should().Equal(commits,
            "every commit touched a different entity, so there are no holes: far more checkpoints than the policy requires");
    }

    [Fact]
    public async Task EveryLocallyAuthoredCommitIsACheckpoint()
    {
        var entityId = Guid.NewGuid();
        var first = await WriteNextChange(SetWord(entityId, "first"));
        var second = await WriteNextChange(SetWord(entityId, "second"));

        (await CheckpointsIn([first, second])).Should().Equal(first, second);
    }

    [Fact]
    public async Task ALateCommitResumesFromTheNewestCheckpointBeforeItRatherThanRebuildingEverything()
    {
        var commits = await AddInOneBatch(this, PlanHistory(20, seed: 5));
        const int lateIndex = 9; // mid-history, so there is a checkpoint both before and after it

        var checkpoints = await CheckpointsIn(commits);
        var resumeCheckpoint = checkpoints.Last(c => c.CompareTo(commits[lateIndex]) <= 0);
        var keptSnapshotIds = await DbContext.Snapshots.AsNoTracking()
            .WhereBefore(resumeCheckpoint, inclusive: true)
            .Select(s => s.Id)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        keptSnapshotIds.Should().NotBeEmpty();

        await WriteNoOpCommitAfter(commits[lateIndex]);

        (await SnapshotIds()).Should().Contain(keptSnapshotIds, "snapshots at or before the resume checkpoint are never rebuilt");
        (await CheckpointsIn(commits)).Should().Contain(resumeCheckpoint, "a commit before the replay window keeps its flag");
    }

    [Fact]
    public async Task AnOrdinaryAppendReplaysOnlyTheCommitsItAdds()
    {
        await AddInOneBatch(this, PlanHistory(20, seed: 7));
        var existingSnapshotIds = await SnapshotIds();

        await WriteNextChange(SetWord(Guid.NewGuid(), "appended"));

        (await SnapshotIds()).Should().Contain(existingSnapshotIds,
            "the head is a checkpoint, so an append resumes there and rebuilds nothing");
    }

    [Fact]
    public async Task ADatabaseWithNoCheckpointsRebuildsEverySnapshotOnAnOrdinaryAppend()
    {
        var commits = await AddInOneBatch(this, PlanHistory(12, seed: 6));
        var expected = await CurrentState(this);
        var staleSnapshotIds = await SnapshotIds();
        await ClearCheckpointFlags();

        await WriteNoOpCommit();

        (await SnapshotIds()).Should().NotIntersectWith(staleSnapshotIds,
            "with nothing to resume from, every snapshot is rebuilt");
        (await CheckpointsIn(commits)).Should().Contain([commits[0], commits[^1]],
            "any write replays the unflagged history and flags it, rather than waiting for a late commit");
        var state = await CurrentState(this);
        state.Should().BeEquivalentTo(expected, RespectSnapshotTypes);
    }

    private static void UseTestMaxChanges(IServiceCollection services) =>
        services.Configure<HarmonyConfig>(config => config.MaxChangesBetweenSnapshotCheckpoints = TestMaxChanges);

    private static DataModelTestBase NewModel() => new(configure: UseTestMaxChanges);

    private sealed record PlannedChange(DateTimeOffset Date, IChange Change);

    /// <summary>
    /// A history of creates, edits, references and cascading deletes. Randomized so the tests cover the shapes of gap
    /// that a hand written history keeps missing, seeded so a failure is reproducible.
    /// </summary>
    private PlannedChange[] PlanHistory(int commitCount, int seed)
    {
        var random = new Random(seed);
        List<Guid> words = [];
        List<Guid> definitions = [];
        var plan = new List<PlannedChange>();
        while (plan.Count < commitCount)
        {
            plan.Add(new PlannedChange(NextDate(), NextChange()));
        }

        return [.. plan];

        IChange NextChange()
        {
            if (words.Count == 0 || random.Next(4) == 0) return NewWord();
            var wordId = words[random.Next(words.Count)];
            return random.Next(6) switch
            {
                0 => new SetWordNoteChange(wordId, $"note {plan.Count}"),
                1 => Antonym(wordId),
                2 => NewDefinitionFor(wordId),
                3 when definitions.Count > 0 => new SetDefinitionPartOfSpeechChange(definitions[random.Next(definitions.Count)], $"part of speech {plan.Count}"),
                4 => new DeleteChange<Word>(wordId),
                _ => new SetWordTextChange(wordId, $"text {plan.Count}"),
            };
        }

        IChange NewWord()
        {
            var wordId = Guid.NewGuid();
            words.Add(wordId);
            return new SetWordTextChange(wordId, $"word {words.Count}");
        }

        IChange Antonym(Guid wordId)
        {
            var others = words.Where(w => w != wordId).ToArray();
            if (others is []) return new SetWordTextChange(wordId, $"text {plan.Count}");
            return new SetAntonymReferenceChange(wordId, others[random.Next(others.Length)]);
        }

        IChange NewDefinitionFor(Guid wordId)
        {
            var definitionId = Guid.NewGuid();
            definitions.Add(definitionId);
            return new NewDefinitionChange(definitionId)
            {
                WordId = wordId,
                Text = $"definition {definitions.Count}",
                PartOfSpeech = "noun",
                Order = definitions.Count
            };
        }
    }

    private static async Task<Commit[]> AddInOneBatch(DataModelTestBase model, IEnumerable<PlannedChange> plan)
    {
        var commits = new List<Commit>();
        foreach (var planned in plan)
        {
            commits.Add(await model.WriteChangeAt(planned.Date, planned.Change, add: false));
        }

        await model.AddCommitsViaSync(commits);
        return [.. commits];
    }

    private static async Task<Dictionary<Guid, object>> CurrentState(DataModelTestBase model)
    {
        var snapshots = await model.DataModel.GetLatestSnapshots().ToArrayAsync(TestContext.Current.CancellationToken);
        return snapshots.ToDictionary(s => s.EntityId, s => s.Entity.DbObject);
    }

    private Task<ObjectSnapshot[]> AllSnapshots() =>
        DbContext.Snapshots.AsNoTracking()
            .Include(s => s.Commit)
            .ToArrayAsync(TestContext.Current.CancellationToken);

    /// <summary>each entity's newest stored snapshot at or before <paramref name="commit"/>, worked out without the production query</summary>
    private static Dictionary<Guid, object> StateAtOrBefore(ObjectSnapshot[] snapshots, Commit commit) =>
        snapshots
            .Where(s => s.Commit.CompareKey.CompareTo(commit.CompareKey) <= 0)
            .GroupBy(s => s.EntityId)
            .ToDictionary(g => g.Key, g => g.MaxBy(s => s.Commit.CompareKey)!.Entity.DbObject);

    //the entities are only known as object here, so without this FluentAssertions compares no members at all
    private static readonly Func<EquivalencyOptions<KeyValuePair<Guid, object>>, EquivalencyOptions<KeyValuePair<Guid, object>>> RespectSnapshotTypes =
        options => options.PreferringRuntimeMemberTypes();

    private Task<Guid[]> SnapshotIds() =>
        DbContext.Snapshots.AsNoTracking().Select(s => s.Id).ToArrayAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// Returns the commits in <paramref name="history"/> that are marked as checkpoints in the DB.
    /// </summary>
    private async Task<Commit[]> CheckpointsIn(IEnumerable<Commit> history)
    {
        var checkpointIds = await DbContext.Commits.AsNoTracking()
            .Where(c => c.IsSnapshotCheckpoint)
            .Select(c => c.Id)
            .ToHashSetAsync(TestContext.Current.CancellationToken);
        return [.. history.Where(c => checkpointIds.Contains(c.Id))];
    }
}
