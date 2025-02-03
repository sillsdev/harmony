namespace SIL.Harmony.Changes;

public class ChangeContext : IChangeContext
{
    private readonly SnapshotWorker _worker;
    private readonly CrdtConfig _crdtConfig;

    internal ChangeContext(Commit commit, SnapshotWorker worker, CrdtConfig crdtConfig)
    {
        _worker = worker;
        _crdtConfig = crdtConfig;
        Commit = commit;
    }

    public CommitBase Commit { get; }
    public async ValueTask<IObjectSnapshot?> GetSnapshot(Guid entityId) => await _worker.GetSnapshot(entityId);
    public IAsyncEnumerable<object> GetObjectsReferencing(Guid entityId, bool includeDeleted = false)
    {
        return _worker.GetSnapshotsReferencing(entityId, includeDeleted).Select(s => s.Entity.DbObject);
    }
    public async ValueTask<T?> GetCurrent<T>(Guid entityId) where T : class
    {
        var snapshot = await GetSnapshot(entityId);
        if (snapshot is null) return null;
        return (T) snapshot.Entity.DbObject;
    }

    public async ValueTask<bool> IsObjectDeleted(Guid entityId) => (await GetSnapshot(entityId))?.EntityIsDeleted ?? true;
    IObjectBase IChangeContext.Adapt(object obj) => _crdtConfig.ObjectTypeListBuilder.Adapt(obj);
}
