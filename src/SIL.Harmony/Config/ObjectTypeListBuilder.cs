using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Adapters;
using SIL.Harmony.Db;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Config;

public class ObjectTypeListBuilder
{
    private bool _frozen;

    /// <summary>
    /// we call freeze when the builder is used to create a json serializer options, as it is not possible to add new types after that.
    /// </summary>
    internal void Freeze()
    {
        if (_frozen) return;
        _frozen = true;
        foreach (var registration in AdapterProviders.SelectMany(a => a.GetRegistrations()))
        {
            ModelConfigurations.Add((builder, config) =>
            {
                if (!config.EnableProjectedTables) return;
                var entity = registration.EntityBuilder(builder);
                entity.HasOne(typeof(ObjectSnapshot))
                    .WithOne()
                    .HasForeignKey(registration.ObjectDbType, ObjectSnapshot.ShadowRefName)
                    .OnDelete(DeleteBehavior.SetNull);
            });
        }
    }

    internal void CheckFrozen()
    {
        if (_frozen) throw new InvalidOperationException($"{nameof(ObjectTypeListBuilder)} is frozen");
    }

    internal Dictionary<Type, List<JsonDerivedType>> JsonTypes { get; } = [];
    internal List<Action<ModelBuilder, HarmonyConfig>> ModelConfigurations { get; } = [];
    internal List<IObjectAdapterProvider> AdapterProviders { get; } = [];

    public DefaultAdapterProvider DefaultAdapter()
    {
        CheckFrozen();
        if (AdapterProviders.OfType<DefaultAdapterProvider>().SingleOrDefault() is { } adapter) return adapter;
        adapter = new DefaultAdapterProvider(this);
        AdapterProviders.Add(adapter);
        return adapter;
    }

    /// <summary>
    /// add a custom adapter for a common interface
    /// this is required as CRDT objects must express their references and have an Id property
    /// using a custom adapter allows your model to not take a dependency on Harmony
    /// </summary>
    /// <typeparam name="TCommonInterface">
    /// A common interface that all objects in your application implement
    /// which System.Text.Json will deserialize your objects to, they must support polymorphic deserialization
    /// </typeparam>
    /// <typeparam name="TAdapter">
    /// This adapter will be serialized and stored in the database,
    /// it should include the object it is adapting otherwise Harmony will not work
    /// </typeparam>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException">when another adapter has already been added or the config has been frozen</exception>
    public CustomAdapterProvider<TCommonInterface, TAdapter> CustomAdapter<TCommonInterface, TAdapter>()
        where TCommonInterface : class where TAdapter : class, ICustomAdapter<TAdapter, TCommonInterface>, IPolyType
    {
        CheckFrozen();
        if (AdapterProviders.OfType<CustomAdapterProvider<TCommonInterface, TAdapter>>().SingleOrDefault() is { } adapter) return adapter;
        adapter = new CustomAdapterProvider<TCommonInterface, TAdapter>(this);
        AdapterProviders.Add(adapter);
        return adapter;
    }

    internal IObjectBase Adapt(object obj)
    {
        if (AdapterProviders is [{ } defaultAdapter])
        {
            return defaultAdapter.Adapt(obj);
        }

        foreach (var objectAdapterProvider in AdapterProviders)
        {
            if (objectAdapterProvider.CanAdapt(obj))
            {
                return objectAdapterProvider.Adapt(obj);
            }
        }
        throw new ArgumentException($"Unable to adapt object of type {obj.GetType()}");
    }
}
