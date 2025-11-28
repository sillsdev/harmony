using SIL.Harmony.Entities;

namespace SIL.Harmony.Sample.Models;

public class Word : IObjectBase<Word>
{
    public required string Text { get; set; }
    public string? Note { get; set; }

    public Guid Id { get; init; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Word? Antonym { get; set; }
    public Guid? AntonymId { get; set; }
    public Guid? ImageResourceId { get; set; }
    public List<Tag> Tags { get; set; } = new();

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
        if (AntonymId == id)
        {
            AntonymId = null;
            Antonym = null;
        }
    }

    public IObjectBase Copy()
    {
        return new Word
        {
            Id = Id,
            Text = Text,
            Note = Note,
            Antonym = Antonym,
            AntonymId = AntonymId,
            DeletedAt = DeletedAt,
            ImageResourceId = ImageResourceId,
            Tags = Tags.Select(t => t.Copy()).Cast<Tag>().ToList(),
        };
    }

    public override string ToString()
    {
        return
            $"{nameof(Text)}: {Text}, {nameof(Id)}: {Id}, {nameof(Note)}: {Note}, {nameof(DeletedAt)}: {DeletedAt}, {nameof(Antonym)}: {Antonym}, {nameof(AntonymId)}: {AntonymId}, {nameof(ImageResourceId)}: {ImageResourceId}" +
            $", {nameof(Tags)}: {string.Join(", ", Tags.Select(t => t.Text))}";
    }
}
