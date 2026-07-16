namespace SIL.Harmony.Refs;

/// <summary>
/// Materializes only main-line commits (no <see cref="RefMetadata.BranchIdKey"/>).
/// Branch-scoped commits remain stored and synced; they become queryable after merge (later) or on a branch checkout view (ticket 11).
/// </summary>
public sealed class MainLineOnlyMaterializationFilter : ICommitMaterializationFilter
{
    public static MainLineOnlyMaterializationFilter Instance { get; } = new();

    public bool Include(Commit commit) =>
        RefMetadata.GetBranchId(commit.Metadata) is null;
}
