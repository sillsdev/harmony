namespace SIL.Harmony.Tests;

/// <summary>
/// A SQLite database file unique to one test, deleted on dispose.
/// A fixed relative path like <c>test.db</c> is shared across runs and worktrees, so a stale schema leaks into later runs.
/// </summary>
public sealed class TempDbFile : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"harmony-test-{Guid.NewGuid():N}.db");

    // Pooling off so closing the last connection releases the file handle and Dispose can delete it.
    public string ConnectionString => $"Data Source={Path};Pooling=False";

    public void Dispose()
    {
        File.Delete(Path);
    }
}
