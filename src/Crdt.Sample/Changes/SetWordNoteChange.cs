using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;
using Crdt.Sample.Models;

namespace Crdt.Sample.Changes;

public class SetWordNoteChange(Guid entityId, string note) : EditChange<Word>(entityId), ISelfNamedType<SetWordNoteChange>
{
    public string Note { get; } = note;

    public override ValueTask ApplyChange(Word entity, ChangeContext context)
    {
        entity.Note = Note;
        return ValueTask.CompletedTask;
    }
}
