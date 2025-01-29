using SIL.Harmony.Entities;

namespace SIL.Harmony.Sample.Models;

public class Tag : IObjectBase<Tag>
{
    public required string Text { get; set; }

    public Guid Id { get; init; }
    public DateTimeOffset? DeletedAt { get; set; }

    public Guid[] GetReferences()
    {
        return [];
    }

    public void RemoveReference(Guid id, Commit commit)
    {
    }

    public IObjectBase Copy()
    {
        return new Tag
        {
            Id = Id,
            Text = Text,
            DeletedAt = DeletedAt
        };
    }
}