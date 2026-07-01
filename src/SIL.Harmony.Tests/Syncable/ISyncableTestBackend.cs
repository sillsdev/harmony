namespace SIL.Harmony.Tests.Syncable;

public sealed class SyncableTestContext(
    ISyncable syncable,
    DataModel readModel,
    Guid clientId,
    bool syncableUpdatesReadModel,
    Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    public ISyncable Syncable { get; } = syncable;
    public DataModel ReadModel { get; } = readModel;
    public Guid ClientId { get; } = clientId;
    internal bool SyncableUpdatesReadModel { get; } = syncableUpdatesReadModel;

    public ValueTask DisposeAsync() => disposeAsync();
}

/// <summary>Factory for a backend under test (DataModel, JsonSyncable, ...).</summary>
public interface ISyncableTestBackend
{
    string Name { get; }
    Task<SyncableTestContext> CreateAsync();
}
