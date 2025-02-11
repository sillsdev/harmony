using System.Text.Json.Serialization;
using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample.Models;
using Ycs;

namespace SIL.Harmony.Sample.Changes;

public class NewExampleChange : CreateChange<Example>, ISelfNamedType<NewExampleChange>
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

    public override async ValueTask<Example> NewEntity(Commit commit, IChangeContext context)
    {
        return new Example
        {
            Id = EntityId,
            DefinitionId = DefinitionId,
            YTextBlob = UpdateBlob,
            DeletedAt = await context.IsObjectDeleted(DefinitionId)? commit.DateTime : null
        };
    }
}
