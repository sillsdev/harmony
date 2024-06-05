using System.Text.Json.Serialization;
using Crdt.Core;
using Crdt.Entities;
using Ycs;

namespace Crdt.Sample.Models;

public class Example: IObjectBase<Example>
{
    public Guid Id { get; init; }
    public DateTimeOffset? DeletedAt { get; set; }
    public required Guid DefinitionId { get; init; }
    private YDoc _yDoc = new YDoc();

    [JsonIgnore]
    public YText YText => _yDoc.GetText();

    public string Text => YText.ToString();

    public string YTextBlob
    {
        get => Convert.ToBase64String(_yDoc.EncodeStateAsUpdateV2());
        set
        {
            _yDoc = new YDoc();
            _yDoc.ApplyUpdateV2(Convert.FromBase64String(value));
        }
    }

    public Guid[] GetReferences()
    {
        return [DefinitionId];
    }

    public void RemoveReference(Guid id, Commit commit)
    {
        if (DefinitionId == id)
        {
            DeletedAt = commit.DateTime;
        }
    }

    public IObjectBase Copy()
    {
        return new Example
        {
            Id = Id,
            DefinitionId = DefinitionId,
            DeletedAt = DeletedAt,
            YTextBlob = YTextBlob
        };
    }
}