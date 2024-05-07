using System.Text.Json.Serialization;

namespace Crdt.Entities;

[JsonPolymorphic]
public interface IObjectBase: IPolyType
{
    Guid Id { get; init; }
    DateTimeOffset? DeletedAt { get; set; }

    public T Is<T>() where T : IObjectBase
    {
        return (T)this;
    }

    public T? As<T>() where T : class, IObjectBase
    {
        return this as T;
    }

    public Guid[] GetReferences();
    public void RemoveReference(Guid id, Commit commit);

    public IObjectBase Copy();
    new string TypeName { get; }
    static string IPolyType.TypeName => throw new NotImplementedException();
}

public interface IObjectBase<TThis> : IObjectBase where TThis : IPolyType
{
    string IObjectBase.TypeName => TThis.TypeName;
    static string IPolyType.TypeName => typeof(TThis).Name;
}

public interface INewableObject<out T> : IObjectBase
{
    static abstract T New(Guid id, Commit commit);
}