namespace SIL.Harmony.Core;

public interface IChangeContext
{
    public CommitBase Commit { get; }
    ValueTask<IObjectSnapshot?> GetSnapshot(Guid entityId);
    public async ValueTask<T?> GetCurrent<T>(Guid entityId) where T : class
    {
        var snapshot = await GetSnapshot(entityId);
        if (snapshot is null) return null;
        return (T) snapshot.Entity.DbObject;
    }

    public async ValueTask<bool> IsObjectDeleted(Guid entityId) => (await GetSnapshot(entityId))?.EntityIsDeleted ?? true;
    internal IObjectBase Adapt(object obj);
    IAsyncEnumerable<object> GetObjectsReferencing(Guid entityId, bool includeDeleted = false);
    IAsyncEnumerable<T> GetObjectsOfType<T>(string jsonTypeName, bool includeDeleted = false) where T : class;
}
