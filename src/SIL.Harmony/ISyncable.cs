namespace SIL.Harmony;

public interface ISyncable
{
    /// <returns>The commits this syncable didn't already have. One that can't tell returns all it was given.</returns>
    Task<Commit[]> AddRangeFromSync(IEnumerable<Commit> commits);
    Task<SyncState> GetSyncState();
    Task<ChangesResult<Commit>> GetChanges(SyncState otherHeads);
    Task<SyncResults> SyncWith(ISyncable remoteModel);
    Task SyncMany(ISyncable[] remotes);
    ValueTask<bool> ShouldSync();
}

public class NullSyncable : ISyncable
{
    public static readonly ISyncable Instance = new NullSyncable();

    public Task<Commit[]> AddRangeFromSync(IEnumerable<Commit> commits)
    {
        return Task.FromResult<Commit[]>([]);
    }

    public Task<SyncState> GetSyncState()
    {
        return Task.FromResult(new SyncState([], []));
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
