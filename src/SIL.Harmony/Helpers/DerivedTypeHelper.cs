using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Helpers;

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

    public static void AddDerivedType(this Dictionary<Type, List<JsonDerivedType>> types, Type baseType, Type derivedType, string discriminator)
    {
        types.TryGetValue(baseType, out var list);
        if (list is null)
        {
            list = new List<JsonDerivedType>();
            types.Add(baseType, list);
        }
        else
        {
            if (list.Any(dt => dt.DerivedType == derivedType))
            {
                throw new InvalidOperationException($"Type {derivedType} already added for type {baseType}");
            }
        }
        list.Add(new JsonDerivedType(derivedType, discriminator));
    }
}
