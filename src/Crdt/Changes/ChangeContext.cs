using Crdt.Db;

namespace Crdt.Changes;

public class ChangeContext(Commit commit, SnapshotWorker worker, CrdtRepository crdtRepository)
{
    public Commit Commit { get; } = commit;
    public async ValueTask<ObjectSnapshot?> GetSnapshot(Guid entityId) => await worker.GetSnapshot(entityId);

    public async ValueTask<bool> IsObjectDeleted(Guid entityId) => (await GetSnapshot(entityId))?.EntityIsDeleted ?? true;
}