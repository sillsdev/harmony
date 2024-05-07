using Crdt.Entities;

namespace Crdt.Changes;

public interface IOrderableCrdt
{
    public double Order { get; set; }
}

public class SetOrderChange<T> : Change<T>, IPolyType
    where T : IPolyType, IObjectBase, IOrderableCrdt
{
    public static IChange Between(Guid entityId, T left, T right)
    {
        return new SetOrderChange<T>(entityId, (left.Order + right.Order) / 2);
    }

    public static IChange After(Guid entityId, T previous)
    {
        return new SetOrderChange<T>(entityId, previous.Order + 1);
    }

    public static IChange Before(Guid entityId, T preceding)
    {
        return new SetOrderChange<T>(entityId, preceding.Order - 1);
    }

    protected SetOrderChange(Guid entityId, double order) : base(entityId)
    {
        Order = order;
    }

    public double Order { get; init; }
    public static string TypeName => "setOrder:" + T.TypeName;

    public override IObjectBase NewEntity(Commit commit)
    {
        throw new NotImplementedException();
    }

    public override ValueTask ApplyChange(T entity, ChangeContext context)
    {
        entity.Order = Order;
        return ValueTask.CompletedTask;
    }
}