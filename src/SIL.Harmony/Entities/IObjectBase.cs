using System.Text.Json.Serialization;

namespace SIL.Harmony.Entities;

public interface IObjectBase<TThis> : IObjectBase, IPolyType where TThis : IPolyType
{
    string IObjectBase.GetObjectTypeName() => TThis.TypeName;
    static string IPolyType.TypeName => typeof(TThis).Name;
    [JsonIgnore]
    object IObjectBase.DbObject => this;
}
