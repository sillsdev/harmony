namespace SIL.Harmony;

public interface ISyncable
{
    Task AddRangeFromSync(IEnumerable<Commit> commits);
    Task<SyncState> GetSyncState();
    Task<ChangesResult<Commit>> GetChanges(SyncState otherHeads);
    Task<SyncResults> SyncWith(ISyncable remoteModel);
    Task SyncMany(ISyncable[] remotes);
    ValueTask<bool> ShouldSync();
}

public class NullSyncable : ISyncable
{
    public static readonly ISyncable Instance = new NullSyncable();

    public Task AddRangeFromSync(IEnumerable<Commit> commits)
    {
        return Task.CompletedTask;
    }

    public Task<SyncState> GetSyncState()
    {
        return Task.FromResult(new SyncState([]));
    }

    public Task<ChangesResult<Commit>> GetChanges(SyncState otherHeads)
    {
        return Task.FromResult(ChangesResult<Commit>.Empty);
    }

    public Task<SyncResults> SyncWith(ISyncable remoteModel)
    {
        return Task.FromResult(new SyncResults([], [], false));
    }

    public Task SyncMany(ISyncable[] remotes)
    {
        return Task.CompletedTask;
    }

    public ValueTask<bool> ShouldSync()
    {
        return new ValueTask<bool>(false);
    }
}
