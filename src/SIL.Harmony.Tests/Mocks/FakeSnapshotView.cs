using System.Linq.Expressions;
using SIL.Harmony.Db;

namespace SIL.Harmony.Tests.Mocks;

/// <summary>an in-memory baseline: one snapshot per entity, no database</summary>
internal sealed class FakeSnapshotView(params ObjectSnapshot[] snapshots) : ISnapshotView
{
    private readonly Dictionary<Guid, ObjectSnapshot> _snapshots = snapshots.ToDictionary(s => s.EntityId);

    public ValueTask<ObjectSnapshot?> GetAsync(Guid entityId) => ValueTask.FromResult(_snapshots.GetValueOrDefault(entityId));

    public IAsyncEnumerable<ObjectSnapshot> Where(Expression<Func<ObjectSnapshot, bool>> predicate) =>
        _snapshots.Values.Where(predicate.Compile()).ToAsyncEnumerable();

    public IAsyncEnumerable<ObjectSnapshot> All() => _snapshots.Values.ToAsyncEnumerable();

    public Task PreloadAsync(IReadOnlyCollection<Guid> entityIds) => Task.CompletedTask;

    public Task PreloadAllAsync() => Task.CompletedTask;
}
