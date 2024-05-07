using System.Text.Json.Serialization;
using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;
using Crdt.Sample.Models;
using Ycs;

namespace Crdt.Sample.Changes;

public class NewExampleChange : Change<Example>, ISelfNamedType<NewExampleChange>
{
    public static NewExampleChange FromString(Guid definitionId, string example, Guid? exampleId = default)
    {
        return FromAction(definitionId, exampleId, text => text.Insert(0, example));
    }

    public static NewExampleChange FromAction(Guid definitionId, Guid? exampleId, Action<YText> change)
    {
        var doc = new YDoc();
        var stateBefore = doc.EncodeStateVectorV2();
        change(doc.GetText());
        var updateBlob = Convert.ToBase64String(doc.EncodeStateAsUpdateV2(stateBefore));
        return new NewExampleChange(exampleId ?? Guid.NewGuid())
        {
            DefinitionId = definitionId,
            UpdateBlob = updateBlob
        };
    }

    public required Guid DefinitionId { get; init; }
    public required string UpdateBlob { get; set; }

    [JsonConstructor]
    private NewExampleChange(Guid entityId) : base(entityId)
    {
    }

    public override IObjectBase NewEntity(Commit commit)
    {
        return new Example
        {
            Id = EntityId,
            DefinitionId = DefinitionId,
            YTextBlob = UpdateBlob
        };
    }

    public override async ValueTask ApplyChange(Example entity, ChangeContext context)
    {
        entity.YTextBlob = UpdateBlob;
        if (await context.IsObjectDeleted(DefinitionId))
        {
            entity.DeletedAt = context.Commit.DateTime;
        }
    }
}