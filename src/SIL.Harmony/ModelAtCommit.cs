using SIL.Harmony.Config;
using SIL.Harmony.Db;

namespace SIL.Harmony;

/// <summary>
/// The model as it was at <see cref="Commit"/>, opened by <see cref="DataModel.OpenAtCommit(Guid)"/>; history is replayed
/// once on open, so it suits reading several entities at the same point. Holds a database connection until disposed.
/// </summary>
public sealed class ModelAtCommit : IAsyncDisposable
{
    private readonly CrdtRepository _repo;
    private readonly ISnapshotView _view;
    private readonly HarmonyConfig _config;
    private bool _disposed;

    internal ModelAtCommit(CrdtRepository repo, ISnapshotView view, Commit commit, HarmonyConfig config)
    {
        _repo = repo;
        _view = view;
        _config = config;
        Commit = commit;
    }

    /// <summary>untracked, and its <see cref="CommitBase{TChange}.ChangeEntities"/> aren't loaded</summary>
    public Commit Commit { get; }

    /// <summary>
    /// null when the entity doesn't exist at <see cref="Commit"/>. <typeparamref name="T"/> must be a registered object type;
    /// without one at compile time, use <see cref="GetSnapshot"/>.
    /// </summary>
    public async Task<T?> Get<T>(Guid entityId) where T : class
    {
        if (!_config.ObjectTypes.Contains(typeof(T)))
            throw new InvalidOperationException($"{typeof(T).Name} is not a registered object type");
        var snapshot = await GetSnapshot(entityId);
        return (T?)snapshot?.Entity.DbObject;
    }

    public async Task<ObjectSnapshot?> GetSnapshot(Guid entityId) => await Open().GetAsync(entityId);

    /// <summary>snapshots whose entity holds a reference to <paramref name="entityId"/></summary>
    public IAsyncEnumerable<ObjectSnapshot> WhereReferences(Guid entityId, bool includeDeleted = false) =>
        Open().WhereReferences(entityId, includeDeleted);

    private ISnapshotView Open()
    {
        //the replayed part lives in memory and would keep answering after the connection is gone
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _view;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _repo.DisposeAsync();
    }
}
