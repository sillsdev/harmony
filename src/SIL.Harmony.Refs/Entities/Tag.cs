using SIL.Harmony.Entities;

namespace SIL.Harmony.Refs.Entities;

/// <summary>
/// A named movable pointer to a commit. Display <see cref="Name"/> is not unique; identity is <see cref="Id"/>.
/// Polymorphic type name is <c>harmony:tag</c> to avoid colliding with app-domain tag types.
/// </summary>
public class Tag : IObjectBase<Tag>
{
    static string IPolyType.TypeName => "harmony:tag";

    public Guid Id { get; init; }
    public required string Name { get; set; }
    public Guid TargetCommitId { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public Guid[] GetReferences() => [];

    public void RemoveReference(Guid id, CommitBase commit)
    {
    }

    public IObjectBase Copy() => new Tag
    {
        Id = Id,
        Name = Name,
        TargetCommitId = TargetCommitId,
        DeletedAt = DeletedAt
    };
}
