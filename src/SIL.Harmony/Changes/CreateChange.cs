namespace SIL.Harmony.Changes;

public abstract class CreateChange<T>(Guid entityId) : Change<T>(entityId) where T : class
{
    public override ValueTask ApplyChange(T entity, IChangeContext context)
    {
        throw new NotSupportedException($"type {GetType().Name} does not support ApplyChange, because it inherits from {nameof(CreateChange<T>)}, this means it must be called with a new Guid and not one from an existing entity");
    }
}
