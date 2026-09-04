# Snapshot checkpoints

Design notes for fixing sillsdev/harmony#105. Written after a long investigation with a lot of
dead ends; the point of this file is so nobody repeats them. Everything under "Measurements" was
produced by running code, not reasoned about.

## The problem

Commits are the source of truth, snapshots are derived state. When a commit arrives dated before
commits already in the database (a "late commit", L), `AddNewCommits` sets the replay window to all
commits after `parent(L)`, `UpdateSnapshots` deletes snapshots after L, and the replay resumes each
entity from whatever snapshot survived.

That resume is the bug. An entity's newest surviving snapshot can be *older* than `parent(L)`,
because the snapshot at its last touch was pruned by the `CommitIndex % 2 == 0` rule in
`SnapshotWorker.GenerateSnapshotForEntity`. The commits between that snapshot and `parent(L)` are
not in the window, so nothing re-applies them. Two symptoms, both with repro tests in
`src/SIL.Harmony.Tests/LateCommitTests.cs`:

1. Silent loss of an edit. Recoverable by `RegenerateSnapshots`.
2. A cascade-deleted entity comes back to life, its projected row is re-inserted, and the FK to its
   deleted parent fails: `SQLite Error 19: 'FOREIGN KEY constraint failed'`. This wedged a
   production project. Sync fails hard until repaired.

The cut and the window already share a boundary (no commit lies strictly between `parent(L)` and L),
so this is not a cut/window mismatch. It is purely about what the resume snapshot is.

## The three concepts

**Completeness at P.** For every entity E, E's newest snapshot at or before P is at or after E's
most recent touch at or before P. "Touch" means a change to E, or a cascade from a delete in that
commit. This is a property of P alone, not of the prefix before it.

**Checkpoint.** A commit at which completeness holds, so it is safe to resume a replay from.
Critically, a checkpoint does **not** mean a snapshot row dated at that commit for every entity.
The project-wide state at a checkpoint is virtual: the union over entities of each one's newest
snapshot at or before it. That is the whole economy of the idea. An entity untouched for 10,000
commits contributes its old row and costs nothing.

**Hole.** When E's snapshot at commit `t` is dropped and E's next surviving snapshot is at `n`,
every position in `[t, n)` is unsafe for E. A hole is an interval, not a point. Getting this wrong
is what made the first attempt lose data (see Corrections).

## What we are building

- **Explicit checkpoints, marked with a bool on `Commit`.** Local-only bookkeeping: `[JsonIgnore]`,
  never synced, not part of the commit hash (the hash is `f(Id, parentHash)` only, so a new column
  is safe). Different devices will legitimately have different checkpoint sets.
- **A bool is enough.** A generation/level int would make tiered thinning declarative, but thinning
  can pick which flags to clear by any criterion at the time (for example keep every 4th flagged
  commit in commit order), so the extra column buys nothing now. Add it later if thinning wants it.
  A datestamp buys nothing at all: commits already carry their own date, which is what you would
  thin by.
- **The flag is a decision, not a record.** Choose which commits will be checkpoints, then make the
  pruner respect that choice: never drop a snapshot if the resulting hole would span a chosen
  checkpoint. This is the opposite direction of causality from the first attempt, which recorded
  after the fact which commits happened to be safe.
- **Many checkpoints per `SnapshotWorker` run, not one.** Density is the only dial in the design
  (see below). Any policy works because the choice is recorded: every Nth commit by position in the
  batch is the simplest, and unlike `% 2` it is entirely under our control.
- **Consumers need a migration** for the new `Commits` column. Harmony ships no migrations.

### Density is the only dial

Once checkpoints exist, a snapshot earns its keep only by being some entity's resume state at some
checkpoint. Everything else is dead weight. So there is no separate pruning policy to design:

| checkpoint density | what it is |
|---|---|
| every commit | never prune anything |
| one in K | the design here |
| dense at the head, sparse with age | thinning, deferred |

Storage scales with density. Rollback distance and point-in-time read cost scale inversely with it.
Both costs move the same way against storage, so there is one number to choose, not two.

The only exception to "worthless unless a checkpoint needs it" is each entity's newest snapshot,
which is its current state and feeds `CurrentSnapshots`, `GetLatest`, and the projected row that
references it by id. That is the same rule read forwards: it is the resume state for every
checkpoint after it, including ones that do not exist yet.

Roots are worth keeping unconditionally, but as insurance rather than necessity. By the rule a root
whose interval spans no checkpoint is worthless; deleting one makes a later resume apply an edit
against a null snapshot, which throws instead of degrading. One row per entity is cheap insurance.

## Rules that must not be broken

1. **Never add a flag retroactively.** Claiming safety at a commit that pruning has already holed is
   silent data loss. Flags may only be set on commits inside a window being replayed, since that
   window is re-pruned under the current policy and completeness there is under our control.
   Removing flags is always safe. There are no exceptions to this; see "Existing projects" for the
   one that was considered and dropped.
2. **No checkpoint lookup may guess.** If there is no flagged commit before L, that means replay
   everything: `DeleteSnapshotsAfter(null)` and replay all of history. Do not derive, infer, or
   approximate a checkpoint. The first attempt did and it picked unsafe commits.
3. **Deleting a snapshot is no longer free.** This was the assumption before this work. A snapshot
   may only be deleted if the hole it creates spans no live checkpoint.
4. **Thinning order.** Drop flags first, then sweep snapshots. The reverse breaks resume points.
5. **Checkpoints may only ever be coarsened.** Going back to a finer density needs a regenerate.

## Corrections to intuitions that turned out wrong

- **A hole is an interval.** The first pushed attempt marked only the commit whose snapshot was
  dropped. Demonstrated failure: entity A touched at c1/c3/c5 and B at c2/c4, one batch, c3 setting
  a note and c5 setting text. A's snapshot at c3 is pruned, flags come out c1=T c2=T c3=F c4=T c5=T,
  and a late commit landing between c4 and c5 resumes A from c1 and loses the note. c4 is inside
  A's hole `[c3, c5)` but was marked safe.
- **A commit-local predicate cannot detect holes.** "Every change at this commit has a snapshot
  here" is local and fails the same way. It is also blind to cascades: a cascade writes a snapshot
  for an entity with no `ChangeEntity`, so a pruned cascade snapshot is invisible to any anti-join
  over `ChangeEntities`. Verified: `ChangeEntities` for a cascade-deleted definition at its delete
  commit is 0.
- **`% 2` is not a checkpoint policy.** It is a retention policy on individual snapshots, and it
  gates on the index of the commit *doing the dropping*, which says nothing about where the hole
  falls. Safety at a position is a conjunction over all entities, so holes from different entities
  union together and cover nearly everything. Simulated: 0.6% to 1.4% of positions safe in synced
  batch shapes, 35% when the batch is mostly creates. It does produce checkpoints reliably in one
  place, where it never fires: a locally authored commit is its own batch with nothing prunable, so
  every one is safe. That is why this bug hides until a project syncs or clones.
- **Position parity in history does not work either.** "Call every second commit a checkpoint" was
  disproven: in the A/B history above the hole lands at odd index 3 while both even indices are safe.
- **Changes read other entities far more than assumed.** 15 of 38 change files in FW Lite's
  `LcmCrdt/Changes` read at apply time. `IsObjectDeleted` is a default method on `IChangeContext`
  that wraps `GetSnapshot`, which is easy to miss when grepping; five sample change files call it,
  including `NewDefinitionChange` and `NewExampleChange`. Consequence: per-entity state is not
  independently meaningful, so completeness has to be project-wide. This is also why every scheme
  that replays only some entities is unsound without tracking read sets.
- **Every replay path is affected, not just the late-commit path.** `GetSnapshotAtCommit` seeds from
  the subject's own nearest snapshot and replays the whole range after it, so the subject's own chain
  is correct. But the neighbours it reads are seeded from the scoped repository at "newest at or
  before X", which is both the wrong position (state as of X, not the replay position) and possibly
  stale. Cascades and `IsObjectDeleted` then compute from wrong state and feed the subject.

## Consequences for the other replay paths

`GetSnapshotAtCommit` should resume from the newest checkpoint at or before X, seeding every entity
from its newest snapshot at or before that checkpoint (scoped to the checkpoint, not to X as today),
then replay the range. Completeness at the checkpoint makes every seed correct, and every entity
that changes inside the range is replayed alongside the subject so reads land at the right position.
Both the staleness and the position error go away. It does not need a checkpoint at which the
subject itself was touched; any checkpoint works.

This also means `UpdateSnapshots` and `GetSnapshotAtCommit` share one primitive: "resume from the
newest checkpoint at or before P".

**Do not filter commits inside that range** without tracking read sets. Skipping commits that do not
touch the subject reintroduces exactly the neighbour-read bug: a neighbour that changed inside the
range would be left at its checkpoint state while the subject's later change reads it. With dense
checkpoints the range is small, so replay it fully. Prefer density over filtering.

## Pulling selection out of the playback

Deciding what to persist after the playback instead of during it is the right shape, but holding
every generated snapshot in memory is not viable: a clone or regenerate is one batch of the whole
history, so that is up to one snapshot per touch (hundreds of thousands of entity JSON blobs).

Checkpoints give the bounded version for free. No hole can span a checkpoint, so no decision about a
snapshot before one can depend on anything after it. Decide and flush at each checkpoint, holding at
most one checkpoint interval's worth of snapshots, O(K) rather than O(batch). At each checkpoint the
rule collapses to something trivial: every entity touched since the previous checkpoint keeps its
latest snapshot, everything else it accumulated is discarded.

Extract the decision as a pure function over (previous snapshot commit, current commit, checkpoint
set). The same function is what thinning runs later, and it can be tested without driving a replay.

## What shipped

`SnapshotCheckpointPolicy` holds both halves of the decision at an interval of 8: `IsCheckpoint` picks every 8th commit
of a replayed batch plus its last, and `MustKeepSnapshot` is the pure function the pruner and, later, thinning share.
`CrdtRepository.SetCheckpoints` writes the flags before the replay starts. `DataModel.ResumeFromCheckpoint` is the shared
primitive, used by `UpdateSnapshots`, `GetSnapshotsAtCommit` and `GetSnapshotAtCommit`.

Two things from the sections above were left alone:

- **The playback still decides during the walk**, incrementally, rather than accumulating an interval's snapshots and
  deciding at each checkpoint. Same outcome from the same function, and the restructure would not save much: the batch
  already holds a snapshot per touched entity in `_pendingSnapshots` regardless, which is the O(batch) part.
- **Reading state at an old commit on a database with no checkpoints replays all of history**, since there is nothing to
  resume from. Correct but slow, and it lasts until the first late commit establishes checkpoints.

## Deferred: thinning

Not in the first change. Recorded here so we know the room exists.

Keep snapshot `s` for entity E at commit `c` iff a live checkpoint lies in `[c, next snapshot of E)`.
Two properties make this cheap:

- **It is one pass, not a fixpoint.** Deleting `s(i)` widens `s(i-1)`'s gap, but if no checkpoint sits
  in `[s(i-1), s(i))` and none sits in `[s(i), s(i+1))` then none sits in the union, so every snapshot
  can be evaluated independently against its *original* neighbour and deleted in a single sweep.
- **It is the same predicate the pruner uses at write time**, just applied to rows already on disk.

Query shape: each snapshot paired with the next commit for the same entity, so a `LEAD` window
function over Snapshots joined to Commits in commit order, or per-entity grouping in memory. The
`Snapshots(EntityId)` index supports the ordering.

Always keep each entity's newest snapshot (current state, referenced by the projected row) and its
root (insurance, see above).

## Existing projects

**Decision: do nothing. Ship no migration step, no detection, and do not mark anything.**

Legacy databases have no flags, so the first late commit finds no checkpoint and replays all of
history, which rebuilds every snapshot correctly and establishes checkpoints throughout. That is
simultaneously the repair and the bootstrap, triggered automatically at the moment it is actually
needed, with no upgrade path to write and nothing to detect.

The reasoning for not marking the newest commit, which was the obvious alternative:

- **We cannot know whether a client's snapshots are already broken.** Corruption is per device,
  because it depends on the order batches arrived, and it lives in snapshot *content* rather than in
  any structural property. Nothing in the database records that a past replay resumed inside a hole,
  so there is no signature to look for short of recomputing and diffing.
- **Marking the head would freeze that corruption.** The flag would be structurally honest (no
  earlier snapshot is needed to resume at the head) while resuming from wrong values, and narrow
  rollbacks would then never reach back past it. Today's wide rollbacks occasionally heal corruption
  by accident; marking the head removes even that.
- **Marking the head does not avoid the full replay, it defers it.** Any late commit dated before the
  marker still finds no checkpoint before it and replays everything. In a multi-device workflow that
  is very likely: as soon as a client has edited locally, a peer's commits from the meantime are
  interleaved with its own, and any of them dated before the client's last local edit triggers the
  rewind. So the cost arrives anyway, just at an unpredictable moment and without the repair.

Accepted consequences:

- The first sync that carries a late commit is slow, once, on the order of seconds to a few minutes
  by the measurements below. Afterwards checkpoints exist and rollbacks are narrow.
- A client that never receives a late commit never heals and never gets checkpoints. That is
  self-consistent, since it also never rewinds, but any existing corruption stays and can still
  escape at the FieldWorks sync boundary, where current snapshot state is authoritative rather than
  the commits. Devices that sync to FLEx are therefore the ones worth watching, and
  `RegenerateSnapshots` stays the support path for them.
- **Implementation note:** when no checkpoint is found, take the regenerate path rather than the
  rewind path. A rewind covering all of history measured about 3x more per commit than a regenerate,
  because it replays against a still-populated table, and it leaves the projected tables to be
  updated row by row instead of rebuilt. See the crossover rule under Measurements.

Unapplied changes (an `OpaqueChange` this client cannot apply, or a change that does not support
updating an existing entity) produce no snapshot but also no state change, so they do not affect
completeness.

## Measurements

All from probes run during the investigation. Release, .NET 10, SQLite unless noted.

**Pruning storage saving**
- Nothing at all when an entity is touched twice in a batch: measured byte-identical databases,
  because the only edit's previous snapshot is the root and the `!IsRoot` guard fails.
- 500 entities x 11 touches in one batch (5500 commits): 3250 snapshots pruned vs 5500 unpruned
  (0.59x), snapshot payload 0.59x, vacuumed file 4.53 MB vs 5.64 MB (0.80x). Marginal disk cost of a
  snapshot row is about 490 bytes including indexes; payload alone about 177 bytes.
- Extrapolated to 50,000 entities x 11 touches: about 225,000 extra rows, roughly 110 MB at sample
  entity sizes. FW Lite entries are plausibly 1 to 3 KB, which would put it at 300 to 500 MB.
  **Nobody has measured a real project.** This query gives the exact number of rows never-prune
  would add on any database:
  `SELECT count(*) FROM (SELECT DISTINCT CommitId, EntityId FROM ChangeEntities) ce WHERE NOT EXISTS
  (SELECT 1 FROM Snapshots s WHERE s.CommitId = ce.CommitId AND s.EntityId = ce.EntityId)`
- Pruning is intra-batch only (`IsNew(prevSnapshot)`), so the device that authored a history already
  stores 100% of its snapshots. The saving only exists on devices that received history in bulk: a
  fresh clone, and `RegenerateSnapshots`.
- The existing `DataModelPerformanceTests` never exercises pruning at all: every change in it creates
  a brand new entity, so the parity branch is unreachable.

**Replay**
- `RegenerateSnapshots`, 10k commits: 1186 ms projected on, 1013 ms off, so 0.10 to 0.12 ms per
  commit with small entities. A second harness measured about 1 ms per commit plus 80 ms fixed;
  the difference is consistent with Debug, larger entities, or validation on. Either way 100k commits
  is minutes, not hours.
- Split of replay time with projection on: `ApplyCommitChanges` 23%, `AddSnapshots` 51% (of which
  `ProjectSnapshot` 15% and `SaveChanges` 32%), loading commits and dropping tables 26%.
- Late commit into a 10k history: 100 back 173 ms, 1000 back 996 ms, 5000 back 2013 ms.
- **Rewinding costs about 3x per commit compared with regenerating** (2013 ms for a 5000-commit
  rewind vs 1186 ms for a full 10k regenerate), because rewind replays against a still-populated
  table. Practical consequence, independent of this design: when the window covers more than roughly
  half of history, drop everything and regenerate instead of rewinding.

**Simulated checkpoint density** (2000 commits, 20 seeds)
- Under `% 2`: 1.4% of positions safe interleaved, 0.8% clustered, 0.6% two-device interleave, 35%
  mostly creates.
- Under a one-in-8 rule that respects holes: mean rollback gap 1 to 3 commits, max 11 clustered and
  54 interleaved; 34% to 38% of snapshots retained where edits cluster, 97% under uniform-random
  touching. Humans do not edit uniformly at random.

**Legacy gap detection queries** (2000 commits, 2050 snapshots) both translate to plain SQLite with
no raw SQL, including `References.Contains` becoming a correlated `json_each` subquery. The ordinary
anti-join runs in about 3 ms worst case, the cascade-aware one about 86 ms, with no false positives
over 50 intact cascade groups.

## Separate performance findings, worth their own issues

- **`CurrentSnapshots()` in the delete path.** Every delete of a referenced entity runs
  `MakeCurrentSnapshotsQuery`, a raw-SQL window function over the whole Snapshots table, once per
  cascade level. About 10 ms per 1000 snapshot rows per call, measured 134 ms for a single delete at
  8k snapshots. Superlinear in project size and live in production today. Wants an indexed
  current-snapshot-per-entity lookup. This is probably worth more than the bug fix.
- **No index on `ChangeEntities.EntityId`.** Every "which commits touch this entity" question
  currently scans.

## What to test

The highest-value test is the invariant itself, as a property over randomized histories: for every
flagged checkpoint b and every entity E, E's newest snapshot at or before b is at E's most recent
touch at or before b. That checks the design rather than a scenario, and it is what would have caught
the interval bug immediately.

Then: the interleaved history from Corrections (it fails on the first attempt and on `main`); a
density assertion so nobody silently reintroduces a policy that produces one checkpoint per batch;
and a repro for the point-in-time path, which has **no test today** even though we now know it is
wrong. A history where a neighbour's snapshot is pruned and a change reads it via `IsObjectDeleted`,
then `GetAtCommit` for the subject, is enough.

Note that `LateCommitTests`'s cascade test asserts `AssertSnapshotWasPruned` as a precondition. Any
design that stops pruning that particular snapshot makes the test fail on its precondition rather
than its assertion, which is correct behaviour but means the test needs re-pointing.

## Rejected

- **Fixpoint widening** (roll back to the affected entities' oldest surviving snapshot, follow the
  reference closure, repeat): unbounded, drags in unrelated entities, and the UNIQUE violations it hit
  on `Snapshots(CommitId, EntityId)` were a symptom of widening the replay without widening the
  delete to match.
- **One checkpoint per batch** (the batch's last commit): correct but the rollback reaches the start
  of the batch, and for a fresh clone that is the whole history.
- **Never prune**: correct and the smallest diff, but it is the most expensive point on the density
  dial, and at 50k entities x 11 touches that is 110 MB or more on every cloned or regenerated
  database.
- **Choosing checkpoints by a function of the commit id** (`Id.ToByteArray()[0] % K == 0`): elegant,
  needs no column, and the invariant is a two-line proof, but it freezes the policy (only ever
  coarsenable, since `% 16` boundaries are a subset of `% 8` ones) and the predicate is not
  SQL-translatable, so finding the nearest one is an unindexed in-memory walk. Explicit flags give
  the same guarantee with a free choice of policy and an indexed lookup.
- **Sparse or partial replay** of only the affected entities: blocked by the read sites above. Its
  bail-out condition fires on nearly every real window.
- **Forward merge without rewind**: FW Lite's changes are op-based with arbitrary reads; last-write-
  wins would need per-field timestamps in every entity and a rewrite of every change type.
- **Lazy repair**: symptom 2 fails during the narrow replay itself, and wrong state would be visible
  to the user in between.
- **Dropping the projected-table FKs**: turns a hard wedge into silent divergence. The FK is the
  canary.
