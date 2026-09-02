namespace SIL.Harmony.Db;

public enum ProjectedChangeKind
{
    Upsert,
    Delete
}

public interface IProjectedEntityInterceptor
{
    ValueTask OnProjectedEntitiesChanged(ProjectedEntityBatch batch);
}

public sealed class ProjectedEntityBatch
{
    public required ICrdtDbContext DbContext { get; init; }
    public required IReadOnlyList<ProjectedEntityChange> Changes { get; init; }
}

public sealed class ProjectedEntityChange
{
    public required object Entity { get; init; }
    public required Guid EntityId { get; init; }
    public required Type ClrType { get; init; }
    public required ProjectedChangeKind Kind { get; init; }
    public required ObjectSnapshot Snapshot { get; init; }
}
