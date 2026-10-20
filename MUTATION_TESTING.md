# Mutation testing with Stryker.NET

Use mutation testing to find weak tests: Stryker changes the source code (a "mutant") and reruns the tests. If no test fails, the mutant "survived" and shows behavior that no test checks.

## Setup

```bash
dotnet tool install -g dotnet-stryker   # needs DOTNET_ROOT set if dotnet is not in the default location
```

## Running

Run it from the test project directory (the one exception to running from the repo root). Always use `--test-runner mtp`: the default VSTest runner cannot find this project's tests. Limit the scope with `-m`, because one mutant takes about 15 seconds:

```bash
cd src/SIL.Harmony.Tests
dotnet stryker --test-runner mtp --project SIL.Harmony.csproj \
  -m "**/SnapshotCheckpointPolicy.cs" -m "**/CommitChain.cs" \
  --reporter json --reporter html --output /tmp/stryker
```

- `--project` must name a project that the test project references directly. To mutate `SIL.Harmony.Core`, add a temporary `<ProjectReference>` to it in `SIL.Harmony.Tests.csproj`, and remove it after the run.
- Leave `dotnet test` idle while Stryker runs: both use the same build output folder.
- Read survivors from `<output>/reports/mutation-report.json`: for each file, list the `mutants` with status `Survived` or `NoCoverage`, with `location.start.line`, `mutatorName` and `replacement`.

## Triaging survivors

Treat survivors as hints, not a target score:

- Write a test only when the survivor shows real behavior that no test checks (a missing guard test, a value that is not compared, a branch that no test reaches).
- Skip equivalent mutants (the change does not alter behavior) and mutants in code that exists only to simulate something in tests.
- Name each new test after the behavior it protects, not the mutant it kills.
- Before you write a test, find out why the mutant survived. Some code has no effect. For example, the `Commit` JSON constructor sets `Hash` and `ParentHash`, but EF Core does not call it and JSON ignores both properties.

## Proving a new test closes the gap

Skip the full Stryker rerun. Make the same change by hand, run only the related test class, confirm a test goes red, then revert the change (`git checkout <file>`).
