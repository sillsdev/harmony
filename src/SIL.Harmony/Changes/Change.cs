using System.Text.Json.Serialization;
using SIL.Harmony.Core;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Changes;

[JsonPolymorphic(TypeDiscriminatorPropertyName = CrdtConstants.ChangeDiscriminatorProperty)]
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
/// a change that can be applied to an entity, recommend inheriting from <see cref="CreateChange{T}"/> or <see cref="EditChange{T}"/>
/// </summary>
/// <typeparam name="T">Object type modified by this change</typeparam>
public abstract class Change<T> : IChange where T : class
{
    protected Change(Guid entityId)
    {
        EntityId = entityId;
    }

    [JsonIgnore]
    public Guid CommitId { get; set; }

    public Guid EntityId { get; set; }

    async ValueTask<IObjectBase> IChange.NewEntity(Commit commit, ChangeContext context)
    {
        return context.Adapt(await NewEntity(commit, context));
    }

    public abstract ValueTask<T> NewEntity(Commit commit, ChangeContext context);
    public abstract ValueTask ApplyChange(T entity, ChangeContext context);

    public async ValueTask ApplyChange(IObjectBase entity, ChangeContext context)
    {
        if (this is CreateChange<T>)
            return; // skip attempting to apply changes on CreateChange as it does not support apply changes
        if (entity is T entityT) await ApplyChange(entityT, context);
    }

    [JsonIgnore]
    public Type EntityType => typeof(T);
}