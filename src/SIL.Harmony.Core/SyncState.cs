using System.IO.Hashing;

namespace SIL.Harmony.Core;

public record SyncState(Dictionary<Guid, long> ClientHeads, ClientState[]? ClientStates = null)
{
    public SyncState(ClientState[] clientStates) : this(clientStates.ToDictionary(k => k.ClientId, v => v.MaxTimestamp), clientStates)
    {
    }
    public ClientState[] ClientStates { get; } = ClientStates ?? [];
}
public record ClientState(Guid ClientId, long MaxTimestamp, int CommitCount, ulong Hash);

public class ClientStateBuilder
{
    public Guid ClientId;
    public long Timestamp;
    public int Count;
    public XxHash3 Hash = new();

    public ClientState Build()
    {
        return new ClientState(ClientId, Timestamp, Count, Hash.GetCurrentHashAsUInt64());
    }
}

public interface IChangesResult
{
    IEnumerable<CommitBase> MissingFromClient { get; }
    SyncState ServerSyncState { get; }
}
public record ChangesResult<TCommit>(TCommit[] MissingFromClient, SyncState ServerSyncState) : IChangesResult where TCommit : CommitBase
{
    IEnumerable<CommitBase> IChangesResult.MissingFromClient => MissingFromClient;
    public static ChangesResult<TCommit> Empty => new([], new SyncState([]));
}
