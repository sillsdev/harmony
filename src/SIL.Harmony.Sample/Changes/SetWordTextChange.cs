using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Sample.Changes;

/// <summary>
/// set text is used in many tests as a simple change that either creates a word, or updates the text of a word.
/// because of this it implements both NewEntity and ApplyChange, it's recommended to use a CreateChange for new entities and EditChange for updates.
/// </summary>
public class SetWordTextChange(Guid entityId, string text) : Change<Word>(entityId), ISelfNamedType<SetWordTextChange>
{
    public string Text { get; } = text;

    public override ValueTask<Word> NewEntity(Commit commit, IChangeContext context)
    {
        return new(new Word()
        {
            Id = EntityId,
            Text = Text
        });
    }


    public override ValueTask ApplyChange(Word entity, IChangeContext context)
    {
        entity.Text = Text;
        return ValueTask.CompletedTask;
    }
}
