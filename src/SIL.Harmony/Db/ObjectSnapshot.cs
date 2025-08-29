using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using SIL.Harmony.Entities;
using SIL.Harmony.Helpers;

namespace SIL.Harmony.Db;

public record SimpleSnapshot(
    Guid Id,
    string TypeName,
    Guid EntityId,
    Guid CommitId,
    bool IsRoot,
    HybridDateTime HybridDateTime,
    string CommitHash,
    bool EntityIsDeleted)
{
    public bool IsType<T>() where T : IObjectBase, IPolyType => TypeName == DerivedTypeHelper.GetEntityDiscriminator<T>();

    public SimpleSnapshot(ObjectSnapshot snapshot) : this(snapshot.Id,
        snapshot.TypeName,
        snapshot.EntityId,
        snapshot.CommitId,
        snapshot.IsRoot,
        snapshot.Commit.HybridDateTime,
        snapshot.Commit.Hash,
        snapshot.EntityIsDeleted)
    {
    }
}

public class ObjectSnapshot : IObjectSnapshot
{
    public static ObjectSnapshot ForTesting(Commit commit)
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
    public ObjectSnapshot(IObjectBase entity, Commit commit, bool isRoot) : this()
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
    public required Commit Commit { get; init; }
    CommitBase IObjectSnapshot.Commit => Commit;
    public required bool IsRoot { get; init; }

    public override string ToString()
    {
        return $"{Id} [{CommitId}] {TypeName} {EntityId} Deleted:{EntityIsDeleted}, Entity: {Entity}";
    }
}
