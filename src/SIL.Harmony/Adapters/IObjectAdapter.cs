using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Adapters;

public record AdapterRegistration(Type ObjectDbType, Func<ModelBuilder, EntityTypeBuilder> EntityBuilder);

public interface IObjectAdapter
{
    IEnumerable<AdapterRegistration> GetRegistrations();
    IObjectBase Adapt(object obj);

    Dictionary<Type, List<JsonDerivedType>> JsonTypes { get; }
}