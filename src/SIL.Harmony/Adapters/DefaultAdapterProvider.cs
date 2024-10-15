using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIL.Harmony.Entities;
using SIL.Harmony.Helpers;

namespace SIL.Harmony.Adapters;

public class DefaultAdapterProvider(ObjectTypeListBuilder objectTypeListBuilder) : IObjectAdapterProvider
{
    private readonly List<AdapterRegistration> _objectTypes = [];

    IEnumerable<AdapterRegistration> IObjectAdapterProvider.GetRegistrations()
    {
        return _objectTypes.AsReadOnly();
    }

    public DefaultAdapterProvider Add<T>(Action<EntityTypeBuilder<T>>? configureEntry = null) where T : class, IObjectBase<T>
    {
        objectTypeListBuilder.CheckFrozen();
        JsonTypes.AddDerivedType(typeof(IObjectBase), typeof(T), T.TypeName);
        _objectTypes.Add(new(typeof(T), builder =>
        {
            var entity = builder.Entity<T>();
            configureEntry?.Invoke(entity);
            return entity;
        }));
        return this;
    }

    IObjectBase IObjectAdapterProvider.Adapt(object obj)
    {
        if (obj is IObjectBase objectBase)
        {
            return objectBase;
        }

        throw new ArgumentException(
            $"Object is of type {obj.GetType().Name} which does not implement {nameof(IObjectBase)}");
    }

    public bool CanAdapt(object obj)
    {
        return obj is IObjectBase;
    }

    private Dictionary<Type, List<JsonDerivedType>> JsonTypes => objectTypeListBuilder.JsonTypes;
}