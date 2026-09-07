using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Changes;
using SIL.Harmony.Prototype;
using SIL.Harmony.Sample.Changes;

namespace SIL.Harmony.Tests;

// PROTOTYPE - throwaway, answers sillsdev/harmony#115. Delete with the branch.
//
//   dotnet test src/SIL.Harmony.Tests -- --filter-class "SIL.Harmony.Tests.PrototypeResumeRangeTests"
//
// The question: can the derived safe set be stored as runs and read back in two index seeks? Correctness is
// checked against the hole derived answer from #110, which its own invariant test already proved correct.
public class PrototypeResumeRangeTests(ITestOutputHelper output) : DataModelTestBase
{
    private IQueryable<ResumeRange> Ranges => DbContext.Set<ResumeRange>().AsNoTracking();
    private IQueryable<SnapshotHole> Holes => DbContext.Set<SnapshotHole>().AsNoTracking();
    private IQueryable<Commit> HistoryCommits => DbContext.Commits.AsNoTracking();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string ReportPath = Path.Combine(AppContext.BaseDirectory, "prototype-115-report.txt");

    private void Report(string line)
    {
        output.WriteLine(line);
        File.AppendAllText(ReportPath, line + Environment.NewLine);
    }

    // --- the write side, as a pure function ------------------------------------------------------------------

    [Fact]
    public void SafeRunsIsTheComplementOfTheHoles()
    {
        //the sample from the #110 report: holes at [4,8), [9,13), [28,32), [33,39) leave five runs
        var runs = PrototypeResumeRangeQueries.SafeRuns(40, [(4, 8), (9, 13), (28, 32), (33, 39)]);
        runs.Should().Equal([(1, 3), (8, 8), (13, 27), (32, 32), (39, 40)]);
    }

    [Fact]
    public void OverlappingAndAdjacentHolesCollapse()
    {
        PrototypeResumeRangeQueries.SafeRuns(10, [(2, 5), (3, 7)]).Should().Equal([(1, 1), (7, 10)]);
        PrototypeResumeRangeQueries.SafeRuns(10, [(2, 5), (5, 8)]).Should().Equal([(1, 1), (8, 10)]);
    }

    [Fact]
    public void NoHolesIsOneRunAndEveryPositionHoledIsNone()
    {
        PrototypeResumeRangeQueries.SafeRuns(10, []).Should().Equal([(1, 10)]);
        PrototypeResumeRangeQueries.SafeRuns(10, [(1, 11)]).Should().BeEmpty();
    }

    // --- the read side --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TheRangeLookupAgreesWithTheHoleDerivedAnswerAtEveryPosition(int seed)
    {
        var commits = await AddPlanInOneBatch(this, PlanHistory(40, seed));

        foreach (var commit in commits)
        {
            var derived = await PrototypeResumePointQueries.FromHoles(HistoryCommits, Holes, commit);
            var fromRanges = await PrototypeResumeRangeQueries.FindNewestResumePoint(HistoryCommits, Ranges, commit, inclusive: true);
            ((Guid?)fromRanges?.Id).Should().Be(derived?.Id, $"the ranges must hold the same answer at position {Position(commits, commit)}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ResumingStrictlyBeforeAPositionIsTheSameAsResumingAtItsPredecessor(int seed)
    {
        //this is the form UpdateSnapshots needs: a late commit may never resume from its own position
        var commits = await AddPlanInOneBatch(this, PlanHistory(40, seed));

        for (var i = 1; i < commits.Length; i++)
        {
            var exclusive = await PrototypeResumeRangeQueries.FindNewestResumePoint(HistoryCommits, Ranges, commits[i], inclusive: false);
            var atPredecessor = await PrototypeResumeRangeQueries.FindNewestResumePoint(HistoryCommits, Ranges, commits[i - 1], inclusive: true);
            ((Guid?)exclusive?.Id).Should().Be(atPredecessor?.Id, $"position {i + 1} resumed exclusively");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryPositionTheRangesReturnReallyHoldsCompleteState(int seed)
    {
        var plan = PlanHistory(24, seed);
        var commits = await AddPlanInOneBatch(this, plan);

        var returned = new HashSet<int>();
        foreach (var commit in commits)
        {
            var resume = await PrototypeResumeRangeQueries.FindNewestResumePoint(HistoryCommits, Ranges, commit, inclusive: true);
            if (resume is not null) returned.Add(Position(commits, resume));
        }

        returned.Should().HaveCountGreaterThan(1, "otherwise this only checks the end of the batch");
        foreach (var position in returned)
        {
            //a history added from empty in one batch is complete at its last commit by construction
            await using var fromScratch = new DataModelTestBase();
            await AddPlanInOneBatch(fromScratch, plan.Take(position));
            var atPosition = await StateFromSnapshotsAtOrBefore(commits[position - 1]);
            atPosition.Should().BeEquivalentTo(await CurrentStateOf(fromScratch),
                $"the ranges offered position {position} as a place to resume from");
        }
    }

    [Fact]
    public async Task AHistoryWithNoRangesResumesFromNowhere()
    {
        var commits = await AddPlanInOneBatch(this, PlanHistory(12, seed: 5));
        await DbContext.Set<ResumeRange>().ExecuteDeleteAsync(Ct);

        var resume = await PrototypeResumeRangeQueries.FindNewestResumePoint(HistoryCommits, Ranges, commits[^1], inclusive: true);
        resume.Should().BeNull("an empty table has to mean replay everything, which is what makes legacy databases safe");
    }

    // --- the report -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ReportTheStorageAndTheTwoSeeks()
    {
        var commits = await AddPlanInOneBatch(this, PlanHistory(600, seed: 11));
        var before = commits[^1];

        Report($"commits={commits.Length} snapshots={await DbContext.Snapshots.CountAsync(Ct)} " +
               $"holes={await Holes.CountAsync(Ct)} ranges={await Ranges.CountAsync(Ct)} " +
               $"flags={await HistoryCommits.CountAsync(c => c.IsSnapshotCheckpoint, Ct)}");

        var safeFromRanges = new HashSet<Guid>();
        foreach (var commit in commits)
        {
            if (await PrototypeResumeRangeQueries.FindNewestResumePoint(HistoryCommits, Ranges, commit, inclusive: true) is { } p
                && p.Id == commit.Id) safeFromRanges.Add(commit.Id);
        }

        Report($"safe positions: {safeFromRanges.Count} of {commits.Length}");
        Report($"ranges: {string.Join(" ", (await Ranges.ToArrayAsync(Ct)).OrderBy(r => Position(commits, r.FromCommitId)).Select(r => $"[{Position(commits, r.FromCommitId)}-{Position(commits, r.ToCommitId)}]"))}");

        var range = await PrototypeResumeRangeQueries.NewestRangeStartingWithinBound(Ranges, before, inclusive: true).FirstAsync(Ct);
        await Explain("SEEK 1, the only candidate range",
            PrototypeResumeRangeQueries.NewestRangeStartingWithinBound(Ranges, before, inclusive: true).Take(1));
        await Explain("SEEK 2a, the range's own end, when the bound is past the range",
            HistoryCommits.Where(c => c.Id == range.ToCommitId).Take(1));
        await Explain("SEEK 2b, the commit before the bound, when the bound is inside the range",
            HistoryCommits.WhereBefore(before).DefaultOrderDescending().Take(1));
        await Explain("for comparison, the compound two sided bound this replaced",
            PrototypeResumeRangeQueries.CommitsWithin(HistoryCommits, range, before, inclusive: true).Take(1));
    }

    private async Task Explain<T>(string label, IQueryable<T> query)
    {
        var statementLines = new List<string>();
        var parameters = new List<(string Name, string Value)>();
        foreach (var line in query.ToQueryString().Split('\n'))
        {
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

    // --- helpers --------------------------------------------------------------------------------------------

    private static int Position(Commit[] commits, Commit commit) => Position(commits, commit.Id);
    private static int Position(Commit[] commits, Guid commitId) => Array.FindIndex(commits, c => c.Id == commitId) + 1;

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
