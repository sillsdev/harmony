namespace Crdt.Entities;

public interface IPolyType
{
    static abstract string TypeName { get; }
}

public interface ISelfNamedType<T>: IPolyType where T : ISelfNamedType<T>
{
    static string IPolyType.TypeName => typeof(T).Name;
}