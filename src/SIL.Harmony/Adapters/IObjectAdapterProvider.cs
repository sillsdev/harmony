using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Adapters;

internal record AdapterRegistration(Type ObjectDbType, Func<ModelBuilder, EntityTypeBuilder> EntityBuilder);

internal interface IObjectAdapterProvider
{
    IEnumerable<AdapterRegistration> GetRegistrations();
    IObjectBase Adapt(object obj);

    Dictionary<Type, List<JsonDerivedType>> JsonTypes { get; }
}