using SIL.Harmony.Db;

namespace SIL.Harmony.Changes;

internal class ChangeContext : IChangeContext
{
    private readonly SnapshotWorker _worker;
    private readonly CrdtConfig _crdtConfig;

    internal ChangeContext(Commit commit, SnapshotWorker worker, CrdtConfig crdtConfig)
    {
        _worker = worker;
        _crdtConfig = crdtConfig;
        Commit = commit;
    }

    CommitBase IChangeContext.Commit => Commit;
    public Commit Commit { get; }
    public async ValueTask<IObjectSnapshot?> GetSnapshot(Guid entityId) => await _worker.GetSnapshot(entityId);
    public IAsyncEnumerable<object> GetObjectsReferencing(Guid entityId, bool includeDeleted = false)
    {
        return _worker.GetSnapshotsReferencing(entityId, includeDeleted).Select(s => s.Entity.DbObject);
    }
    IObjectBase IChangeContext.Adapt(object obj) => _crdtConfig.ObjectTypeListBuilder.Adapt(obj);
}
