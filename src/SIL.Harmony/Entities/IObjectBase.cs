using System.Text.Json.Serialization;

namespace SIL.Harmony.Entities;

[JsonPolymorphic]
public interface IObjectBase
{
    Guid Id { get; }
    DateTimeOffset? DeletedAt { get; set; }

    public T Is<T>()
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
    public string ObjectTypeName { get; }
    public Type ObjectType { get; }
    [JsonIgnore]
    public object DbObject { get; }
    // static string IPolyType.TypeName => throw new NotImplementedException();
}

public interface IObjectBase<TThis> : IObjectBase, IPolyType where TThis : IPolyType
{
    string IObjectBase.ObjectTypeName => TThis.TypeName;
    Type IObjectBase.ObjectType => typeof(TThis);
    static string IPolyType.TypeName => typeof(TThis).Name;
    [JsonIgnore]
    object IObjectBase.DbObject => this;
}
