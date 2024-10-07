using SIL.Harmony.Entities;

namespace SIL.Harmony.Changes;

public class DeleteChange<T>(Guid entityId) : EditChange<T>(entityId), IPolyType
    where T : class, IPolyType, IObjectBase
{
    public static string TypeName => "delete:" + T.TypeName;

    public override ValueTask ApplyChange(T entity, ChangeContext context)
    {
        entity.DeletedAt = context.Commit.DateTime;
        return ValueTask.CompletedTask;
    }
}
