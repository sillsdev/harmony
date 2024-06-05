namespace Crdt.Core;

public static class CrdtConstants
{
    /// <summary>
    /// discriminates the IChange type in the json serialization
    /// should never be changed, as it will break serialization in all apps as this is the name of a property stored in the db
    /// and shared between all clients
    /// </summary>
    public const string ChangeDiscriminatorProperty = "$type";
}
