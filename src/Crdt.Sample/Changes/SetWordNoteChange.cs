using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;
using Crdt.Sample.Models;

namespace Crdt.Sample.Changes;

public class SetWordNoteChange(Guid entityId, string note) : Change<Word>(entityId), ISelfNamedType<SetWordNoteChange>
{
    public string Note { get; } = note;

    public override IObjectBase NewEntity(Commit commit)
    {
        throw new System.NotImplementedException();
    }

    public override ValueTask ApplyChange(Word entity, ChangeContext context)
    {
        entity.Note = Note;
        return ValueTask.CompletedTask;
    }
}