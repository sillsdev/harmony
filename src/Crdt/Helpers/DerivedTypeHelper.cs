using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using Crdt.Entities;

namespace Crdt.Helpers;

internal static class DerivedTypeHelper
{
    //call static method via compiler reflection on type JsonPolymorphismOptions
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod)]
    private static extern JsonPolymorphismOptions CreateFromAttributeDeclarations(JsonPolymorphismOptions? options,
        Type type);

    private static IList<JsonDerivedType> LookupDerivedTypes<T>()
    {
        var options = CreateFromAttributeDeclarations(null, typeof(T));
        ArgumentNullException.ThrowIfNull(options);
        return options.DerivedTypes;
    }

    public static string? GetEntityDiscriminator<TBase>(Type instanceType)
    {
        if (!instanceType.IsAssignableTo(typeof(TBase))) throw new ArgumentException($"Type {instanceType} must implement IObjectBase", nameof(instanceType));
        return LookupDerivedTypes<TBase>().SingleOrDefault(dt => dt.DerivedType == instanceType).TypeDiscriminator as string;
    }

    public static Type? GetEntityType<T>(string discriminator)
    {
        return LookupDerivedTypes<T>().SingleOrDefault(dt => dt.TypeDiscriminator as string == discriminator).DerivedType;
    }

    public static string GetEntityDiscriminator<T>() where T: IPolyType
    {
        return T.TypeName;
    }
}
