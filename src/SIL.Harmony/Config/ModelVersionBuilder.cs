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
    private readonly Dictionary<string, int> _changeVersions = new(StringComparer.Ordinal);

    public IReadOnlyList<ModelVersion> Versions => _versions.AsReadOnly();

    /// <summary>change types added with <see cref="ModelVersionChanges.Change{T}"/>, which must be registered</summary>
    internal IReadOnlyCollection<Type> ChangeTypes => _changeTypes;

    /// <summary>the latest version of each change type listed by a model version</summary>
    internal IReadOnlyDictionary<string, int> ChangeVersions => _changeVersions;

    /// <summary>
    /// Adds a version. Changes of the listed types are replayed when upgrading to it,
    /// see <see cref="ModelVersionChanges.Change{T}"/> for which ones.
    /// </summary>
    /// <param name="number">the next number: versions are numbered 1, 2, 3 and so on</param>
    public ModelVersionBuilder Add(int number, string name, Action<ModelVersionChanges> changes)
    {
        return Add(number, name, false, changes);
    }

    /// <summary>
    /// Adds a version that regenerates all snapshots when upgrading to it.
    /// The listed change types are still authored with their new version.
    /// </summary>
    /// <param name="number">the next number: versions are numbered 1, 2, 3 and so on</param>
    public ModelVersionBuilder AddMajor(int number, string name, Action<ModelVersionChanges>? changes = null)
    {
        return Add(number, name, true, changes);
    }

    private ModelVersionBuilder Add(int number, string name, bool major, Action<ModelVersionChanges>? configure)
    {
        CheckFrozen();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var expectedNumber = _versions.Count + 1;
        if (number != expectedNumber)
            throw new ArgumentOutOfRangeException(nameof(number), number,
                $"Model version {name} must be number {expectedNumber}, versions are numbered 1, 2, 3 and so on");
        var changes = new ModelVersionChanges();
        configure?.Invoke(changes);
        foreach (var change in changes.Changes.Values)
        {
            var previous = _changeVersions.GetValueOrDefault(change.ChangeType);
            if (change.Version != previous + 1)
                throw new ArgumentException(
                    $"{change.ChangeType} version {change.Version} in model version {number} ({name}) must be {previous + 1}, one more than its previous version {previous}");
        }

        foreach (var change in changes.Changes.Values)
        {
            _changeVersions[change.ChangeType] = change.Version;
        }
        _changeTypes.UnionWith(changes.Types);
        _versions.Add(new ModelVersion(number, name, major,
            changes.Changes.Values.OrderBy(c => c.ChangeType, StringComparer.Ordinal).ToArray()));
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

public class ModelVersionChanges
{
    internal Dictionary<string, ModelVersionChange> Changes { get; } = new(StringComparer.Ordinal);
    internal List<Type> Types { get; } = [];

    /// <summary>
    /// The change type was added or modified in this version.
    /// </summary>
    /// <param name="version">
    /// the version changes of this type are authored with from now on: one more than its previous version, so 1 the first time it is listed.
    /// Changes from before model versions existed are version 0.
    /// </param>
    /// <param name="invalidateFrom">
    /// on upgrade, changes of this type with this version or higher are replayed. Defaults to <paramref name="version"/>,
    /// so only changes authored by this app or a newer one are replayed. When an earlier, released version added or
    /// modified the type but forgot to list it, pass the version its apps authored the type with, to replay those too.
    /// </param>
    public ModelVersionChanges Change<T>(int version, int? invalidateFrom = null) where T : IChange, IPolyType
    {
        Change(T.TypeName, version, invalidateFrom);
        Types.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// <see cref="Change{T}"/> by discriminator.
    /// Only for change types that no longer exist in the code, use <see cref="Change{T}"/> otherwise.
    /// </summary>
    public ModelVersionChanges Change(string discriminator, int version, int? invalidateFrom = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        var from = invalidateFrom ?? version;
        ArgumentOutOfRangeException.ThrowIfNegative(from, nameof(invalidateFrom));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, version, nameof(invalidateFrom));
        if (!Changes.TryAdd(discriminator, new ModelVersionChange(discriminator, version, from)))
            throw new ArgumentException($"{discriminator} is listed more than once in the same model version");
        return this;
    }
}
