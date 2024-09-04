namespace SIL.Harmony.Core;

public record SyncState(Dictionary<Guid, long> ClientHeads);
public interface IChangesResult
{
    IEnumerable<CommitBase> MissingFromClient { get; }
    SyncState ServerSyncState { get; }
}
public record ChangesResult<TCommit>(TCommit[] MissingFromClient, SyncState ServerSyncState): IChangesResult where TCommit : CommitBase
{
    IEnumerable<CommitBase> IChangesResult.MissingFromClient => MissingFromClient;
    public static ChangesResult<TCommit> Empty => new([], new SyncState([]));
}
