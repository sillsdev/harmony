using SIL.Harmony.Entities;

namespace SIL.Harmony.Sample.Models;

public class Word : IObjectBase<Word>
{
    public required string Text { get; set; }
    public string? Note { get; set; }

    public Guid Id { get; init; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? AntonymId { get; set; }
    public Guid? ImageResourceId { get; set; }

    public Guid[] GetReferences()
    {
        return Refs().ToArray();

        IEnumerable<Guid> Refs()
        {
            if (AntonymId.HasValue) yield return AntonymId.Value;
            if (ImageResourceId.HasValue) yield return ImageResourceId.Value;
        }
    }

    public void RemoveReference(Guid id, CommitBase commit)
    {
        if (AntonymId == id) AntonymId = null;
    }

    public IObjectBase Copy()
    {
        return new Word
        {
            Id = Id,
            Text = Text,
            Note = Note,
            AntonymId = AntonymId,
            DeletedAt = DeletedAt
        };
    }
}