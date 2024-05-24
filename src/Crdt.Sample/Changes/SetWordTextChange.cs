using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;
using Crdt.Sample.Models;

namespace Crdt.Sample.Changes;

/// <summary>
/// set text is used in many tests as a simple change that either creates a word, or updates the text of a word.
/// because of this it implementes both NewEntity and ApplyChange, it's recommend to use a CreateChange for new entities and EditChange for updates.
/// </summary>
public class SetWordTextChange(Guid entityId, string text) : Change<Word>(entityId), ISelfNamedType<SetWordTextChange>
{
    public string Text { get; } = text;

    public override ValueTask<IObjectBase> NewEntity(Commit commit, ChangeContext context)
    {
        return new(new Word()
        {
            Id = EntityId,
            Text = Text
        });
    }


    public override ValueTask ApplyChange(Word entity, ChangeContext context)
    {
        entity.Text = Text;
        return ValueTask.CompletedTask;
    }
}
