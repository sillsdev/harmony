using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SIL.Harmony.Refs;

/// <summary>
/// The refs bridge into <see cref="DataModel"/>'s generic extension points, so clients can author
/// and sync through <see cref="DataModel"/> directly and still get branch/tag behaviour without the
/// <see cref="RefsDataModel"/> wrapper.
/// <list type="bullet">
/// <item>As an <see cref="ICommitInterceptor"/> it stamps the current checkout's branch assignment
/// on locally-authored commits (or rejects authoring on a tag).</item>
/// <item>As an <see cref="ICommitAppliedListener"/> it rolls an active tag checkout forward after
/// any apply — local or synced — when the tag's tip moved.</item>
/// </list>
/// <see cref="DataModel"/> is resolved lazily (only when rolling forward) because the roll-forward
/// depends on it, yet it is already constructed by the time an apply completes; requiring it in the
/// constructor would form a cycle, since this same instance is also the constructor-injected interceptor.
/// </summary>
public sealed class CheckoutRefsHandler(
    CheckoutMaterializationFilter filter,
    IOptions<CrdtConfig> config,
    IServiceProvider serviceProvider)
    : ICommitInterceptor, ICommitAppliedListener
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

    public async Task OnCommitsAppliedAsync(IReadOnlyCollection<Commit> commits)
    {
        // Roll-forward only concerns tag checkouts; a main/branch view never moves on apply.
        if (filter.Checkout is not TagCheckout tag) return;

        var dataModel = serviceProvider.GetRequiredService<DataModel>();
        var tip = await TagTipResolver.ResolveTagTip(dataModel, tag.TagId);
        // Skip rematerialization unless the tag's tip actually moved.
        if (filter.AsOfTipId == tip.Id) return;

        filter.SetAsOfTip(tip);
        await dataModel.RegenerateSnapshots();
    }
}
