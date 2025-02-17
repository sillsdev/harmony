namespace SIL.Harmony.Changes;

public abstract class EditChange<T>(Guid entityId) : Change<T>(entityId)
    where T : class
{
    public override ValueTask<T> NewEntity(Commit commit, IChangeContext context)
    {
        throw new NotSupportedException(
            $"type {GetType().Name} does not support NewEntity, because it inherits from {nameof(EditChange<T>)}, this means it must be called with a from an existing entity, not a newly generated one");
    }
}
