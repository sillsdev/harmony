using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;
using Crdt.Sample.Models;

namespace Crdt.Sample.Changes;

public class NewDefinitionChange(Guid entityId) : Change<Definition>(entityId), ISelfNamedType<NewDefinitionChange>
{
    public required string Text { get; init; }
    public string? OneWordDefinition { get; init; }
    public required string PartOfSpeech { get; init; }
    public required double Order { get; set; }
    public required Guid WordId { get; init; }

    public override IObjectBase NewEntity(Commit commit)
    {
        return new Definition
        {
            Id = EntityId,
            Text = Text,
            Order = Order,
            OneWordDefinition = OneWordDefinition,
            PartOfSpeech = PartOfSpeech,
            WordId = WordId
        };
    }

    public override async ValueTask ApplyChange(Definition definition, ChangeContext context)
    {
        definition.Text = Text;
        definition.OneWordDefinition = OneWordDefinition;
        definition.PartOfSpeech = PartOfSpeech;
        definition.Order = Order;
        if (await context.IsObjectDeleted(WordId))
        {
            definition.DeletedAt = context.Commit.DateTime;
        }
    }
}