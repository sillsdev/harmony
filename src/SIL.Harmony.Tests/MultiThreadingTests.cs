using Microsoft.Data.Sqlite;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

public class MultiThreadingTests(ITestOutputHelper output)
{
    private const string _connectionString = "Data Source=file:MultiThreadingTests.db?mode=memory&cache=shared";
    private const int _changesPerThread = 100;

    private static async Task<(Guid id, string lastValue, Exception? exception)> Run(ITestOutputHelper output,
        CancellationTokenSource cancellationTokenSource,
        bool debug)
    {
        return await Task.Run(() =>
        {
            Exception? exception = null;
            var id = Guid.NewGuid();
            var lastValue = "";
            var t = new Thread(() =>
            {
                var random = new Random();
                var fixture = new DataModelTestBase(new SqliteConnection(_connectionString));
                try
                {
                    fixture.InitializeAsync().GetAwaiter().GetResult();
                    for (var i = 0; i < _changesPerThread; i++)
                    {
                        var value = "test" + i;
                        try
                        {
                            Thread.Sleep(random.Next(1, 10));

                            _ = fixture.WriteNextChange(fixture.SetWord(id, value)).Result;
                            lastValue = value;

                            if (debug) output.WriteLine($"id: {id}, value:{value}");
                            if (cancellationTokenSource.IsCancellationRequested) return;
                        }
                        catch (Exception e)
                        {
                            output.WriteLine($"id: {id}, value:{value}, error: {e}");
                            cancellationTokenSource.Cancel();
                            exception = e;
                            return;
                        }
                    }
                }
                finally
                {
                    //dispose this thread's fixture (and its DI ServiceProvider); the test-level fixture
                    //keeps the shared in-memory database alive until assertions complete.
                    //guard the cleanup so a disposal failure can't surface as an unhandled exception on
                    //this raw thread (which would crash the test host rather than fail the test).
                    try
                    {
                        fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception disposeException)
                    {
                        output.WriteLine($"error disposing fixture: {disposeException}");
                    }
                }
            });
            t.Start();
            t.Join();
            return (id, lastValue, exception);
        });
    }

    [Fact]
    public async Task CanApplyChangesWithoutError()
    {
        //ensure the database is created before running the tests, and keep this fixture alive (and its
        //connection open) so the shared in-memory database survives while the worker fixtures dispose
        await using var fixture = new DataModelTestBase(new SqliteConnection(_connectionString));
        bool debug = false;
        var cancellationTokenSource = new CancellationTokenSource();
        var results = await Task.WhenAll(
            Run(output, cancellationTokenSource, debug),
            Run(output, cancellationTokenSource, debug),
            Run(output, cancellationTokenSource, debug)
        );
        foreach (var (_, _, exception) in results)
        {
            exception.Should().BeNull();
        }

        //every thread wrote to its own entity; assert the model converged to each thread's final write
        //(a lost update or corruption that doesn't throw would otherwise pass unnoticed)
        foreach (var (id, lastValue, _) in results)
        {
            lastValue.Should().Be("test" + (_changesPerThread - 1));
            var word = await fixture.DataModel.GetLatest<Word>(id);
            word.Should().NotBeNull();
            word.Text.Should().Be(lastValue);
        }
    }
}
