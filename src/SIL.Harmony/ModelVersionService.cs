using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Db;

namespace SIL.Harmony;

internal class ModelVersionService(
    CrdtRepositoryFactory crdtRepositoryFactory,
    IOptions<HarmonyConfig> crdtConfig,
    SnapshotReplay snapshotReplay,
    ILogger<ModelVersionService> logger)
{
    private HarmonyConfig Config => crdtConfig.Value;

    /// <summary>
    /// Brings the snapshots up to date with the model versions of the current config, see <see cref="ModelVersionBuilder"/>.
    /// Changes authored by a newer app are applied with the old code by an older app, or skipped as
    /// <see cref="OpaqueChange"/> when the older app doesn't know their type. After an upgrade those changes are replayed,
    /// from the oldest commit with a change whose version a new model version invalidates.
    /// A major version, or a downgrade, regenerates all snapshots.
    /// Call this when opening a database, before using it. Querying changes by type is SQLite only.
    /// </summary>
    /// <exception cref="InvalidOperationException">a model version that was already applied to this database was modified</exception>
    public async Task<ModelVersionReconcileResult> ReconcileModelVersions()
    {
        await using var repo = await crdtRepositoryFactory.CreateRepository();
        using var locked = await repo.Lock();
        repo.ClearChangeTracker();
        await using var transaction = await repo.BeginTransactionAsync();
        //nothing stored means the snapshots were made before model versions, which is version 0
        var stored = await repo.GetLocalState<StoredModelVersions>(StoredModelVersions.LocalStateKey);
        var storedVersion = stored?.Version ?? 0;
        var current = Config.ModelVersions;
        var currentVersion = Config.CurrentModelVersion;
        if (stored is not null)
        {
            EnsureAppliedVersionsUnchanged(stored, current);
            if (storedVersion == currentVersion)
                return new ModelVersionReconcileResult(storedVersion, currentVersion, false, null);
        }

        var fullRegeneration = false;
        Commit? oldestInvalidated;
        if (currentVersion < storedVersion || NewVersions(storedVersion, current).Any(v => v.Major))
        {
            //a downgrade is rare and the snapshots were made by newer code, so start over
            fullRegeneration = true;
            oldestInvalidated = await repo.CurrentCommits().FirstOrDefaultAsync();
            await snapshotReplay.RegenerateAll(repo);
        }
        else
        {
            oldestInvalidated = await repo.FindOldestCommitWithModelVersions(ModelVersionsToInvalidate(storedVersion, current));
            if (oldestInvalidated is not null)
            {
                await snapshotReplay.UpdateSnapshots(repo, await repo.ReplayWindowFrom(oldestInvalidated));
            }
        }

        if (oldestInvalidated is not null)
        {
            logger.LogInformation(
                "Model version changed from {StoredVersion} to {CurrentVersion}, oldest invalidated commit {CommitId}, full regeneration: {FullRegeneration}",
                storedVersion, currentVersion, oldestInvalidated.Id, fullRegeneration);
        }

        await repo.SetLocalState(StoredModelVersions.LocalStateKey, StoredModelVersions.From(current));
        await transaction.CommitAsync();
        return new ModelVersionReconcileResult(storedVersion, currentVersion, fullRegeneration, oldestInvalidated);
    }

    private static void EnsureAppliedVersionsUnchanged(StoredModelVersions stored, IReadOnlyList<ModelVersion> current)
    {
        var applied = Math.Min(stored.Versions.Length, current.Count);
        for (var i = 0; i < applied; i++)
        {
            if (stored.Versions[i].SameAs(current[i])) continue;
            throw new InvalidOperationException(
                $"Model version {current[i].Number} ({current[i].Name}) was modified after it was applied to this database. " +
                "Released model versions must not change, add a new version instead.");
        }
    }

    /// <summary>the versions after <paramref name="storedVersion"/>, versions are numbered from 0</summary>
    private static IEnumerable<ModelVersion> NewVersions(int storedVersion, IReadOnlyList<ModelVersion> versions)
    {
        return versions.Skip(storedVersion + 1);
    }

    /// <summary>
    /// For each change type in the versions after <paramref name="storedVersion"/>, the lowest authored model version to invalidate:
    /// the lowest <see cref="ModelVersionChange.InvalidateFrom"/> those versions give the type.
    /// </summary>
    internal static (string ChangeType, int FromVersion)[] ModelVersionsToInvalidate(int storedVersion,
        IReadOnlyList<ModelVersion> versions)
    {
        return NewVersions(storedVersion, versions)
            .SelectMany(v => v.Changes)
            .GroupBy(c => c.ChangeType, StringComparer.Ordinal)
            .Select(changes => (changes.Key, changes.Min(c => c.InvalidateFrom)))
            .ToArray();
    }
}
