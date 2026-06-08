# Agent Instructions for SIL.Harmony

## Project Overview

Harmony is a CRDT (Conflict-free Replicated Data Type) application
library for C#, designed for building offline-first applications with
eventual consistency. It ships as NuGet packages (`SIL.Harmony`,
`SIL.Harmony.Core`, `SIL.Harmony.Linq2db`).

This library is the **CRDT substrate** for FieldWorks Lite (FwLite),
LexBox, and other SIL applications. Changes here ripple to every
consumer. Deployed clients (FwLite Android / Mac / Windows / Web) must
be able to replay commit histories produced by any prior version of
the library.

### Structure

```text
harmony/
├── src/
│   ├── SIL.Harmony/          # Main library (DataModel, IChangeContext, etc.)
│   ├── SIL.Harmony.Core/     # Core types (Commit, Snapshot, ChangeEntity)
│   ├── SIL.Harmony.Linq2db/  # Optional linq2db integration
│   ├── SIL.Harmony.Sample/   # Reference CRDT objects (Word/Definition/Example)
│   └── SIL.Harmony.Tests/    # Test suite
```

## Versioning & releases

NuGet package versions are derived from git tags plus commit messages by
`src/calculate-version.sh`: the most recent `vX.Y.Z` tag sets the base,
and commits since then bump it according to `+semver:` markers in the
messages —

- `+semver: major` / `+semver: breaking` → bump major, reset minor + patch.
- `+semver: minor` / `+semver: feature` → bump minor, reset patch.
- anything else (or `+semver: patch` / `+semver: fix`) → bump patch.

A breaking change to the consumer contract (section D) MUST land with a
`+semver: major` commit so downstream package resolution reflects it.

## Substrate-author standards

These are the invariants harmony must preserve across versions.
Reviewers treat a violation as blocking, not advisory.

### A. Change application semantics

`Change<T>` subclasses (`CreateChange<T>`, `EditChange<T>`, custom
`Change` derivatives) must be:

- **Commutative** in the conditions the change type states. Two
  changes to disjoint properties of the same entity must produce the
  same final state regardless of order.
- **Idempotent** where the change type advertises idempotence
  (typically `CreateChange<T>` on an existing entity).
- **Stateless** in the change object itself — it captures *intent*,
  not state. The change body must use `IChangeContext` to read current
  state.

A new `Change` subclass violating any of these is blocking and needs a
test demonstrating the divergence before merge.

### B. Snapshot equivalence

Projected snapshots (`Snapshot`, `ObjectSnapshot`, projected-table
generators) must be:

- **Pure functions** of the commit DAG up to the queried point. No
  external state.
- **Deterministic** — same commits → same snapshot, bit-for-bit,
  across rebuilds and across machines.
- **Tombstone-stripped** at the projected-table level — consumers
  expect `DeletedAt` to mean "never returned in the projected view".

Changes to projection logic are blocking until a snapshot-equivalence
test exists.

### C. Commit / DAG ordering

- `HybridDateTime` ordering is authoritative. Wall-clock skew doesn't
  matter; the HLC's logical counter does.
- `IsObjectDeleted` guards on dependent operations — never apply a
  change to an entity whose `DeletedAt` is set; the tombstone has won.
- Reference cycles between entities must be detected and broken at
  creation, not at projection.

### D. Backward compatibility — the consumer contract

Harmony is consumed by FwLite (multiple deployed builds on user
machines). **Old commits must replay through new code.**

- Don't rename a serialized property without a JSON migration.
- Don't change a `Change` subclass's JSON shape without a `Replaces`
  attribute or equivalent migration.
- Don't remove a `[Replaces]` attribute or migration shim that commits
  in the wild still depend on.
- Don't change `Change` constructor parameter names — they're the JSON
  deserialization contract for commits in the wild.
- Public API of `IChangeContext`, projection generators, and `DataModel`
  (including `QueryLatest<T>`) — treat as deployed; additive evolution
  only without a major version (see *Versioning & releases*).

Public API break without a migration story is blocking.

### E. Performance — projections are hot paths

- New projected tables / indexes: justify the cost in the PR body.
- New iteration over the full commit log per query: blocking until a
  snapshot-cache invalidation strategy is documented.

### F. Test coverage

Every new `Change` subclass needs:

- A serialization round-trip test.
- A commutativity test (where the type advertises commutativity).
- A `UseChangesTests`-style integration test.

## Reviewing changes here

Open prescriptive nits with *"let's …"*; cite existing harmony files by
path as precedent. Frame data-loss / consumer-break findings bluntly:

> *"This breaks the JSON deserialization contract for `EditChange<T>` —
> commits created before this PR won't replay. Let's add a `[Replaces]`
> attribute or back the rename out."*

What counts as **blocking** here: a violation of any invariant above —
sync divergence, data loss, a broken consumer contract, or a failing
test. Everything else is a judgment call the reviewer weighs.

## Cross-references

Consumed by:
[languageforge-lexbox](https://github.com/sillsdev/languageforge-lexbox)
— its `harmony-sentinel` agent
(`.claude/agents/harmony-sentinel.md`) reads this file as the
authoritative source for substrate-author standards.
