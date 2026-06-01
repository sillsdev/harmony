using Microsoft.EntityFrameworkCore;

namespace SIL.Harmony;

public partial class DataModel
{
    /// <summary>
    /// Implementation of <see cref="Maintenance.DataModelMaintenance.ReseedProject"/>. See that method
    /// for the contract. Kept internal on a separate partial so the destructive op isn't part of the
    /// public DataModel surface.
    /// </summary>
    internal async Task ReseedProjectImpl(Guid clientId)
    {
        await using var repo = await _crdtRepositoryFactory.CreateRepository();
        using var locked = await repo.Lock();
        repo.ClearChangeTracker();

        // Load the whole chain in Harmony's canonical order: (DateTime, Counter, Id).
        var commits = await repo.CurrentCommits().AsNoTracking().ToArrayAsync();

        // --- Preconditions ---
        if (commits.Length == 0)
            throw new InvalidOperationException(
                "ReseedProject requires a non-empty commit chain; nothing was loaded to reseed.");

        var distinctClientIds = commits.Select(c => c.ClientId).Distinct().Count();
        if (distinctClientIds > 1)
            throw new InvalidOperationException(
                $"ReseedProject requires a single-author commit chain, but found {distinctClientIds} distinct ClientIds. " +
                "A multi-author chain is an already-authored chain, not a pre-built one — refusing to reseed it.");

        // The canonical order's final tiebreaker is Commit.Id. Because we mint fresh random Ids, any
        // two commits sharing an identical (DateTime, Counter) could be reordered relative to each other
        // after reseeding — which would change both the parent-hash linkage and the per-entity "latest
        // snapshot" winner. A single-author chain never produces such a tie (the HybridDateTimeProvider
        // bumps Counter on collision), so a tie here means this isn't the pre-built chain the API is for.
        // Refuse loudly rather than silently reorder. (commits are sorted, so ties are adjacent.)
        for (var i = 1; i < commits.Length; i++)
        {
            var previous = commits[i - 1].HybridDateTime;
            var current = commits[i].HybridDateTime;
            if (previous.DateTime == current.DateTime && previous.Counter == current.Counter)
                throw new InvalidOperationException(
                    $"ReseedProject requires every commit to have a unique (DateTime, Counter); commits " +
                    $"{commits[i - 1].Id} and {commits[i].Id} share {previous.DateTime:o} / {previous.Counter}. " +
                    "Re-minting Commit Ids would reorder them and break the chain.");
        }

        // --- Plan the rewrite ---
        // (DateTime, Counter) is unique (guarded above), so the new-Id sort order equals the current
        // order; we can chain hashes in the loaded order directly. Mint all new Ids up front.
        var plan = new (Guid OldId, Guid NewId, string Hash, string ParentHash)[commits.Length];
        var parentHash = CommitBase.NullParentHash;
        for (var i = 0; i < commits.Length; i++)
        {
            var newId = Guid.NewGuid();
            var hash = CommitBase.GenerateHash(newId, parentHash);
            plan[i] = (commits[i].Id, newId, hash, parentHash);
            parentHash = hash;
        }

        // --- Apply, atomically ---
        // Mirror DataModel.Add's transaction guard so a caller that wraps this in an outer transaction
        // doesn't trigger a nested-transaction error.
        await using var transaction = repo.IsInTransaction ? null : await repo.BeginTransactionAsync();

        // Phase 1: insert the re-identified commits alongside the originals (Ids differ, no PK clash).
        // DateTime/Counter/Metadata are copied verbatim from the original row; Id/ClientId/Hash/ParentHash
        // are the new values.
        foreach (var (oldId, newId, hash, newParentHash) in plan)
        {
            await repo.ExecuteSqlAsync($"""
                INSERT INTO "Commits" ("Id", "ClientId", "DateTime", "Counter", "Metadata", "Hash", "ParentHash")
                SELECT {newId}, {clientId}, "DateTime", "Counter", "Metadata", {hash}, {newParentHash}
                FROM "Commits" WHERE "Id" = {oldId}
                """);
        }

        // Phase 2: re-point every ChangeEntities / Snapshots row off the original commit onto the new one.
        foreach (var (oldId, newId, _, _) in plan)
        {
            await repo.ExecuteSqlAsync($"""UPDATE "ChangeEntities" SET "CommitId" = {newId} WHERE "CommitId" = {oldId}""");
            await repo.ExecuteSqlAsync($"""UPDATE "Snapshots" SET "CommitId" = {newId} WHERE "CommitId" = {oldId}""");
        }

        // Defensive: both child FKs are ON DELETE CASCADE, so if any row still referenced an original
        // commit the phase-3 DELETE would silently cascade-delete content. Verify none do before deleting.
        var oldIds = Array.ConvertAll(plan, p => p.OldId);
        var dangling = await repo.CountReferencesToCommits(oldIds);
        if (dangling != 0)
            throw new InvalidOperationException(
                $"ReseedProject FK rewrite is incomplete: {dangling} ChangeEntities/Snapshots row(s) still " +
                "reference the original commit Ids. Aborting before delete to avoid cascade data loss.");

        // Phase 3: delete the now-orphaned original commits.
        foreach (var (oldId, _, _, _) in plan)
        {
            await repo.ExecuteSqlAsync($"""DELETE FROM "Commits" WHERE "Id" = {oldId}""");
        }

        if (transaction is not null) await transaction.CommitAsync();
    }
}
