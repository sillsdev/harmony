using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Db;
using SIL.Harmony.Refs.Changes;

namespace SIL.Harmony.Refs;

/// <summary>
/// Materializes commits visible for the current local checkout:
/// main line (no branch id), commits for incorporated (merged) branches on main,
/// plus commits scoped to the checked-out branch when on a branch.
/// </summary>
public sealed class CheckoutMaterializationFilter : ICommitMaterializationFilter, IMaterializationApplyWindow
{
    private readonly HashSet<Guid> _incorporatedBranchIds = [];

    public RefCheckout Checkout { get; set; } = RefCheckout.Main;

    public bool Include(Commit commit)
    {
        var branchId = RefMetadata.GetBranchId(commit.Metadata);
        if (branchId is null) return true;

        return Checkout switch
        {
            BranchCheckout branch => branchId == branch.BranchId || _incorporatedBranchIds.Contains(branchId.Value),
            _ => _incorporatedBranchIds.Contains(branchId.Value)
        };
    }

    async Task<SortedSet<Commit>> IMaterializationApplyWindow.PrepareApplyWindowAsync(
        CrdtRepository repo,
        SortedSet<Commit> commitsToApply)
    {
        _incorporatedBranchIds.Clear();
        _incorporatedBranchIds.UnionWith(await repo.GetEntityIdsForChangeType<MergeBranchChange>());

        var mergedInWindow = commitsToApply
            .SelectMany(MergesIn)
            .Select(m => m.EntityId)
            .ToHashSet();
        if (mergedInWindow.Count == 0) return commitsToApply;

        // CurrentCommits is DefaultOrder (oldest first). First hit is the window start:
        // earliest merged-branch commit, or the apply-window head if that is older.
        var applyMinId = commitsToApply.Min!.Id;
        var allCommits = await repo.CurrentCommits()
            .Include(c => c.ChangeEntities)
            .ToListAsync();

        for (var i = 0; i < allCommits.Count; i++)
        {
            var commit = allCommits[i];
            var branchId = RefMetadata.GetBranchId(commit.Metadata);
            if (commit.Id == applyMinId ||
                (branchId is Guid id && mergedInWindow.Contains(id)))
            {
                return allCommits.Skip(i).ToSortedSet();
            }
        }

        return commitsToApply;
    }

    private static IEnumerable<MergeBranchChange> MergesIn(Commit commit) =>
        commit.ChangeEntities
            .Select(ce => ce.Change)
            .OfType<MergeBranchChange>();
}
