# Coding standards

Judgement calls a reviewer applies to a diff. Mechanical style lives in `.editorconfig` and fails the build; the CRDT invariants (replay, ordering, compatibility) live under "Substrate-author standards" in `AGENTS.md`.

## Control flow

- Put the loop condition first: a `while` that checks for null or completion up front reads better than `do`/`while` with a negated condition at the bottom.

## Types and naming

- Where `.editorconfig` leaves a preference at `suggestion` (for example `var` versus an explicit type), match the surrounding file over the IDE hint.

## Tests

- Bug fixes are test-first: the regression test lands red before the fix, in the same PR.
- A property test may stay red as a living repro of a known bug. Keep the generator honest and say in the PR which property fails and why.
- Tag any test that asserts on wall-clock time `[Trait("Category", "Performance")]`, so the everyday run can exclude it.
- A test that needs a database file uses `TempDbFile`; a fixed relative path leaks state between runs and worktrees.

## Serialization

- Serialize changes and objects with `HarmonyConfig.JsonSerializerOptions`; a hand-built `JsonSerializerOptions` misses the registered types and the `$type` ordering.
- Treat a change type's name and JSON shape as a published contract: add properties as optional, and pair any rename or removal with a migration and a test that reads the old JSON.

## Review and PR hygiene

- Link code with commit-SHA permalinks and a line range (`/blob/<sha>/path#L10-L20`), so the link survives later edits and highlights the lines.
- In workflows, reach for a maintained marketplace action before writing a bespoke script.
- State in the PR description anything left red on purpose (living repros, known-flaky Performance tests).
