using SIL.Harmony.Core;
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
        snapshot.Commit.Hash, // PROBLEM: ObjectSnapshot.Commit is now a CommitBase which doesn't have a Hash property
        snapshot.EntityIsDeleted)
    {
    }
}
