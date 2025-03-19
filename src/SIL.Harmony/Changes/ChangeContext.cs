using SIL.Harmony.Db;

namespace SIL.Harmony.Changes;

public class ChangeContext : IChangeContext
{
    private readonly SnapshotWorker _worker;
    private readonly CrdtConfig _crdtConfig;

    internal ChangeContext(Commit commit, int commitIndex, IDictionary<Guid, ObjectSnapshot> intermediateSnapshots, SnapshotWorker worker, CrdtConfig crdtConfig)
    {
        _worker = worker;
        _crdtConfig = crdtConfig;
        Commit = commit;
        CommitIndex = commitIndex;
        IntermediateSnapshots = intermediateSnapshots;
    }

    CommitBase IChangeContext.Commit => Commit;
    public Commit Commit { get; }
    public int CommitIndex { get; }
    public IDictionary<Guid, ObjectSnapshot> IntermediateSnapshots { get; }
    public async ValueTask<IObjectSnapshot?> GetSnapshot(Guid entityId) => await _worker.GetSnapshot(entityId);
    public IAsyncEnumerable<object> GetObjectsReferencing(Guid entityId, bool includeDeleted = false)
    {
        return _worker.GetSnapshotsReferencing(entityId, includeDeleted).Select(s => s.Entity.DbObject);
    }
    IObjectBase IChangeContext.Adapt(object obj) => _crdtConfig.ObjectTypeListBuilder.Adapt(obj);
}
