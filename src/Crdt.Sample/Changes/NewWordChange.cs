using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;
using Crdt.Sample.Models;

namespace Crdt.Sample.Changes;

public class NewWordChange(Guid entityId, string text, string? note = null) : CreateChange<Word>(entityId), ISelfNamedType<NewWordChange>
{
    public string Text { get; } = text;
    public string? Note { get; } = note;

    public override ValueTask<IObjectBase> NewEntity(Commit commit, ChangeContext context)
    {
        return new(new Word { Text = Text, Note = Note, Id = EntityId });
    }
}
