using SIL.Harmony.Changes;
using SIL.Harmony.Sample.Models;
using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony.Tests;

public class DefinitionTests : DataModelTestBase
{
    [Fact]
    public async Task CanAddADefinitionToAWord()
    {
        var wordId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "hello"));
        await WriteNextChange(NewDefinition(wordId, "a greeting", "verb"));
        var snapshot = await DataModel.GetProjectSnapshot();
        var definitionSnapshot = snapshot.Snapshots.Values.Single(s => s.IsType<Definition>());
        var definition = await DataModel.GetBySnapshotId<Definition>(definitionSnapshot.Id);
        definition.Text.Should().Be("a greeting");
        definition.WordId.Should().Be(wordId);
    }

    [Fact]
    public async Task DeletingAWordDeletesTheDefinition()
    {
        var wordId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "hello"));
        await WriteNextChange(NewDefinition(wordId, "a greeting", "verb"));
        await WriteNextChange(new DeleteChange<Word>(wordId));
        var snapshot = await DataModel.GetProjectSnapshot();
        snapshot.Snapshots.Values.Where(s => !s.EntityIsDeleted).Should().BeEmpty();
    }

    [Fact]
    public async Task AddingADefinitionToADeletedWordDeletesIt()
    {
        var wordId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "hello"));
        await WriteNextChange(new DeleteChange<Word>(wordId));
        await WriteNextChange(NewDefinition(wordId, "a greeting", "verb"));
        var snapshot = await DataModel.GetProjectSnapshot();
        snapshot.Snapshots.Values.Where(s => !s.EntityIsDeleted).Should().BeEmpty();
    }


    [Fact]
    public async Task CanGetInOrder()
    {
        var wordId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "hello"));
        await WriteNextChange(NewDefinition(wordId, "greet someone", "verb", 2));
        await WriteNextChange(NewDefinition(wordId, "a greeting", "noun", 1));

        var definitions = await DataModel.GetLatestObjects<Definition>().ToArrayAsync();
        definitions.Select(d => d.PartOfSpeech).Should().ContainInConsecutiveOrder(
            "noun",
            "verb"
        );
    }

    [Fact]
    public async Task CanChangeOrderBetweenExistingDefinitions()
    {
        var wordId = Guid.NewGuid();
        var definitionAId = Guid.NewGuid();
        var definitionBId = Guid.NewGuid();
        var definitionCId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "hello"));
        await WriteNextChange(NewDefinition(wordId, "a greeting", "noun", 1, definitionAId));
        await WriteNextChange(NewDefinition(wordId, "greet someone", "verb", 2, definitionBId));
        await WriteNextChange(NewDefinition(wordId, "used as a greeting", "exclamation", 3, definitionCId));

        var definitions = await DataModel.GetLatestObjects<Definition>().ToArrayAsync();
        definitions.Select(d => d.PartOfSpeech).Should().ContainInConsecutiveOrder(
            "noun",
            "verb",
            "exclamation"
        );

        //change the order of the exclamation to be between the noun and verb
        await WriteNextChange(SetOrderChange<Definition>.Between(definitionCId, definitions[0], definitions[1]));

        definitions = await DataModel.GetLatestObjects<Definition>().ToArrayAsync();
        definitions.Select(d => d.PartOfSpeech).Should().ContainInConsecutiveOrder(
            "noun",
            "exclamation",
            "verb"
        );
    }

    [Fact]
    public async Task ConsistentlySortsItems()
    {
        var wordId = Guid.NewGuid();
        //these ids are hardcoded to ensure the order is consistent
        var definitionAId = new Guid("a40fb7c8-f3b1-441b-b5fb-0261ae32bff1");
        var definitionBId = new Guid("0197b80d-9691-4896-8c9b-277a3455a7f6");
        await WriteNextChange(SetWord(wordId, "hello"));
        await WriteNextChange(NewDefinition(wordId, "greet someone", "verb", 1, definitionAId));
        await WriteNextChange(NewDefinition(wordId, "a greeting", "noun", 1, definitionBId));

        var definitions = await DataModel.GetLatestObjects<Definition>().ToArrayAsync();
        definitions.Select(d => d.Id).Should().ContainInConsecutiveOrder(
            definitionBId,
            definitionAId
        );
    }
}