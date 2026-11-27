using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Tests;

namespace SIL.Harmony.Benchmarks;

/// <summary>
/// Deleting a word in an existing project: each delete cascades to its 2 definitions and their examples, and every
/// deleted entity looks up what references it.
/// </summary>
[SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits")]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 2, iterationCount: 10)]
public class DeleteBenchmarks
{
    private DataModelTestBase _template = null!;
    private DataModelTestBase _model = null!;
    private Guid[] _wordIds = null!;
    private int _next;

    /// <summary>each word brings 2 definitions and 2 examples, and is edited once: 6 snapshots per word</summary>
    [Params(2_000, 20_000)]
    public int WordCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _template = new DataModelTestBase(alwaysValidate: false, performanceTest: true);
        var clientId = Guid.NewGuid();
        _wordIds = Enumerable.Range(0, WordCount).Select(_ => Guid.NewGuid()).ToArray();
        var creates = _wordIds.Select((wordId, i) =>
        {
            Guid[] definitionIds = [Guid.NewGuid(), Guid.NewGuid()];
            return BenchmarkWorkloadBuilders.NewCommit(clientId, _template.NextDate(),
                _template.SetWord(wordId, $"word {i}"),
                _template.NewDefinition(wordId, $"definition a {i}", "noun", definitionId: definitionIds[0]),
                _template.NewDefinition(wordId, $"definition b {i}", "verb", definitionId: definitionIds[1]),
                NewExampleChange.FromString(definitionIds[0], $"example a {i}"),
                NewExampleChange.FromString(definitionIds[1], $"example b {i}"));
        });
        var edits = _wordIds.Select(wordId =>
            BenchmarkWorkloadBuilders.NewCommit(clientId, _template.NextDate(), _template.SetWordNote(wordId, "note")));
        foreach (var chunk in creates.Concat(edits).Chunk(5000))
        {
            _template.AddCommitsViaSync(chunk).Wait();
            _template.DbContext.ChangeTracker.Clear();
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _model = _template.ForkDatabase(alwaysValidate: false);
        _next = 0;
    }

    [Benchmark(OperationsPerInvoke = 5)]
    public void DeleteWord()
    {
        for (var i = 0; i < 5; i++)
        {
            var wordId = _wordIds[_next++ * 97 % _wordIds.Length];
            _model.WriteNextChange(_model.DeleteWord(wordId)).AsTask().Wait();
        }
    }

    [IterationCleanup]
    public void IterationCleanup() => _model.DisposeAsync().AsTask().Wait();

    [GlobalCleanup]
    public void GlobalCleanup() => _template.DisposeAsync().AsTask().Wait();
}
