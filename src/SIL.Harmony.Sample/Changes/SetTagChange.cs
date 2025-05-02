using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Sample.Changes;

public class SetTagChange(Guid entityId, string text) : Change<Tag>(entityId), ISelfNamedType<SetTagChange>
{
    public string Text { get; } = text;

    public override async ValueTask<Tag> NewEntity(Commit commit, IChangeContext context)
    {
        var tagExists = await context.GetObjectsOfType<Tag>(nameof(Tag)).AnyAsync(t => t.Text == Text);
        return new Tag()
        {
            Id = EntityId,
            Text = Text,
            DeletedAt = tagExists ?  commit.DateTime : null
        };
    }


    public override async ValueTask ApplyChange(Tag entity, IChangeContext context)
    {
        if (entity.Text == Text) return;
        var tagExists = await context.GetObjectsOfType<Tag>(nameof(Tag)).AnyAsync(t => t.Id != EntityId && t.Text == Text);
        if (tagExists)
        {
            entity.DeletedAt = context.Commit.DateTime;
        }
        entity.Text = Text;
    }
}
