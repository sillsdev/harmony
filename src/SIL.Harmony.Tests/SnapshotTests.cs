using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class SnapshotTests : DataModelTestBase
{
    [Fact]
    public async Task FirstChangeResultsInRootSnapshot()
    {
        await WriteNextChange(SetWord(Guid.NewGuid(), "test"));
        var snapshot = DbContext.Snapshots.Should().ContainSingle().Subject;
        snapshot.IsRoot.Should().BeTrue();
        snapshot.Entity.Should().NotBeNull();
    }

    [Fact]
    public async Task MultipleChangesPreservesRootSnapshot()
    {
        var entityId = Guid.NewGuid();
        var commits = new List<Commit>();
        for (var i = 0; i < 4; i++)
        {
            commits.Add(CommitAt(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(i),
                SetWord(entityId, $"test {i}")));
        }

        await AddCommitsViaSync(commits);

        var snapshots = await DbContext.Snapshots.ToArrayAsync(TestContext.Current.CancellationToken);
        snapshots.Should().HaveCountGreaterThan(1);
        snapshots.Should().ContainSingle(s => s.IsRoot);
    }

    [Fact]
    public async Task MultipleChangesPreservesSomeIntermediateSnapshots()
    {
        //a low max so a short batch still crosses several checkpoint boundaries and keeps intermediates
        await using var model = new DataModelTestBase(configure: services =>
            services.Configure<HarmonyConfig>(config => config.MaxChangesBetweenSnapshotCheckpoints = 4));
        var entityId = Guid.NewGuid();
        var commits = new List<Commit>();
        for (var i = 0; i < 20; i++)
        {
            commits.Add(model.NextCommit(model.SetWord(entityId, $"test {i}"), model.SetWord(entityId, $"test {i} again")));
        }

        await model.AddCommitsViaSync(commits);

        var latestSnapshot = await model.DataModel.GetLatestSnapshotByObjectId(entityId);
        var snapshots = await model.DbContext.Snapshots.ToArrayAsync(TestContext.Current.CancellationToken);
        snapshots.Should().HaveCountGreaterThan(2);
        snapshots.Should().ContainSingle(s => s.Id == latestSnapshot.Id);
        snapshots.Should().ContainSingle(s => s.IsRoot);
        var intermediateSnapshot = snapshots.FirstOrDefault(s => !s.IsRoot && s.Id != latestSnapshot.Id);
        ArgumentNullException.ThrowIfNull(intermediateSnapshot);
        intermediateSnapshot.IsRoot.Should().BeFalse();
        intermediateSnapshot.Id.Should().NotBe(latestSnapshot.Id);
        intermediateSnapshot.Entity.Should().BeOfType<Word>().Which.Text.Should().Contain("again");
    }

    [Fact]
    public async Task OnlySaveTheLastSnapshotWhenThereAreMultipleChangesToAnEntityInOneCommit()
    {
        var entityId = Guid.NewGuid();
        await WriteNextChange(
            [
                SetWord(entityId, "change1"),
                SetWord(entityId, "change2"),
                SetWord(entityId, "change3"),
            ]);
        DbContext.Snapshots.Should().ContainSingle();
    }

    [Fact]
    public async Task DontAddTheSameSnapshotTwice()
    {
        var entityId = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId, "test root"));
        await WriteNextChange(SetWord(entityId, "test non root"));

        await AddCommitsViaSync([
            //the order here is important, the second commit was causing the snapshot for 'test non root' to attempt to be inserted again
            NextCommit(SetWord(Guid.NewGuid(), "test 1")),
            NextCommit(SetWord(entityId, "test 2")),
        ]);

        (await DataModel.GetLatest<Word>(entityId))!.Text.Should().Be("test 2");
        DbContext.Set<Word>().Should().HaveCount(2);
    }

    [Fact]
    public async Task CanRecreateUniqueConstraintConflictingValueInOneCommit()
    {
        var entityId = Guid.NewGuid();
        var recreatedTagId = Guid.NewGuid();
        await WriteNextChange(SetTag(entityId, "tag-1"));
        await WriteNextChange(
            [
                DeleteTag(entityId),
                SetTag(recreatedTagId, "tag-1"),
            ]);

        (await DataModel.GetLatest<Tag>(entityId))!.DeletedAt.Should().NotBeNull("the original tag was deleted");
        var recreatedTag = await DataModel.GetLatest<Tag>(recreatedTagId);
        recreatedTag!.Text.Should().Be("tag-1");
        recreatedTag.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task DuplicatePreventionHandlesDuplicatesInSingleCommit()
    {
        var wordId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        await WriteNextChange(
            [
                SetWord(wordId, "test root"),
                SetTag(tagId, "tag-1"),
            ]);
        await WriteNextChange(
            [
                TagWord(wordId, tagId),
                TagWord(wordId, tagId),
            ]);

        var word = await DataModel.QueryLatest<Word>(q => q.Include(w => w.Tags)
            .Where(w => w.Id == wordId)).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        word.Should().NotBeNull();
        word.Tags.Should().BeEquivalentTo([new Tag { Id = tagId, Text = "tag-1" }]);
    }

    [Fact]
    public async Task DuplicatePreventionHandlesADuplicateBeingCreatedBeforeTheCurrentVersion()
    {
        var wordId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        await WriteNextChange(
            [
                SetWord(wordId, "test root"),
                SetTag(tagId, "tag-1"),
            ]);
        var tagCreation = await WriteNextChange(TagWord(wordId, tagId));
        await WriteChangeBefore(tagCreation, TagWord(wordId, tagId));

        var word = await DataModel.QueryLatest<Word>(q => q.Include(w => w.Tags)
            .Where(w => w.Id == wordId)).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        word.Should().NotBeNull();
        word.Tags.Should().BeEquivalentTo([new Tag { Id = tagId, Text = "tag-1" }]);
    }

    //An entity id belongs to one type. A commit authored here is replayed strictly, so reusing an id for another type
    //throws and the transaction rolls back; a commit that is already in shared history is replayed leniently.

    [Fact]
    public async Task AuthoringACreateForAnIdThatBelongsToAnotherTypeThrowsAndPersistsNothing()
    {
        //ported from an app test: two creates in one commit sharing an id
        var shared = Guid.NewGuid();
        var act = async () => await WriteNextChange(
            [
                new NewWordChange(shared, "in"),
                NewDefinition(shared, "in", "preposition", definitionId: shared),
            ]);

        await act.Should().ThrowAsync<CommitValidationException>().WithMessage($"*{shared}*Word*NewDefinitionChange*Definition*");
        (await DbContext.Commits.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await DbContext.Snapshots.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task AuthoringAChangeForAnIdThatBelongsToAnotherTypeThrowsAcrossCommits()
    {
        var shared = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(shared, "in"));

        var create = async () => await WriteNextChange(NewDefinition(shared, "in", "preposition", definitionId: shared));
        await create.Should().ThrowAsync<CommitValidationException>();
        //an edit on the wrong type used to surface as NotSupportedException from inside replay
        var edit = async () => await WriteNextChange(new SetDefinitionPartOfSpeechChange(shared, "noun"));
        await edit.Should().ThrowAsync<CommitValidationException>();

        (await DbContext.Commits.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        var word = await DataModel.GetLatest<Word>(shared);
        word.Should().NotBeNull();
        word.Text.Should().Be("in");
    }

    [Fact]
    public async Task AuthoringACreateForAnIdWhoseEntityOfAnotherTypeWasDeletedThrows()
    {
        //snapshots chain by entity id, so a Definition on a deleted Word's id would continue the Word's chain
        var shared = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(shared, "in"));
        await WriteNextChange(new DeleteChange<Word>(shared));

        var act = async () => await WriteNextChange(NewDefinition(shared, "in", "preposition", definitionId: shared));

        await act.Should().ThrowAsync<CommitValidationException>();
        (await DbContext.Commits.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task AuthoringManyChangesWithAnIdReusedAcrossTypesThrowsAndPersistsNothing()
    {
        //AddManyChanges picks its own replay policy, so it is covered apart from AddChanges
        var shared = Guid.NewGuid();
        var act = async () => await DataModel.AddManyChanges(_localClientId,
            [new NewWordChange(shared, "in"), NewDefinition(shared, "in", "preposition", definitionId: shared)],
            () => null);

        await act.Should().ThrowAsync<CommitValidationException>();
        (await DbContext.Commits.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await DbContext.Snapshots.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task AuthoringADuplicateCreateOfTheSameTypeIsStillAccepted()
    {
        var shared = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(shared, "in"));
        await WriteNextChange(new NewWordChange(shared, "on"));

        var word = await DataModel.GetLatest<Word>(shared);
        word.Should().NotBeNull();
        word.Text.Should().Be("in");
    }

    [Fact]
    public async Task ASyncedCreateForAnIdThatBelongsToAnotherTypeIsSkippedNotThrown()
    {
        //the commit is already in another client's history, so replay must stay deterministic and must not fail
        var shared = Guid.NewGuid();
        var commit = NextCommit(
            new NewWordChange(shared, "in"),
            NewDefinition(shared, "in", "preposition", definitionId: shared));

        await AddCommitsViaSync([commit]);

        var word = await DataModel.GetLatest<Word>(shared);
        word.Should().NotBeNull();
        (await DataModel.QueryLatest<Definition>().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        //a later local commit replays the window containing the synced one, which must stay lenient
        await WriteNextChange(SetWord(shared, "on"));
        (await DataModel.GetLatest<Word>(shared))!.Text.Should().Be("on");
        //and a rebuild from history agrees
        var before = await DataModel.QueryLatest<Word>().ToArrayAsync(TestContext.Current.CancellationToken);
        await DataModel.RegenerateSnapshots();
        var after = await DataModel.QueryLatest<Word>().ToArrayAsync(TestContext.Current.CancellationToken);
        after.Should().BeEquivalentTo(before);
        (await DataModel.QueryLatest<Definition>().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task ASyncedCreateForADeletedIdOfAnotherTypeIsSkippedNotRevived()
    {
        //a deleted Word's id must not come back as a Tag: later changes referencing the Word would find a Tag,
        //and history would have one id pointing at two types
        var shared = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(shared, "in"));
        await WriteNextChange(new DeleteChange<Word>(shared));

        await AddCommitsViaSync([NextCommit(SetTag(shared, "tag-1"))]);

        var snapshot = await DataModel.GetLatestSnapshotByObjectId(shared);
        snapshot.Should().NotBeNull();
        snapshot.Entity.DbObject.Should().BeOfType<Word>();
        snapshot.EntityIsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task ASyncedEditForALiveIdOfAnotherTypeIsSkippedNotThrown()
    {
        //an edit on the wrong type used to throw NotSupportedException from inside replay, which would fail every sync
        var shared = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(shared, "in"));

        await AddCommitsViaSync([NextCommit(new SetDefinitionPartOfSpeechChange(shared, "noun"))]);

        var word = await DataModel.GetLatest<Word>(shared);
        word.Should().NotBeNull();
        word.Text.Should().Be("in");
        (await DataModel.QueryLatest<Definition>().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task RegenerateSnapshots_WillArriveAtTheSameState()
    {
        var entityId = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId, "test root"));
        await WriteNextChange(SetWord(entityId, "test1"));
        await WriteNextChange(SetWord(entityId, "test2"));
        await WriteNextChange(SetWord(entityId, "test3"));
        var beforeSnapshotIds = await DbContext.Snapshots.Select(s => s.Id).ToArrayAsync(TestContext.Current.CancellationToken);
        var beforeRegenerate = await DataModel.QueryLatest<Word>().ToArrayAsync(TestContext.Current.CancellationToken);

        await DataModel.RegenerateSnapshots();

        var afterRegenerate = await DataModel.QueryLatest<Word>().ToArrayAsync(TestContext.Current.CancellationToken);
        var afterSnapshotsIds = await DbContext.Snapshots.Select(s => s.Id).ToArrayAsync(TestContext.Current.CancellationToken);
        afterRegenerate.Should().BeEquivalentTo(beforeRegenerate);

        //we probably won't have the same number of snapshots, which is ok. but none of the ids should be the same
        afterSnapshotsIds.Should().NotIntersectWith(beforeSnapshotIds);
    }

    [Fact]
    public async Task RegenerateSnapshots_LeavesTheProjectIntactWhenTheRebuildFails()
    {
        var entityId = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId, "test root"));
        await WriteNextChange(SetWord(entityId, "test1"));
        var beforeSnapshotIds = await DbContext.Snapshots.Select(s => s.Id).ToArrayAsync(TestContext.Current.CancellationToken);
        var beforeRegenerate = await DataModel.QueryLatest<Word>().ToArrayAsync(TestContext.Current.CancellationToken);

        _services.GetRequiredService<IOptions<HarmonyConfig>>().Value.BeforeSaveObject =
            (_, _) => throw new InvalidOperationException("rebuild failed");
        Func<Task> act = () => DataModel.RegenerateSnapshots();
        await act.Should().ThrowAsync<InvalidOperationException>();

        var afterSnapshotIds = await DbContext.Snapshots.Select(s => s.Id).ToArrayAsync(TestContext.Current.CancellationToken);
        afterSnapshotIds.Should().BeEquivalentTo(beforeSnapshotIds);
        var afterRegenerate = await DataModel.QueryLatest<Word>().ToArrayAsync(TestContext.Current.CancellationToken);
        afterRegenerate.Should().BeEquivalentTo(beforeRegenerate);
    }
}
