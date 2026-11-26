using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Sample;

namespace SIL.Harmony.Tests.Db;

/// <summary>
/// Timing tests can't see a full table scan until the history is far bigger than a test can afford to seed,
/// so these check the query plans instead. SQLite picks the same plan at any size while a database has no ANALYZE statistics.
/// </summary>
public class WriteQueryPlanTests
{
    [Fact]
    public async Task WritingAChange_DoesNotScanTheHistory()
    {
        var plans = new QueryPlanRecorder();
        //validation is off because it reads every commit on purpose
        await using var dataModelTest = new DataModelTestBase(alwaysValidate: false, configure: services =>
            services.ConfigureDbContext<SampleDbContext>(builder => builder.AddInterceptors(plans)));
        //the first write has no checkpoint to resume from, so it loads the whole (empty) history, which is a scan
        await dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "first"));
        plans.Steps.Clear();

        await dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "second"));

        plans.Steps.Should().NotBeEmpty();
        plans.Steps.Where(step => step.Plan.StartsWith("SCAN", StringComparison.Ordinal) && step.Plan != "SCAN CONSTANT ROW")
            .Select(step => $"{step.Plan}: {step.Sql}")
            .Should().BeEmpty();
    }

    [Fact]
    public async Task WritingABatchThatPrefetches_DoesNotScanTheHistory()
    {
        var plans = new QueryPlanRecorder();
        await using var dataModelTest = new DataModelTestBase(alwaysValidate: false, configure: services =>
            services.ConfigureDbContext<SampleDbContext>(builder => builder.AddInterceptors(plans)));
        var wordIds = Enumerable.Range(0, dataModelTest.CrdtConfig.PrefetchSnapshotsBreakpoint + 1).Select(_ => Guid.NewGuid()).ToArray();
        await dataModelTest.WriteNextChange(wordIds.Select(id => dataModelTest.SetWord(id, "first")));
        plans.Steps.Clear();

        await dataModelTest.WriteNextChange(wordIds.Select(id => dataModelTest.SetWord(id, "second")));

        plans.Steps.Should().Contain(step => step.Sql.Contains("json_each"), "the batch should be prefetched");
        plans.Steps.Where(step => step.Plan.StartsWith("SCAN", StringComparison.Ordinal))
            .Select(step => step.Plan).Distinct()
            .Should().BeSubsetOf([
                //the prefetched rows, and the json_each over the id list
                "SCAN s",
                "SCAN json_each VIRTUAL TABLE INDEX 1:",
                "SCAN CONSTANT ROW",
            ]);
    }

    [Fact]
    public async Task DeletingAReferencedEntity_DoesNotGroupTheWholeHistory()
    {
        var plans = new QueryPlanRecorder();
        await using var dataModelTest = new DataModelTestBase(alwaysValidate: false, configure: services =>
            services.ConfigureDbContext<SampleDbContext>(builder => builder.AddInterceptors(plans)));
        var wordId = Guid.NewGuid();
        await dataModelTest.WriteNextChange(dataModelTest.SetWord(wordId, "word"));
        await dataModelTest.WriteNextChange(dataModelTest.NewDefinition(wordId, "definition", "noun"));
        plans.Steps.Clear();

        await dataModelTest.WriteNextChange(dataModelTest.DeleteWord(wordId));

        plans.Steps.Should().Contain(step => step.Sql.Contains("instr(\"References\""), "the delete should look up what references the word");
        //scanning the snapshots for the id is the point; grouping every entity's history is what this guards against
        plans.Steps.Where(step => step.Plan.StartsWith("SCAN", StringComparison.Ordinal))
            .Select(step => step.Plan).Distinct()
            .Should().BeSubsetOf([
                "SCAN Snapshots",
                "SCAN s",
                "SCAN r VIRTUAL TABLE INDEX 1:",
                "SCAN CONSTANT ROW",
            ]);
    }

    private sealed record PlanStep(string Plan, string Sql);

    private sealed class QueryPlanRecorder : DbCommandInterceptor
    {
        public List<PlanStep> Steps { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Explain(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Explain(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Explain(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Explain(command);
            return ValueTask.FromResult(result);
        }

        private void Explain(DbCommand command)
        {
            using var explain = command.Connection!.CreateCommand();
            explain.Transaction = command.Transaction;
            explain.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
            foreach (DbParameter parameter in command.Parameters)
            {
                var copy = explain.CreateParameter();
                copy.ParameterName = parameter.ParameterName;
                copy.Value = parameter.Value;
                explain.Parameters.Add(copy);
            }

            using var reader = explain.ExecuteReader();
            while (reader.Read()) Steps.Add(new PlanStep(reader.GetString(3), command.CommandText));
        }
    }
}
