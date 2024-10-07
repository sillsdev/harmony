using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Adapters;

public record AdapterRegistration(
    Type ObjectType,
    string ObjectName,
    Func<ModelBuilder, EntityTypeBuilder> EntityBuilder,
    IObjectAdapter Adapter);
public interface IObjectAdapter
{
    IEnumerable<AdapterRegistration> GetRegistrations();
    IObjectBase Adapt(object obj);
}