using SIL.Harmony.Entities;

namespace SIL.Harmony.Sample.Models;

public class WordTag : IObjectBase<WordTag>
{
    public Guid Id { get; init; }
    public required Guid WordId { get; init; }
    public required Guid TagId { get; init; }
    public DateTimeOffset? DeletedAt { get; set; }

    public Guid[] GetReferences()
    {
        return [WordId, TagId];
    }

    public void RemoveReference(Guid id, CommitBase commit)
    {
        if (WordId == id || TagId == id)
        {
            DeletedAt = commit.DateTime;
        }
    }

    public IObjectBase Copy()
    {
        return new WordTag
        {
            Id = Id,
            WordId = WordId,
            TagId = TagId,
            DeletedAt = DeletedAt
        };
    }
}