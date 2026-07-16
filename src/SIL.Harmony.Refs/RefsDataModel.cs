using Microsoft.Extensions.Options;
using SIL.Harmony.Refs.Changes;
using SIL.Harmony.Refs.Entities;

namespace SIL.Harmony.Refs;

/// <summary>
/// Checkout switching and ref lifecycle over <see cref="DataModel"/>.
/// Authoring and sync go through <see cref="DataModel"/> directly; the refs handler stamps
/// branch assignment on commit and rolls tag checkouts forward after apply.
/// Checkout state lives on <see cref="CheckoutMaterializationFilter"/> (single source of truth);
/// changing it rematerializes snapshots for the new view.
/// </summary>
public class RefsDataModel(DataModel dataModel, CheckoutMaterializationFilter filter, IOptions<CrdtConfig> config)
{
    public DataModel DataModel { get; } = dataModel;

    public RefCheckout Checkout => filter.Checkout;

    /// <summary>
    /// When true, authoring on a tag checkout writes to main. Default is false (rejected).
    /// Configured via <see cref="CrdtConfig.AllowAuthoringOnTagToMain"/>.
    /// </summary>
    public bool AllowAuthoringOnTagToMain => config.Value.AllowAuthoringOnTagToMain;

    public async Task CheckoutMain()
    {
        if (Checkout is MainCheckout && !filter.HasAsOfTip) return;
        filter.ClearAsOfTip();
        filter.Checkout = RefCheckout.Main;
        await DataModel.RegenerateSnapshots();
    }

    public async Task CheckoutBranch(Guid branchId)
    {
        if (Checkout is BranchCheckout current && current.BranchId == branchId && !filter.HasAsOfTip)
            return;

        filter.ClearAsOfTip();
        filter.Checkout = RefCheckout.ForBranch(branchId);
        await DataModel.RegenerateSnapshots();
    }

    public async Task CheckoutTag(Guid tagId)
    {
        var tip = await TagTipResolver.ResolveTagTip(DataModel, tagId);
        filter.Checkout = RefCheckout.ForTag(tagId);
        filter.SetAsOfTip(tip);
        await DataModel.RegenerateSnapshots();
    }

    public Task<Commit> CreateBranch(Guid clientId, Guid branchId, string name) =>
        DataModel.AddChange(clientId, new CreateBranchChange(branchId, name), RefMetadata.SetAssignment(new(), BranchAssignment.Main));

    public Task<Commit> CreateTag(Guid clientId, Guid tagId, string name, Guid targetCommitId) =>
        DataModel.AddChange(clientId, new CreateTagChange(tagId, name, targetCommitId),
            RefMetadata.SetAssignment(new(), BranchAssignment.Main));

    public Task<Commit> MoveTag(Guid clientId, Guid tagId, Guid targetCommitId) =>
        DataModel.AddChange(clientId, new MoveTagChange(tagId, targetCommitId),
            RefMetadata.SetAssignment(new(), BranchAssignment.Main));

    public IAsyncEnumerable<Branch> ListBranches() => DataModel.QueryLatest<Branch>();

    public IAsyncEnumerable<Tag> ListTags() => DataModel.QueryLatest<Tag>();

    /// <summary>
    /// Incorporates <paramref name="branchId"/> into main visibility and deletes the branch entity.
    /// Authored as a main-line commit; materialization expands from the earliest branch commit.
    /// </summary>
    public async Task<Commit> MergeBranch(Guid clientId, Guid branchId)
    {
        await CheckoutMain();
        return await DataModel.AddChange(clientId, new MergeBranchChange(branchId),
            RefMetadata.SetAssignment(new(), BranchAssignment.Main));
    }
}
