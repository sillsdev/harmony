using System.Text.Json.Serialization;

namespace SIL.Harmony.Core;

[JsonPolymorphic]
public interface IObjectBase
{
    Guid Id { get; }
    DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// provides the references this object has to other objects, when those objects are deleted
    /// <see cref="RemoveReference"/> will be called to remove the reference
    /// </summary>
    /// <returns></returns>
    public Guid[] GetReferences();
    /// <summary>
    /// remove a reference to another object, in some cases this may cause this object to be deleted
    /// </summary>
    /// <param name="id">id of the deleted object</param>
    /// <param name="commit">
    /// commit where the reference was removed
    /// should be used to set the deleted date for this object
    /// </param>
    public void RemoveReference(Guid id, CommitBase commit);

    public IObjectBase Copy();
    /// <summary>
    /// the name of the object type, this is used to discriminate between different types of objects in the snapshots table
    /// </summary>
    /// <returns>a stable type name of this object, should not change over time</returns>
    public string GetObjectTypeName();
    [JsonIgnore]
    public object DbObject { get; }
}
