using SIL.Harmony.Config;
using SIL.Harmony.Db;

namespace SIL.Harmony.Changes;

internal class ChangeContext : IChangeContext
{
    private readonly ISnapshotView _snapshots;
    private readonly HarmonyConfig _crdtConfig;

    internal ChangeContext(Commit commit, int batchCommitIndex, ISnapshotView snapshots, HarmonyConfig crdtConfig)
    {
        _snapshots = snapshots;
        _crdtConfig = crdtConfig;
        Commit = commit;
        BatchCommitIndex = batchCommitIndex;
    }

    CommitBase IChangeContext.Commit => Commit;
    public Commit Commit { get; }
    /// <summary>the commit's zero-based position in the batch being replayed</summary>
    public int BatchCommitIndex { get; }
    public async ValueTask<IObjectSnapshot?> GetSnapshot(Guid entityId) => await _snapshots.GetAsync(entityId);
    public IAsyncEnumerable<object> GetObjectsReferencing(Guid entityId, bool includeDeleted = false)
    {
        return _snapshots.Where(s => (includeDeleted || !s.EntityIsDeleted) && s.References.Contains(entityId))
            .Select(s => s.Entity.DbObject);
    }

    public IAsyncEnumerable<T> GetObjectsOfType<T>(string jsonTypeName, bool includeDeleted = false) where T : class
    {
        return _snapshots.Where(s => (includeDeleted || !s.EntityIsDeleted) && s.TypeName == jsonTypeName)
            .Select(s => s.Entity.DbObject)
            .OfType<T>();
    }

    IObjectBase IChangeContext.Adapt(object obj) => _crdtConfig.ObjectTypeListBuilder.Adapt(obj);
}
