﻿using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;
using Crdt.Sample.Models;

namespace Crdt.Sample.Changes;

public class NewDefinitionChange(Guid entityId) : CreateChange<Definition>(entityId), ISelfNamedType<NewDefinitionChange>
{
    public required string Text { get; init; }
    public string? OneWordDefinition { get; init; }
    public required string PartOfSpeech { get; init; }
    public required double Order { get; set; }
    public required Guid WordId { get; init; }

    public override async ValueTask<IObjectBase> NewEntity(Commit commit, ChangeContext context)
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
