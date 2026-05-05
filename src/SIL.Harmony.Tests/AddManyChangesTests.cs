using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Changes;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

/// <summary>
/// Tests for <see cref="DataModel.AddManyChanges"/>. Prior to these tests the public batch API
/// was not exercised by the harmony test suite — in particular, the snapshot-preload path
/// in <see cref="SnapshotWorker"/> (gated on total change count) was un-tested outside of
/// the benchmark. These tests cover:
///
/// <list type="bullet">
///   <item>Basic batch correctness — all changes in the batch are applied and produce snapshots.</item>
///   <item>Non-preload path (batch at or below gate of 50 changes) — FindSnapshot fallback.</item>
///   <item>Preload path (batch above gate) — in-memory dict lookup in GetSnapshot.</item>
///   <item>Delete cascades under preload — GetSnapshotsReferencing filters the pre-loaded cache,
///     NOT the per-call CurrentSnapshots CTE.</item>
///   <item>Batched result equals incremental result — batching is just a perf optimization,
///     never a semantic difference.</item>
/// </list>
/// </summary>
public class AddManyChangesTests : DataModelTestBase
{
    private IEnumerable<IChange> MakeWords(int count, string prefix = "word")
    {
        for (var i = 0; i < count; i++)
        {
            yield return SetWord(Guid.NewGuid(), $"{prefix}-{i}");
        }
    }

    [Fact]
    public async Task AppliesAllChangesInBatch()
    {
        var changes = MakeWords(10).ToList();
        await DataModel.AddManyChanges(_localClientId, changes, () => null);

        DbContext.Snapshots.Should().HaveCount(10);
        DataModel.QueryLatest<Word>().ToBlockingEnumerable().Should().HaveCount(10);
    }

    [Fact]
    public async Task SmallBatch_AtGateBoundary_AppliesCorrectly_NonPreloadPath()
    {
        // 50 changes: exactly at the preload gate. Gate is `> 50` so preload does NOT fire;
        // the non-preload FindSnapshot fallback path is used instead. Must produce correct
        // state regardless of which path runs.
        var changes = MakeWords(50).ToList();
        await DataModel.AddManyChanges(_localClientId, changes, () => null);

        DbContext.Snapshots.Should().HaveCount(50);
        DataModel.QueryLatest<Word>().ToBlockingEnumerable().Should().HaveCount(50);
    }

    [Fact]
    public async Task LargeBatch_JustAboveGate_AppliesCorrectly_PreloadPath()
    {
        // 51 changes: one past the gate → preload fires. All snapshots are loaded
        // into memory up-front and GetSnapshot serves from the dict cache.
        var changes = MakeWords(51).ToList();
        await DataModel.AddManyChanges(_localClientId, changes, () => null);

        DbContext.Snapshots.Should().HaveCount(51);
        DataModel.QueryLatest<Word>().ToBlockingEnumerable().Should().HaveCount(51);
    }

    [Fact]
    public async Task VeryLargeBatch_AppliesCorrectly_PreloadPath()
    {
        // 500 changes spanning multiple commits. Exercises the preload path including
        // its interaction with per-commit chunking (changesPerCommitMax = 100 by default).
        var changes = MakeWords(500).ToList();
        await DataModel.AddManyChanges(_localClientId, changes, () => null);

        DbContext.Snapshots.Should().HaveCount(500);
        DataModel.QueryLatest<Word>().ToBlockingEnumerable().Should().HaveCount(500);
        DbContext.Commits.Should().HaveCount(5, "500 changes / 100 per commit = 5 commits");
    }

    [Fact]
    public async Task DeletesCascadeCorrectlyViaPreloadPath()
    {
        // Pre-populate 20 words that reference a shared antonym.
        var antonymId = Guid.NewGuid();
        var wordIds = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();

        var setupChanges = new List<IChange> { SetWord(antonymId, "antonym") };
        foreach (var (id, i) in wordIds.Select((id, i) => (id, i)))
        {
            setupChanges.Add(SetWord(id, $"word-{i}"));
            setupChanges.Add(new SetAntonymReferenceChange(id, antonymId));
        }
        // 1 + 20 + 20 = 41 changes — below gate, preload does not fire during setup.
        // That's fine: setup correctness is checked independently of the delete step.
        await DataModel.AddManyChanges(_localClientId, setupChanges, () => null);

        // Sanity: antonym reference is set on every word before delete.
        foreach (var id in wordIds)
        {
            var word = await DataModel.GetLatest<Word>(id);
            word!.AntonymId.Should().Be(antonymId, "antonym reference should be set pre-delete");
        }

        // Now delete the antonym + re-set text on many words in one large batch. 52+ changes
        // puts us past the gate → preload fires. DeleteChange → MarkDeleted cascades must
        // see referencing snapshots through the pre-loaded cache (the critical path).
        var cascadeChanges = new List<IChange> { new DeleteChange<Word>(antonymId) };
        foreach (var (id, i) in wordIds.Select((id, i) => (id, i)))
        {
            cascadeChanges.Add(SetWord(id, $"word-{i}-updated"));
            cascadeChanges.Add(SetWord(id, $"word-{i}-final"));
            cascadeChanges.Add(SetWord(id, $"word-{i}-done"));
        }
        // 1 + 60 = 61 changes → preload fires.
        await DataModel.AddManyChanges(_localClientId, cascadeChanges, () => null);

        // Every word's antonym reference should have been removed by the cascade.
        foreach (var id in wordIds)
        {
            var word = await DataModel.GetLatest<Word>(id);
            word.Should().NotBeNull();
            word!.AntonymId.Should().BeNull("MarkDeleted cascade should clear antonym refs when antonym is deleted");
            word.Text.Should().EndWith("-done", "later changes in the same batch should win");
        }

        // Antonym itself should be soft-deleted.
        var deletedAntonym = await DataModel.GetLatest<Word>(antonymId);
        deletedAntonym.Should().NotBeNull();
        deletedAntonym!.DeletedAt.Should().NotBeNull("antonym should be soft-deleted (DeletedAt set)");
    }

    [Fact]
    public async Task BatchedResult_MatchesIncrementalResult()
    {
        // Batching must be a pure perf optimization — final state must be identical to
        // applying each change in its own commit. Run both and compare projected Word rows.
        var wordSpecs = Enumerable.Range(0, 100)
            .Select(i => (Id: Guid.NewGuid(), Text: $"w-{i}"))
            .ToList();

        // Incremental model: one change per commit.
        await using var incremental = new DataModelTestBase();
        foreach (var (id, text) in wordSpecs)
        {
            await incremental.WriteNextChange(incremental.SetWord(id, text));
        }
        var incrementalWords = incremental.DbContext.Set<Word>()
            .OrderBy(w => w.Text).Select(w => new { w.Id, w.Text }).ToList();

        // Batched model (this instance): one AddManyChanges with all 100 → preload path.
        var changes = wordSpecs.Select(spec => (IChange)SetWord(spec.Id, spec.Text)).ToList();
        await DataModel.AddManyChanges(_localClientId, changes, () => null);
        var batchedWords = DbContext.Set<Word>()
            .OrderBy(w => w.Text).Select(w => new { w.Id, w.Text }).ToList();

        batchedWords.Should().BeEquivalentTo(incrementalWords,
            "batched AddManyChanges must produce the same final entity state as per-change commits");
    }

    [Fact]
    public async Task ZeroChanges_IsANoOp()
    {
        await DataModel.AddManyChanges(_localClientId, [], () => null);
        DbContext.Commits.Should().BeEmpty();
        DbContext.Snapshots.Should().BeEmpty();
    }

    [Fact]
    public async Task SingleChange_UsesNonPreloadPath()
    {
        // 1 change — well below the 50 gate. Must apply correctly via the non-preload
        // (FindSnapshot) path. This is the UI-edit scenario we explicitly protect from
        // preload overhead.
        var entityId = Guid.NewGuid();
        await DataModel.AddManyChanges(_localClientId, [SetWord(entityId, "single")], () => null);

        var word = await DataModel.GetLatest<Word>(entityId);
        word!.Text.Should().Be("single");
    }
}
