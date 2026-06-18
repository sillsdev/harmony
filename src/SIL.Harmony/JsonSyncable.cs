using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using SIL.Harmony.Changes;

namespace SIL.Harmony;

public class JsonSyncable : ISyncable
{
    private readonly DirectoryInfo _rootDir;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly ILogger _logger;
    private static readonly ConcurrentDictionary<Guid, AsyncLock> ClientLocks = new();

    public const string FilenamePrefix = "client_";
    public const string FilenameExtension = ".json";

    public JsonSyncable(DirectoryInfo dir, JsonSerializerOptions serializerOptions, ILogger logger)
    {
        if (!dir.Exists) dir.Create();
        _rootDir = dir;
        _serializerOptions = serializerOptions;
        _logger = logger;
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
        var heads = new ConcurrentDictionary<Guid, long>();
        await Parallel.ForEachAsync(AllClientFiles(), async (file, ct) =>
        {
            var ts = await GetHeadTimestampAsync(file, ct);
            if (ts is not null)
                heads[ClientIdForFile(file)] = ts.Value.ToUnixTimeMilliseconds();
        });
        return new SyncState(new Dictionary<Guid, long>(heads));
    }

    public async Task<ChangesResult<Commit>> GetChanges(SyncState otherHeads)
    {
        var localState = await GetSyncState();
        var allCommits = new ConcurrentBag<Commit>();
        await Parallel.ForEachAsync(localState.ClientHeads.Keys, async (clientId, ct) =>
        {
            await foreach (var commit in ReadAllCommitsAsync(FileForClientId(clientId), ct))
                allCommits.Add(commit);
        });
        var missing = allCommits.GetMissingCommits<Commit, IChange>(localState, otherHeads).ToArray();
        return new ChangesResult<Commit>(missing, localState);
    }

    public Task<SyncResults> SyncWith(ISyncable remoteModel)
    {
        return SyncHelper.SyncWith(this, remoteModel, _serializerOptions);
    }

    public Task SyncMany(ISyncable[] remotes)
    {
        return SyncHelper.SyncMany(this, remotes, _serializerOptions);
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
        if (!file.Exists || file.Length == 0)
            yield break;

        await using var stream = file.OpenRead();
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var commit = JsonSerializer.Deserialize<Commit>(line, _serializerOptions);
            if (commit is not null)
                yield return commit;
        }
    }

    private async Task<DateTimeOffset?> GetHeadTimestampAsync(FileInfo file, CancellationToken cancellationToken)
    {
        return await ReadAllCommitsAsync(file, cancellationToken).MaxAsync(c => (DateTimeOffset?)c.HybridDateTime.DateTime, cancellationToken);
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
