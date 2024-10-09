using SIL.Harmony.Db;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Changes;

public class ChangeContext
{
    private readonly SnapshotWorker _worker;
    private readonly CrdtConfig _crdtConfig;

    internal ChangeContext(Commit commit, SnapshotWorker worker, CrdtConfig crdtConfig)
    {
        _worker = worker;
        _crdtConfig = crdtConfig;
        Commit = commit;
    }

    public Commit Commit { get; }
    public async ValueTask<ObjectSnapshot?> GetSnapshot(Guid entityId) => await _worker.GetSnapshot(entityId);

    public async ValueTask<bool> IsObjectDeleted(Guid entityId) => (await GetSnapshot(entityId))?.EntityIsDeleted ?? true;
    public IObjectBase Adapt(object obj) => _crdtConfig.ObjectTypeListBuilder.AdapterProvider.Adapt(obj);
}
