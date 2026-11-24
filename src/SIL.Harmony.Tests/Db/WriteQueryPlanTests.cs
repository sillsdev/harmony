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
        await using var dataModelTest = new DataModelTestBase(alwaysValidate: false, configure: services =>
            services.ConfigureDbContext<SampleDbContext>(builder => builder.AddInterceptors(plans)));
        var entityId = Guid.NewGuid();
        await dataModelTest.WriteNextChange(dataModelTest.SetWord(entityId, "first"));
        await dataModelTest.WriteNextChange(dataModelTest.SetWord(Guid.NewGuid(), "other"));

        plans.Steps.Clear();
        await dataModelTest.WriteNextChange(dataModelTest.SetWord(entityId, "second"));

        plans.Steps.Should().NotBeEmpty();
        plans.Steps.Where(step => step.Plan.StartsWith("SCAN", StringComparison.Ordinal) && step.Plan != "SCAN CONSTANT ROW")
            .Select(step => $"{step.Plan}: {step.Sql}")
            .Should().BeEmpty();
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
