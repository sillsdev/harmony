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
        var allCommits = await repo.CurrentCommits()
            .Include(c => c.ChangeEntities)
            .AsNoTracking()
            .ToSortedSetAsync();

        _incorporatedBranchIds.Clear();
        foreach (var commit in allCommits)
        {
            foreach (var merge in MergesIn(commit))
                _incorporatedBranchIds.Add(merge.EntityId);
        }

        var mergedInWindow = commitsToApply
            .SelectMany(MergesIn)
            .Select(m => m.EntityId)
            .ToHashSet();
        if (mergedInWindow.Count == 0) return commitsToApply;

        Commit? oldest = commitsToApply.MinBy(c => c.CompareKey);
        foreach (var commit in allCommits)
        {
            var branchId = RefMetadata.GetBranchId(commit.Metadata);
            if (branchId is Guid id && mergedInWindow.Contains(id) &&
                (oldest is null || commit.CompareKey.CompareTo(oldest.CompareKey) < 0))
            {
                oldest = commit;
            }
        }

        if (oldest is null) return commitsToApply;

        var parent = await repo.FindPreviousCommit(oldest);
        return (await repo.GetCommitsAfter(parent)).ToSortedSet();
    }

    private static IEnumerable<MergeBranchChange> MergesIn(Commit commit) =>
        commit.ChangeEntities
            .Select(ce => ce.Change)
            .OfType<MergeBranchChange>();
}
