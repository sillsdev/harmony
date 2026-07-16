using Microsoft.Extensions.Options;

namespace SIL.Harmony.Refs;

/// <summary>
/// Applies the current checkout's branch assignment to locally-authored commits so that
/// clients can author through <see cref="DataModel.AddChange"/> directly without routing
/// through <see cref="RefsDataModel"/>. Commits that already carry an explicit assignment
/// are left untouched.
/// </summary>
public sealed class CheckoutCommitInterceptor(CheckoutMaterializationFilter filter, IOptions<CrdtConfig> config)
    : ICommitInterceptor
{
    public void OnCommitAuthored(Commit commit)
    {
        // Explicit per-call override or a ref-lifecycle assignment already decided this commit.
        // ConsumeAssignment also clears the transient marker so it is never persisted or synced.
        if (RefMetadata.ConsumeAssignment(commit.Metadata)) return;

        switch (filter.Checkout)
        {
            case BranchCheckout branch:
                RefMetadata.SetBranchId(commit.Metadata, branch.BranchId);
                break;
            case TagCheckout when !config.Value.AllowAuthoringOnTagToMain:
                throw new InvalidOperationException(
                    "Authoring is not allowed while checked out on a tag. Set AllowAuthoringOnTagToMain to write to main, "
                    + "author with an explicit BranchAssignment, or checkout main/branch first.");
            // MainCheckout, or a tag checkout with AllowAuthoringOnTagToMain: author to main (no branch id).
        }
    }
}
