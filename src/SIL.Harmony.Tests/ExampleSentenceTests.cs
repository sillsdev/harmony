using SIL.Harmony.Changes;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class ExampleSentenceTests : DataModelTestBase
{
    public IChange NewExampleSentence(Guid wordId, string text, Guid? exampleId = default)
    {
        return NewExampleChange.FromString(wordId, text, exampleId);
    }
    
    [Fact]
    public async Task CanAddAnExampleSentenceToAWord()
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "hello"));
        await WriteNextChange(NewDefinition(wordId, "a greeting", "verb", 0, definitionId));
        await WriteNextChange(NewExampleSentence(definitionId, "Hello, world!"));
        var snapshot = await DataModel.GetProjectSnapshot();
        var exampleSentenceSnapshot = snapshot.Snapshots.Values.Single(s => s.IsType<Example>());
        var exampleSentence = (Example)await DataModel.GetBySnapshotId(exampleSentenceSnapshot.Id);
        exampleSentence.Text.Should().Be("Hello, world!");
        exampleSentence.DefinitionId.Should().Be(definitionId);
    }

    [Fact]
    public async Task DeletingAWordDeletesTheExampleSentence()
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "hello"));
        await WriteNextChange(NewDefinition(wordId, "a greeting", "verb", 0, definitionId));
        await WriteNextChange(NewExampleSentence(definitionId, "Hello, world!"));
        await WriteNextChange(new DeleteChange<Word>(wordId));
        var snapshot = await DataModel.GetProjectSnapshot();
        snapshot.Snapshots.Values.Where(s => !s.EntityIsDeleted).Should().BeEmpty();
    }

    [Fact]
    public async Task AddingAnExampleSentenceToADeletedWordDeletesIt()
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "hello"));
        await WriteNextChange(NewDefinition(wordId, "a greeting", "verb", 0, definitionId));
        await WriteNextChange(new DeleteChange<Word>(wordId));
        await WriteNextChange(NewExampleSentence(definitionId, "Hello, world!"));
        var snapshot = await DataModel.GetProjectSnapshot();
        snapshot.Snapshots.Values.Where(s => !s.EntityIsDeleted).Should().BeEmpty();
    }

    [Fact]
    public async Task CanEditExampleText()
    {
        var wordId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        var exampleId = Guid.NewGuid();
        await WriteNextChange(SetWord(wordId, "hello"));
        await WriteNextChange(NewDefinition(wordId, "a greeting", "verb", 0, definitionId));
        await WriteNextChange(NewExampleSentence(definitionId, "Yo Bob", exampleId));
        var example = await DataModel.GetLatest<Example>(exampleId);
        example.Should().NotBeNull();
        await WriteNextChange(EditExampleChange.EditExample(example!, text => text.Insert(3, "What's up ")));

        var actualExample = await DataModel.GetLatest<Example>(exampleId);
        actualExample.Should().NotBeSameAs(example);
        actualExample!.YText.ToString().Should().Be("Yo What's up Bob");
    }
}