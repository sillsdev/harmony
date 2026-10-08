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
        var stored = await repo.GetLocalState<StoredModelVersions>(StoredModelVersions.LocalStateKey)
                     ?? StoredModelVersions.None;
        var current = Config.ModelVersions;
        EnsureAppliedVersionsUnchanged(stored, current);
        if (stored.Version == current.Count)
            return new ModelVersionReconcileResult(stored.Version, current.Count, false, null);

        var fullRegeneration = false;
        Commit? replayFrom;
        if (current.Count < stored.Version || current.Skip(stored.Version).Any(v => v.Major))
        {
            //a downgrade is rare and the snapshots were made by newer code, so start over
            fullRegeneration = true;
            replayFrom = await repo.CurrentCommits().FirstOrDefaultAsync();
            await SnapshotReplay.RegenerateAll(repo, Config);
        }
        else
        {
            replayFrom = await repo.FindOldestCommitWithChangeVersions(ChangeVersionsToReplay(stored.Version, current));
            if (replayFrom is not null)
            {
                await SnapshotReplay.UpdateSnapshots(repo, await repo.ReplayWindowFrom(replayFrom), Config);
            }
        }

        if (replayFrom is not null)
        {
            logger.LogInformation(
                "Model version changed from {StoredVersion} to {CurrentVersion}, replayed from commit {CommitId}, full regeneration: {FullRegeneration}",
                stored.Version, current.Count, replayFrom.Id, fullRegeneration);
        }

        await repo.SetLocalState(StoredModelVersions.LocalStateKey, StoredModelVersions.From(current));
        await transaction.CommitAsync();
        return new ModelVersionReconcileResult(stored.Version, current.Count, fullRegeneration, replayFrom);
    }

    private static void EnsureAppliedVersionsUnchanged(StoredModelVersions stored, IReadOnlyList<ModelVersion> current)
    {
        var applied = Math.Min(stored.Versions.Length, current.Count);
        for (var i = 0; i < applied; i++)
        {
            if (stored.Versions[i].SameAs(current[i])) continue;
            throw new InvalidOperationException(
                $"Model version {i + 1} ({current[i].Name}) was modified after it was applied to this database. " +
                "Released model versions must not change, add a new version instead.");
        }
    }

    /// <summary>
    /// For each change type in the versions after <paramref name="storedVersion"/>, the lowest change version to replay:
    /// the lowest <see cref="ModelVersionChange.InvalidateFrom"/> those versions give the type.
    /// </summary>
    internal static (string ChangeType, int FromVersion)[] ChangeVersionsToReplay(int storedVersion,
        IReadOnlyList<ModelVersion> versions)
    {
        return versions.Skip(storedVersion)
            .SelectMany(v => v.Changes)
            .GroupBy(c => c.ChangeType, StringComparer.Ordinal)
            .Select(changes => (changes.Key, changes.Min(c => c.InvalidateFrom)))
            .ToArray();
    }
}
