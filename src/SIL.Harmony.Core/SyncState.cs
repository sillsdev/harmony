using System.IO.Hashing;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SIL.Harmony.Core;

[JsonConverter(typeof(SyncState.SyncStateConverter))]
public record SyncState(Dictionary<Guid, long> ClientHeads, ClientState[]? ClientStates = null)
{
    public SyncState(Dictionary<Guid, long> clientHeads) : this(clientHeads, clientHeads.Select(c => new ClientState(c.Key, c.Value, 0, 0)).ToArray())
    {

    }
    public SyncState(ClientState[] clientStates) : this(clientStates.ToDictionary(k => k.ClientId, v => v.MaxTimestamp), clientStates)
    {
    }
    public ClientState[] ClientStates { get; } = ClientStates ?? [];
    public ClientState? GetClientState(Guid clientId) => ClientStates?.FirstOrDefault(cs => cs.ClientId == clientId);
    public SyncState Without(IReadOnlySet<Guid> clientIds) => new(ClientStates.Where(cs => !clientIds.Contains(cs.ClientId)).ToArray());

    public class SyncStateConverter : JsonConverter<SyncState>
    {
        public override SyncState? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var jsonObject = JsonSerializer.Deserialize<JsonObject>(ref reader, options);
            if (jsonObject == null)
                return null;
            var clientHeads = jsonObject[options.PropertyNamingPolicy?.ConvertName("ClientHeads") ?? "ClientHeads"]?.Deserialize<Dictionary<Guid, long>>(options);
            var clientStates = jsonObject[options.PropertyNamingPolicy?.ConvertName("ClientStates") ?? "ClientStates"]?.Deserialize<ClientState[]>(options);
            return (clientHeads, clientStates) switch
            {
                (null, null) => null,
                (var h, null) => new SyncState(h),
                (null, var s) => new SyncState(s),
                (_, _) => new SyncState(clientHeads, clientStates)
            };
        }

        public override void Write(Utf8JsonWriter writer, SyncState value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("ClientHeads") ?? "ClientHeads");
            JsonSerializer.Serialize(writer, value.ClientHeads, options);
            writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("ClientStates") ?? "ClientStates");
            JsonSerializer.Serialize(writer, value.ClientStates, options);
            writer.WriteEndObject();
        }
    }
}
public record ClientState(Guid ClientId, long MaxTimestamp, int CommitCount, ulong Hash)
{
    //0 means this came from a ClientHeads-only SyncState (a peer on an older version);
    //states we build always have at least one commit
    public bool OnlyHasTimestamp => CommitCount == 0;

    //the count and XOR hash identify the commit set (the timestamp follows from it) and give the state
    //of a union from the states of its disjoint parts
    public ClientState Plus(ClientState? other) => other is null ? this : this with
    {
        MaxTimestamp = Math.Max(MaxTimestamp, other.MaxTimestamp),
        CommitCount = CommitCount + other.CommitCount,
        Hash = Hash ^ other.Hash
    };

    public bool HasSameCommits(ClientState other) => CommitCount == other.CommitCount && Hash == other.Hash;
}

public class ClientStateBuilder
{
    public Guid ClientId { get; init; }
    private readonly byte[] _commitIdBytes = new byte[16];
    private long _timestamp;
    private int _count;
    private ulong _hash;

    public void Add(Guid commitId, DateTimeOffset dateTime)
    {
        _count++;
        _timestamp = Math.Max(_timestamp, dateTime.ToUnixTimeMilliseconds());
        if (!commitId.TryWriteBytes(_commitIdBytes))
            throw new InvalidOperationException("Commit ID is too large to fit in a 16-byte buffer.");
        //XOR keeps the hash independent of the order commits arrive in, so backends that sort
        //differently still agree. Only safe because commit IDs are unique within a client.
        _hash ^= XxHash3.HashToUInt64(_commitIdBytes);
    }

    public ClientState Build() => new(ClientId, _timestamp, _count, _hash);
}

public interface IChangesResult
{
    IEnumerable<CommitBase> MissingFromClient { get; }
    SyncState ServerSyncState { get; }
}

public record ChangesResult<TCommit>(TCommit[] MissingFromClient, SyncState ServerSyncState) : IChangesResult where TCommit : CommitBase
{
    IEnumerable<CommitBase> IChangesResult.MissingFromClient => MissingFromClient;
    public static ChangesResult<TCommit> Empty => new([], new SyncState([], []));
}
