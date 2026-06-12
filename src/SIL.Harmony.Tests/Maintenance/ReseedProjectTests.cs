using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using SIL.Harmony.Maintenance;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests.Maintenance;

public class ReseedProjectTests : DataModelTestBase
{
    private readonly Guid _word1Id = Guid.NewGuid();
    private readonly Guid _word2Id = Guid.NewGuid();
    private readonly Guid _newClientId = Guid.NewGuid();

    /// <summary>
    /// Writes a small single-author chain (authored by <see cref="DataModelTestBase._localClientId"/>,
    /// the stand-in for a template-source client) with distinct timestamps, multiple entities, and
    /// snapshots/projected rows.
    /// </summary>
    private async Task SeedChain()
    {
        await WriteNextChange(SetWord(_word1Id, "apple"));
        await WriteNextChange(SetWord(_word2Id, "banana"));
        await WriteNextChange(new SetWordNoteChange(_word1Id, "a fruit"));
        await WriteNextChange(SetWord(_word1Id, "apple-updated"));
    }

    private Task<Commit[]> CurrentChain() =>
        DbContext.Commits.AsNoTracking().DefaultOrder().ToArrayAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task ReseedProject_MintsFreshCommitIds()
    {
        await SeedChain();
        var beforeIds = (await CurrentChain()).Select(c => c.Id).ToArray();

        await DataModelMaintenance.ReseedProject(DataModel, _newClientId);

        var afterIds = (await CurrentChain()).Select(c => c.Id).ToArray();
        afterIds.Should().HaveCount(beforeIds.Length);
        afterIds.Should().OnlyHaveUniqueItems();
        beforeIds.Should().NotIntersectWith(afterIds);
    }

    [Fact]
    public async Task ReseedProject_SetsClientIdOnAllCommits()
    {
        await SeedChain();

        await DataModelMaintenance.ReseedProject(DataModel, _newClientId);

        var clientIds = await DbContext.Commits.AsNoTracking().Select(c => c.ClientId).Distinct().ToArrayAsync(TestContext.Current.CancellationToken);
        clientIds.Should().ContainSingle().Which.Should().Be(_newClientId);
        _newClientId.Should().NotBe(_localClientId);
    }

    [Fact]
    public async Task ReseedProject_RecomputesHashesCorrectly()
    {
        await SeedChain();

        await DataModelMaintenance.ReseedProject(DataModel, _newClientId);

        var parentHash = CommitBase.NullParentHash;
        foreach (var commit in await CurrentChain())
        {
            commit.ParentHash.Should().Be(parentHash);
            commit.Hash.Should().Be(CommitBase.GenerateHash(commit.Id, parentHash));
            parentHash = commit.Hash;
        }
    }

    [Fact]
    public async Task ReseedProject_PreservesChangeEntities()
    {
        await SeedChain();
        var before = await DbContext.Set<ChangeEntity<IChange>>().AsNoTracking()
            .Select(c => new { c.EntityId, c.Index }).ToArrayAsync(TestContext.Current.CancellationToken);
        var beforeCommitIds = await DbContext.Set<ChangeEntity<IChange>>().AsNoTracking()
            .Select(c => c.CommitId).Distinct().ToArrayAsync(TestContext.Current.CancellationToken);

        await DataModelMaintenance.ReseedProject(DataModel, _newClientId);

        var after = await DbContext.Set<ChangeEntity<IChange>>().AsNoTracking()
            .Select(c => new { c.EntityId, c.Index }).ToArrayAsync(TestContext.Current.CancellationToken);
        var afterCommitIds = await DbContext.Set<ChangeEntity<IChange>>().AsNoTracking()
            .Select(c => c.CommitId).Distinct().ToArrayAsync(TestContext.Current.CancellationToken);

        // (EntityId, Index) is preserved exactly...
        after.Should().BeEquivalentTo(before);
        // ...while every CommitId FK was repointed onto the new commits.
        beforeCommitIds.Should().NotIntersectWith(afterCommitIds);
    }

    [Fact]
    public async Task ReseedProject_PreservesSnapshots()
    {
        await SeedChain();
        var before = await DbContext.Snapshots.AsNoTracking()
            .Select(s => new { s.Id, s.EntityId, s.EntityIsDeleted, s.TypeName }).ToArrayAsync(TestContext.Current.CancellationToken);

        await DataModelMaintenance.ReseedProject(DataModel, _newClientId);

        var after = await DbContext.Snapshots.AsNoTracking()
            .Select(s => new { s.Id, s.EntityId, s.EntityIsDeleted, s.TypeName }).ToArrayAsync(TestContext.Current.CancellationToken);
        // Snapshots.Id (and the rest of the row) is preserved verbatim — only CommitId changes.
        after.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task ReseedProject_PreservesProjectedTables()
    {
        await SeedChain();
        var before = await DbContext.Set<Word>().AsNoTracking()
            .OrderBy(w => w.Id).Select(w => new { w.Id, w.Text, w.Note }).ToArrayAsync(TestContext.Current.CancellationToken);

        await DataModelMaintenance.ReseedProject(DataModel, _newClientId);

        var after = await DbContext.Set<Word>().AsNoTracking()
            .OrderBy(w => w.Id).Select(w => new { w.Id, w.Text, w.Note }).ToArrayAsync(TestContext.Current.CancellationToken);
        after.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task ReseedProject_PreservesChainOrder()
    {
        await SeedChain();
        var before = (await CurrentChain())
            .Select(c => (c.HybridDateTime.DateTime, c.HybridDateTime.Counter)).ToArray();

        await DataModelMaintenance.ReseedProject(DataModel, _newClientId);

        // CurrentChain() orders by (DateTime, Counter, NEW Id); the sequence must be unchanged.
        var after = (await CurrentChain())
            .Select(c => (c.HybridDateTime.DateTime, c.HybridDateTime.Counter)).ToArray();
        after.Should().Equal(before);
    }

    [Fact]
    public async Task ReseedProject_HashChainValidatesAfterReseed()
    {
        await SeedChain();

        await DataModelMaintenance.ReseedProject(DataModel, _newClientId);

        // Adding another commit runs ValidateCommits (AlwaysValidateCommits defaults to true in the
        // fixture), which walks the whole chain and throws on any hash mismatch.
        var act = async () => await WriteNextChange(SetWord(Guid.NewGuid(), "post-reseed"));
        await act.Should().NotThrowAsync();

        // Content survived the reseed.
        (await DataModel.GetLatest<Word>(_word1Id))!.Text.Should().Be("apple-updated");
        (await DataModel.GetLatest<Word>(_word2Id))!.Text.Should().Be("banana");
    }

    [Fact]
    public async Task ReseedProject_ThrowsOnMultiAuthorChain()
    {
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        await WriteChange(clientA, NextDate(), SetWord(Guid.NewGuid(), "a"));
        await WriteChange(clientB, NextDate(), SetWord(Guid.NewGuid(), "b"));

        var act = async () => await DataModelMaintenance.ReseedProject(DataModel, _newClientId);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*single-author*");
    }

    [Fact]
    public async Task ReseedProject_ThrowsOnEmptyChain()
    {
        var act = async () => await DataModelMaintenance.ReseedProject(DataModel, _newClientId);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*non-empty*");
    }

    [Fact]
    public async Task ReseedProject_ThrowsOnDuplicateHybridDateTime()
    {
        // Two commits at the same instant: the mock clock sets Counter=0 for both, so they share an
        // identical (DateTime, Counter). Re-minting random Ids would reorder them, so reseed must refuse.
        var sharedDate = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteChange(_localClientId, sharedDate, SetWord(Guid.NewGuid(), "x"));
        await WriteChange(_localClientId, sharedDate, SetWord(Guid.NewGuid(), "y"));

        var act = async () => await DataModelMaintenance.ReseedProject(DataModel, _newClientId);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unique (DateTime, Counter)*");
    }

    [Fact]
    public async Task ReseedProject_LeavesChainUntouchedWhenAPreconditionFails()
    {
        // A failed precondition must not mutate the chain (atomicity for the cheap, pre-write guards).
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        await WriteChange(clientA, NextDate(), SetWord(_word1Id, "a"));
        await WriteChange(clientB, NextDate(), SetWord(_word2Id, "b"));
        var before = (await CurrentChain()).Select(c => (c.Id, c.ClientId, c.Hash, c.ParentHash)).ToArray();

        var act = async () => await DataModelMaintenance.ReseedProject(DataModel, _newClientId);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var after = (await CurrentChain()).Select(c => (c.Id, c.ClientId, c.Hash, c.ParentHash)).ToArray();
        after.Should().Equal(before);
    }
}
