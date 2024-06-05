namespace Crdt.Entities;

/// <summary>
/// used to denote a type with a name, used for polymorphic serialization (where there's a type property that discriminates between different types)
/// </summary>
public interface IPolyType
{
    static abstract string TypeName { get; }
}

public interface ISelfNamedType<T>: IPolyType where T : ISelfNamedType<T>
{
    static string IPolyType.TypeName => typeof(T).Name;
}
