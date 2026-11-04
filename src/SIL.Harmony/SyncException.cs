namespace SIL.Harmony;

/// <summary>
/// Applying commits failed in at least one direction. The other direction was still applied.
/// </summary>
public sealed class SyncException(Commit[] pulled, Exception? pullError, Commit[] pushed, Exception? pushError)
    : Exception($"Sync failed: {Outcome("pulled", pulled, pullError)}; {Outcome("pushed", pushed, pushError)}",
        pullError is not null && pushError is not null ? new AggregateException(pullError, pushError) : pullError ?? pushError)
{
    public Exception? PullError { get; } = pullError;
    public Exception? PushError { get; } = pushError;

    /// <summary>
    /// The commits known to have landed. A direction that failed is empty, though a failed push may still have reached the remote.
    /// </summary>
    public SyncResults Applied { get; } = new(pullError is null ? pulled : [], pushError is null ? pushed : [], false);

    private static string Outcome(string direction, Commit[] commits, Exception? error) =>
        error is null ? $"{commits.Length} {direction} commits applied" : $"applying {commits.Length} {direction} commits failed: {error.Message}";
}
