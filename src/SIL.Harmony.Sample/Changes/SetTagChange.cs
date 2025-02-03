using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Sample.Changes;

public class SetTagChange(Guid entityId, string text) : Change<Tag>(entityId), ISelfNamedType<SetTagChange>
{
    public string Text { get; } = text;

    public override ValueTask<Tag> NewEntity(Commit commit, IChangeContext context)
    {
        return new(new Tag()
        {
            Id = EntityId,
            Text = Text
        });
    }


    public override ValueTask ApplyChange(Tag entity, IChangeContext context)
    {
        entity.Text = Text;
        return ValueTask.CompletedTask;
    }
}
