# Agent notes

## Build and test

- .NET 10 SDK. Tests use xUnit v3 on Microsoft.Testing.Platform (MTP), see `global.json`.
- Run all tests: `dotnet test --project src/SIL.Harmony.Tests` (about 2 minutes).
- Run one test: `dotnet test --project src/SIL.Harmony.Tests -- --filter-method "*TestName"`.
- Run a new test alone at least once. A test that passes only in the full suite depends on other tests or on files left by an earlier run.
- Use in-memory SQLite (`Data Source=:memory:`) in tests. A file database stays in the build output and changes the next run.

## Mutation testing with Stryker.NET

Use mutation testing to find weak tests: Stryker changes the source code (a "mutant") and reruns the tests. If no test fails, the mutant "survived" and shows behavior that no test checks.

Setup:

```bash
dotnet tool install -g dotnet-stryker   # needs DOTNET_ROOT set if dotnet is not in the default location
```

Run it from the test project directory. Always use `--test-runner mtp` (the default VSTest runner cannot find this project's tests). Limit the scope with `-m`, because one mutant takes about 15 seconds:

```bash
cd src/SIL.Harmony.Tests
dotnet stryker --test-runner mtp --project SIL.Harmony.csproj \
  -m "**/SnapshotCheckpointPolicy.cs" -m "**/CommitChain.cs" \
  --reporter json --reporter html --output /tmp/stryker
```

- `--project` must name a project that the test project references directly. To mutate `SIL.Harmony.Core`, add a temporary `<ProjectReference>` to it in `SIL.Harmony.Tests.csproj`, and remove it after the run.
- Do not run `dotnet test` while Stryker runs. Both use the same build output folder.
- Read survivors from `reports/mutation-report.json`: for each file, list the `mutants` with status `Survived` or `NoCoverage`, with `location.start.line`, `mutatorName` and `replacement`.

Use the survivors as hints, not as a target score:

- Write a test only when the survivor shows real behavior that no test checks (a missing guard test, a value that is not compared, a branch that no test reaches).
- Skip equivalent mutants (the change does not alter behavior) and mutants in code that exists only to simulate something in tests.
- Each new test must state the behavior it protects in its name. Do not write a test only to kill a mutant.
- Before you write a test, find out why the mutant survived. Some code has no effect. For example, the `Commit` JSON constructor sets `Hash` and `ParentHash`, but EF Core does not call it and JSON ignores both properties.

To prove that a new test closes a gap, do not rerun the full Stryker run. Make the same change by hand, run only the related test class, make sure that a test fails, and then revert the change (`git checkout <file>`).

## Before you push

- `dotnet format --verify-no-changes` (CI runs it).
- Run the full test suite.
