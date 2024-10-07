using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Adapters;

public class DefaultAdapter : IObjectAdapter
{
    private readonly List<AdapterRegistration> _objectTypes = new();

    IEnumerable<AdapterRegistration> IObjectAdapter.GetRegistrations()
    {
        return _objectTypes.AsReadOnly();
    }

    public DefaultAdapter Add<T>(Action<EntityTypeBuilder<T>>? configureEntry = null) where T : class, IObjectBase
    {
        _objectTypes.Add(new(typeof(T), T.TypeName, builder =>
        {
            var entity = builder.Entity<T>();
            configureEntry?.Invoke(entity);
            return entity;
        }, this));
        return this;
    }

    IObjectBase IObjectAdapter.Adapt(object obj)
    {
        if (obj is IObjectBase objectBase)
        {
            return objectBase;
        }

        throw new ArgumentException(
            $"Object is of type {obj.GetType().Name} which does not implement {nameof(IObjectBase)}");
    }
}