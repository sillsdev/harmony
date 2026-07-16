namespace SIL.Harmony;

/// <summary>
/// Decides which commits are applied when materializing snapshots.
/// Commits are still stored and synced regardless of this filter.
/// </summary>
public interface ICommitMaterializationFilter
{
    bool Include(Commit commit);
}

public sealed class IncludeAllCommitsFilter : ICommitMaterializationFilter
{
    public static IncludeAllCommitsFilter Instance { get; } = new();

    public bool Include(Commit commit) => true;
}
