namespace SIL.Harmony;

/// <summary>
/// What comes after each commit of a batch: the next commit, and the next required checkpoint.
/// </summary>
internal sealed class CommitChain
{
    /// <summary>by commit id</summary>
    private readonly Dictionary<Guid, Link> _links;

    internal CommitChain(int capacity)
    {
        _links = new Dictionary<Guid, Link>(capacity);
    }

    // a commit may already be in the map, or not yet, depending on which of its successors we learn first
    internal void SetNext(Commit commit, Commit next) =>
        _links[commit.Id] = _links.GetValueOrDefault(commit.Id) with { Next = next };

    internal void SetNextRequiredCheckpoint(Commit commit, Commit checkpoint) =>
        _links[commit.Id] = _links.GetValueOrDefault(commit.Id) with { NextRequiredCheckpoint = checkpoint };

    /// <summary>the first required checkpoint at or after <paramref name="commit"/>; none after the last one</summary>
    internal Commit? NextRequiredCheckpoint(Commit commit) =>
        _links.GetValueOrDefault(commit.Id).NextRequiredCheckpoint;

    /// <summary>the commits from <paramref name="from"/> up to, not including, <paramref name="until"/></summary>
    internal IEnumerable<Commit> Between(Commit from, Commit until)
    {
        Commit? commit = from;
        while (commit is not null && commit.Id != until.Id)
        {
            yield return commit;
            commit = _links[commit.Id].Next;
        }
    }

    /// <param name="Next">the next commit in the batch; none for the last</param>
    /// <param name="NextRequiredCheckpoint">
    /// the first required checkpoint at or after this commit; none after the last one. A required checkpoint is a commit
    /// where maxChanges changes have accumulated since the previous one.
    /// </param>
    private readonly record struct Link(Commit? Next, Commit? NextRequiredCheckpoint);
}
