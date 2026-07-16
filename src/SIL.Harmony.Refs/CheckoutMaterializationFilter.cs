namespace SIL.Harmony.Refs;

/// <summary>
/// Materializes commits visible for the current local checkout:
/// main line (no branch id), plus commits scoped to the checked-out branch when on a branch.
/// </summary>
public sealed class CheckoutMaterializationFilter : ICommitMaterializationFilter
{
    public RefCheckout Checkout { get; set; } = RefCheckout.Main;

    public bool Include(Commit commit)
    {
        var branchId = RefMetadata.GetBranchId(commit.Metadata);
        return Checkout switch
        {
            BranchCheckout branch => branchId is null || branchId == branch.BranchId,
            _ => branchId is null
        };
    }
}
