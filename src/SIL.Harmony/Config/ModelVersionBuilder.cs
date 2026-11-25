using SIL.Harmony.Changes;
using SIL.Harmony.Entities;
using SIL.Harmony.Resource;

namespace SIL.Harmony.Config;

public readonly record struct RegisteredChangeType(Type Type, string Discriminator);

/// <summary>
/// Declares the ordered list of model versions, which is required. Version 0 lists every change type that existed
/// before model versions. Add a version when a release adds a change type, or changes how an existing change type
/// applies. The change types listed with <see cref="ModelVersionChanges.Change{T}"/> are the ones Harmony registers.
/// A change is authored with the number of the latest version, whether or not it lists the change's type, and
/// changes authored by a newer app are replayed when an older app upgrades, see <see cref="DataModel.ReconcileModelVersions"/>.
/// Released versions must never be modified, use <see cref="HarmonyConfig.DescribeModelVersions"/> in a snapshot test to make sure.
/// </summary>
public class ModelVersionBuilder
{
    private bool _frozen;
    private readonly List<ModelVersion> _versions = [];
    private readonly List<RegisteredChangeType> _changeTypes = [];

    public IReadOnlyList<ModelVersion> Versions => _versions.AsReadOnly();

    internal IReadOnlyList<RegisteredChangeType> ChangeTypes => _changeTypes.AsReadOnly();

    internal Type? RemoteResourceChangesMetadataType { get; private set; }

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
        var changes = new ModelVersionChanges(number);
        configure?.Invoke(changes);
        foreach (var changeType in changes.Types)
        {
            var registered = _changeTypes.Find(ct => ct.Discriminator == changeType.Discriminator);
            if (registered.Type is not null && registered.Type != changeType.Type)
                throw new ArgumentException(
                    $"Model version {name} lists {changeType.Type} as {changeType.Discriminator}, which {registered.Type} already uses");
        }
        _changeTypes.AddRange(changes.Types.Except(_changeTypes));
        RemoteResourceChangesMetadataType ??= changes.RemoteResourceChangesMetadataType;
        _versions.Add(new ModelVersion(number, name, major,
            changes.Changes.Values.OrderBy(c => c.ChangeType, StringComparer.Ordinal).ToArray()));
        return this;
    }

    /// <summary>
    /// removes all versions, and so every change type they registered,
    /// so a test can declare a different history than the model it starts from
    /// </summary>
    internal void Clear()
    {
        CheckFrozen();
        _versions.Clear();
        _changeTypes.Clear();
        RemoteResourceChangesMetadataType = null;
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
    private readonly int _number;

    internal ModelVersionChanges(int number)
    {
        _number = number;
    }

    internal Dictionary<string, ModelVersionChange> Changes { get; } = new(StringComparer.Ordinal);
    internal List<RegisteredChangeType> Types { get; } = [];
    internal Type? RemoteResourceChangesMetadataType { get; private set; }

    /// <summary>
    /// The change type was added or modified in this version, or existed before model versions when this is version 0.
    /// Also registers the type.
    /// </summary>
    /// <param name="invalidateFrom">
    /// on upgrade, changes of this type authored with this model version or later are invalidated, so only changes
    /// authored by this app or a newer one. Pass an earlier version to invalidate the changes its apps authored too:
    /// when the new behavior must be applied to older changes, or when that version changed how the type applies
    /// but forgot to list it. Every change is authored with the app's latest version, so passing the version that
    /// forgot invalidates only the changes its apps and later ones authored.
    /// </param>
    public ModelVersionChanges Change<T>(int? invalidateFrom = null) where T : IChange, IPolyType
    {
        return Change(new RegisteredChangeType(typeof(T), T.TypeName), invalidateFrom);
    }

    internal ModelVersionChanges Change(RegisteredChangeType changeType, int? invalidateFrom = null)
    {
        Change(changeType.Discriminator, invalidateFrom);
        Types.Add(changeType);
        return this;
    }

    /// <summary>
    /// Registers the change types <see cref="CrdtKernel.AddCrdtRemoteResources{TMetadata}"/> needs. Options validation
    /// requires it, with the same metadata type, when remote resources are enabled, and rejects it otherwise.
    /// Harmony doesn't version these types yet, see https://github.com/sillsdev/harmony/issues/141.
    /// </summary>
    public ModelVersionChanges RemoteResourceChanges<TMetadata>()
        where TMetadata : class
    {
        RemoteResourceChangesMetadataType = typeof(TMetadata);
        return Change<RemoteResourceUploadedChange<TMetadata>>()
            .Change<CreateRemoteResourceChange<TMetadata>>()
            .Change<CreateRemoteResourcePendingUploadChange<TMetadata>>()
            .Change<SetRemoteResourceMetadataChange<TMetadata>>()
            .Change<DeleteRemoteResourceChange<TMetadata>>();
    }

    /// <summary>
    /// <see cref="Change{T}"/> by discriminator, which doesn't register a type.
    /// Only for change types that no longer exist in the code, use <see cref="Change{T}"/> otherwise.
    /// </summary>
    public ModelVersionChanges Change(string discriminator, int? invalidateFrom = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);
        var from = invalidateFrom ?? _number;
        ArgumentOutOfRangeException.ThrowIfNegative(from, nameof(invalidateFrom));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, _number, nameof(invalidateFrom));
        if (!Changes.TryAdd(discriminator, new ModelVersionChange(discriminator, from)))
            throw new ArgumentException($"{discriminator} is listed more than once in the same model version");
        return this;
    }
}
