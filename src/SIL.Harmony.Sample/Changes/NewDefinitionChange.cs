using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Sample.Changes;

public class NewDefinitionChange(Guid entityId) : CreateChange<Definition>(entityId), ISelfNamedType<NewDefinitionChange>
{
    public required string Text { get; init; }
    public string? OneWordDefinition { get; init; }
    public required string PartOfSpeech { get; init; }
    public required double Order { get; set; }
    public required Guid WordId { get; init; }

    public override async ValueTask<Definition> NewEntity(Commit commit, IChangeContext context)
    {
        return new Definition
        {
            Id = EntityId,
            Text = Text,
            Order = Order,
            OneWordDefinition = OneWordDefinition,
            PartOfSpeech = PartOfSpeech,
            WordId = WordId,
            DeletedAt = await context.IsObjectDeleted(WordId) ? commit.DateTime : null
        };
    }
}
