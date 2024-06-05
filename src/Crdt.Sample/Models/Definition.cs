using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;

namespace Crdt.Sample.Models;

public class Definition : IObjectBase<Definition>, IOrderableCrdt
{
    public Guid Id { get; init; }
    public required string Text { get; set; }
    public required double Order { get; set; }
    public string? OneWordDefinition { get; set; }
    public required string PartOfSpeech { get; set; }
    public required Guid WordId { get; init; }
    public DateTimeOffset? DeletedAt { get; set; }

    public Guid[] GetReferences()
    {
        return [WordId];
    }

    public void RemoveReference(Guid id, Commit commit)
    {
        if (WordId == id)
        {
            DeletedAt = commit.DateTime;
        }
    }

    public IObjectBase Copy()
    {
        return new Definition
        {
            Id = Id,
            Text = Text,
            Order = Order,
            OneWordDefinition = OneWordDefinition,
            PartOfSpeech = PartOfSpeech,
            WordId = WordId,
            DeletedAt = DeletedAt
        };
    }
}