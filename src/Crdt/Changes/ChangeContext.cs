using Crdt.Db;

namespace Crdt.Changes;

public class ChangeContext
{
    private readonly SnapshotWorker _worker;

    internal ChangeContext(Commit commit, SnapshotWorker worker)
    {
        _worker = worker;
        Commit = commit;
    }

    public Commit Commit { get; }
    public async ValueTask<ObjectSnapshot?> GetSnapshot(Guid entityId) => await _worker.GetSnapshot(entityId);

    public async ValueTask<bool> IsObjectDeleted(Guid entityId) => (await GetSnapshot(entityId))?.EntityIsDeleted ?? true;
}
