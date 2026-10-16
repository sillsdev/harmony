using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Resource;

namespace SIL.Harmony.Config;

/// <summary>
/// Declares the ordered list of model versions, which is required. Version 0 lists every change type that existed
/// before model versions, with change version 0. Add a version when a release adds a change type, or changes how
/// an existing change type applies. Every registered change type must be listed by a version.
/// Changes authored by a newer app are then replayed when an older app upgrades, see <see cref="DataModel.ReconcileModelVersions"/>.
/// Released versions must never be modified, use <see cref="HarmonyConfig.DescribeModelVersions"/> in a snapshot test to make sure.
/// </summary>
public class ModelVersionBuilder
{
    private bool _frozen;
    private readonly List<ModelVersion> _versions = [];
    private readonly HashSet<Type> _changeTypes = [];
    private readonly Dictionary<string, int> _changeVersions = new(StringComparer.Ordinal);
    private bool _remoteResourcesListed;

    public IReadOnlyList<ModelVersion> Versions => _versions.AsReadOnly();

    /// <summary>change types added with <see cref="ModelVersionChanges.Change{T}"/>, which must be registered</summary>
    internal IReadOnlyCollection<Type> ChangeTypes => _changeTypes;

    /// <summary>the latest version of each change type listed by a model version</summary>
    internal IReadOnlyDictionary<string, int> ChangeVersions => _changeVersions;

    /// <summary>whether a version called <see cref="ModelVersionChanges.RemoteResourceChanges{TMetadata}"/></summary>
    internal bool RemoteResourcesListed => _remoteResourcesListed;

    /// <summary>
    /// Adds a version. Changes of the listed types are replayed when upgrading to it,
    /// see <see cref="ModelVersionChanges.Change{T}"/> for which ones.
    /// </summary>
    /// <param name="number">the next number: versions are numbered 0, 1, 2 and so on</param>
    public ModelVersionBuilder Add(int number, string name, Action<ModelVersionChanges> changes)
    {
        return Add(number, name, false, changes);
    }

    /// <summary>
    /// Adds a version that regenerates all snapshots when upgrading to it. Version 0 can't be major.
    /// The listed change types are still authored with their new version.
    /// </summary>
    /// <param name="number">the next number: versions are numbered 0, 1, 2 and so on</param>
    public ModelVersionBuilder AddMajor(int number, string name, Action<ModelVersionChanges>? changes = null)
    {
        return Add(number, name, true, changes);
    }

    private ModelVersionBuilder Add(int number, string name, bool major, Action<ModelVersionChanges>? configure)
    {
        CheckFrozen();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var expectedNumber = _versions.Count;
        if (number != expectedNumber)
            throw new ArgumentOutOfRangeException(nameof(number), number,
                $"Model version {name} must be number {expectedNumber}, versions are numbered 0, 1, 2 and so on");
        if (major && number == 0)
            throw new ArgumentException($"Model version 0 ({name}) can't be major, it is the state before model versions");
        var changes = new ModelVersionChanges();
        configure?.Invoke(changes);
        _remoteResourcesListed |= changes.RemoteResourcesListed;
        foreach (var change in changes.Changes.Values)
        {
            if (number == 0)
            {
                if (change.Version != 0)
                    throw new ArgumentException(
                        $"{change.ChangeType} version {change.Version} in model version 0 ({name}) must be 0, version 0 lists the change types from before model versions");
                continue;
            }

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

    /// <summary>removes all versions, so a test can declare a different history than the model it starts from</summary>
    internal void Clear()
    {
        CheckFrozen();
        _versions.Clear();
        _changeTypes.Clear();
        _changeVersions.Clear();
        _remoteResourcesListed = false;
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
    internal bool RemoteResourcesListed { get; private set; }

    /// <summary>
    /// The change type was added or modified in this version, or existed before model versions when this is version 0.
    /// </summary>
    /// <param name="version">
    /// the version changes of this type are authored with from now on. 0 in model version 0,
    /// otherwise one more than its previous version, so 1 the first time a new type is listed.
    /// </param>
    /// <param name="invalidateFrom">
    /// on upgrade, changes of this type with this version or higher are replayed. Defaults to <paramref name="version"/>,
    /// so only changes authored by this app or a newer one are replayed. When an earlier, released version modified
    /// how the type applies but forgot to list it, pass the version its apps authored the type with, to replay those too.
    /// </param>
    public ModelVersionChanges Change<T>(int version, int? invalidateFrom = null) where T : IChange, IPolyType
    {
        Change(T.TypeName, version, invalidateFrom);
        Types.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// The change types registered by <see cref="CrdtKernel.AddCrdtRemoteResources{TMetadata}"/>, all with the same version.
    /// Options validation requires it when remote resources are enabled. Harmony doesn't version these types yet,
    /// see https://github.com/sillsdev/harmony/issues/141.
    /// </summary>
    public ModelVersionChanges RemoteResourceChanges<TMetadata>(int version, int? invalidateFrom = null)
        where TMetadata : class
    {
        RemoteResourcesListed = true;
        return Change<RemoteResourceUploadedChange<TMetadata>>(version, invalidateFrom)
            .Change<CreateRemoteResourceChange<TMetadata>>(version, invalidateFrom)
            .Change<CreateRemoteResourcePendingUploadChange<TMetadata>>(version, invalidateFrom)
            .Change<SetRemoteResourceMetadataChange<TMetadata>>(version, invalidateFrom)
            .Change<DeleteRemoteResourceChange<TMetadata>>(version, invalidateFrom);
    }

    /// <summary>
    /// <see cref="Change{T}"/> by discriminator.
    /// Only for change types that no longer exist in the code, use <see cref="Change{T}"/> otherwise.
    /// </summary>
    public ModelVersionChanges Change(string discriminator, int version, int? invalidateFrom = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        var from = invalidateFrom ?? version;
        ArgumentOutOfRangeException.ThrowIfNegative(from, nameof(invalidateFrom));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, version, nameof(invalidateFrom));
        if (!Changes.TryAdd(discriminator, new ModelVersionChange(discriminator, version, from)))
            throw new ArgumentException($"{discriminator} is listed more than once in the same model version");
        return this;
    }
}
