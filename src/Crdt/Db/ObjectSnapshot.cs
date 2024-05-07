using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Crdt.Core;
using Crdt.Entities;
using Crdt.Helpers;

namespace Crdt.Db;

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
    public bool IsType<T>() where T : IObjectBase => TypeName == DerivedTypeHelper.GetEntityDiscriminator<T>();

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

public class ObjectSnapshot
{
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
        TypeName = entity.TypeName;
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
    public required bool IsRoot { get; init; }
}