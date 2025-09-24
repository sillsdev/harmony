using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SIL.Harmony.Adapters;

internal record AdapterRegistration(Type ObjectDbType, Func<ModelBuilder, EntityTypeBuilder> EntityBuilder, Action<JsonTypeInfo>? JsonTypeModifier = null);

internal interface IObjectAdapterProvider
{
    IEnumerable<AdapterRegistration> GetRegistrations();
    IObjectBase Adapt(object obj);
    bool CanAdapt(object obj);
}
