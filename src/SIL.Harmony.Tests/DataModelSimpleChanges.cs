using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;
using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony.Tests;

public class DataModelSimpleChanges : DataModelTestBase
{
    private readonly Guid _entity1Id = Guid.NewGuid();
    private readonly Guid _entity2Id = Guid.NewGuid();

    [Fact]
    public async Task WritingAChangeMakesASnapshot()
    {
        await WriteNextChange(SetWord(_entity1Id, "test-value"));
        var snapshot = DbContext.Snapshots.Should().ContainSingle().Subject;
        snapshot.Entity.Is<Word>().Text.Should().Be("test-value");

        await Verify(AllData());
    }

    [Fact]
    public async Task CanUpdateTheNoteField()
    {
        await WriteNextChange(SetWord(_entity1Id, "test-value"));
        await WriteNextChange(new SetWordNoteChange(_entity1Id, "a word note"));
        var word = await DataModel.GetLatest<Word>(_entity1Id);
        word!.Text.Should().Be("test-value");
        word.Note.Should().Be("a word note");
    }

    [Fact]
    public async Task CanUpdateAWordAfterRestarting()
    {
        await WriteNextChange(SetWord(_entity1Id, "test-value"));
        var instance2 = ForkDatabase();//creates new services, but copies database. Simulates restarting the application
        await instance2.WriteNextChange(new SetWordNoteChange(_entity1Id, "a word note"));
        var word = await instance2.DataModel.GetLatest<Word>(_entity1Id);
        word!.Text.Should().Be("test-value");
        word.Note.Should().Be("a word note");
    }

    [Fact]
    public async Task WritingA2ndChangeDoesNotEffectTheFirstSnapshot()
    {
        await WriteNextChange(SetWord(_entity1Id, "change1"));
        await WriteNextChange(SetWord(_entity1Id, "change2"));

        DbContext.Snapshots.Should()
            .SatisfyRespectively(
                snap1 => snap1.Entity.Is<Word>().Text.Should().Be("change1"),
                snap2 => snap2.Entity.Is<Word>().Text.Should().Be("change2")
            );

        await Verify(AllData());
    }

    [Fact]
    public async Task WritingACommitWithMultipleChangesWorks()
    {
        await WriteNextChange([
            SetWord(_entity1Id, "first"),
            SetWord(_entity2Id, "second")
        ]);
        await Verify(AllData());
    }

    [Fact]
    public async Task WriteMultipleCommits()
    {
        await WriteNextChange(SetWord(Guid.NewGuid(), "change 1"));
        await WriteNextChange(SetWord(Guid.NewGuid(), "change 2"));
        DbContext.Snapshots.Should().HaveCount(2);
        await Verify(DbContext.Commits.DefaultOrder().Include(c => c.ChangeEntities).Include(c => c.Snapshots));

        await WriteNextChange(SetWord(Guid.NewGuid(), "change 3"));
        DbContext.Snapshots.Should().HaveCount(3);
        DataModel.QueryLatest<Word>().ToBlockingEnumerable().Should().HaveCount(3);
    }

    [Fact]
    public async Task WritingNoChangesWorks()
    {
        await WriteNextChange(SetWord(_entity1Id, "test-value"));
        await AddCommitsViaSync(Array.Empty<Commit>());

        var snapshot = DbContext.Snapshots.Should().ContainSingle().Subject;
        snapshot.Entity.Is<Word>().Text.Should().Be("test-value");
    }

    [Fact]
    public async Task Writing2ChangesSecondOverwritesFirst()
    {
        await WriteNextChange(SetWord(_entity1Id, "first"));
        await WriteNextChange(SetWord(_entity1Id, "second"));
        var snapshot = await DbContext.Snapshots.DefaultOrder().LastAsync();
        snapshot.Entity.Is<Word>().Text.Should().Be("second");
    }

    [Fact]
    public async Task CanWriteChangesWhenAnUnchangedWordExists()
    {
        await WriteNextChange(SetWord(_entity2Id, "word-2"));

        await WriteNextChange(SetWord(_entity1Id, "word-1"));
        await WriteNextChange(SetWord(_entity1Id, "second"));
        await WriteNextChange(SetWord(_entity1Id, "third"));
        var snapshot = await DbContext.Snapshots.DefaultOrder().LastAsync();
        snapshot.Entity.Is<Word>().Text.Should().Be("third");
    }

    [Fact]
    public async Task Writing2ChangesSecondOverwritesFirstWithLocalFirst()
    {
        var firstDate = DateTimeOffset.Now;
        var secondDate = DateTimeOffset.UtcNow.AddSeconds(1);
        await WriteChange(_localClientId, firstDate, SetWord(_entity1Id, "first"));
        await WriteChange(_localClientId, secondDate, SetWord(_entity1Id, "second"));
        var snapshot = await DbContext.Snapshots.DefaultOrder().LastAsync();
        snapshot.Entity.Is<Word>().Text.Should().Be("second");
    }

    [Fact]
    public async Task Writing2ChangesSecondOverwritesFirstWithUtcFirst()
    {
        var firstDate = DateTimeOffset.UtcNow;
        var secondDate = DateTimeOffset.Now.AddSeconds(1);
        await WriteChange(_localClientId, firstDate, SetWord(_entity1Id, "first"));
        await WriteChange(_localClientId, secondDate, SetWord(_entity1Id, "second"));
        var snapshot = await DbContext.Snapshots.DefaultOrder().LastAsync();
        snapshot.Entity.Is<Word>().Text.Should().Be("second");
    }

    [Fact]
    public async Task Writing2ChangesAtOnceWithMergedHistory()
    {
        await WriteNextChange(SetWord(_entity1Id, "first"));
        var second = await WriteNextChange(SetWord(_entity1Id, "second"));
        //add vis sync has some additional logic that depends on proper commit ordering
        await AddCommitsViaSync([
            await WriteChangeBefore(second, new SetWordNoteChange(_entity1Id, "a word note"), false),
            await WriteNextChange(SetWord(_entity1Id, "third"), false)
        ]);
        var word = await DataModel.GetLatest<Word>(_entity1Id);
        word!.Text.Should().Be("third");
        word.Note.Should().Be("a word note");

        await Verify(AllData());
    }

    [Fact]
    public async Task ChangeInsertedInTheMiddleOfHistoryWorks()
    {
        var first = await WriteNextChange(SetWord(_entity1Id, "first"));
        await WriteNextChange(SetWord(_entity1Id, "second"));

        await WriteChangeAfter(first, new SetWordNoteChange(_entity1Id, "a word note"));
        var word = await DataModel.GetLatest<Word>(_entity1Id);
        word!.Text.Should().Be("second");
        word.Note.Should().Be("a word note");
    }


    [Fact]
    public async Task CanTrackMultipleEntries()
    {
        await WriteNextChange(SetWord(_entity1Id, "entity1"));
        await WriteNextChange(SetWord(_entity2Id, "entity2"));

        (await DataModel.GetLatest<Word>(_entity1Id))!.Text.Should().Be("entity1");
        (await DataModel.GetLatest<Word>(_entity2Id))!.Text.Should().Be("entity2");
    }

    [Fact]
    public async Task CanCreate2EntriesOutOfOrder()
    {
        var commit1 = await WriteNextChange(SetWord(_entity1Id, "entity1"));
        await WriteChangeBefore(commit1, SetWord(_entity2Id, "entity2"));
    }

    [Fact]
    public async Task CanDeleteAnEntry()
    {
        await WriteNextChange(SetWord(_entity1Id, "test-value"));
        var deleteCommit = await WriteNextChange(new DeleteChange<Word>(_entity1Id));
        var snapshot = await DbContext.Snapshots.DefaultOrder().LastAsync();
        snapshot.Entity.DeletedAt.Should().Be(deleteCommit.DateTime);
    }

    [Fact]
    public async Task CanModifyAnEntryAfterDelete()
    {
        await WriteNextChange(SetWord(_entity1Id, "test-value"));
        var deleteCommit = await WriteNextChange(new DeleteChange<Word>(_entity1Id));
        await WriteNextChange(SetWord(_entity1Id, "after-delete"));
        var snapshot = await DbContext.Snapshots.DefaultOrder().LastAsync();
        var word = snapshot.Entity.Is<Word>();
        word.Text.Should().Be("after-delete");
        word.DeletedAt.Should().Be(deleteCommit.DateTime);
    }

    [Fact]
    public async Task ChangesToSnapshotsAreNotSaved()
    {
        await WriteNextChange(SetWord(_entity1Id, "test-value"));
        var word = await DataModel.GetLatest<Word>(_entity1Id);
        word!.Text.Should().Be("test-value");
        word.Note.Should().BeNull();
        
        //change made outside the model, should not be saved when writing the next change
        word.Note = "a note";
        
        var commit = await WriteNextChange(SetWord(_entity1Id, "after-change"));
        var objectSnapshot = commit.Snapshots.Should().ContainSingle().Subject;
        objectSnapshot.Entity.Is<Word>().Text.Should().Be("after-change");
        objectSnapshot.Entity.Is<Word>().Note.Should().BeNull();
    }


    // [Fact]
    // public async Task CanGetEntryLinq2Db()
    // {
    //     await WriteNextChange(SetWord(_entity1Id, "test-value"));
    //
    //     var entries = await DataModel.GetLatestObjects<Word>().ToArrayAsyncLinqToDB();
    //     entries.Should().ContainSingle();
    // }
}
