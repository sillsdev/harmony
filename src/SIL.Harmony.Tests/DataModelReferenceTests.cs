﻿using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Changes;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;
using SIL.Harmony.Tests;

namespace Tests;

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


    [Fact]
    public async Task DeleteAfterTheFactRewritesReferences()
    {
        var addRef = await WriteNextChange(new AddAntonymReferenceChange(_word1Id, _word2Id));
        var entryWithRef = await DataModel.GetLatest<Word>(_word1Id);
        entryWithRef!.AntonymId.Should().Be(_word2Id);

        await WriteChangeBefore(addRef, new DeleteChange<Word>(_word2Id));
        var entryWithoutRef = await DataModel.GetLatest<Word>(_word1Id);
        entryWithoutRef!.AntonymId.Should().BeNull();
    }

    [Fact]
    public async Task DeleteRemovesAllReferences()
    {
        await WriteNextChange(new AddAntonymReferenceChange(_word1Id, _word2Id));
        var entryWithRef = await DataModel.GetLatest<Word>(_word1Id);
        entryWithRef!.AntonymId.Should().Be(_word2Id);

        await WriteNextChange(new DeleteChange<Word>(_word2Id));
        var entryWithoutRef = await DataModel.GetLatest<Word>(_word1Id);
        entryWithoutRef!.AntonymId.Should().BeNull();
    }

    [Fact]
    public async Task SnapshotsDontGetMutatedByADelete()
    {
        var refAdd = await WriteNextChange(new AddAntonymReferenceChange(_word1Id, _word2Id));
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
        await WriteNextChange(new AddAntonymReferenceChange(_word1Id, _word2Id));
        var delete = await WriteNextChange(new DeleteChange<Word>(_word2Id));

        //a ref was synced in the past, it happened before the delete, the reference should be retroactively removed
        await WriteChangeBefore(delete, new AddAntonymReferenceChange(entityId3, _word2Id));
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
}