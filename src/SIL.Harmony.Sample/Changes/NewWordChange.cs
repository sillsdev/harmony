using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Sample.Changes;

public class NewWordChange(Guid entityId, string text, string? note = null) : CreateChange<Word>(entityId), ISelfNamedType<NewWordChange>
{
    public string Text { get; } = text;
    public string? Note { get; } = note;

    public override ValueTask<IObjectBase> NewEntity(Commit commit, ChangeContext context)
    {
        return new(new Word { Text = Text, Note = Note, Id = EntityId });
    }
}
