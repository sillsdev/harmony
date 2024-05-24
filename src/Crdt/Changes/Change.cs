using System.Text.Json.Serialization;
using Crdt.Core;
using Crdt.Entities;

namespace Crdt.Changes;

[JsonPolymorphic]
public interface IChange
{
    [JsonIgnore]
    Guid CommitId { get; set; }

    [JsonIgnore]
    Guid EntityId { get; set; }

    [JsonIgnore]
    Type EntityType { get; }

    ValueTask ApplyChange(IObjectBase entity, ChangeContext context);
    ValueTask<IObjectBase> NewEntity(Commit commit, ChangeContext context);
}

/// <summary>
/// a change that can be applied to an entity, recommend inheriting from CreateChange or EditChange
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract class Change<T> : IChange where T : IObjectBase
{
    protected Change(Guid entityId)
    {
        EntityId = entityId;
    }

    [JsonIgnore]
    public Guid CommitId { get; set; }

    public Guid EntityId { get; set; }

    public abstract ValueTask<IObjectBase> NewEntity(Commit commit, ChangeContext context);
    public abstract ValueTask ApplyChange(T entity, ChangeContext context);

    public async ValueTask ApplyChange(IObjectBase entity, ChangeContext context)
    {
        if (this is CreateChange<T>) return; // skip attempting to apply changes on CreateChange as it does not support apply changes
        if (entity is T entityT) await ApplyChange(entityT, context);
    }

    [JsonIgnore]
    public Type EntityType => typeof(T);
}
