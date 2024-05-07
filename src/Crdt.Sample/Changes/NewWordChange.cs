using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;
using Crdt.Sample.Models;

namespace Crdt.Sample.Changes;

public class NewWordChange(Guid entityId, string text, string? note = null) : Change<Word>(entityId), ISelfNamedType<NewWordChange>
{
    public string Text { get; } = text;
    public string? Note { get; } = note;

    public override IObjectBase NewEntity(Commit commit)
    {
        return new Word
        {
            Text = Text,
            Note = Note,
            Id = EntityId
        };
    }

    public override ValueTask ApplyChange(Word entity, ChangeContext context)
    {
        entity.Text = Text;
        entity.Note = Note;
        return ValueTask.CompletedTask;
    }
}