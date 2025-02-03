namespace SIL.Harmony.Tests;

public static class ObjectBaseTestingHelpers
{
    public static T Is<T>(this IObjectBase obj) where T : class
    {
        return (T) obj.DbObject;
    }
}