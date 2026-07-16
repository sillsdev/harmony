using SIL.Harmony.Entities;

namespace SIL.Harmony.Refs.Entities;

/// <summary>
/// A named line of work. Display <see cref="Name"/> is not unique; identity is <see cref="Id"/>.
/// </summary>
public class Branch : IObjectBase<Branch>
{
    public Guid Id { get; init; }
    public required string Name { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public Guid[] GetReferences() => [];

    public void RemoveReference(Guid id, CommitBase commit)
    {
    }

    public IObjectBase Copy() => new Branch
    {
        Id = Id,
        Name = Name,
        DeletedAt = DeletedAt
    };
}
