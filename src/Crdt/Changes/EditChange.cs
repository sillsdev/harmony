using Crdt.Entities;

namespace Crdt.Changes;

public abstract class EditChange<T>(Guid entityId) : Change<T>(entityId)
    where T : IObjectBase
{
    public override ValueTask<IObjectBase> NewEntity(Commit commit, ChangeContext context)
    {
        throw new NotSupportedException(
            $"type {GetType().Name} does not support NewEntity, because it inherits from {nameof(EditChange<T>)}, this means it must be called with a from an existing entity, not a newly generated one");
    }
}
