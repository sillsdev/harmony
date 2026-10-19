# harmony

CRDT library for .NET: offline-first apps record changes as commits, and Harmony replays them into snapshots stored through EF Core.

## Layout

`harmony.slnx` at the root (there is no `.sln`). Every project targets net10.0; shared settings live in `src/Directory.Build.props`.

| Project | Role |
|---|---|
| `src/SIL.Harmony.Core` | Commit/`HybridDateTime` primitives shared with sync servers |
| `src/SIL.Harmony` | The library: `DataModel` (public entry point), `SnapshotWorker` (replays commits into snapshots), `CrdtRepository` (EF Core persistence), `Config/HarmonyConfig` (registration and JSON options) |
| `src/SIL.Harmony.Linq2db` | linq2db query support |
| `src/SIL.Harmony.Sample` | Sample model (`Word`, `Definition`, `Example`) the tests run against |
| `src/SIL.Harmony.Tests` | xUnit v3 tests; `DataModelTestBase` is the usual fixture (in-memory SQLite, mock clock) |
| `src/SIL.Harmony.Benchmarks` | BenchmarkDotNet suites |
| `src/Ycs` | Vendored C# port of Yjs; marked generated, so `dotnet format` and analyzers skip it. Leave it as is. |

## Paths

Run every command from the repo root with repo-relative paths. Worktrees live in `.claude/worktrees/<name>`, and each one is its own repo root: run `git rev-parse --show-toplevel` before git or dotnet commands so you build the checkout you are editing.

## Build and test

`global.json` pins the 10.0 SDK band (`rollForward: latestFeature`), matching CI. A `NETSDK1057` preview banner means the pin is being bypassed.

The build treats every warning as an error and enforces `.editorconfig` style (`EnforceCodeStyleInBuild`), so a clean local build matches the Format CI job. Fix warnings rather than suppressing them; a suppression needs a one-line reason beside it.

Tests run on Microsoft.Testing.Platform: filters go after `--`, and `--filter` is rejected.

```sh
dotnet build harmony.slnx -c Release
dotnet test src/SIL.Harmony.Tests -- --filter-not-trait Category=Performance   # everyday run
dotnet test src/SIL.Harmony.Tests -- --filter-method '*CanAdaptACustomObject*'
dotnet test src/SIL.Harmony.Tests -- --filter-class '*SnapshotWorkerTests'
dotnet test src/SIL.Harmony.Tests -- --filter-namespace 'SIL.Harmony.Tests.Adapter'
dotnet test src/SIL.Harmony.Tests -- --filter-trait Category=Performance       # timing tests only
dotnet format --verify-no-changes
dotnet run -c Release --project src/SIL.Harmony.Benchmarks -- --filter '*GetSyncState*' --job short
```

- Timing-threshold tests carry `[Trait("Category", "Performance")]` and are flaky on a loaded machine. Exclude them locally; CI runs the whole suite in Release because the benchmark step reads `DataModelPerformanceBenchmarks` output from that run.
- Tests that need a real file use `TempDbFile` (unique temp path, deleted on dispose); the rest use in-memory SQLite.
- Benchmark classes declare `[SimpleJob]`, so `--job short` adds a ShortRun job beside the default one instead of replacing it: the `GetSyncState` run above takes about 4 minutes. List names with `-- --list flat`.
- CI runs `dotnet test --configuration Release` plus `dotnet format --verify-no-changes`; both are required checks.

## Line endings

`.gitattributes` forces LF. If `dotnet format` reports `ENDOFLINE` on files you did not touch, the checkout has CRLF: run `git add --renormalize .` and re-check.

## CI-only failures

PR CI builds `refs/pull/N/merge`, the merge with current `main`. When CI fails and local passes, run `git fetch && git merge origin/main` and reproduce before changing code.

## Issues

Issues live in `sillsdev/harmony`. When `gh issue view N` prints nothing, use `gh api repos/sillsdev/harmony/issues/N` (an issue number may also be a PR). Local planning tickets live in an untracked `.scratch/` folder; check there when a ticket number has no matching issue.

## Substrate-author standards

Harmony is the substrate other apps sync through, so a bug here corrupts every client's data. Hold every change to these invariants:

- **Deterministic replay.** Applying a set of commits yields the same snapshots on every client regardless of arrival order. Concurrent commits are ordered, never merged by wall-clock arrival.
- **Commit order** is `HybridDateTime.DateTime`, then `HybridDateTime.Counter`, then commit `Id` (`CommitBase.CompareKey`, `DefaultOrder()`). Every query and in-memory sort over commits or snapshots uses that order.
- **Snapshots are a cache of replay.** Snapshots written incrementally (via `SnapshotWorker`, from a checkpoint) must equal what `DataModel.RegenerateSnapshots()` produces from the whole history. `SnapshotTests.RegenerateSnapshots_WillArriveAtTheSameState` shows the test pattern.
- **Hash chain.** `Commit.Hash` chains over `ParentHash` in commit order; `AlwaysValidateCommits` (on in tests) checks it after every write.
- **Serialized changes are forever.** Commits written by old clients must still deserialize. Keep every change type's `$type` discriminator and JSON property names stable; a rename or removal needs a migration path. `PeekThenConcreteChangeConverter` owns `IChange` discrimination and needs `$type` first; unknown types become `OpaqueChange` when configured.
- **JSON options come from `HarmonyConfig.JsonSerializerOptions`**, never a fresh `JsonSerializerOptions`, so registered change and object types resolve.

## Review

Judgement-call rules for reviewing a diff are in `CODING_STANDARDS.md`.
