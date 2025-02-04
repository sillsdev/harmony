using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SIL.Harmony.Adapters;

internal record AdapterRegistration(Type ObjectDbType, Func<ModelBuilder, EntityTypeBuilder> EntityBuilder);

internal interface IObjectAdapterProvider
{
    IEnumerable<AdapterRegistration> GetRegistrations();
    IObjectBase Adapt(object obj);
    bool CanAdapt(object obj);
}