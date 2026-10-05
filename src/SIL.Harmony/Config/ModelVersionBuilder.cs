using SIL.Harmony.Changes;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Config;

/// <summary>
/// Declares the ordered list of model versions. Add a version when a release adds a change type, or changes how
/// an existing change type applies. Changes authored by a newer app are then replayed when an older app upgrades,
/// see <see cref="DataModel.ReconcileModelVersions"/>.
/// Released versions must never be modified, use <see cref="HarmonyConfig.DescribeModelVersions"/> in a snapshot test to make sure.
/// </summary>
public class ModelVersionBuilder
{
    private bool _frozen;
    private readonly List<ModelVersion> _versions = [];
    private readonly HashSet<Type> _changeTypes = [];

    public IReadOnlyList<ModelVersion> Versions => _versions.AsReadOnly();

    /// <summary>change types added with <see cref="ModelVersionChangeTypes.Change{T}"/>, which must be registered</summary>
    internal IReadOnlyCollection<Type> ChangeTypes => _changeTypes;

    /// <summary>
    /// Adds a version. Changes of the listed types that were authored with this version or later
    /// are replayed when upgrading to it.
    /// </summary>
    public ModelVersionBuilder Add(string name, Action<ModelVersionChangeTypes> changeTypes)
    {
        return Add(name, false, changeTypes);
    }

    /// <summary>
    /// Adds a version that regenerates all snapshots when upgrading to it.
    /// The listed change types still get a new version number for the changes authored with them.
    /// </summary>
    public ModelVersionBuilder AddMajor(string name, Action<ModelVersionChangeTypes>? changeTypes = null)
    {
        return Add(name, true, changeTypes);
    }

    private ModelVersionBuilder Add(string name, bool major, Action<ModelVersionChangeTypes>? configure)
    {
        CheckFrozen();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var changeTypes = new ModelVersionChangeTypes();
        configure?.Invoke(changeTypes);
        _changeTypes.UnionWith(changeTypes.Types);
        _versions.Add(new ModelVersion(name, major,
            changeTypes.Discriminators.Order(StringComparer.Ordinal).ToArray()));
        return this;
    }

    internal void Freeze()
    {
        _frozen = true;
    }

    private void CheckFrozen()
    {
        if (_frozen) throw new InvalidOperationException($"{nameof(ModelVersionBuilder)} is frozen");
    }
}

public class ModelVersionChangeTypes
{
    internal HashSet<string> Discriminators { get; } = new(StringComparer.Ordinal);
    internal List<Type> Types { get; } = [];

    /// <summary>the change type was added or modified in this version</summary>
    public ModelVersionChangeTypes Change<T>() where T : IChange, IPolyType
    {
        if (Discriminators.Add(T.TypeName)) Types.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// The change type with this discriminator was added or modified in this version.
    /// Only for change types that no longer exist in the code, use <see cref="Change{T}"/> otherwise.
    /// </summary>
    public ModelVersionChangeTypes Change(string discriminator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);
        Discriminators.Add(discriminator);
        return this;
    }
}
