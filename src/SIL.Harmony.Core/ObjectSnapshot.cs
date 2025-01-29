using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SIL.Harmony.Core;

public class ObjectSnapshot
{
    public static ObjectSnapshot ForTesting(CommitBase commit)
    {
        return new ObjectSnapshot
        {
            Commit = commit,
            Entity = null!,
            Id = Guid.Empty,
            References = [],
            CommitId = commit.Id,
            EntityId = Guid.Empty,
            IsRoot = false,
            TypeName = "Test",
            EntityIsDeleted = false
        };
    }
    //determines column name used in projected object tables, changing this will require a migration
    public const string ShadowRefName = "SnapshotId";
    [JsonConstructor]
    protected ObjectSnapshot()
    {
    }

    [SetsRequiredMembers]
    public ObjectSnapshot(IObjectBase entity, CommitBase commit, bool isRoot) : this()
    {
        Id = Guid.NewGuid();
        Entity = entity;
        References = entity.GetReferences();
        EntityId = entity.Id;
        EntityIsDeleted = entity.DeletedAt.HasValue;
        TypeName = entity.GetObjectTypeName();
        CommitId = commit.Id;
        Commit = commit;
        IsRoot = isRoot;
    }

    public required Guid Id { get; init; }
    public required string TypeName { get; init; }
    public required IObjectBase Entity { get; init; }
    public required Guid[] References { get; init; }
    public required Guid EntityId { get; init; }
    public required bool EntityIsDeleted { get; init; }
    public required Guid CommitId { get; init; }
    public required CommitBase Commit { get; init; }
    public required bool IsRoot { get; init; }
}
