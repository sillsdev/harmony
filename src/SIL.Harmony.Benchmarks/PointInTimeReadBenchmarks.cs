using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Tests;

namespace SIL.Harmony.Benchmarks;

public enum RowLayout
{
    /// <summary>the rows are consecutive commits from one sync, so their snapshots were pruned</summary>
    Dense,

    /// <summary>the rows are 100 commits apart, so each one's snapshot is persisted and GetAtCommit skips the replay</summary>
    Sparse,
}

/// <summary>An activity page: the state of one entity at and before each of <see cref="Rows"/> commits.</summary>
[SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits")]
[MemoryDiagnoser]
[ReturnValueValidator(failOnError: true)]
public class PointInTimeReadBenchmarks
{
    private const int BackgroundWords = 500;
    private const int SparseGap = 100;

    private DataModelTestBase _model = null!;
    private Guid _wordId;
    private Commit[] _rows = null!;
    private Commit _beforeFirstRow = null!;
    private HashSet<Guid> _pauses = null!;
    private Dictionary<Guid, Guid> _previous = null!;

    [Params(5, 20, 100)]
    public int Rows { get; set; }

    [ParamsAllValues]
    public RowLayout Layout { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _model = new DataModelTestBase(alwaysValidate: false, performanceTest: true);
        var clientId = Guid.NewGuid();
        var backgroundIds = Enumerable.Range(0, BackgroundWords).Select(_ => Guid.NewGuid()).ToArray();
        _wordId = Guid.NewGuid();
        List<Commit> history =
        [
            .. backgroundIds.Select(id => Commit(new SetWordTextChange(id, "background"))),
            Commit(new SetWordTextChange(_wordId, "row word")),
        ];
        var rows = new List<Commit>();
        for (var row = 0; row < Rows; row++)
        {
            if (Layout == RowLayout.Sparse)
            {
                history.AddRange(Enumerable.Range(1, SparseGap - 1)
                    .Select(i => Commit(new SetWordNoteChange(backgroundIds[(row * SparseGap + i) % BackgroundWords], $"note {row} {i}"))));
            }

            var rowCommit = Commit(new SetWordTextChange(_wordId, $"text {row}"));
            rows.Add(rowCommit);
            history.Add(rowCommit);
        }

        _model.AddCommitsViaSync(history).Wait();
        _rows = [.. rows];
        _beforeFirstRow = history[history.IndexOf(_rows[0]) - 1];
        _previous = _rows.ToDictionary(r => r.Id, r => history[history.IndexOf(r) - 1].Id);
        _pauses = [.. _rows.Select(r => r.Id), .. _previous.Values];

        Commit Commit(IChange change) => BenchmarkWorkloadBuilders.NewCommit(clientId, _model.NextDate(), change);
    }

    [Benchmark(Baseline = true)]
    public async Task<int> GetAtCommitPerRow()
    {
        var found = 0;
        foreach (var row in _rows)
        {
            if (await _model.DataModel.GetAtCommit<object>(row, _wordId) is not null) found++;
            if (await _model.DataModel.GetBeforeCommit<object>(row, _wordId) is not null) found++;
        }

        return found;
    }

    [Benchmark]
    public async Task<int> OpenViewPerRow()
    {
        var found = 0;
        foreach (var row in _rows)
        {
            await using (var at = await _model.DataModel.OpenViewAtCommit(row))
            {
                if (await ReadWord(at) is not null) found++;
            }

            await using var before = await _model.DataModel.OpenViewBeforeCommit(row);
            if (await ReadWord(before) is not null) found++;
        }

        return found;
    }

    [Benchmark]
    public async Task<int> CommitReplay()
    {
        await using var replay = await _model.DataModel.StartCommitReplay(_beforeFirstRow, _rows[^1]);
        var states = new Dictionary<Guid, ObjectSnapshot?>();
        while (await replay.ApplyNextCommit() is { } commit)
        {
            if (_pauses.Contains(commit.Id)) states[commit.Id] = await ReadWord(replay);
        }

        return _rows.Sum(row => (states[row.Id] is null ? 0 : 1) + (states[_previous[row.Id]] is null ? 0 : 1));
    }

    private Task<ObjectSnapshot?> ReadWord(ISnapshotView view) => view.GetAsync(_wordId).AsTask();

    [GlobalCleanup]
    public void GlobalCleanup() => _model.DisposeAsync().AsTask().Wait();
}
