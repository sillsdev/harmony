using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using SIL.Harmony.Changes;

namespace SIL.Harmony;

internal class JsonSyncable : ISyncable
{
    private readonly DirectoryInfo _rootDir;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly ILogger _logger;
    private readonly SyncService _syncService;
    private static readonly ConcurrentDictionary<Guid, AsyncLock> ClientLocks = new();

    public const string FilenamePrefix = "client_";
    public const string FilenameExtension = ".jsonl";

    public JsonSyncable(DirectoryInfo dir, JsonSerializerOptions serializerOptions, SyncService syncService, ILogger logger)
    {
        if (!dir.Exists) dir.Create();
        _rootDir = dir;
        _serializerOptions = serializerOptions;
        _logger = logger;
        _syncService = syncService;
    }

    public async Task AddRangeFromSync(IEnumerable<Commit> commits)
    {
        var commitArray = commits.ToArray();
        if (commitArray.Length == 0) return;

        var groups = commitArray.GroupBy(c => c.ClientId).ToArray();
        await Parallel.ForEachAsync(groups, async (group, ct) =>
        {
            using (await ClientLocks.GetOrAdd(group.Key, _ => new AsyncLock()).LockAsync())
            {
                var file = FileForClientId(group.Key);
                await RepairTrailingFragmentAsync(file, ct);
                var existingIds = await GetExistingCommitIdsAsync(file, ct);
                var newCommits = group.Where(c => existingIds.Add(c.Id)).DefaultOrder().ToArray();
                if (newCommits.Length == 0) return;

                await using var stream = new StreamWriter(file.FullName, append: true);
                foreach (var commit in newCommits)
                    WriteCommit(stream, commit);
            }
        });
    }

    public async Task<SyncState> GetSyncState()
    {
        var heads = new ConcurrentBag<ClientState>();
        await Parallel.ForEachAsync(AllClientFiles(), async (file, ct) =>
        {
            var clientStateBuilder = new ClientStateBuilder()
            {
                ClientId = ClientIdForFile(file)
            };
            await foreach (var commit in ReadAllCommitsAsync(file, ct))
            {
                clientStateBuilder.Add(commit.Id, commit.HybridDateTime.DateTime);
            }
            var state = clientStateBuilder.Build();
            //an empty client file would otherwise produce a state that reads as ClientState.OnlyHasTimestamp
            if (state.CommitCount > 0)
                heads.Add(state);
        });
        return new SyncState(heads.ToArray());
    }

    public async Task<ChangesResult<Commit>> GetChanges(SyncState otherHeads)
    {
        var heads = new ConcurrentBag<ClientState>();
        var allCommits = new ConcurrentBag<Commit>();
        var files = AllClientFiles().ToArray();
        await Parallel.ForEachAsync(files, async (file, ct) =>
        {
            var clientStateBuilder = new ClientStateBuilder()
            {
                ClientId = ClientIdForFile(file)
            };
            await foreach (var commit in ReadAllCommitsAsync(file, ct))
            {
                clientStateBuilder.Add(commit.Id, commit.HybridDateTime.DateTime);
                allCommits.Add(commit);
            }
            var state = clientStateBuilder.Build();
            if (state.CommitCount > 0)
                heads.Add(state);
        });
        var localState = new SyncState(heads.ToArray());
        var missing = allCommits.GetCommitsMissingFromRemote<Commit, IChange>(localState, otherHeads).ToArray();
        return new ChangesResult<Commit>(missing, localState);
    }

    public Task<SyncResults> SyncWith(ISyncable remoteModel)
    {
        return _syncService.SyncWith(this, remoteModel);
    }

    public Task SyncMany(ISyncable[] remotes)
    {
        return _syncService.SyncMany(this, remotes);
    }

    public ValueTask<bool> ShouldSync()
    {
        return new ValueTask<bool>(true);
    }

    private IEnumerable<FileInfo> AllClientFiles()
    {
        return _rootDir.EnumerateFiles($"{FilenamePrefix}*{FilenameExtension}");
    }

    private FileInfo FileForClientId(Guid clientId)
    {
        var path = Path.Join(_rootDir.FullName, $"{FilenamePrefix}{clientId}{FilenameExtension}");
        return new FileInfo(path);
    }

    private static Guid ClientIdForFile(FileInfo clientIdFile)
    {
        var id = clientIdFile.Name[FilenamePrefix.Length..^FilenameExtension.Length];
        return Guid.Parse(id);
    }

    private async IAsyncEnumerable<Commit> ReadAllCommitsAsync(FileInfo file, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!file.Exists)
            yield break;

        await using var stream = file.OpenRead();
        if (stream.Length == 0)
            yield break;
        var endsWithNewline = await EndsWithNewlineAsync(stream, cancellationToken);
        using var reader = new StreamReader(stream);
        var line = await reader.ReadLineAsync(cancellationToken);
        while (line is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = await reader.ReadLineAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(line))
            {
                Commit? commit;
                //an unterminated last line is most likely a write that was interrupted (e.g. a drive pulled mid-sync),
                //the commit still exists on the client that wrote it, so it will be re-sent on the next sync
                if (next is null && !endsWithNewline)
                {
                    if (!TryDeserializeCommit(line, out commit))
                    {
                        _logger.LogWarning("Ignoring incomplete trailing line in {File}", file.FullName);
                        yield break;
                    }
                }
                else
                {
                    commit = JsonSerializer.Deserialize<Commit>(line, _serializerOptions);
                }

                if (commit is not null)
                    yield return commit;
            }
            line = next;
        }
    }

    /// <summary>
    /// Ensures the file ends with a newline so the next append starts on its own line.
    /// A complete commit missing only its newline gets one; an incomplete fragment is truncated away.
    /// Must be called while holding the client lock.
    /// </summary>
    private async Task RepairTrailingFragmentAsync(FileInfo file, CancellationToken cancellationToken)
    {
        if (!file.Exists)
            return;

        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var fragmentStart = await FindEndOfLastCompleteLineAsync(stream, cancellationToken);
        if (fragmentStart == stream.Length)
            return;

        var fragment = new byte[stream.Length - fragmentStart];
        stream.Position = fragmentStart;
        await stream.ReadExactlyAsync(fragment, cancellationToken);
        if (TryDeserializeCommit(fragment, out _))
        {
            stream.Position = stream.Length;
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        }
        else
        {
            _logger.LogWarning("Truncating incomplete trailing line ({Length} bytes) from {File}", fragment.Length, file.FullName);
            stream.SetLength(fragmentStart);
        }
    }

    private static async Task<bool> EndsWithNewlineAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var lastByte = new byte[1];
        stream.Position = stream.Length - 1;
        await stream.ReadExactlyAsync(lastByte, cancellationToken);
        stream.Position = 0;
        return lastByte[0] == (byte)'\n';
    }

    /// <returns>The position just past the last '\n' in the file, or 0 if there is none.</returns>
    private static async Task<long> FindEndOfLastCompleteLineAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var end = stream.Length;
        while (end > 0)
        {
            var start = Math.Max(0, end - buffer.Length);
            var count = (int)(end - start);
            stream.Position = start;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
            //'\n' never occurs inside a multi-byte UTF-8 sequence, so a byte search is safe
            var index = buffer.AsSpan(0, count).LastIndexOf((byte)'\n');
            if (index >= 0)
                return start + index + 1;
            end = start;
        }
        return 0;
    }

    private bool TryDeserializeCommit(string json, out Commit? commit)
    {
        try
        {
            commit = JsonSerializer.Deserialize<Commit>(json, _serializerOptions);
            return true;
        }
        catch (JsonException)
        {
            commit = null;
            return false;
        }
    }

    private bool TryDeserializeCommit(ReadOnlySpan<byte> utf8Json, out Commit? commit)
    {
        try
        {
            commit = JsonSerializer.Deserialize<Commit>(utf8Json, _serializerOptions);
            return true;
        }
        catch (JsonException)
        {
            commit = null;
            return false;
        }
    }

    private async Task<HashSet<Guid>> GetExistingCommitIdsAsync(FileInfo file, CancellationToken cancellationToken)
    {
        return await ReadAllCommitsAsync(file, cancellationToken).Select(c => c.Id)
            .ToHashSetAsync(cancellationToken: cancellationToken);
    }

    private void WriteCommit(StreamWriter stream, Commit commit)
    {
        var json = JsonSerializer.Serialize(commit, _serializerOptions);
        stream.WriteLine(json);
    }
}
