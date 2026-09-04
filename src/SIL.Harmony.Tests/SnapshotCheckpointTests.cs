using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Changes;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

/// <summary>
/// Checkpoints are the commits a replay may resume from: every entity's newest snapshot at or before one holds that
/// entity's state there. These tests check that property rather than any single scenario, because the ways it can break
/// all look local and harmless (see docs/snapshot-checkpoints.md).
/// </summary>
public class SnapshotCheckpointTests : DataModelTestBase
{
    private sealed record PlannedChange(DateTimeOffset Date, IChange Change);

    /// <summary>
    /// A history of creates, edits, references and cascading deletes. Randomized so the tests cover the shapes of gap
    /// that a hand written history keeps missing, seeded so a failure is reproducible.
    /// </summary>
    private static PlannedChange[] PlanHistory(int commitCount, int seed)
    {
        var random = new Random(seed);
        var date = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        List<Guid> words = [];
        List<Guid> definitions = [];
        var plan = new List<PlannedChange>();
        while (plan.Count < commitCount)
        {
            date = date.AddDays(1);
            plan.Add(new PlannedChange(date, NextChange()));
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

    private async Task<Commit[]> AddInOneBatch(DataModelTestBase model, IEnumerable<PlannedChange> plan)
    {
        var commits = new List<Commit>();
        foreach (var planned in plan)
        {
            commits.Add(await model.WriteChange(_localClientId, planned.Date, planned.Change, add: false));
        }

        await model.AddCommitsViaSync(commits);
        return [.. commits];
    }

    private static async Task<Dictionary<Guid, string>> CurrentState(DataModelTestBase model)
    {
        var snapshots = await model.DataModel.GetLatestSnapshots().ToArrayAsync(TestContext.Current.CancellationToken);
        return snapshots.ToDictionary(s => s.EntityId, s => Describe(s.Entity.DbObject));
    }

    private async Task<Dictionary<Guid, string>> StateFromSnapshotsAtOrBefore(Commit commit)
    {
        var snapshots = await DbContext.Snapshots.AsNoTracking()
            .Include(s => s.Commit)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        return snapshots
            .Where(s => s.Commit.CompareKey.CompareTo(commit.CompareKey) <= 0)
            .GroupBy(s => s.EntityId)
            .ToDictionary(g => g.Key, g => Describe(g.MaxBy(s => s.Commit.CompareKey)!.Entity.DbObject));
    }

    //comparing json rather than the objects keeps FluentAssertions from comparing them as bare objects, which finds no members at all
    private static string Describe(object entity) => JsonSerializer.Serialize(entity, entity.GetType());

    private async Task<Guid[]> CheckpointIds()
    {
        return await DbContext.Commits.AsNoTracking()
            .Where(c => c.IsSnapshotCheckpoint)
            .DefaultOrder()
            .Select(c => c.Id)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryCheckpointHoldsTheStateAReplayWouldResumeFrom(int seed)
    {
        var plan = PlanHistory(24, seed);
        var commits = await AddInOneBatch(this, plan);
        var checkpoints = await CheckpointIds();
        checkpoints.Should().HaveCountGreaterThan(1, "otherwise this only checks the end of the batch");

        foreach (var checkpointId in checkpoints)
        {
            var commitCount = Array.FindIndex(commits, c => c.Id == checkpointId) + 1;
            //a history added from empty in one batch is complete at its last commit by construction, so it can say what the checkpoint should hold
            await using var fromScratch = new DataModelTestBase();
            await AddInOneBatch(fromScratch, plan.Take(commitCount));

            var atCheckpoint = await StateFromSnapshotsAtOrBefore(commits[commitCount - 1]);
            atCheckpoint.Should().BeEquivalentTo(await CurrentState(fromScratch),
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

        for (var position = 0; position < commits.Length; position++)
        {
            await using var fork = ForkDatabase();
            var late = await fork.WriteChangeAfter(commits[position], fork.SetWord(Guid.NewGuid(), "written late"));

            var state = await CurrentState(fork);
            state.Remove(late.ChangeEntities[0].EntityId).Should().BeTrue();
            state.Should().BeEquivalentTo(expected, $"a commit landing after commit {position + 1} only adds a word");
        }
    }

    [Fact]
    public async Task ALateCommitInsideAGapKeepsTheEditThatGapSpans()
    {
        //the history that broke the first attempt at this: A is touched at 1, 3 and 5 and B at 2 and 4, so A's snapshot
        //at 3 is dropped and the late commit lands at 4, a position that looks safe from B's snapshots alone
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
    public async Task ASyncedBatchGetsACheckpointEveryEighthCommitAndOneAtItsEnd()
    {
        var commits = await AddInOneBatch(this, PlanHistory(20, seed: 4));

        //pinned on purpose: one checkpoint per batch is also correct, but it rolls a fresh clone back to the start of history
        (await CheckpointIds()).Should().Equal(commits[7].Id, commits[15].Id, commits[19].Id);
    }

    [Fact]
    public async Task EveryLocallyAuthoredCommitIsACheckpoint()
    {
        var entityId = Guid.NewGuid();
        var first = await WriteNextChange(SetWord(entityId, "first"));
        var second = await WriteNextChange(SetWord(entityId, "second"));

        (await CheckpointIds()).Should().Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task ALateCommitResumesFromTheNewestCheckpointBeforeItRatherThanRebuildingEverything()
    {
        var commits = await AddInOneBatch(this, PlanHistory(20, seed: 5));
        var checkpoint = commits[7];
        var untouchedSnapshotIds = await DbContext.Snapshots.AsNoTracking()
            .Include(s => s.Commit)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        var keptSnapshotIds = untouchedSnapshotIds
            .Where(s => s.Commit.CompareKey.CompareTo(checkpoint.CompareKey) <= 0)
            .Select(s => s.Id)
            .ToArray();
        keptSnapshotIds.Should().NotBeEmpty();

        await WriteChangeAfter(commits[9], SetWord(Guid.NewGuid(), "written late"));

        var snapshotIds = await DbContext.Snapshots.AsNoTracking().Select(s => s.Id).ToArrayAsync(TestContext.Current.CancellationToken);
        snapshotIds.Should().Contain(keptSnapshotIds);
        (await CheckpointIds()).Should().Contain(checkpoint.Id, "a commit before the replay window keeps its flag");
    }

    [Fact]
    public async Task ADatabaseWithNoCheckpointsRebuildsEverySnapshotOnTheFirstLateCommit()
    {
        var plan = PlanHistory(12, seed: 6);
        var commits = await AddInOneBatch(this, plan);
        var expected = await CurrentState(this);
        //what a database written before checkpoints existed looks like
        await DbContext.Commits.ExecuteUpdateAsync(s => s.SetProperty(c => c.IsSnapshotCheckpoint, false), TestContext.Current.CancellationToken);
        DbContext.ChangeTracker.Clear();

        var late = await WriteChangeAfter(commits[5], SetWord(Guid.NewGuid(), "written late"));

        (await CheckpointIds()).Should().NotBeEmpty("the repair is also the bootstrap");
        var state = await CurrentState(this);
        state.Remove(late.ChangeEntities[0].EntityId).Should().BeTrue();
        state.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task ReadingStateAtAnOldCommitDoesNotSeeANeighbourItsChangeCouldNotHaveSeen()
    {
        //the neighbour is deleted at commit 2, its snapshot there is dropped at commit 6, and its next snapshot is the
        //revival at 6, so its newest snapshot at commit 4 says it is still alive
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
}
