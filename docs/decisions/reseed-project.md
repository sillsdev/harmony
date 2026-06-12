# `DataModelMaintenance.ReseedProject` — design record

**Status:** **implemented on Harmony** (branch `reseed-project-api`, based on harmony `96a75b2`) **and wired up on the lexbox side** (§11), with one manual step outstanding: `template.sql` must be regenerated (needs FwData) before the template path works, because the new `ApplyAsync` no longer substitutes the placeholders the current `template.sql` still contains. The implementation was reviewed against the live codebase before landing; the adaptations made versus the original design are listed below.

> **Design evolved past §11 as written.** The lexbox side no longer substitutes WS placeholders into the SQL. The template ships *without* a vernacular WS, `ApplyAsync` is now a pure parameterless loader, and `CreateProjectFromTemplate` does **load → `ReseedProject` → `CreateWritingSystem`** for the vernacular WS. This removes all SQL string-substitution (and its injection surface).
>
> Note on *how* the WS leaves the template: excluding it during the FW→CRDT import does **not** work — the importer queries entries, and `EnsureWritingSystemIsPopulated` requires a default vernacular WS, so a WS-less import throws. The generator therefore imports normally, then deletes the vernacular WS's commit (which cascades its change + snapshot), deletes its projected row, and calls `ReseedProject` to rehash the shortened chain before dumping `template.sql`. See `TEMPLATE-FOLLOWUPS.md` in lexbox for the current shape.

**Audience:** the next agent (potentially a different person) who will wire this up on the lexbox side (§11). This doc captures everything needed to implement without ambiguity *and* the decision history so future maintainers understand why each choice was made.

---

## 0. What landed, and how it differs from the original design

The Harmony side is implemented. Files added: `src/SIL.Harmony/Maintenance/DataModelMaintenance.cs` (public façade) and `src/SIL.Harmony/Maintenance/DataModel.Reseed.cs` (internal `DataModel.ReseedProjectImpl`); tests in `src/SIL.Harmony.Tests/Maintenance/ReseedProjectTests.cs` (12 tests, all green). Files modified: `DataModel.cs` (→ `partial`), `Db/CrdtRepository.cs` (added internal `ExecuteSqlAsync` + `CountReferencesToCommits`), and `SIL.Harmony.Core/CommitBase.cs` (see below).

Adaptations versus the design as originally written — each verified against the code:

1. **Added a third precondition: every commit's `(DateTime, Counter)` must be unique.** This was the design's one real blind spot. Harmony's canonical order (`QueryHelpers.DefaultOrder` / `CommitBase.CompareKey`) breaks ties on `Commit.Id`. Because the reseed mints fresh **random** Guids, two commits sharing an identical `(DateTime, Counter)` would be reordered relative to each other afterward — silently changing both the parent-hash linkage *and* which same-timestamp write wins a projected snapshot (`CrdtRepository` picks the latest by `CompareKey`). A single-author chain never produces such a tie (`HybridDateTimeProvider` bumps `Counter` on collision), so the guard never fires on a valid pre-built chain — but it fails loudly instead of silently reordering. See §8.1.
2. **Reuse `CommitBase.GenerateHash`; no copied `ComputeHash`.** `CommitBase.GenerateHash` was refactored to expose a `public static string GenerateHash(Guid id, string parentHash)` (the instance method now delegates to it). The reseed calls that static. This removes the duplicate hash implementation the design proposed — and the "Do not reorder" warning that came with it — eliminating a drift hazard.
3. **Defensive cascade check before deleting old commits.** Both child FKs (`ChangeEntities.CommitId`, `Snapshots.CommitId`) are `ON DELETE CASCADE`, not restrict. If phase 2 ever missed a row, phase 3's `DELETE` would silently cascade-delete content. The impl counts dangling references (`CountReferencesToCommits`) after phase 2 and throws before phase 3 if any remain.
4. **Mint all new Ids first, then chain hashes in canonical order.** Because the tie guard guarantees `(DateTime, Counter)` is unique, the new-Id sort order equals the loaded order, so hashes are chained in the order `ValidateCommits` will later read them back.
5. **Tempered the single-author precondition's framing** (it gates multi-author corruption but does **not** distinguish a template from a solo-authored project — see §1).
6. **Factual corrections to this doc** (verified against code): the projected-table shadow column is **`SnapshotId`** (`ObjectSnapshot.ShadowRefName`), not `__sk`; the FK is `OnDelete SetNull`. `CrdtRepository._dbContext` is `private` (not internal). `CrdtRepositoryFactory.CreateRepository()` returns `Task<CrdtRepository>`.

---

## 1. What this API is

```csharp
namespace SIL.Harmony.Maintenance;

public static class DataModelMaintenance
{
    public static Task ReseedProject(DataModel model, Guid clientId);
}
```

Given a `DataModel` whose commit chain was just bulk-loaded from a pre-built source (e.g. running a SQL template against the project's sqlite), `ReseedProject`:

1. Mints a new `Commit.Id` (`Guid.NewGuid()`) for every commit in the chain.
2. Sets every commit's `Commit.ClientId` to the caller's value.
3. Recomputes every commit's `Commit.Hash` and `Commit.ParentHash` in chain order against the new Ids.
4. Updates the `CommitId` foreign keys on `ChangeEntities` and `Snapshots` rows to point at the newly-minted Commit Ids.
5. Preserves everything else: `Commit.HybridDateTime`, `Commit.Metadata`, all `ChangeEntities` columns except CommitId, all `Snapshots` columns except CommitId, and the entire projected-entity-table state.

The whole operation runs in a single transaction. If any step throws, the chain is left untouched.

### Preconditions

| | |
|---|---|
| Commit chain must be non-empty | Empty chain → `InvalidOperationException` (caller should handle the "nothing was loaded" case before calling). |
| Commit chain must be single-author | Every commit must share one `ClientId`. Multi-author → `InvalidOperationException`. This gate refuses to corrupt a *multi-author* chain. **Caveat:** it does **not** distinguish a pre-built template from an ordinary solo-authored project — both have exactly one ClientId — so it is *not* by itself "what makes the API safe to expose." The real protection is that lexbox only calls `ReseedProject` on the create-from-template path. |
| Every commit's `(DateTime, Counter)` must be unique | Duplicate → `InvalidOperationException`. The canonical order breaks ties on `Commit.Id`; minting fresh random Ids would silently reorder tied commits. A single-author chain never produces a tie, so this is a corollary of the single-author gate, enforced explicitly. See §8.1. |

### Why the caller provides `clientId` rather than Harmony picking one

Harmony's existing API treats `clientId` as a per-call parameter (`AddChange(clientId, ...)`, `AddChanges(clientId, ...)`). Harmony never tracks "the current project's clientId" anywhere. Lexbox owns `ProjectData.ClientId`. Making `ReseedProject` invent a clientId and return it would flip the ownership arrow for one method and break that consistency.

---

## 2. Why this exists

**Driving use case:** Lexbox (FwLite + LexBox server) ships pre-built CRDT projects as a single SQL "template" file. New users bootstrap a project by:
1. Running the template SQL into their sqlite (gets a chain authored by the template-source project).
2. Inserting a fresh `ProjectData` row with new identity.
3. Re-identifying the chain so the commits are theirs (this method).

**Why re-identify at all:** Without this step, every project bootstrapped from one template would share byte-identical Commit Ids in its local chain. The server-side LexBox table `CrdtCommits` was updated in PR #2281 to a composite `(ProjectId, Id)` primary key so cross-project commit-Id overlap doesn't break the server insert. **But the cross-project overlap is still a foot-gun:**

- `Dictionary<Guid, Commit>` keyed on Id alone would conflate commits from different projects.
- Logs/traces quoting "Commit `abc-123` failed to sync" would be ambiguous.
- Future Harmony features that hash-address content for dedup could silently merge unrelated histories.

Team-lead position (paraphrased): *"We rewrite `ClientId` on import to keep client identity coherent across projects. The same logic applies to `Commit.Id`. Pay the build-cost once; don't accept the vigilance-cost forever."*

---

## 3. Naming decisions

**Final names:**
- Namespace: `SIL.Harmony.Maintenance`
- Class: `DataModelMaintenance`
- Method: `ReseedProject` (no `Async` suffix)

### `Async` suffix decision

**Decision:** no `Async` suffix.

**Why:** Harmony's public async methods on `DataModel` are consistently unsuffixed: `AddChange`, `AddChanges`, `AddManyChanges`, `SyncWith`, `SyncMany`, `GetProjectSnapshot`, `RegenerateSnapshots`, `GetLatest`, etc. Adding `Async` here would be inconsistent.

If you're tempted to add it back: don't, unless you're also renaming the rest of `DataModel`'s public async surface.

### Method name: alternatives rejected

| Candidate | Why rejected |
|---|---|
| `SeedChain` | "Seed" implies creating from nothing; the chain exists at call time. |
| `Hydrate` | Vague — applies to many serialization scenarios. |
| `Import` | Strong implication of "bringing in an existing project for sync." Renaming Ids of a project you intended to sync with would be silent data loss. The metaphor is exactly backwards. |
| `Reinitialize` | "Re-" wrongly suggests the chain was once initialized differently. |
| `InstantiateChain` | Programmer-formal but reads heavy. |
| `Bootstrap` | Overloaded — already means runtime/system init in many domains. |
| `Claim` / `Adopt` | Short but abstract; could be misread as "lock for me." |
| `Reidentify` | Most literal but clinical, and uses "identity" — a word that isn't in Harmony's vocabulary. Use specific terms (`Commit.Id`, `ClientId`, `Hash`) instead. |
| `Realize` | Metaphorical, poetic. |
| `ReseedAsync` (drop "Project") | Loses too much context. "Reseed" alone is ambiguous in this domain (could imply re-seeding entities, the canonical morph-types catalog, etc.). "Project" pins the scope to the whole chain. |
| `Rebrand`, `Reissue`, `Reattribute` | Either too informal or too obscure (`Reattribute` is provenance-literature jargon). |

`ReseedProject` won because:
- "Seed" / "reseed" matches the existing codebase vocabulary (`SeedNewProjectData`, `AddPredefinedMorphTypes` "seeds" canonical data, the design doc references "seed commits").
- "Re-" disambiguates from "seed alone" — the chain already exists.
- "Project" scopes the operation to the whole chain (mirrors the only other Project-as-noun method in Harmony: `GetProjectSnapshot`).

### Class / namespace: alternatives rejected

| Candidate | Why rejected |
|---|---|
| `SIL.Harmony.Templates.*` | "Template" is lexbox vocabulary. Harmony has no notion of templates. A reader of Harmony seeing `Templates.*` would expect a `CreateTemplate` API too — leaky abstraction. |
| `SIL.Harmony.Identity.*` | The word "identity" isn't in Harmony's vocabulary either. |
| `SIL.Harmony.Provenance.ChainRebaser.Reattribute` | Technically precise per Shapiro et al. CRDT papers and W3C PROV-DM, but inside-baseball. Future maintainers won't reach for "provenance" or "rebaser." |
| `SIL.Harmony.ImportedChain.ReissueIdentities` | "Imported" is exactly the misleading word noted above; namespace name reinforces the wrong misreading. |
| `SIL.Harmony.Bootstrap.*` | Generic but unhelpfully so; also overlaps with the rejected method-name `Bootstrap`. |

`SIL.Harmony.Maintenance` won because:
- Generic in the right way: catches future "operations on a chain that aren't normal CRDT mutation" (verify integrity, vacuum, reset to commit, etc.) without leaking caller vocabulary.
- Word "maintenance" carries the right connotation: this is a tool you reach for in specific circumstances, not a general-purpose method.

`DataModelMaintenance` follows the .NET convention of `<noun><role>` (`StringBuilder`, `HttpClientFactory`). Reads as "the Maintenance ops for a DataModel."

---

## 4. C# shape decisions

### Static class on a separate type, NOT extension method

**Decision:** plain `public static class` with `public static Task ReseedProject(DataModel model, Guid clientId)`. **NOT** an extension method on `DataModel`.

**Why:** Friction-at-call-site is the point. The op is destructive when misused. Comparison:

| | Extension method | Static method on class |
|---|---|---|
| Call site | `await dataModel.ReseedProject(clientId);` | `await DataModelMaintenance.ReseedProject(dataModel, clientId);` |
| After `using SIL.Harmony.Maintenance;` is added to a file (for any reason) | Method appears in `dataModel.<dot>` autocomplete throughout the file forever. Indistinguishable from regular DataModel methods. | Still requires explicit `DataModelMaintenance.<dot>` reach-out. Visually distinct. Greppable. |

The whole point of putting this in `Maintenance` namespace is gating. Extension methods *defeat that gate* once any file in the project imports the namespace for any reason. Static class keeps the gate up indefinitely.

### NOT on `DataModel` directly (plain instance method)

**Decision rejected because:** too accessible. Shows in `dataModel.<dot>` autocomplete next to `AddChange` / `SyncWith` etc. The op is one-shot and destructive; making it look like a regular method invites misuse.

### NOT explicit-interface implementation (`((IReseedable)dataModel).ReseedProject(...)`)

**Decision rejected because:** Harmony uses explicit-interface internally for `ISyncable.AddRangeFromSync`, but **lexbox doesn't actually use that pattern** — lexbox calls `dataModel.SyncWith(...)` directly. Inventing a cast-based convention specifically for this method would be inconsistent with how Harmony is actually consumed.

### NOT a capability token (`ReseedProject(model, TemplateSeedToken.Acquire(), clientId)`)

**Decision rejected because:** no .NET precedent. Ugly. Token-based capability gating isn't a pattern users recognize.

### NOT a nested-operations property (`dataModel.Maintenance.ReseedProject(...)`)

**Decision rejected because:** `dataModel.Maintenance.<dot>` still autocompletes once you find `.Maintenance`. The "have to reach for a separate static class" gating is stronger.

---

## 5. Where the code lives

**Files to create (paths relative to `backend/harmony/`):**

```
src/SIL.Harmony/Maintenance/DataModelMaintenance.cs
src/SIL.Harmony/Maintenance/DataModel.Reseed.cs            ← internal partial of DataModel
src/SIL.Harmony.Tests/Maintenance/ReseedProjectTests.cs    ← test class
```

**File to modify:**

```
src/SIL.Harmony/DataModel.cs
```

Change `public class DataModel : ISyncable, IAsyncDisposable` → `public partial class DataModel : ISyncable, IAsyncDisposable`. (The implementation needs access to `DataModel`'s private `_crdtRepositoryFactory`. Making `DataModel` partial and placing the implementation method in a separate partial-class file is the cleanest separation — keeps `DataModel.cs` from bloating.)

### `DataModelMaintenance.cs` shape

```csharp
namespace SIL.Harmony.Maintenance;

public static class DataModelMaintenance
{
    /// <summary>{see XML-doc proposal below}</summary>
    public static Task ReseedProject(DataModel model, Guid clientId)
        => model.ReseedProjectImpl(clientId);
}
```

### `DataModel.Reseed.cs` shape

```csharp
namespace SIL.Harmony;

public partial class DataModel
{
    internal async Task ReseedProjectImpl(Guid clientId)
    {
        // implementation — see section 6 for the SQL approach
    }
}
```

---

## 6. Implementation approach (chosen)

### The high-level algorithm

1. Load all commits from the local DB in chain order: `OrderBy(c => c.HybridDateTime.DateTime).ThenBy(c => c.HybridDateTime.Counter).ThenBy(c => c.Id)`.
2. Validate preconditions (non-empty, single-author, **and no two commits share `(DateTime, Counter)`** — see §8.1). Throw `InvalidOperationException` on failure.
3. Mint a fresh `Guid` for every commit up front, then walk the chain in canonical order. For each commit:
    - Compute `Hash` via `CommitBase.GenerateHash(newId, parentHash)` (the existing algorithm — no re-implementation; see §6.1).
    - Record `(oldId → (newId, hash, parentHash))`. Carry `parentHash` forward to the next commit.
    - (Because `(DateTime, Counter)` is unique, the new-Id sort order equals the loaded order, so chaining in the loaded order matches what `ValidateCommits` will later read.)
4. Begin a transaction.
5. **SQL phase 1: INSERT new commit rows.** For each commit, `INSERT INTO Commits` a new row with `(newId, newClientId, oldDateTime, oldCounter, oldMetadata, newHash, newParentHash)`. Old commits still exist; new commits live alongside (no PK collision because Ids are different).
6. **SQL phase 2: rewrite FK references.** For each commit, `UPDATE ChangeEntities SET CommitId = {newId} WHERE CommitId = {oldId}`, and same for `Snapshots`. Now all FKs point at new commits; old commits are orphaned (no incoming FK refs).
7. **Safety check.** Count `ChangeEntities`/`Snapshots` rows still referencing any old `CommitId` (`CrdtRepository.CountReferencesToCommits`); throw if non-zero. Both child FKs are `ON DELETE CASCADE`, so a missed re-point in phase 2 would otherwise make phase 3 silently cascade-delete content.
8. **SQL phase 3: DELETE old commit rows.** For each commit, `DELETE FROM Commits WHERE Id = {oldId}`. Safe because phase 2 re-pointed everything and step 7 confirmed it.
9. Commit transaction.

### Why this order (INSERT/UPDATE/DELETE), not in-place UPDATE Commits.Id

FK constraints on `ChangeEntities.CommitId` and `Snapshots.CommitId` would fire mid-update if we did `UPDATE Commits SET Id = newId` first. Two options:

- **Approach considered:** `PRAGMA defer_foreign_keys = ON` (SQLite-specific) defers FK checks to transaction commit time. Works but couples Harmony to SQLite — Harmony is supposed to be DB-agnostic at the EF Core layer.
- **Approach chosen:** INSERT new / UPDATE FKs / DELETE old. DB-agnostic; works on SQLite, Postgres, etc. ~3× more SQL statements but still bulk operations on small data (typical template: ~17 commits, hundreds of ChangeEntities/Snapshots). Sub-50ms.

### 6.1 Hash algorithm — reuse, don't reimplement

**Do not copy the hash logic.** `CommitBase.GenerateHash` was refactored to expose a static overload, and the reseed calls it directly:

```csharp
// SIL.Harmony.Core/CommitBase.cs
public string GenerateHash(string parentHash) => GenerateHash(Id, parentHash);
public static string GenerateHash(Guid id, string parentHash) { /* idBytes + parentHashBytes → XxHash64 → hex */ }

// reseed:
var hash = CommitBase.GenerateHash(newId, parentHash);
```

This is the single source of truth; if Harmony ever changes its hashing, the reseed moves with it (and would fail to compile / fail `ValidateCommits` rather than silently diverge). For reference:

- `XxHash64.Hash` returns 8 bytes → 16 hex characters.
- `parentHash` for the first commit is `CommitBase.NullParentHash` (the string `"0000"`).

### 6.2 SQL details

**Table column names (exact case, double-quote in SQL):**

| Table | Columns |
|---|---|
| `Commits` | `"Id"`, `"Hash"`, `"ParentHash"`, `"DateTime"`, `"Counter"`, `"Metadata"`, `"ClientId"` |
| `ChangeEntities` | `"CommitId"`, `"Index"`, `"EntityId"`, `"Change"` (PK = `("CommitId", "Index")`) |
| `Snapshots` | `"Id"`, `"CommitId"`, `"EntityId"`, ... (PK = `"Id"`, unique index on `("CommitId", "EntityId")`) |

Note: `HybridDateTime` is configured as a *complex property* on `Commit` in `CommitEntityConfig.cs`, with column names `DateTime` (a `DateTimeOffset` stored as `UtcDateTime`) and `Counter` (a `long`). The columns are at the top level of the `Commits` table, not nested.

**Execution:** use `dbContext.Database.ExecuteSqlInterpolatedAsync($"...")` for all SQL. Parameterization is handled automatically via `FormattableString` — never string-concat values into SQL.

**Example INSERT for phase 1** (FormattableString — `{newId}` etc. parameterize safely):

```csharp
await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
    INSERT INTO ""Commits"" (""Id"", ""ClientId"", ""DateTime"", ""Counter"", ""Metadata"", ""Hash"", ""ParentHash"")
    SELECT {newId}, {clientId}, ""DateTime"", ""Counter"", ""Metadata"", {newHash}, {newParentHash}
    FROM ""Commits"" WHERE ""Id"" = {oldId}");
```

**Where to get the DbContext:** `_crdtRepositoryFactory.CreateRepository()` returns a `Task<CrdtRepository>`. The repository's `_dbContext` is `private`, so a small internal helper was added to `CrdtRepository`:

```csharp
internal Task<int> ExecuteSqlAsync(FormattableString sql)
    => _dbContext.Database.ExecuteSqlInterpolatedAsync(sql);
```

The reseed forwards interpolated SQL through this (parameterized via `FormattableString` — never string-concat). Kept internal and narrow so the raw-SQL surgery stays contained and greppable.

### 6.3 Concurrency / locking

`CrdtRepository.Lock()` returns an `IDisposable` that holds the per-database lock. The implementation should acquire the lock before doing any work, same pattern as `DataModel.Add`:

```csharp
await using var repo = await _crdtRepositoryFactory.CreateRepository();
using var locked = await repo.Lock();
repo.ClearChangeTracker();
await using var transaction = repo.IsInTransaction ? null : await repo.BeginTransactionAsync();
// ... SQL phases 1, 2, 3 ...
if (transaction is not null) await transaction.CommitAsync();
```

---

## 7. XML doc proposal for the method

Use this docstring on `DataModelMaintenance.ReseedProject`. **Terminology rules:** use existing Harmony vocabulary (`Commit`, `Commit.Id`, `ClientId`, `ChangeEntity`, `ObjectSnapshot`, `Hash`, `ParentHash`). Don't use the word "identity" / "identities" — Harmony doesn't use them. The phrase "commit chain" is OK because it qualifies the bare word "chain" which is new.

```csharp
/// <summary>
/// Mints a new <see cref="Commit.Id"/> for every commit in this DataModel's commit chain,
/// sets every commit's <see cref="Commit.ClientId"/> to <paramref name="clientId"/>, and
/// recomputes the chain's <see cref="Commit.Hash"/> / <see cref="Commit.ParentHash"/>
/// against the new Ids. Commit content — <see cref="ChangeEntity{T}"/> rows and
/// <see cref="ObjectSnapshot"/> rows — is preserved exactly, with their <c>CommitId</c>
/// foreign keys updated to point at the new Commit Ids.
/// </summary>
/// <remarks>
/// <para>
/// Intended for first-time bootstrap of a project from a pre-built commit chain (for
/// example, one bulk-loaded from a SQL snapshot at project creation). NOT for syncing a
/// chain in from another peer — for that, use the sync protocol via
/// <see cref="DataModel.SyncWith"/>, which preserves Commit Ids so peer histories can
/// reconcile.
/// </para>
/// <para>
/// Refuses to run on a commit chain whose commits have multiple distinct
/// <see cref="Commit.ClientId"/> values — that's the shape of an already-authored chain,
/// not a pre-built one. Refusing prevents corrupting a chain that's been edited.
/// </para>
/// <para>
/// All SQL runs in a single transaction. The chain is left untouched if any step fails.
/// </para>
/// </remarks>
/// <param name="model">The <see cref="DataModel"/> whose commit chain will be reseeded.</param>
/// <param name="clientId">The ClientId to set on every commit in the chain.</param>
/// <exception cref="InvalidOperationException">
/// Thrown if the commit chain is empty, or if its commits have more than one distinct ClientId.
/// </exception>
```

---

## 8. What gets preserved vs rewritten

| Column | Touched? | Notes |
|---|---|---|
| `Commits.Id` | **rewritten** | Fresh `Guid.NewGuid()` per commit. |
| `Commits.ClientId` | **rewritten** | Set to the caller's value uniformly across all commits. |
| `Commits.Hash` | **recomputed** | From new Id + new ParentHash. |
| `Commits.ParentHash` | **recomputed** | Chain order = sort order on (HybridDateTime, Counter, Id). |
| `Commits.DateTime` (HybridDateTime) | preserved | Chain order is preserved. |
| `Commits.Counter` (HybridDateTime) | preserved | |
| `Commits.Metadata` | preserved | Bytes flow through INSERT…SELECT verbatim. |
| `ChangeEntities.CommitId` | **rewritten** (FK update) | Follows the new Commit.Id. |
| `ChangeEntities.Index` | preserved | |
| `ChangeEntities.EntityId` | preserved | Canonical entity Ids (morph types, parts of speech, etc.) must NOT change — they're how data is keyed semantically. |
| `ChangeEntities.Change` (JSONB) | preserved | The serialized IChange. |
| `Snapshots.Id` | **preserved** | NOT rewritten — see "open design question" below. |
| `Snapshots.CommitId` | **rewritten** (FK update) | Follows the new Commit.Id. |
| `Snapshots.EntityId` | preserved | |
| `Snapshots.Entity` (JSONB) | preserved | |
| Projected entity tables (`MorphType`, `PartOfSpeech`, etc., when `EnableProjectedTables` is on) | **untouched entirely** | Their shadow column `SnapshotId` (`ObjectSnapshot.ShadowRefName`) references `Snapshots.Id`, which is preserved. The FK is `OnDelete SetNull`. |

---

### 8.1 Chain order and the `(DateTime, Counter)` tie guard

Harmony's canonical commit order is `(HybridDateTime.DateTime, HybridDateTime.Counter, Id)` — defined once in `QueryHelpers.DefaultOrder` / `CommitBase.CompareKey`, and used by both the parent-hash build (`CrdtRepository.UpdateCommitHashes`) and validation (`DataModel.ValidateCommits`). `Id` is the **final tiebreaker**.

The reseed mints fresh **random** Guids. So for any two commits with an identical `(DateTime, Counter)`, their pre-reseed relative order (old-Guid comparison) and post-reseed order (new-Guid comparison) are uncorrelated — they can swap. That swap would change the parent-hash linkage *and* which of two same-timestamp writes wins a projected snapshot (the snapshot worker also picks the latest by `CompareKey`). The doc's "chain order is preserved" guarantee therefore holds **only if no two commits share `(DateTime, Counter)`**.

A single-author chain never produces such a tie: `HybridDateTimeProvider.GetDateTime` bumps `Counter` whenever the clock doesn't advance, so one author's commits always have strictly increasing `(DateTime, Counter)`. That is *why* the single-author precondition is load-bearing beyond its "don't corrupt an authored chain" role. Rather than rely on that implicitly, the impl **explicitly throws** if it finds a tie — a future multi-source or hand-built template that introduced one would fail loudly instead of silently reordering. (Hashes are chained in the loaded order, which — given uniqueness — equals the order `ValidateCommits` reads back.)

## 9. Open design question

**Should `Snapshots.Id` also be rewritten to fresh Guids?**

**Argument FOR rewriting:**
- Consistency: if Commit.Ids change, why not Snapshot.Ids? Both are CRDT-internal identifiers.
- Future-proofing against features that key cross-project structures on Snapshot.Id.

**Argument AGAINST:**
- More SQL: each `Snapshots.Id` change would require updating the `SnapshotId` shadow column on every projected-entity-table row that references that snapshot. (And since that FK is `OnDelete SetNull`, a naive rewrite that didn't update it would *silently null* the projected rows rather than error — making a Snapshots.Id rewrite more dangerous than it looks.)
- `Snapshots.Id` doesn't appear on the sync wire. The sync protocol exchanges `Commit` objects (which embed `ObjectSnapshot` instances), but `Snapshot.Id` isn't part of the protocol's identity contract — the receiving side projects its own snapshots.
- No concrete foot-gun has been identified for cross-project Snapshot.Id collision.

**This design says NO** to rewriting `Snapshots.Id`. If a future need surfaces (e.g. a feature that does cross-project content-addressing on Snapshot.Id), it can be added as an extension to `ReseedProject` without breaking existing callers.

**Same answer for `ChangeEntities`:** it has no independent Id column — its identity is `(CommitId, Index)`, which is implicitly updated when CommitId changes. No separate rewrite needed.

The user briefly suggested ChangeEntity might need fresh Ids. ChangeEntity has none to rewrite — the (CommitId, Index) pair effectively becomes a new identity when CommitId is rewritten. No action needed.

---

## 10. Tests (implemented)

`src/SIL.Harmony.Tests/Maintenance/ReseedProjectTests.cs`, inheriting `DataModelTestBase` — 12 tests, all green. The table below is the original plan; what landed matches it, plus:
- **`ReseedProject_ThrowsOnDuplicateHybridDateTime`** — added for the tie guard (§8.1). Two `WriteChange(client, sameDate, …)` calls collide because the mock clock sets `Counter = 0` for both; reseed must throw.
- `ReseedProject_LeavesChainUntouchedWhenAPreconditionFails` covers the §10 "IsAtomicOnFailure" intent for the cheap pre-write guards (a forced mid-SQL failure is left out — the single transaction makes it implicit).

Fixture notes for whoever extends these: `DataModelTestBase.WriteChange` overloads are `protected` and **require** a `DateTimeOffset` (no 2-arg `WriteChange(clientId, change)`); the integrity path throws `CommitValidationException` (a specific subtype), not a generic exception; `DbContext` is the typed `SampleDbContext` (use `AsNoTracking()` for post-reseed reads since the surgery is raw SQL).

Each test should:
1. Set up a multi-commit chain on the DataModel (via `WriteNextChange` for a few canonical entities).
2. Optionally mutate the chain so it looks like a pre-built one (e.g. set all commits' ClientId to a single template-source-style value).
3. Call `DataModelMaintenance.ReseedProject(DataModel, newClientId)`.
4. Assert.

### Required test methods

| Test | What it asserts |
|---|---|
| `ReseedProject_MintsFreshCommitIds` | After the call, none of the pre-reseed Commit.Ids exist in the DB. All new Commit.Ids are distinct. |
| `ReseedProject_SetsClientIdOnAllCommits` | Every `Commits.ClientId` equals the passed-in clientId. |
| `ReseedProject_RecomputesHashesCorrectly` | For each commit in chain order, `Commit.Hash == ComputeHash(Commit.Id, parentHash)` where `parentHash` is `"0000"` for the first and `previousCommit.Hash` thereafter. |
| `ReseedProject_PreservesChangeEntities` | The set of `(Index, EntityId, Change-JSON)` triples in `ChangeEntities` (sorted) is identical pre- and post-reseed. Only `CommitId` columns change. |
| `ReseedProject_PreservesSnapshots` | Same, for `Snapshots.(Id, EntityId, Entity-JSON)`. Only `CommitId` changes. |
| `ReseedProject_PreservesProjectedTables` | Row counts and content of projected entity tables (e.g. `Word`, the sample model in tests) are identical pre- and post-reseed. |
| `ReseedProject_PreservesChainOrder` | Order of commits by `(HybridDateTime.DateTime, Counter, Id)` (where `Id` is the NEW Id) matches the pre-reseed order. |
| `ReseedProject_HashChainValidatesAfterReseed` | After reseed, `DataModel.AddChange(clientId, someNewChange)` succeeds. Implicitly validates the hash chain — `AddChange`'s `ValidateCommits` would throw on a broken chain. |
| `ReseedProject_ThrowsOnMultiAuthorChain` | Set up a chain with commits from 2 different ClientIds (use `WriteChange(clientA, ...)` then `WriteChange(clientB, ...)`). Calling ReseedProject throws `InvalidOperationException`. |
| `ReseedProject_ThrowsOnEmptyChain` | Fresh DataModel with no commits. Calling ReseedProject throws `InvalidOperationException`. |
| `ReseedProject_IsAtomicOnFailure` | Force a failure mid-operation (e.g. by passing a clientId that fails some hypothetical check, or by using a mock that throws on the third commit). Assert the original chain is unchanged — no partially-rewritten state. (Skip if too hard to engineer cleanly; the single-transaction guarantee should make this implicit.) |

### Sample fixture usage

`DataModelTestBase` provides `WriteNextChange(IChange)` which adds a commit via the normal path. For "make this look like a freshly-loaded pre-built chain," you'll want to either:
- Use a single ClientId for all `WriteChange(clientId, ...)` calls — the default `_localClientId` is fine.
- Or set up specifically: `WriteChange(templateSourceClientId, dt, change)` for several commits with one template-source-style clientId.

The existing test `DataModelIntegrityTests.InvalidCommitHashesResultInException` shows how to break the chain hash directly — useful pattern for the "hashes are correct after reseed" tests (compare actual `commit.Hash` against expected via the helper).

---

## 11. Caller-side workflow (the next agent's deliverable AFTER this)

This is what needs to happen on the lexbox side once the Harmony API is in place. **Do not do this work as part of the Harmony PR** — it's a follow-up on `sillsdev/languageforge-lexbox`.

### Current lexbox state (branch `claude/investigate-issue-1920-f6yHG`)

`ProjectTemplate.ApplyAsync` currently substitutes three placeholders in the template SQL:
- `{{vernacular-ws-id}}` / `{{vernacular-name}}` / `{{vernacular-abbr}}` — per-project writing-system parameters.
- `{{client-id}}` — substituted with `projectData.ClientId` so loaded commits have the new client's identity baked in.

After this Harmony API lands, the caller workflow becomes:

```csharp
// In CrdtProjectsService.CreateProjectFromTemplate (approximate):
await ProjectTemplate.ApplyAsync(template, sqliteFile, vernacularWs);     // bulk SQL load (WS placeholders only)
await InsertProjectDataRow(serviceScope, projectData);                     // identity row via EF
await DataModelMaintenance.ReseedProject(dataModel, projectData.ClientId); // mint fresh Commit Ids, set ClientId, rehash
```

### Lexbox-side changes pending

1. **Drop the `{{client-id}}` placeholder.** The template SQL can ship with the template-source's `ClientId` literally in `Commits.ClientId` columns — `ReseedProject` will rewrite it on apply. Remove:
    - `ProjectTemplate.ClientIdPlaceholder` constant.
    - The `.Replace(ClientIdPlaceholder, …)` call in `ApplyAsync`.
    - The `clientId` parameter on `ApplyAsync` — **and update every call site**, not just `GenerateTemplate`: `FindMissingMigrationsAgainstTemplate` (`ProjectTemplateTests.cs`, ~line 148) also passes a `Guid.NewGuid()` to `ApplyAsync`.
    - The corresponding substitution in `ProjectTemplateTests.GenerateTemplate`.
2. **Add the `DataModelMaintenance.ReseedProject` call** to `CrdtProjectsService.CreateProjectFromTemplate`, after `InsertProjectDataRow`.
3. **Regenerate `template.sql`** via `ProjectTemplateTests.GenerateTemplate` (the dev-tool Skip-Fact). Verify the regenerated template no longer has `{{client-id}}` placeholders.
4. **Update lexbox tests:**
    - `OpenProjectTests.CreateProjectFromTemplateAppliesRequestedIdentity` — add an assertion that every `Commits.ClientId` equals the new project's ClientId (not the template-source's).
    - Re-add `TemplatedProjects_HaveDisjointCommitIds` (it was removed when we landed the static-template approach; with `ReseedProject` in place, two templated projects' Commit.Ids should be disjoint again).
5. **Update `TEMPLATE-FOLLOWUPS.md`** in lexbox:
    - The "On commit-Id uniqueness" section is wrong as written (it documented the static-template approach we're reversing). Replace with: "Commit.Ids are unique per project, minted by `Harmony.DataModelMaintenance.ReseedProject` on project create." **Note the "permanent design" framing isn't confined to that one section** — the same now-reversed claim appears in the file's header preamble and in the header comment at the top of `ProjectTemplate.cs`; rewrite all of them.
    - The morph-types `MigrateDb` safety-net concern is unchanged: every templated project still gets two morph-types commits in its chain (one from the template, one from `MigrateDb`'s seeder), netting to a no-op. (Mechanism nuance: with the static template the two commits' Ids already diverge, so the no-op lands at the snapshot layer, not via a commit-Id match — don't trust the stale "resolves to `MorphTypesSeedCommitId`" comment in `CurrentProjectService`.) Still a candidate for the legacy-safety-net removal described in `TEMPLATE-FOLLOWUPS.md` ②.
6. **Verify `TemplateIsCurrentWithEfMigrations` (drift-detection test) still passes** after the regen.
7. **Ordering constraint:** `ReseedProject` must run at create time, **before the project is first opened**. The first open triggers `MigrateDb`, whose morph-types seeder authors a commit under the *new* project's ClientId — a chain reseeded after that point would briefly be multi-author and trip the single-author guard. The workflow above (ApplyAsync → InsertProjectDataRow → ReseedProject, all at create) already satisfies this; don't move the call later.
8. **Don't reseed through a stale DbContext.** The surgery is raw SQL on the repository's own context. If the caller's scoped `DbContext` had tracked the bulk-loaded commit rows, those tracked entities would be stale after the rewrite. Lexbox's current flow is safe (the template load is raw SQL, and `InsertProjectDataRow` only tracks the `ProjectData` row), but don't introduce an EF read of the loaded commits in the same scope before/around the `ReseedProject` call.

---

## 12. Related artifacts and history

- **Lexbox PR #2281** — "Wire up template-based project creation" — current home of the lexbox-side work. This Harmony PR is intended to plug into that PR's lexbox branch (`claude/investigate-issue-1920-f6yHG`).
- **Harmony PR #67** (`rebuild-commit-hashes-api`) — earlier attempt; exposed `RebuildCommitHashes` as a separate primitive. Superseded by this design. **Should be closed** before this Harmony PR lands. (Or repurposed if the reviewer wants to retain `RebuildCommitHashes` as a standalone primitive — but with `ReseedProject` covering the use case, it shouldn't be needed.)
- **Lexbox commits to be aware of on the target branch:**
    - `131347369` — "Template ships identity-free; rewrite Commits.ClientId on apply" — adds the `{{client-id}}` placeholder pattern that this Harmony API obsoletes.
    - `bf5c61760` — "Regenerate template.sql + add drift-detection test" — sets up the drift-detection mechanism. Stays useful.
    - `553efbe96` — "Drop per-project commit-Id hydration; keep template byte-static" — the static-template decision. The lexbox-side follow-up needs to *partially* reverse this: keep the byte-static template SQL shape, drop the static-Commit-Ids consequence by adding the `ReseedProject` call.
- **Lexbox issue #1920** — original spec for the template feature. This design matches the issue's step "change the commit ClientId after importing the template" and extends it to Commit.Id, matching the team-lead's preference.

---

## 13. Vocabulary glossary (terms Harmony uses, for the doc comment)

| Term | Use this | NOT this |
|---|---|---|
| The thing being rewritten | `Commit.Id` (or "Commit Ids" / "commit Ids") | "identifiers", "identities" |
| The chain of commits | "commit chain" (always qualified) | "chain" alone |
| The client tag on a commit | `ClientId` | "author", "author Id" |
| The hash | `Hash` / `ParentHash` (or "the chain's hashes") | "checksum", "fingerprint" |
| The change record | `ChangeEntity` | "change row", "operation" |
| The snapshot | `ObjectSnapshot` (often just "snapshot" inline) | "state record" |
| The whole DB-load operation | "bulk-load" / "pre-built commit chain" | "import" (overloaded; implies syncing existing) |
| What this method is for | "first-time bootstrap from a pre-built commit chain" | "initialize a template project" |

Stick to existing terms wherever possible; the goal of the docstring is to read as a natural continuation of the codebase's voice.
