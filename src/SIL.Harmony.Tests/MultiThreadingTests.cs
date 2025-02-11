using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace SIL.Harmony.Tests;

public class MultiThreadingTests(ITestOutputHelper output)
{
    private const string _connectionString = "Data Source=file:MultiThreadingTests.db?mode=memory&cache=shared";
    private static async Task<Exception?> Run(ITestOutputHelper output,
        CancellationTokenSource cancellationTokenSource,
        bool debug)
    {
        return await Task.Run(() =>
        {
            Exception? exception = null;
            var t = new Thread(() =>
            {
                var random = new Random();
                var fixture = new DataModelTestBase(new SqliteConnection(_connectionString));
                fixture.InitializeAsync().Wait();
                var id = Guid.NewGuid();
                for (var i = 0; i < 100; i++)
                {
                    var value = "test" + i;
                    try
                    {
                        Thread.Sleep(random.Next(1, 10));

                        _ = fixture.WriteNextChange(fixture.SetWord(id, value)).Result;

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
            });
            t.Start();
            t.Join();
            return exception;
        });
    }

    [Fact]
    public async Task CanApplyChangesWithoutError()
    {
        //ensure the database is created before running the tests
        _ = new DataModelTestBase(new SqliteConnection(_connectionString));
        bool debug = false;
        var cancellationTokenSource = new CancellationTokenSource();
        var results = await Task.WhenAll(
            Run(output, cancellationTokenSource, debug),
            Run(output, cancellationTokenSource, debug),
            Run(output, cancellationTokenSource, debug)
        );
        foreach (var exception in results)
        {
            exception.Should().BeNull();
        }

    }
}