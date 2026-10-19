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

    /// <summary>
    /// The entity as it was projected before this batch was applied: null when it didn't exist then or was deleted.
    /// It's the state before the batch, not where the replay resumed from, so a commit that arrives out of order and
    /// rewinds history still sees what the projected tables held. Never an intermediate state from within the batch.
    /// </summary>
    public object? Previous { get; init; }
}
