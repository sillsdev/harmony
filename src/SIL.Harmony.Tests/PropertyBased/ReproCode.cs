using System.Globalization;
using System.Text;
using Xunit;

namespace SIL.Harmony.Tests.PropertyBased;

/// <summary>Which property body to emit in a hard-coded reproduction.</summary>
public enum ReproTemplate
{
    Converge,
    IncrementalVsFromScratch,
    ReingestIdempotent,
    ReplayDeterministic,
}

/// <summary>
/// Renders a failing <see cref="Schedule"/> as the C# source of a self-contained, ready-to-run
/// xUnit test method with every generated value hard-coded — so a shrunk CsCheck counterexample
/// can be pasted straight into the test project and debugged without CsCheck or a seed. Passed as
/// the <c>print:</c> argument to CsCheck's <c>SampleAsync</c>, so it appears in the failure
/// message. One body template per property type (<see cref="ReproTemplate"/>).
///
/// The emitted method assumes it is pasted into a class with
/// <c>using static SIL.Harmony.Tests.PropertyBased.HarmonyEngineHarness;</c> (e.g.
/// <see cref="ProjectionProperties"/>); it relies only on the harness statics and FluentAssertions.
/// Keep the bodies here in sync with the property bodies in <see cref="ProjectionProperties"/>.
/// </summary>
public static class ReproCode
{
    public static string Guid(Guid value) => $"System.Guid.Parse(\"{value:D}\")";

    public static string Str(string? value) =>
        value is null ? "null" : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string Dbl(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Time(HybridDateTime time) =>
        $"new HybridDateTime(System.DateTimeOffset.Parse(\"{time.DateTime:O}\"), {time.Counter}L)";

    /// <summary>
    /// Writes the full ready-to-run reproduction to the test output (untruncated in CI logs) and
    /// returns a compact summary for CsCheck's failure message, which CsCheck hard-caps at 5000
    /// chars. Call this from a property's <c>print:</c> argument; CsCheck invokes it exactly once,
    /// on the final shrunk counterexample. Pass the <see cref="ITestOutputHelper"/> captured at the
    /// start of the test (so it is available regardless of which thread CsCheck calls back on).
    /// </summary>
    public static string Emit(ITestOutputHelper? output, Schedule schedule, ReproTemplate template)
    {
        var code = Render(schedule, template);
        if (output is not null) output.WriteLine(code);
        else Console.WriteLine(code);
        return schedule + "// Full ready-to-run reproduction written to the test output above.";
    }

    public static string Render(Schedule schedule, ReproTemplate template)
    {
        var indexById = new Dictionary<Guid, int>();
        for (var i = 0; i < schedule.Commits.Count; i++) indexById[schedule.Commits[i].Id] = i;

        var sb = new StringBuilder();

        sb.AppendLine("// ---- Ready-to-run reproduction (hard-coded CsCheck counterexample) ----");
        sb.AppendLine("// Paste into a class with: using static SIL.Harmony.Tests.PropertyBased.HarmonyEngineHarness;");
        sb.AppendLine("[Fact]");
        sb.AppendLine($"public async Task {MethodName(template)}()");
        sb.AppendLine("{");

        // commits
        sb.AppendLine("    var commits = new CommitSpec[]");
        sb.AppendLine("    {");
        foreach (var commit in schedule.Commits)
        {
            var changes = string.Join(", ", commit.Changes.Select(c => c.ToCode()));
            sb.AppendLine($"        new({Guid(commit.Id)}, {Time(commit.Time)}, new ChangeSpec[] {{ {changes} }}),");
        }
        sb.AppendLine("    };");
        sb.AppendLine();

        // arrivals
        AppendArrivals(sb, "arrivalsA", schedule.ArrivalsA, indexById);
        AppendArrivals(sb, "arrivalsB", schedule.ArrivalsB, indexById);
        sb.AppendLine("    var schedule = new Schedule(commits, arrivalsA, arrivalsB);");
        sb.AppendLine();

        foreach (var line in Body(template)) sb.Append("    ").AppendLine(line);

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendArrivals(StringBuilder sb, string name, IReadOnlyList<Arrival> arrivals, IReadOnlyDictionary<Guid, int> indexById)
    {
        sb.AppendLine($"    var {name} = new Arrival[]");
        sb.AppendLine("    {");
        foreach (var arrival in arrivals)
        {
            var refs = string.Join(", ", arrival.Batch.Select(c => $"commits[{indexById[c.Id]}]"));
            sb.AppendLine($"        new(new[] {{ {refs} }}),");
        }
        sb.AppendLine("    };");
    }

    private static string MethodName(ReproTemplate template) => "Repro_" + template;

    private static IEnumerable<string> Body(ReproTemplate template) => template switch
    {
        ReproTemplate.Converge =>
        [
            "await using var replicaA = await NewEngine();",
            "await using var replicaB = await NewEngine();",
            "await Feed(replicaA.DataModel, schedule.ArrivalsA);",
            "await Feed(replicaB.DataModel, schedule.ArrivalsB);",
            "var a = await Read(replicaA.DataModel);",
            "var b = await Read(replicaB.DataModel);",
            "a.Entities.Should().BeEquivalentTo(b.Entities, \"two replicas receiving the same commit set in different arrival orders must converge\");",
            "a.LastCommitHash.Should().Be(b.LastCommitHash);",
        ],
        ReproTemplate.IncrementalVsFromScratch =>
        [
            "await using var engine = await NewEngine();",
            "await Feed(engine.DataModel, schedule.ArrivalsA);",
            "var incremental = await Read(engine.DataModel);",
            "await engine.DataModel.RegenerateSnapshots();",
            "var fromScratch = await Read(engine.DataModel);",
            "incremental.Entities.Should().BeEquivalentTo(fromScratch.Entities, \"the incrementally rolled-back projection must equal a from-scratch replay of the same commits\");",
            "incremental.LastCommitHash.Should().Be(fromScratch.LastCommitHash);",
        ],
        ReproTemplate.ReingestIdempotent =>
        [
            "await using var engine = await NewEngine();",
            "await Feed(engine.DataModel, schedule.ArrivalsA);",
            "var before = await Read(engine.DataModel);",
            "await Feed(engine.DataModel, schedule.Commits);",
            "var after = await Read(engine.DataModel);",
            "before.Entities.Should().BeEquivalentTo(after.Entities, \"re-ingesting already-present commits must leave the projection unchanged\");",
            "before.LastCommitHash.Should().Be(after.LastCommitHash);",
        ],
        ReproTemplate.ReplayDeterministic =>
        [
            "await using var first = await NewEngine();",
            "await using var second = await NewEngine();",
            "await Feed(first.DataModel, schedule.ArrivalsA);",
            "await Feed(second.DataModel, schedule.ArrivalsA);",
            "var a = await Read(first.DataModel);",
            "var b = await Read(second.DataModel);",
            "a.Entities.Should().BeEquivalentTo(b.Entities, \"feeding the identical schedule twice must produce identical projections\");",
            "a.LastCommitHash.Should().Be(b.LastCommitHash);",
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(template)),
    };
}
