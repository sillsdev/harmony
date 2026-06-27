namespace SIL.Harmony;

public interface ISyncable
{
    Task AddRangeFromSync(IEnumerable<Commit> commits, IProgress<HarmonyProgress>? progress = null);
    Task<SyncState> GetSyncState();
    Task<ChangesResult<Commit>> GetChanges(SyncState otherHeads);
    Task<SyncResults> SyncWith(ISyncable remoteModel, IProgress<HarmonyProgress>? progress = null);
    Task SyncMany(ISyncable[] remotes, IProgress<HarmonyProgress>? progress = null);
    ValueTask<bool> ShouldSync();
}

public class NullSyncable : ISyncable
{
    public static readonly ISyncable Instance = new NullSyncable();

    public Task AddRangeFromSync(IEnumerable<Commit> commits, IProgress<HarmonyProgress>? progress = null)
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

    public Task<SyncResults> SyncWith(ISyncable remoteModel, IProgress<HarmonyProgress>? progress = null)
    {
        return Task.FromResult(new SyncResults([], [], false));
    }

    public Task SyncMany(ISyncable[] remotes, IProgress<HarmonyProgress>? progress = null)
    {
        return Task.CompletedTask;
    }

    public ValueTask<bool> ShouldSync()
    {
        return new ValueTask<bool>(false);
    }
}
