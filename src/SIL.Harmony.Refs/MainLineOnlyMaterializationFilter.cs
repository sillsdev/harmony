namespace SIL.Harmony.Refs;

/// <summary>
/// Materializes only main-line commits (no <see cref="RefMetadata.BranchIdKey"/>).
/// Used as the <see cref="CrdtConfig"/> fallback when checkout-aware DI is not registered.
/// Prefer <see cref="CheckoutMaterializationFilter"/> via <c>AddHarmonyRefsDataModel</c> for branch views.
/// </summary>
public sealed class MainLineOnlyMaterializationFilter : ICommitMaterializationFilter
{
    public static MainLineOnlyMaterializationFilter Instance { get; } = new();

    public bool Include(Commit commit) =>
        RefMetadata.GetBranchId(commit.Metadata) is null;
}
