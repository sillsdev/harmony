using System.Diagnostics;
using System.Text.Json.Serialization;

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

    ValueTask ApplyChange(IObjectBase entity, IChangeContext context);
    ValueTask<IObjectBase> NewEntity(Commit commit, IChangeContext context);
    /// <summary>
    /// Indicates whether this change supports applying changes to an existing entity (whether deleted or not).
    /// Essentially just avoids creating redundant snapshots for change objects that won't apply changes.
    /// (e.g. redundant change objects intended only for NewEntity)
    /// </summary>
    bool SupportsApplyChange();
    /// <summary>
    /// Indicates whether this change supports creating entities (both creating brand new entities as well as recreating deleted entities).
    /// Primarily for differentiating between updating vs recreating deleted entities.
    /// </summary>
    bool SupportsNewEntity();
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

    async ValueTask<IObjectBase> IChange.NewEntity(Commit commit, IChangeContext context)
    {
        return context.Adapt(await NewEntity(commit, context));
    }

    public abstract ValueTask<T> NewEntity(Commit commit, IChangeContext context);
    public abstract ValueTask ApplyChange(T entity, IChangeContext context);

    public async ValueTask ApplyChange(IObjectBase entity, IChangeContext context)
    {
        if (!SupportsApplyChange())
        {
            Debug.Fail("ApplyChange called on a Change that does not support it");
            return; // skip attempting to apply changes on CreateChange as it does not support apply changes
        }

        if (entity.DbObject is T entityT)
        {
            await ApplyChange(entityT, context);
        }
        else
        {
            throw new NotSupportedException($"Type {entity.DbObject.GetType()} is not type {typeof(T)}");
        }
    }

    public virtual bool SupportsApplyChange()
    {
        return this is not CreateChange<T>;
    }

    public virtual bool SupportsNewEntity()
    {
        return this is not EditChange<T>;
    }

    [JsonIgnore]
    public Type EntityType => typeof(T);
}
