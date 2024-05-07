using Crdt.Changes;
using Crdt.Core;
using Crdt.Entities;
using Crdt.Sample.Models;

namespace Crdt.Sample.Changes;

public class SetWordTextChange(Guid entityId, string text) : Change<Word>(entityId), ISelfNamedType<SetWordTextChange>
{
    public string Text { get; } = text;

    public override IObjectBase NewEntity(Commit commit)
    {
        return new Word()
        {
            Id = EntityId,
            Text = Text
        };
    }

    public override async ValueTask ApplyChange(Word entity, ChangeContext context)
    {
        entity.Text = Text;
    }
}