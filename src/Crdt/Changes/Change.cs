using System.Text.Json.Serialization;
using Crdt.Core;
using Crdt.Entities;

namespace Crdt.Changes;

[JsonPolymorphic]
public interface IChange
{
    public ChangeEntity<IChange> ToChangeEntity(int index)
    {
        return new ChangeEntity<IChange>
        {
            Change = this,
            Index = index,
            CommitId = CommitId,
            EntityId = EntityId
        };
    }
    [JsonIgnore]
    Guid CommitId { get; set; }

    [JsonIgnore]
    Guid EntityId { get; set; }

    [JsonIgnore]
    Type EntityType { get; }

    ValueTask ApplyChange(IObjectBase entity, ChangeContext context);
    IObjectBase NewEntity(Commit commit);
}

public abstract class Change<T> : IChange where T : IObjectBase
{
    protected Change(Guid entityId)
    {
        EntityId = entityId;
    }

    [JsonIgnore]
    public Guid CommitId { get; set; }

    public Guid EntityId { get; set; }

    public abstract IObjectBase NewEntity(Commit commit);
    public abstract ValueTask ApplyChange(T entity, ChangeContext context);

    public async ValueTask ApplyChange(IObjectBase entity, ChangeContext context)
    {
        if (entity is T entityT) await ApplyChange(entityT, context);
    }

    [JsonIgnore]
    public Type EntityType => typeof(T);
}
