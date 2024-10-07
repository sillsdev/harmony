using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Adapters;

public class CustomAdapter : IObjectAdapter
{
    public record CustomAdapterRegistration(
        Type Type,
        string TypeName,
        Func<ModelBuilder, EntityTypeBuilder> EntityBuilder,
        IObjectAdapter Adapter,
        Func<object, Guid> GetId,
        Func<object, DateTimeOffset?> GetDeletedAt,
        Action<object, DateTimeOffset?> SetDeletedAt,
        Func<object, Guid[]> GetReferences,
        Action<object, Guid, Commit> RemoveReference,
        Func<object, object> Copy) : AdapterRegistration(Type, TypeName, EntityBuilder, Adapter);

    private readonly Dictionary<Type, CustomAdapterRegistration> _objectTypes = new();

    public CustomAdapter Add<T>(string typeName,
        Func<T, Guid> getId,
        Func<T, DateTimeOffset?> getDeletedAt,
        Action<T, DateTimeOffset?> setDeletedAt,
        Func<T, Guid[]> getReferences,
        Action<T, Guid, Commit> removeReference,
        Func<T, object> copy,
        Action<EntityTypeBuilder<T>>? configureEntry = null
    ) where T : class
    {
        _objectTypes.Add(typeof(T),
            new CustomAdapterRegistration(typeof(T),
                typeName,
                builder =>
                {
                    var entity = builder.Entity<T>();
                    configureEntry?.Invoke(entity);
                    return entity;
                },
                this,
                o => getId.Invoke((T)o),
                o => getDeletedAt.Invoke((T)o),
                (o, deletedAt) => setDeletedAt.Invoke((T)o, deletedAt),
                o => getReferences.Invoke((T)o),
                (o, id, commit) => removeReference.Invoke((T)o, id, commit),
                o => copy.Invoke((T)o)
            ));
        return this;
    }

    public IEnumerable<AdapterRegistration> GetRegistrations()
    {
        return _objectTypes.Values;
    }

    private class CustomIObjectAdapter(CustomAdapterRegistration registration, object obj) : IObjectBase
    {
        public Type ObjectType => registration.ObjectType;
        public object DbObject => obj;
        public T Is<T>()
        {
            return (T)obj;
        }

        public Guid Id => registration.GetId(obj);

        public DateTimeOffset? DeletedAt
        {
            get => registration.GetDeletedAt(obj);
            set => registration.SetDeletedAt(obj, value);
        }

        public Guid[] GetReferences()
        {
            return registration.GetReferences(obj);
        }

        public void RemoveReference(Guid id, Commit commit)
        {
            registration.RemoveReference(obj, id, commit);
        }

        public IObjectBase Copy()
        {
            return registration.Adapter.Adapt(registration.Copy(obj));
        }

        public string TypeName => registration.TypeName;
    }

    public IObjectBase Adapt(object obj)
    {
        var registration = _objectTypes[obj.GetType()];
        return new CustomIObjectAdapter(registration, obj);
    }
}