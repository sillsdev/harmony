using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Changes;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class DataModelReferenceTests : DataModelTestBase
{
    private readonly Guid _word1Id = Guid.NewGuid();
    private readonly Guid _word2Id = Guid.NewGuid();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await WriteNextChange(SetWord(_word1Id, "entity1"));
        await WriteNextChange(SetWord(_word2Id, "entity2"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddReferenceWorks(bool includeObjectInSnapshot)
    {
        // act
        await WriteNextChange(new SetAntonymReferenceChange(_word1Id, _word2Id, setObject: includeObjectInSnapshot));

        // assert - snapshot
        var word = await DataModel.GetLatest<Word>(_word1Id);
        word.Should().NotBeNull();
        word.AntonymId.Should().Be(_word2Id);
        if (includeObjectInSnapshot)
        {
            word.Antonym.Should().NotBeNull();
            word.Antonym.Text.Should().Be("entity2");
        }
        else
        {
            word.Antonym.Should().BeNull();
        }

        // assert - projected entity
        var entityWord = await DataModel.QueryLatest<Word>(w => w.Include(w => w.Antonym))
            .Where(w => w.Id == _word1Id).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.AntonymId.Should().Be(_word2Id);
        entityWord.Antonym.Should().NotBeNull();
        entityWord.Antonym.Text.Should().Be("entity2");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateReferenceTwiceInSameCommitWorks(bool includeObjectInSnapshot)
    {
        // arrange
        var word3Id = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(word3Id, "entity3"));

        // act
        await WriteNextChange(
            [
                new SetAntonymReferenceChange(word3Id, _word1Id, setObject: includeObjectInSnapshot),
                new SetAntonymReferenceChange(word3Id, _word2Id, setObject: includeObjectInSnapshot),
            ]);

        // assert - snapshot
        var word = await DataModel.GetLatest<Word>(word3Id);
        word.Should().NotBeNull();
        word.AntonymId.Should().Be(_word2Id);
        if (includeObjectInSnapshot)
        {
            word.Antonym.Should().NotBeNull();
            word.Antonym.Text.Should().Be("entity2");
        }
        else
        {
            word.Antonym.Should().BeNull();
        }

        // assert - projected entity
        var entityWord = await DataModel.QueryLatest<Word>(w => w.Include(w => w.Antonym))
            .Where(w => w.Id == word3Id).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.AntonymId.Should().Be(_word2Id);
        entityWord.Antonym.Should().NotBeNull();
        entityWord.Antonym.Text.Should().Be("entity2");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateReferenceTwiceInSameSyncWorks(bool includeObjectInSnapshot)
    {
        // arrange
        var word3Id = Guid.NewGuid();
        await WriteNextChange(new NewWordChange(word3Id, "entity3"));

        // act
        await AddCommitsViaSync([
            await WriteNextChange(new SetAntonymReferenceChange(word3Id, _word1Id, setObject: includeObjectInSnapshot), add: false),
            await WriteNextChange(new SetAntonymReferenceChange(word3Id, _word2Id, setObject: includeObjectInSnapshot), add: false),
        ]);

        // assert - snapshot
        var word = await DataModel.GetLatest<Word>(word3Id);
        word.Should().NotBeNull();
        word.AntonymId.Should().Be(_word2Id);
        if (includeObjectInSnapshot)
        {
            word.Antonym.Should().NotBeNull();
            word.Antonym.Text.Should().Be("entity2");
        }
        else
        {
            word.Antonym.Should().BeNull();
        }

        // assert - projected entity
        var entityWord = await DataModel.QueryLatest<Word>(w => w.Include(w => w.Antonym))
            .Where(w => w.Id == word3Id).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.AntonymId.Should().Be(_word2Id);
        entityWord.Antonym.Should().NotBeNull();
        entityWord.Antonym.Text.Should().Be("entity2");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddEntityAndReferenceInSameCommitWorks(bool includeObjectInSnapshot)
    {
        // arrange
        var word3Id = Guid.NewGuid();

        // act
        await WriteNextChange(
            [
                new NewWordChange(word3Id, "entity3"),
                new SetAntonymReferenceChange(word3Id, _word1Id, setObject: includeObjectInSnapshot),
            ]);

        // assert - snapshot
        var word = await DataModel.GetLatest<Word>(word3Id);
        word.Should().NotBeNull();
        word.Text.Should().Be("entity3");
        word.AntonymId.Should().Be(_word1Id);
        if (includeObjectInSnapshot)
        {
            word.Antonym.Should().NotBeNull();
            word.Antonym.Text.Should().Be("entity1");
        }
        else
        {
            word.Antonym.Should().BeNull();
        }

        // assert - projected entity
        var entityWord = await DataModel.QueryLatest<Word>(w => w.Include(w => w.Antonym))
            .Where(w => w.Id == word3Id).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.Text.Should().Be("entity3");
        entityWord.AntonymId.Should().Be(_word1Id);
        entityWord.Antonym.Should().NotBeNull();
        entityWord.Antonym.Text.Should().Be("entity1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddEntityAndReverseReferenceInSameCommitWorks(bool includeObjectInSnapshot)
    {
        // arrange
        var word3Id = Guid.NewGuid();

        // act
        await WriteNextChange(
            [
                new NewWordChange(word3Id, "entity3"),
                new SetAntonymReferenceChange(_word1Id, word3Id, setObject: includeObjectInSnapshot),
            ]);

        // assert - snapshot
        var word = await DataModel.GetLatest<Word>(_word1Id);
        word.Should().NotBeNull();
        word.Text.Should().Be("entity1");
        word.AntonymId.Should().Be(word3Id);
        if (includeObjectInSnapshot)
        {
            word.Antonym.Should().NotBeNull();
            word.Antonym.Text.Should().Be("entity3");
        }
        else
        {
            word.Antonym.Should().BeNull();
        }

        // assert - projected entity
        var entityWord = await DataModel.QueryLatest<Word>(w => w.Include(w => w.Antonym))
            .Where(w => w.Id == _word1Id).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.Text.Should().Be("entity1");
        entityWord.AntonymId.Should().Be(word3Id);
        entityWord.Antonym.Should().NotBeNull();
        entityWord.Antonym.Text.Should().Be("entity3");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddEntityAndReferenceInSameSyncWorks(bool includeObjectInSnapshot)
    {
        // arrange
        var word3Id = Guid.NewGuid();

        // act
        await AddCommitsViaSync([
            await WriteNextChange(new NewWordChange(word3Id, "entity3"), add: false),
            await WriteNextChange(new SetAntonymReferenceChange(word3Id, _word1Id, setObject: includeObjectInSnapshot), add: false),
        ]);

        // assert - snapshot
        var word = await DataModel.GetLatest<Word>(word3Id);
        word.Should().NotBeNull();
        word.Text.Should().Be("entity3");
        word.AntonymId.Should().Be(_word1Id);
        if (includeObjectInSnapshot)
        {
            word.Antonym.Should().NotBeNull();
            word.Antonym.Text.Should().Be("entity1");
        }
        else
        {
            word.Antonym.Should().BeNull();
        }

        // assert - projected entity
        var entityWord = await DataModel.QueryLatest<Word>(w => w.Include(w => w.Antonym))
            .Where(w => w.Id == word3Id).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.Text.Should().Be("entity3");
        entityWord.AntonymId.Should().Be(_word1Id);
        entityWord.Antonym.Should().NotBeNull();
        entityWord.Antonym.Text.Should().Be("entity1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddEntityAndReverseReferenceInSameSyncWorks(bool includeObjectInSnapshot)
    {
        // arrange
        var word3Id = Guid.NewGuid();

        // act
        await AddCommitsViaSync([
            await WriteNextChange(new NewWordChange(word3Id, "entity3"), add: false),
            await WriteNextChange(new SetAntonymReferenceChange(_word1Id, word3Id, setObject: includeObjectInSnapshot), add: false),
        ]);

        // assert - snapshot
        var word = await DataModel.GetLatest<Word>(_word1Id);
        word.Should().NotBeNull();
        word.Text.Should().Be("entity1");
        word.AntonymId.Should().Be(word3Id);
        if (includeObjectInSnapshot)
        {
            word.Antonym.Should().NotBeNull();
            word.Antonym.Text.Should().Be("entity3");
        }
        else
        {
            word.Antonym.Should().BeNull();
        }

        // assert - projected entity
        var entityWord = await DataModel.QueryLatest<Word>(w => w.Include(w => w.Antonym))
            .Where(w => w.Id == _word1Id).SingleOrDefaultAsync();
        entityWord.Should().NotBeNull();
        entityWord.Text.Should().Be("entity1");
        entityWord.AntonymId.Should().Be(word3Id);
        entityWord.Antonym.Should().NotBeNull();
        entityWord.Antonym.Text.Should().Be("entity3");
    }

    [Fact]
    public async Task DeleteAfterTheFactRewritesReferences()
    {
        var addRef = await WriteNextChange(new SetAntonymReferenceChange(_word1Id, _word2Id));
        var entryWithRef = await DataModel.GetLatest<Word>(_word1Id);
        entryWithRef!.AntonymId.Should().Be(_word2Id);

        await WriteChangeBefore(addRef, new DeleteChange<Word>(_word2Id));
        var entryWithoutRef = await DataModel.GetLatest<Word>(_word1Id);
        entryWithoutRef!.AntonymId.Should().BeNull();
    }

    [Fact]
    public async Task DeleteRemovesAllReferences()
    {
        await WriteNextChange(new SetAntonymReferenceChange(_word1Id, _word2Id));
        var entryWithRef = await DataModel.GetLatest<Word>(_word1Id);
        entryWithRef!.AntonymId.Should().Be(_word2Id);

        await WriteNextChange(new DeleteChange<Word>(_word2Id));
        var entryWithoutRef = await DataModel.GetLatest<Word>(_word1Id);
        entryWithoutRef!.AntonymId.Should().BeNull();
    }

    [Fact]
    public async Task SnapshotsDontGetMutatedByADelete()
    {
        var refAdd = await WriteNextChange(new SetAntonymReferenceChange(_word1Id, _word2Id));
        await WriteNextChange(new DeleteChange<Word>(_word2Id));
        var word = await DataModel.GetAtCommit<Word>(refAdd.Id, _word1Id);
        word.Should().NotBeNull();
        word.AntonymId.Should().Be(_word2Id);
    }

    [Fact]
    public async Task DeleteRetroactivelyRemovesRefs()
    {
        var entityId3 = Guid.NewGuid();
        await WriteNextChange(SetWord(entityId3, "entity3"));
        await WriteNextChange(new SetAntonymReferenceChange(_word1Id, _word2Id));
        var delete = await WriteNextChange(new DeleteChange<Word>(_word2Id));

        //a ref was synced in the past, it happened before the delete, the reference should be retroactively removed
        await WriteChangeBefore(delete, new SetAntonymReferenceChange(entityId3, _word2Id));
        var entry = await DataModel.GetLatest<Word>(entityId3);
        entry!.AntonymId.Should().BeNull();
    }

    [Fact]
    public async Task DeleteDoesNotEffectARootSnapshotCreatedBeforeTheDelete()
    {
        var wordId = Guid.NewGuid();
        var initialWordCommit = await WriteNextChange(new NewWordChange(wordId, "entity1", antonymId: _word1Id), add: false);
        var deleteWordCommit = await WriteNextChange(DeleteWord(_word1Id), add: false);
        await AddCommitsViaSync([
            initialWordCommit,
            deleteWordCommit
        ]);
        var snapshot = await DbContext.Snapshots.SingleAsync(s => s.CommitId == initialWordCommit.Id);
        var initialWord = (Word) snapshot.Entity;
        initialWord.AntonymId.Should().Be(_word1Id);
        snapshot = await DbContext.Snapshots.SingleAsync(s => s.CommitId == deleteWordCommit.Id && s.EntityId == wordId);
        var wordWithoutRef = (Word) snapshot.Entity;
        wordWithoutRef.AntonymId.Should().BeNull();
    }

    [Fact]
    public async Task AddAndDeleteInSameCommitDeletesRefs()
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();

        await WriteNextChange(
            [
                SetWord(wordId, "original"),
                NewDefinition(wordId, "the shiny one everything started with", "adj", 0, definitionId),
                new DeleteChange<Word>(wordId),
            ]);

        var def = await DataModel.GetLatest<Definition>(definitionId);
        def.Should().NotBeNull();
        def.DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateAndDeleteParentInSameCommitWorks()
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();

        await WriteNextChange(
            [
                SetWord(wordId, "original"),
                NewDefinition(wordId, "the shiny one everything started with", "adj", 0, definitionId),
            ]);

        await WriteNextChange(
            [
                new SetDefinitionPartOfSpeechChange(definitionId, "pos2"),
                new DeleteChange<Word>(wordId),
            ]);

        var def = await DataModel.GetLatest<Definition>(definitionId);
        def.Should().NotBeNull();
        def.PartOfSpeech.Should().Be("pos2");
        def.DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AddAndDeleteInSameSyncDeletesRefs()
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();

        var initialCommit = await WriteNextChange(
            [
                SetWord(wordId, "original"),
                NewDefinition(wordId, "the shiny one everything started with", "adj", 0, definitionId),
            ], add: false);
        var deleteCommit = await WriteNextChange(
                new DeleteChange<Word>(wordId),
            add: false);
        await AddCommitsViaSync([
            initialCommit,
            deleteCommit
        ]);

        var def = await DataModel.GetLatest<Definition>(definitionId);
        def.Should().NotBeNull();
        def.DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CanCreate2TagsWithTheSameNameOutOfOrder()
    {
        var tagText = "tag1";
        var commitA = await WriteNextChange(SetTag(Guid.NewGuid(), tagText));
        //represents someone syncing in a tag with the same name
        await WriteChangeBefore(commitA, SetTag(Guid.NewGuid(), tagText));
        DataModel.QueryLatest<Tag>().ToBlockingEnumerable().Where(t => t.Text == tagText).Should().ContainSingle();
    }

    [Fact]
    public async Task CanUpdateTagWithTheSameNameOutOfOrder()
    {
        var tagText = "tag1";
        var renameTagId = Guid.NewGuid();
        await WriteNextChange(SetTag(renameTagId, "tag2"));
        var commitA = await WriteNextChange(SetTag(Guid.NewGuid(), tagText));
        //represents someone syncing in a tag with the same name
        await WriteNextChange(SetTag(renameTagId, tagText));
        DataModel.QueryLatest<Tag>().ToBlockingEnumerable().Where(t => t.Text == tagText).Should().ContainSingle();
    }
}
