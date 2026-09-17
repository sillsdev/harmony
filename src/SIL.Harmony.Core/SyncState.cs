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

    public class SyncStateConverter : JsonConverter<SyncState>
    {
        public override SyncState? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var jsonObject = JsonSerializer.Deserialize<JsonObject>(ref reader, options);
            if (jsonObject == null)
                return null;
            var clientHeads = jsonObject["ClientHeads"]?.Deserialize<Dictionary<Guid, long>>(options);
            var clientStates = jsonObject["ClientStates"]?.Deserialize<ClientState[]>(options);
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
            writer.WritePropertyName("ClientHeads");
            JsonSerializer.Serialize(writer, value.ClientHeads, options);
            writer.WritePropertyName("ClientStates");
            JsonSerializer.Serialize(writer, value.ClientStates, options);
            writer.WriteEndObject();
        }
    }
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
    public static ChangesResult<TCommit> Empty => new([], new SyncState([], []));
}
