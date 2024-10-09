using SIL.Harmony.Entities;

namespace SIL.Harmony.Changes;

public class DeleteChange<T>(Guid entityId) : EditChange<T>(entityId), IPolyType
    where T : class
{
    public static string TypeName => "delete:" + typeof(T).Name;

    public override ValueTask ApplyChange(T entity, ChangeContext context)
    {
        context.Adapt(entity).DeletedAt = context.Commit.DateTime;
        return ValueTask.CompletedTask;
    }
}
