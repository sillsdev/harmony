using System.Text.Json;
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
        var checkpoints = await Checkpoints();
        checkpoints.Should().HaveCountGreaterThan(1, "otherwise this only checks the end of the batch");
        checkpoints.Should().HaveCountLessThan(commits.Length, "only a hole unflags a commit, so this says something was dropped");
        var snapshots = await AllSnapshots();

        foreach (var checkpoint in checkpoints)
        {
            var checkpointState = StateAtOrBefore(snapshots, checkpoint);

            //Rebuild from scratch up to the target checkpoint
            await using var fromScratch = NewModel();
            var commitCount = Array.FindIndex(commits, c => c.Id == checkpoint.Id) + 1;
            var planUpToCheckpoint = plan.Take(commitCount);
            planUpToCheckpoint.Last().Change.Should().Be(checkpoint.ChangeEntities.Single());
            await AddInOneBatch(fromScratch, planUpToCheckpoint);
            var expectedState = await CurrentState(fromScratch);

            checkpointState.Should().BeEquivalentTo(expectedState,
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
            // todo it would be simpler if we inserted a no-op commit
            var late = await fork.WriteChangeAfter(commits[index], fork.SetWord(Guid.NewGuid(), "written late"));

            var state = await CurrentState(fork);
            state.Remove(late.ChangeEntities[0].EntityId).Should().BeTrue();
            state.Should().BeEquivalentTo(expected, $"a commit landing after commits[{index}] only adds a word");
        }
    }

    [Fact]
    public async Task ALateCommitInsideAGapKeepsTheEditThatGapSpans()
    {
        //A is touched at 0, 2 and 4 and B at 1 and 3, so A's snapshot at 2 is dropped and the late commit lands at 3,
        //a commit that looks safe from B's snapshots alone
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var commits = new[]
        {
            await WriteNextChange(SetWord(a, "a"), add: false),
            await WriteNextChange(SetWord(b, "b"), add: false),
            await WriteNextChange(new SetWordNoteChange(a, "a note"), add: false),
            await WriteNextChange(SetWord(b, "b renamed"), add: false),
            await WriteNextChange(SetWord(a, "a renamed"), add: false),
        };
        await AddCommitsViaSync(commits);

        await WriteChangeAfter(commits[3], SetWord(Guid.NewGuid(), "written late"));

        var word = await DataModel.GetLatest<Word>(a);
        word!.Text.Should().Be("a renamed");
        word.Note.Should().Be("a note");
    }

    [Fact]
    public async Task ConsecutiveCheckpointsAreNeverMoreThanMaxChangesApart()
    {
        //one word edited every commit: every intermediate snapshot the policy doesn't keep becomes a hole, so the only
        //safe commits are the checkpoint boundaries themselves, the worst case for how far apart checkpoints can be
        var wordId = Guid.NewGuid();
        var plan = Enumerable.Range(0, 24)
            .Select(i => new PlannedChange(Day(i), i == 0 ? SetWord(wordId, "word") : new SetWordNoteChange(wordId, $"note {i}")))
            .ToArray();
        var commits = await AddInOneBatch(this, plan);
        var checkpointIndexes = await CheckpointIndexes(commits);

        checkpointIndexes.Should().Contain(commits.Length - 1, "the last commit is always safe to resume from");
        checkpointIndexes.Zip(checkpointIndexes.Skip(1), (a, b) => b - a)
            .Should().OnlyContain(gap => gap <= TestMaxChanges,
                "a late commit's resume point is never more than a checkpoint interval behind it");
    }

    [Fact]
    public async Task DiscoveryFlagsEverySafeCommitNotJustTheCheckpointBoundaries()
    {
        //every commit creates a distinct word and never touches it again, so nothing is ever dropped and every commit is safe
        var plan = Enumerable.Range(0, 24)
            .Select(i => new PlannedChange(Day(i), SetWord(Guid.NewGuid(), $"word {i}")))
            .ToArray();
        var commits = await AddInOneBatch(this, plan);

        var checkpointIndexes = await CheckpointIndexes(commits);
        checkpointIndexes.Should().Equal(Enumerable.Range(0, commits.Length),
            "with no dropped snapshots there are no holes, so a grid every eighth commit would have missed most of these");
    }

    [Fact]
    public async Task EveryLocallyAuthoredCommitIsACheckpoint()
    {
        var entityId = Guid.NewGuid();
        var first = await WriteNextChange(SetWord(entityId, "first"));
        var second = await WriteNextChange(SetWord(entityId, "second"));

        (await Checkpoints()).Should().Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task ALateCommitResumesFromTheNewestCheckpointBeforeItRatherThanRebuildingEverything()
    {
        var commits = await AddInOneBatch(this, PlanHistory(20, seed: 5));
        const int lateIndex = 9; // mid-history, so there is a checkpoint both before and after it

        var checkpointIds = (await Checkpoints()).ToHashSet();
        var resumeCheckpoint = commits.Take(lateIndex + 1).Last(c => checkpointIds.Contains(c.Id));
        var keptSnapshotIds = await DbContext.Snapshots.AsNoTracking()
            .WhereBefore(resumeCheckpoint, inclusive: true)
            .Select(s => s.Id)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        keptSnapshotIds.Should().NotBeEmpty();

        await WriteChangeAfter(commits[lateIndex], SetWord(Guid.NewGuid(), "written late"));

        (await SnapshotIds()).Should().Contain(keptSnapshotIds, "snapshots at or before the resume checkpoint are never rebuilt");
        (await Checkpoints()).Should().Contain(resumeCheckpoint.Id, "a commit before the replay window keeps its flag");
    }

    [Fact]
    public async Task AnOrdinaryAppendReplaysOnlyTheCommitsItAdds()
    {
        var commits = await AddInOneBatch(this, PlanHistory(20, seed: 7));
        var existingSnapshotIds = await SnapshotIds();

        await WriteChangeAfter(commits[^1], SetWord(Guid.NewGuid(), "appended"));

        (await SnapshotIds()).Should().Contain(existingSnapshotIds,
            "the head is a checkpoint, so an append resumes there and rebuilds nothing");
    }

    [Fact]
    public async Task ReadingAnEntityAtACommitItIsCompleteAtReturnsItsStoredSnapshot()
    {
        //wordId is set once early and edited once at the very end; a second word churns every commit in between,
        //which holes those commits so they are not checkpoints
        var wordId = Guid.NewGuid();
        var churnId = Guid.NewGuid();
        var plan = new List<PlannedChange>
        {
            new(Day(0), SetWord(wordId, "early")),
            new(Day(1), SetWord(churnId, "churn"))
        };
        for (var i = 2; i <= 12; i++) plan.Add(new PlannedChange(Day(i), new SetWordNoteChange(churnId, $"churn {i}")));
        plan.Add(new PlannedChange(Day(13), new SetWordNoteChange(wordId, "late note")));
        var commits = await AddInOneBatch(this, plan);

        var readAt = commits[6];
        (await Checkpoints()).Should().NotContain(readAt.Id, "otherwise this doesn't exercise a non-checkpoint read");

        var word = await DataModel.GetAtCommit<Word>(readAt.Id, wordId);
        word.Text.Should().Be("early");
        word.Note.Should().BeNull("the late note comes after the commit being read");
    }

    [Fact]
    public async Task ReadingStateAtAnOldCommitDoesNotSeeANeighbourItsChangeCouldNotHaveSeen()
    {
        //the neighbour is deleted at 1, that snapshot is dropped, and its next one is the revival at 5,
        //so its newest snapshot at 3 says it is still alive
        var neighbourId = Guid.NewGuid();
        var wordId = Guid.NewGuid();
        var commits = new[]
        {
            await WriteNextChange(SetWord(neighbourId, "neighbour"), add: false),
            await WriteNextChange(DeleteWord(neighbourId), add: false),
            await WriteNextChange(SetWord(wordId, "word"), add: false),
            await WriteNextChange(new SetAntonymReferenceChange(wordId, neighbourId, setObject: false), add: false),
            await WriteNextChange(SetWord(wordId, "word renamed"), add: false),
            await WriteNextChange(SetWord(neighbourId, "neighbour revived"), add: false),
        };
        await AddCommitsViaSync(commits);

        var word = await DataModel.GetAtCommit<Word>(commits[3], wordId);

        //the antonym was deleted when the reference was written, so the change skipped it
        word.AntonymId.Should().BeNull();
        word.Text.Should().Be("word");
    }

    [Fact]
    public async Task ADatabaseWithNoCheckpointsRebuildsEverySnapshotOnTheFirstLateCommit()
    {
        var commits = await AddInOneBatch(this, PlanHistory(12, seed: 6));
        var expected = await CurrentState(this);
        var staleSnapshotIds = await SnapshotIds();
        await ClearCheckpointFlags();

        var late = await WriteChangeAfter(commits[5], SetWord(Guid.NewGuid(), "written late"));

        (await SnapshotIds()).Should().NotIntersectWith(staleSnapshotIds, "with nothing to resume from, every snapshot is rebuilt");
        (await Checkpoints()).Should().NotBeEmpty("the repair is also the bootstrap");
        var state = await CurrentState(this);
        state.Remove(late.ChangeEntities[0].EntityId).Should().BeTrue();
        state.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task ADatabaseWithNoCheckpointsRebuildsEverySnapshotOnAnOrdinaryAppend()
    {
        var commits = await AddInOneBatch(this, PlanHistory(12, seed: 6));
        var expected = await CurrentState(this);
        var staleSnapshotIds = await SnapshotIds();
        await ClearCheckpointFlags();

        var appended = await WriteChangeAfter(commits[^1], SetWord(Guid.NewGuid(), "appended"));

        (await SnapshotIds()).Should().NotIntersectWith(staleSnapshotIds, "with nothing to resume from, every snapshot is rebuilt");
        (await CheckpointIndexes(commits)).Should().Contain(commits.Length - 1,
            "any write replays the unflagged history and flags it, rather than waiting for a late commit");
        var state = await CurrentState(this);
        state.Remove(appended.ChangeEntities[0].EntityId).Should().BeTrue();
        state.Should().BeEquivalentTo(expected);
    }

    private static void UseTestMaxChanges(IServiceCollection services) =>
        services.Configure<HarmonyConfig>(config => config.MaxChangesBetweenSnapshotCheckpoints = TestMaxChanges);

    private static DataModelTestBase NewModel() => new(configure: UseTestMaxChanges);

    //a day apart, so the hour a late commit is written after its predecessor always lands strictly inside the gap
    private static DateTimeOffset Day(int index) => new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(index + 1);

    private sealed record PlannedChange(DateTimeOffset Date, IChange Change);

    /// <summary>
    /// A history of creates, edits, references and cascading deletes. Randomized so the tests cover the shapes of gap
    /// that a hand written history keeps missing, seeded so a failure is reproducible.
    /// </summary>
    private static PlannedChange[] PlanHistory(int commitCount, int seed)
    {
        var random = new Random(seed);
        List<Guid> words = [];
        List<Guid> definitions = [];
        var plan = new List<PlannedChange>();
        while (plan.Count < commitCount)
        {
            plan.Add(new PlannedChange(Day(plan.Count), NextChange()));
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
            //setObject false keeps the snapshots comparable: the whole antonym would otherwise be nested in the word
            return new SetAntonymReferenceChange(wordId, others[random.Next(others.Length)], setObject: false);
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

    private static async Task<Dictionary<Guid, string>> CurrentState(DataModelTestBase model)
    {
        var snapshots = await model.DataModel.GetLatestSnapshots().ToArrayAsync(TestContext.Current.CancellationToken);
        return snapshots.ToDictionary(s => s.EntityId, s => Describe(s.Entity.DbObject));
    }

    private Task<ObjectSnapshot[]> AllSnapshots() =>
        DbContext.Snapshots.AsNoTracking()
            .Include(s => s.Commit)
            .ToArrayAsync(TestContext.Current.CancellationToken);

    /// <summary>each entity's newest stored snapshot at or before <paramref name="commit"/>, worked out without the production query</summary>
    private static Dictionary<Guid, string> StateAtOrBefore(ObjectSnapshot[] snapshots, Commit commit) =>
        snapshots
            .Where(s => s.Commit.CompareKey.CompareTo(commit.CompareKey) <= 0)
            .GroupBy(s => s.EntityId)
            .ToDictionary(g => g.Key, g => Describe(g.MaxBy(s => s.Commit.CompareKey)!.Entity.DbObject));

    //comparing json rather than the objects keeps FluentAssertions from comparing them as bare objects, which finds no members at all
    private static string Describe(object entity) => JsonSerializer.Serialize(entity, entity.GetType());

    private Task<Guid[]> SnapshotIds() =>
        DbContext.Snapshots.AsNoTracking().Select(s => s.Id).ToArrayAsync(TestContext.Current.CancellationToken);

    private Task<Commit[]> Checkpoints() =>
        DbContext.Commits.AsNoTracking()
            .Where(c => c.IsSnapshotCheckpoint)
            .DefaultOrder()
            .ToArrayAsync(TestContext.Current.CancellationToken);

    private async Task<int[]> CheckpointIndexes(Commit[] commits)
    {
        var checkpointIds = (await Checkpoints()).ToHashSet();
        return [.. commits.Index().Where(c => checkpointIds.Contains(c.Item.Id)).Select(c => c.Index)];
    }
}
