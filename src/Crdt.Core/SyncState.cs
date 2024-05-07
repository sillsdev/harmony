namespace Crdt.Core;

public record SyncState(Dictionary<Guid, long> ClientHeads);

public record ChangesResult<TCommit>(TCommit[] MissingFromClient, SyncState ServerSyncState) where TCommit : CommitBase
{
    public static ChangesResult<TCommit> Empty => new([], new SyncState([]));
}
