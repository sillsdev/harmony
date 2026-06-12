using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SIL.Harmony.Changes;

public abstract class CreateChange<T>(Guid entityId) : Change<T>(entityId) where T : class
{
    public override ValueTask ApplyChange(T entity, IChangeContext context)
    {
        //won't be called because it's skipped by the base class for CreateChange
        return default;
    }
}
