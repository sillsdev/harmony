using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIL.Harmony.Entities;
using SIL.Harmony.Helpers;

namespace SIL.Harmony.Adapters;

public class CustomAdapterProvider<TCommonInterface, TCustomAdapter> : IObjectAdapter
    where TCommonInterface : class
    where TCustomAdapter : class, ICustomAdapter<TCustomAdapter, TCommonInterface>, IPolyType
{
    public CustomAdapterProvider()
    {
        JsonTypes.AddDerivedType(typeof(IObjectBase), typeof(TCustomAdapter), TCustomAdapter.TypeName);
    }

    private readonly List<AdapterRegistration> _objectTypes = new();
    public Dictionary<Type, List<JsonDerivedType>> JsonTypes { get; } = [];

    public CustomAdapterProvider<TCommonInterface, TCustomAdapter> AddWithCustomPolymorphicMapping<T>(string typeName,
        Action<EntityTypeBuilder<T>>? configureEntry = null
    ) where T : class, TCommonInterface
    {
        JsonTypes.AddDerivedType(typeof(TCommonInterface), typeof(T), typeName);
        return Add(configureEntry);
    }
    public CustomAdapterProvider<TCommonInterface, TCustomAdapter> Add<T>(
        Action<EntityTypeBuilder<T>>? configureEntry = null
    ) where T : class, TCommonInterface
    {
        _objectTypes.Add(
            new AdapterRegistration(typeof(T),
                builder =>
                {
                    var entity = builder.Entity<T>();
                    configureEntry?.Invoke(entity);
                    return entity;
                })
        );
        return this;
    }

    public IEnumerable<AdapterRegistration> GetRegistrations()
    {
        return _objectTypes;
    }

    public IObjectBase Adapt(object obj)
    {
        return TCustomAdapter.Create((TCommonInterface)obj);
    }
}

public interface ICustomAdapter<TSelf, TCommonInterface> : IObjectBase, IPolyType
    where TSelf : class,
    ICustomAdapter<TSelf, TCommonInterface>
{
    public static abstract TSelf Create(TCommonInterface obj);
    static string IPolyType.TypeName => TSelf.AdapterTypeName;
    public static abstract string AdapterTypeName { get; }

    T IObjectBase.Is<T>()
    {
        return (T)DbObject;
    }
}