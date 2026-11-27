using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests.Db;

public class SnapshotViewTests : DataModelTestBase
{
    [Fact]
    public async Task WhereReferences_SkipsAnEntityWhoseOlderSnapshotHadTheReference()
    {
        var oldAntonym = Guid.NewGuid();
        var newAntonym = Guid.NewGuid();
        var wordId = Guid.NewGuid();
        await WriteNextChange(SetWord(oldAntonym, "hot"));
        await WriteNextChange(SetWord(newAntonym, "warm"));
        await WriteNextChange(SetWord(wordId, "cold"));
        await WriteNextChange(new SetAntonymReferenceChange(wordId, oldAntonym));
        await WriteNextChange(new SetAntonymReferenceChange(wordId, newAntonym));
        (await DbContext.Snapshots.AnyAsync(s => s.EntityId == wordId && s.References.Contains(oldAntonym),
            TestContext.Current.CancellationToken)).Should().BeTrue("the older snapshot is what makes this test meaningful");

        await using var repo = CreateRepository();
        var view = repo.CurrentSnapshotView();

        (await view.WhereReferences(oldAntonym, includeDeleted: true).ToArrayAsync(TestContext.Current.CancellationToken))
            .Should().BeEmpty();
        (await view.WhereReferences(newAntonym, includeDeleted: true).ToArrayAsync(TestContext.Current.CancellationToken))
            .Select(s => s.EntityId).Should().Equal(wordId);
    }

    [Fact]
    public async Task WhereReferences_SkipsDeletedUnlessIncluded()
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "word"));
        await WriteNextChange(NewDefinition(wordId, "definition", "noun", definitionId: definitionId));
        await WriteNextChange(new DeleteChange<Definition>(definitionId));

        await using var repo = CreateRepository();
        var view = repo.CurrentSnapshotView();

        (await view.WhereReferences(wordId, includeDeleted: false).ToArrayAsync(TestContext.Current.CancellationToken))
            .Should().BeEmpty();
        (await view.WhereReferences(wordId, includeDeleted: true).ToArrayAsync(TestContext.Current.CancellationToken))
            .Select(s => s.EntityId).Should().Equal(definitionId);
    }

    [Fact]
    public async Task WhereTypeName_ReturnsOnlyThatType()
    {
        var tagId = Guid.NewGuid();
        var deletedTagId = Guid.NewGuid();
        await WriteNextChange(SetWord(Guid.NewGuid(), "word"));
        await WriteNextChange(SetTag(tagId, "tag"));
        await WriteNextChange(SetTag(deletedTagId, "deleted tag"));
        await WriteNextChange(DeleteTag(deletedTagId));

        await using var repo = CreateRepository();
        var view = repo.CurrentSnapshotView();

        (await view.WhereTypeName("Tag", includeDeleted: false).ToArrayAsync(TestContext.Current.CancellationToken))
            .Select(s => s.EntityId).Should().Equal(tagId);
        (await view.WhereTypeName("Tag", includeDeleted: true).ToArrayAsync(TestContext.Current.CancellationToken))
            .Select(s => s.EntityId).Should().BeEquivalentTo([tagId, deletedTagId]);
    }

    [Fact]
    public async Task PreloadAsync_FindsTheNewestSnapshotOfEachEntity()
    {
        var wordId = Guid.NewGuid();
        var otherWordId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "first"));
        await WriteNextChange(SetWord(wordId, "second"));
        await WriteNextChange(SetWord(otherWordId, "other"));

        await using var repo = CreateRepository();
        var view = repo.CurrentSnapshotView();
        await view.PreloadAsync([wordId, otherWordId, missingId]);

        //preloaded ids are cache hits, so a missed id would read as an entity that doesn't exist
        ((Word)(await view.GetAsync(wordId))!.Entity.DbObject).Text.Should().Be("second");
        (await view.GetAsync(otherWordId)).Should().NotBeNull();
        (await view.GetAsync(missingId)).Should().BeNull();
    }
}
