using System.Text.Json.Serialization;
using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;
using Crdt.Sample.Models;
using Ycs;

namespace Crdt.Sample.Changes;

public class EditExampleChange : EditChange<Example>, ISelfNamedType<EditExampleChange>
{
    public static EditExampleChange EditExample(Example example, Action<YText> change)
    {
        var text = example.YText;
        var stateBefore = text.Doc.EncodeStateVectorV2();
        change(text);
        var updateBlob = Convert.ToBase64String(text.Doc.EncodeStateAsUpdateV2(stateBefore));
        return new EditExampleChange(example.Id, updateBlob);
    }


    [JsonConstructor]
    public EditExampleChange(Guid entityId, string updateBlob) : base(entityId)
    {
        UpdateBlob = updateBlob;
    }

    public string UpdateBlob { get; set; }

    public override ValueTask ApplyChange(Example entity, ChangeContext context)
    {
        entity.YText.Doc.ApplyUpdateV2(Convert.FromBase64String(UpdateBlob));
        return ValueTask.CompletedTask;
    }
}
