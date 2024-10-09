using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIL.Harmony.Entities;
using SIL.Harmony.Helpers;

namespace SIL.Harmony.Adapters;

public class CustomAdapterProvider<TCommonInterface, TCustomAdapter> : IObjectAdapterProvider
    where TCommonInterface : class
    where TCustomAdapter : class, ICustomAdapter<TCustomAdapter, TCommonInterface>, IPolyType
{
    private readonly ObjectTypeListBuilder _objectTypeListBuilder;
    private readonly List<AdapterRegistration> _objectTypes = new();
    private Dictionary<Type, List<JsonDerivedType>> JsonTypes { get; } = [];
    Dictionary<Type, List<JsonDerivedType>> IObjectAdapterProvider.JsonTypes => JsonTypes;

    public CustomAdapterProvider(ObjectTypeListBuilder objectTypeListBuilder)
    {
        _objectTypeListBuilder = objectTypeListBuilder;
        JsonTypes.AddDerivedType(typeof(IObjectBase), typeof(TCustomAdapter), TCustomAdapter.TypeName);
    }
    
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
        _objectTypeListBuilder.CheckFrozen();
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

    IEnumerable<AdapterRegistration> IObjectAdapterProvider.GetRegistrations()
    {
        return _objectTypes;
    }

    IObjectBase IObjectAdapterProvider.Adapt(object obj)
    {
        return TCustomAdapter.Create((TCommonInterface)obj);
    }
}

// it's possible to implement this without a Common interface, but it would require the adapter to have 1 property for each object type
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