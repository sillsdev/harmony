using SIL.Harmony.Db;

namespace SIL.Harmony;

/// <summary>
/// Optional materialization filter hook to expand the commit apply window
/// (e.g. when a merge makes earlier branch-scoped commits newly visible).
/// </summary>
internal interface IMaterializationApplyWindow
{
    Task<SortedSet<Commit>> PrepareApplyWindowAsync(CrdtRepository repo, SortedSet<Commit> commitsToApply);
}
