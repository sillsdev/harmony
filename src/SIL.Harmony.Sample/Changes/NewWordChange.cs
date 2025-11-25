using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Sample.Changes;

public class NewWordChange(Guid entityId, string text, string? note = null, Guid? antonymId = null) : CreateChange<Word>(entityId), ISelfNamedType<NewWordChange>
{
    public string Text { get; } = text;
    public string? Note { get; } = note;
    public Guid? AntonymId { get; } = antonymId;

    public override async ValueTask<Word> NewEntity(Commit commit, IChangeContext context)
    {
        var antonym = AntonymId is null ? null : await context.GetCurrent<Word>(AntonymId.Value);
        antonym = antonym is { DeletedAt: null } ? antonym : null;
        return new Word { Text = Text, Note = Note, Id = EntityId, Antonym = antonym, AntonymId = antonym?.Id };
    }
}
