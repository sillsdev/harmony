using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Sample.Changes;

public class SetWordNoteChange(Guid entityId, string note) : EditChange<Word>(entityId), ISelfNamedType<SetWordNoteChange>
{
    public string Note { get; } = note;

    public override ValueTask ApplyChange(Word entity, ChangeContext context)
    {
        entity.Note = Note;
        return ValueTask.CompletedTask;
    }
}
