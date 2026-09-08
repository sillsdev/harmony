# Property-based tests for the rollback-and-replay engine

> ## ⚠️ These tests currently fail on `main` — by design
>
> The **Tier 2** properties have found a **real bug in Harmony's incremental rollback
> engine** and are intentionally left red as a living reproduction (no engine fix and no
> test weakening). Tier 1 and the generator meta-tests pass.
>
> **Symptom:** a `SetDefinitionPartOfSpeechChange` on a `Definition` is *dropped* by the
> incremental rollback/resume when a straggler commit arrives, while a full from-scratch
> replay applies it. Reproduced two independent ways:
> - **Replica convergence** (`Tier2_Projection_IsIndependentOfArrivalOrder`): the same
>   commit set in two arrival orders projects to different `Definition.PartOfSpeech`.
> - **Incremental ≠ from-scratch** (`Tier2_IncrementalProjection_EqualsFromScratchReplay`):
>   feeding a schedule then calling `RegenerateSnapshots()` (the trusted oracle) on the
>   *same* engine yields different projected state. Since from-scratch replay of the
>   persisted commit log is ground truth, the incremental path is the wrong one.
>
> **Minimized shape (~15 commits):** create word + definition on it, `SetDefinitionPartOfSpeech`
> on the definition, then `DeleteDefinition` + `DeleteWord` (reference cascade) + a second
> `DeleteDefinition`, with the part-of-speech edit arriving as a straggler after later
> commits are already folded. This is the doc's "stale-snapshot / wrong rollback target"
> class; the suspected code is the resume-from-surviving-intermediate-snapshot path in
> `SnapshotWorker` interacting with `CrdtRepository.DeleteStaleSnapshots`.
>
> **Deterministic replay** (CsCheck reproduces the shrunk counterexample from a seed):
> ```bash
> CsCheck_Seed=0WhakBBZlgK1 dotnet test src/SIL.Harmony.Tests \
>   --filter-method "*Tier2_IncrementalProjection_EqualsFromScratchReplay*"
> ```
> (Seeds are tied to this CsCheck version/generator; re-run the property to mint a fresh
> one if they drift.)


These [CsCheck](https://github.com/AnthonyLloyd/CsCheck) property tests exercise
Harmony's incremental projection engine: the machinery that, when a commit arrives
with an author time *earlier* than commits already folded into the projection, rolls
back to earlier state and replays forward with the straggler slotted into canonical
order (`DataModel.AddRangeFromSync` → `CrdtRepository.AddNewCommits` /
`DeleteStaleSnapshots` → `SnapshotWorker`). This is the code most prone to off-by-one
rollback targets, tie mishandling, stale-snapshot invalidation, and snapshot aliasing,
and it was previously covered only by example-based tests.

The suite is inspired by a generic "rollback-and-replay projection engine" testing
handoff, adapted to Harmony's real types. The mapping:

| Generic concept | Harmony |
|---|---|
| A change event | A `Commit` (`HybridDateTime`, `ClientId`, `Id`, ordered `ChangeEntities`) |
| Canonical total order | `CommitBase.CompareKey = (HybridDateTime.DateTime, Counter, Id)` |
| Ingest a (possibly late/duplicate) batch | `((ISyncable)DataModel).AddRangeFromSync(commits)` |
| Rollback to checkpoint + replay | `DeleteStaleSnapshots` + `SnapshotWorker` |
| From-scratch replay | `DataModel.RegenerateSnapshots()` |
| Content-addressed dedup | Commit-`Id` dedup in `FilterExistingCommits` |

## Oracles

Harmony's projection logic (create/edit/delete/reference-cascade) is non-trivial, so a
"dead-simple sorted-fold oracle" would just duplicate `SnapshotWorker`. Instead the
suite leans on two oracles that are strong together:

1. **Replica convergence** (primary, fully independent) — the same commit *set*
   delivered to two fresh engines in two independent arrival orders / batchings must
   project identically. This is genuinely independent of any single code path
   (replica-vs-replica), and different arrival orders create different straggler and
   rollback patterns, so any order-dependent rollback or tiebreak bug shows up as a
   divergence. Implemented by `Projection_IsIndependentOfArrivalOrder`.

2. **Incremental == from-scratch** (secondary) — the state built incrementally by the
   rollback engine must equal `RegenerateSnapshots()`, which deletes all snapshots and
   projected tables and rebuilds without any rollback machinery
   (`DeleteStaleSnapshots`, surviving-snapshot resumption, `AddNewCommits`
   hash-rechaining, the every-other-commit retention optimization are all bypassed).
   So a bug in that machinery surfaces as incremental ≠ regenerate. This is *not* a
   fully independent oracle (it shares the leaf projection code); oracle #1 covers the
   gap. Implemented by `IncrementalProjection_EqualsFromScratchReplay`.

### Compared value

Projections are compared by **entity content** (`QueryLatest<Word>()` → id⇒text) plus
the **canonical commit-hash chain** (`GetProjectSnapshot().LastCommitHash`). We
deliberately exclude `ObjectSnapshot.Id`, which is `Guid.NewGuid()` per run and would
make identical projections compare unequal.

## Properties

| Test | Role |
|---|---|
| `Projection_IsIndependentOfArrivalOrder` | Replica convergence (P1 / P-master) — primary. |
| `IncrementalProjection_EqualsFromScratchReplay` | Incremental == from-scratch (P-master) — secondary oracle. |
| `ReingestingExistingCommits_IsIdempotent` | Duplicate re-send changes nothing (P-dup). |
| `Replay_IsDeterministic` | Same schedule twice ⇒ identical projection (R-determinism). |
| `GeneratorMetaTests.Generator_Produces_AllRollbackPhenomena` | Proves the generator actually emits ties, stragglers, cascades, duplicates, and genesis-depth rollbacks. |

## Generators (`Generators.cs`)

Author time is drawn from a **small** range and is **independent of arrival order**, so
stragglers are the norm; entities come from a **small pool** so commits contend and
order decides the value; ~10% of commits are **deep stragglers** (well before the rest)
to force rollback toward genesis; **duplicates** and random **batching** are always
present. Commit ids and times are deterministic functions of the generated integers, so
a failing case reproduces exactly.

Tier 1 (current) uses only `SetWordTextChange`, which supports **both** create and edit,
so every schedule is valid regardless of order. (A delete or edit of a not-yet-created
entity throws *by design* — see `SnapshotWorker.ApplyCommitChanges` — so those changes
require a "create is canonically first" guarantee, added in Tier 2.)

## Running

Default (local, low iteration count baked into CsCheck):

```bash
dotnet test src/SIL.Harmony.Tests --filter-class "*ProjectionProperties*"
```

Each iteration spins up in-memory SQLite engines, so iterations are not free. Tune the
budget with CsCheck environment variables:

```bash
# Run each property for 60 seconds instead of the default iteration count
CsCheck_Iter=1000000 CsCheck_Time=60 dotnet test src/SIL.Harmony.Tests --filter-class "*ProjectionProperties*"

# Or a fixed iteration count
CsCheck_Iter=1000 dotnet test src/SIL.Harmony.Tests --filter-class "*ProjectionProperties*"
```

CI should raise `CsCheck_Iter` (or set `CsCheck_Time`) well above the local default.

### Reproducing a failure

On failure CsCheck prints the shrunk counterexample and a **seed**. Replay it with:

```bash
CsCheck_Seed=<seed> dotnet test src/SIL.Harmony.Tests --filter-class "*ProjectionProperties*"
```

**A failure that shrinks to a genuine engine defect is a real finding, not a test bug.**
Capture the seed and the minimized schedule and report it — do not loosen the property
or special-case the oracle to make it pass. Only relax a property if it is genuinely
wrong about Harmony's intended semantics (and say so explicitly).

### Ready-to-run reproduction in the test output

On failure each property emits **the full C# source of a self-contained `[Fact]`** with
the shrunk counterexample hard-coded — no CsCheck, no seed, no shrinking needed to re-run
it. Copy the emitted `Repro_*` method into a class that has
`using static SIL.Harmony.Tests.PropertyBased.HarmonyEngineHarness;` (e.g.
`ProjectionProperties`) and run it directly to debug or attach to the bug report. There
is one body template per property type (`ReproTemplate`); each `ChangeSpec` renders itself
via `ToCode()`. Keep the templates in `ReproCode.Body` in sync with the property bodies.

The method is written to the **test output** (`ITestOutputHelper`), which shows in full in
CI logs, because CsCheck hard-caps the value it embeds in the exception *message* at 5000
characters — larger reproductions would be clipped there. The exception message therefore
carries only the compact schedule summary and a pointer to the test output. `ReproCode.Emit`
(passed as CsCheck's `print:`) does this; it is invoked once, on the final shrunk case.

## Proving the suite bites

The strongest evidence is empirical: **the suite already caught a real, pre-existing bug
in the shipping engine** (see the banner at the top) — not an injected one. The generator
meta-tests are the committed, standing proof that the axes (ties, stragglers, cascades,
duplicates, genesis-depth rollbacks) are actually exercised.

For additional bug-injection evidence, temporarily break real engine code, run the suite,
confirm the named property fails and shrinks small, capture the seed + minimized schedule,
then revert (no broken code is committed). Suggested injections and the property each
should trip:

- **Weak tiebreak** — drop `Id` from `CommitBase.CompareKey` (author time + counter
  only) ⇒ `Projection_IsIndependentOfArrivalOrder` fails, shrinking to two equal-keyed
  commits differing only by id.
- **Wrong stale-snapshot boundary** — perturb the `DeleteStaleSnapshots` / `WhereAfter`
  boundary by one ⇒ `IncrementalProjection_EqualsFromScratchReplay` (and convergence)
  fail on a straggler landing just before a retained snapshot.
- **Aliasing** (Tier 2, once mutable multi-field entities are generated) — remove a
  `Copy()` in `SnapshotWorker`/`CrdtRepository` so a checkpoint aliases live state ⇒
  convergence / snapshot-consistency fail after a rollback.

Record the observed counterexamples and seeds in the PR description.
