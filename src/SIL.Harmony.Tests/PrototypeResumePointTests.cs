using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Changes;
using SIL.Harmony.Prototype;
using SIL.Harmony.Sample.Changes;

namespace SIL.Harmony.Tests;

// PROTOTYPE - throwaway, answers sillsdev/harmony#110. Delete with the branch.
//
// Run it with:
//   dotnet test src/SIL.Harmony.Tests --filter Prototype
// The two Report tests print what the gate is judged on: the SQL each design emits, and how the derived
// safe positions compare with the flags the shipped design would have set.
public class PrototypeResumePointTests(ITestOutputHelper output) : DataModelTestBase
{
    private IQueryable<SnapshotHole> Holes => DbContext.Set<SnapshotHole>().AsNoTracking();
    private IQueryable<PrototypeResumePoint> ResumePoints => DbContext.Set<PrototypeResumePoint>().AsNoTracking();
    private IQueryable<Commit> HistoryCommits => DbContext.Commits.AsNoTracking();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    //the report is the artifact this prototype exists to produce, so it goes to a file as well as the test output
    private static readonly string ReportPath = Path.Combine(AppContext.BaseDirectory, "prototype-110-report.txt");

    private void Report(string line)
    {
        output.WriteLine(line);
        File.AppendAllText(ReportPath, line + Environment.NewLine);
    }

    [Fact]
    public async Task ReportTheSqlEachDesignEmits()
    {
        await AddPlanInOneBatch(this, PlanHistory(40, seed: 7));
        var before = await HistoryCommits.DefaultOrderDescending().FirstAsync(Ct);

        Report($"commits={await HistoryCommits.CountAsync(Ct)} " +
                         $"snapshots={await DbContext.Snapshots.CountAsync(Ct)} " +
                         $"holes={await Holes.CountAsync(Ct)} " +
                         $"resumePoints={await ResumePoints.CountAsync(Ct)}");

        Report("\n=== DESIGN 1, shipped: flag on Commits ===");
        Report(PrototypeResumePointQueries.FlagQueryable(HistoryCommits, before).ToQueryString());
        Report("\n=== DESIGN 2: derived from holes ===");
        Report(PrototypeResumePointQueries.HoleQueryable(HistoryCommits, Holes, before).ToQueryString());
        Report("\n=== DESIGN 2b: derived from holes, endpoints rounded outward ===");
        Report(PrototypeResumePointQueries.HoleQueryableConservative(HistoryCommits, Holes, before).ToQueryString());
        Report("\n=== DESIGN 2c: derived from holes, with a max-hole-span guard ===");
        Report(TranslationOf(() => PrototypeResumePointQueries.HoleQueryableBounded(HistoryCommits, Holes, before, 8d)));
        Report("\n=== DESIGN 3: claimed, in its own table ===");
        Report(ResumePoints
            .OrderByDescending(r => r.DateTime).ThenByDescending(r => r.Counter).ThenByDescending(r => r.CommitId)
            .ToQueryString());
    }

    /// <summary>
    /// The bound that would make design 2's subquery a narrow range seek does not survive translation: the position
    /// columns carry a value converter (DateTimeOffset stored as DateTime), and EF will not push a SQL function onto
    /// a converted column. So this reports the failure instead of the SQL, because the failure is the finding.
    /// </summary>
    private static string TranslationOf(Func<IQueryable<Commit>> build)
    {
        try
        {
            return build().ToQueryString();
        }
        catch (InvalidOperationException e)
        {
            return "DOES NOT TRANSLATE: " + e.Message.Split('\n')[0];
        }
    }

    [Fact]
    public async Task ReportHowTheDerivedAnswerComparesWithTheFlags()
    {
        var commits = await AddPlanInOneBatch(this, PlanHistory(40, seed: 7));
        var flagged = await HistoryCommits.Where(c => c.IsSnapshotCheckpoint).Select(c => c.Id).ToArrayAsync(Ct);
        var derived = new List<Guid>();
        var conservative = new List<Guid>();
        foreach (var commit in commits)
        {
            if (await PrototypeResumePointQueries.FromHoles(HistoryCommits, Holes, commit) is { } p && p.Id == commit.Id)
                derived.Add(commit.Id);
            if (await PrototypeResumePointQueries.FromHolesConservative(HistoryCommits, Holes, commit) is { } q && q.Id == commit.Id)
                conservative.Add(commit.Id);
        }

        string Positions(IEnumerable<Guid> ids) => string.Join(",", ids.Select(id => Array.FindIndex(commits, c => c.Id == id) + 1).Order());
        Report($"positions flagged by the shipped design: {Positions(flagged)}");
        Report($"positions derived from holes:            {Positions(derived)}");
        Report($"positions derived, rounded outward:      {Positions(conservative)}");
        Report($"holes: {string.Join(" ", await Holes.Select(h => h.FromCommitId + " -> " + h.ToCommitId).ToArrayAsync(Ct))}");

        derived.Should().Contain(flagged, "every position the pruner protected must derive as safe, or the record disagrees with the pruning");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryDerivedSafePositionReallyHoldsCompleteState(int seed)
    {
        var plan = PlanHistory(24, seed);
        var commits = await AddPlanInOneBatch(this, plan);

        var safe = new List<int>();
        for (var i = 0; i < commits.Length; i++)
        {
            if (await PrototypeResumePointQueries.FromHoles(HistoryCommits, Holes, commits[i]) is { } p && p.Id == commits[i].Id)
                safe.Add(i);
        }

        safe.Should().HaveCountGreaterThan(1, "otherwise this only checks the end of the batch");
        foreach (var i in safe)
        {
            //a history added from empty in one batch is complete at its last commit by construction
            await using var fromScratch = new DataModelTestBase();
            await AddPlanInOneBatch(fromScratch, plan.Take(i + 1));
            var atPosition = await StateFromSnapshotsAtOrBefore(commits[i]);
            atPosition.Should().BeEquivalentTo(await CurrentStateOf(fromScratch),
                $"the derived answer claims position {i + 1} is safe to resume from");
        }
    }

    [Fact]
    public async Task ReportTheQueryPlanForEachDesign()
    {
        //a longer history so the planner has something to prefer an index for
        await AddPlanInOneBatch(this, PlanHistory(600, seed: 11));
        var before = await HistoryCommits.DefaultOrderDescending().FirstAsync(Ct);
        Report($"\nplans over commits={await HistoryCommits.CountAsync(Ct)} " +
               $"snapshots={await DbContext.Snapshots.CountAsync(Ct)} holes={await Holes.CountAsync(Ct)}");

        await Explain("DESIGN 1, flag on Commits", PrototypeResumePointQueries.FlagQueryable(HistoryCommits, before));
        await Explain("DESIGN 2, derived from holes", PrototypeResumePointQueries.HoleQueryable(HistoryCommits, Holes, before));
        Report("\n--- DESIGN 2c, derived with a max-hole-span guard ---");
        Report("  " + TranslationOf(() => PrototypeResumePointQueries.HoleQueryableBounded(HistoryCommits, Holes, before, 8d)));
        await Explain("DESIGN 3, claimed in its own table", ResumePoints
            .OrderByDescending(r => r.DateTime).ThenByDescending(r => r.Counter).ThenByDescending(r => r.CommitId)
            .Select(r => r.CommitId));
    }

    private async Task Explain<T>(string label, IQueryable<T> query)
    {
        var sql = query.ToQueryString();
        var statementLines = new List<string>();
        var parameters = new List<(string Name, string Value)>();
        foreach (var line in sql.Split('\n'))
        {
            //ToQueryString prefixes the statement with sqlite ".param set" lines
            if (!line.TrimStart().StartsWith(".param set "))
            {
                statementLines.Add(line);
                continue;
            }

            var declaration = line.Trim()[".param set ".Length..].Split(' ', 2);
            parameters.Add((declaration[0], declaration[1].Trim('\'')));
        }

        await using var command = DbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + string.Join("\n", statementLines);
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        Report($"\n--- {label} ---");
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) Report("  " + reader.GetString(3));
    }

    // --- helpers, lifted from SnapshotCheckpointTests -------------------------------------------------------------

    private sealed record PlannedChange(DateTimeOffset Date, IChange Change);

    private static PlannedChange[] PlanHistory(int commitCount, int seed)
    {
        var random = new Random(seed);
        var date = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        List<Guid> words = [];
        var plan = new List<PlannedChange>();
        while (plan.Count < commitCount)
        {
            date = date.AddDays(1);
            IChange change;
            if (words.Count == 0 || random.Next(3) == 0)
            {
                var newWordId = Guid.NewGuid();
                words.Add(newWordId);
                change = new SetWordTextChange(newWordId, $"word {words.Count}");
            }
            else
            {
                var wordId = words[random.Next(words.Count)];
                change = random.Next(2) == 0
                    ? new SetWordNoteChange(wordId, $"note {plan.Count}")
                    : new SetWordTextChange(wordId, $"text {plan.Count}");
            }

            plan.Add(new PlannedChange(date, change));
        }

        return [.. plan];
    }

    private async Task<Commit[]> AddPlanInOneBatch(DataModelTestBase model, IEnumerable<PlannedChange> plan)
    {
        //one batch, because pruning is intra-batch only and nothing gets pruned otherwise
        var commits = new List<Commit>();
        foreach (var planned in plan) commits.Add(await model.WriteChange(_localClientId, planned.Date, planned.Change, add: false));
        await model.AddCommitsViaSync(commits);
        return [.. commits];
    }

    private static async Task<Dictionary<Guid, string>> CurrentStateOf(DataModelTestBase model)
    {
        var snapshots = await model.DataModel.GetLatestSnapshots().ToArrayAsync(Ct);
        return snapshots.ToDictionary(s => s.EntityId, s => Describe(s.Entity.DbObject));
    }

    private async Task<Dictionary<Guid, string>> StateFromSnapshotsAtOrBefore(Commit commit)
    {
        var snapshots = await DbContext.Snapshots.AsNoTracking().Include(s => s.Commit).ToArrayAsync(Ct);
        return snapshots
            .Where(s => s.Commit.CompareKey.CompareTo(commit.CompareKey) <= 0)
            .GroupBy(s => s.EntityId)
            .ToDictionary(g => g.Key, g => Describe(g.MaxBy(s => s.Commit.CompareKey)!.Entity.DbObject));
    }

    private static string Describe(object entity) => System.Text.Json.JsonSerializer.Serialize(entity, entity.GetType());
}
