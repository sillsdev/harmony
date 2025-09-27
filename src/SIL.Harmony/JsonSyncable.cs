using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Serialization;
using Microsoft.Extensions.Logging;
using Nito.Disposables.Internals;

namespace SIL.Harmony;

public class JsonSyncable : ISyncable
{
    DirectoryInfo RootDir { get; init; }
    ILogger Logger { get; init; }

    public const string FilenamePrefix = "client_";
    public const string FilenameExtension = ".json";

    public JsonSyncable(DirectoryInfo dir, ILogger logger)
    {
        if (!dir.Exists) dir.Create();
        RootDir = dir;
        Logger = logger;
    }

    public Task AddRangeFromSync(IEnumerable<Commit> commits)
    {
        // TODO: Is it better to initialize this *outside* the Task.Run, or *inside* it?
        var clientFiles = new ConcurrentDictionary<Guid, FileInfo>();
        return Task.Run(() => {
            foreach (var commit in commits)
            {
                var file = clientFiles.GetOrAdd(commit.ClientId, FileForClientId);
                // TODO: Append this commit and the FileInfo to a set of concurrent queues, one for each client ID, and spool off one thread per client ID that will pull from its queue and write the commits one at a time to its own individual file
                // For now, we instead do it serially with the code below
                using var stream = file.AppendText();
                WriteCommit(stream, commit);
            }
        });
    }

    public static Commit? LatestCommit(IEnumerable<Commit> commits)
    {
        return commits.MaxBy(c => c.HybridDateTime.DateTime);
    }

    public static DateTimeOffset LatestCommitDate(IEnumerable<Commit> commits)
    {
        return commits.Select(c => c.HybridDateTime.DateTime).Max(); // TODO: What will this return if no commits? DateTimeOffset.Min or something? Need to verify
    }

    public static async Task<DateTimeOffset> LatestCommitDateAsync(IAsyncEnumerable<Commit?> commits)
    {
        return await commits.Where(c => c is not null).MaxAsync(c => c!.HybridDateTime.DateTime); // TODO: What will this return if no commits? DateTimeOffset.Min or something? Need to verify
    }

    public Task<DateTimeOffset> LatestCommitDateForClient(Guid clientId)
    {
        var file = FileForClientId(clientId);
        return LatestCommitDateForFile(file);
        // TODO: Clean up these methods once I see which ones I'm going to need and which ones I won't
    }

    public Task<DateTimeOffset> LatestCommitDateForFile(FileInfo file)
    {
        var commits = ReadCommits(file);
        return LatestCommitDateAsync(commits);
        // NOTE: This parses the whole file just looking for the latest date, and later we're going to parse it again looking for commits.
        // TODO: Find a better way to store this; maybe while commits are being written, the latest date is calculated and the file's modification date is set, after closing, to be that date? Probably not reliable enough.
        // Or perhaps we keep a second per-client file that contains info like latest commit date and so on.
    }

    private IEnumerable<FileInfo> AllClientFiles()
    {
        return RootDir.EnumerateFiles($"{FilenamePrefix}*{FilenameExtension}");
    }

    private IEnumerable<Guid> AllKnownClientIds()
    {
        return AllClientFiles().Select(ClientIdForFile);
    }

    public async Task<SyncState> GetSyncState()
    {
        var clientIds = AllKnownClientIds().ToArray();
        var dict = clientIds.ToDictionary(id => id, async id => (await LatestCommitDateForClient(id)).ToUnixTimeMilliseconds());
        // TODO: Now we have a dict of Guid,Task<long> but we need to await each one
        return new SyncState(dict);
    }

    public Task<ChangesResult<Commit>> GetChanges(SyncState otherHeads)
    {
        return Task.FromResult(ChangesResult<Commit>.Empty);
    }

    public Task<SyncResults> SyncWith(ISyncable remoteModel)
    {
        return Task.FromResult(new SyncResults([], [], false));
    }

    public Task SyncMany(ISyncable[] remotes)
    {
        return Task.CompletedTask;
    }

    public ValueTask<bool> ShouldSync()
    {
        return new ValueTask<bool>(false);
    }

    private FileInfo FileForClientId(Guid clientId)
    {
        var path = Path.Join(RootDir.FullName, $"{FilenamePrefix}{clientId}{FilenameExtension}");
        return new FileInfo(path);
    }

    private Guid ClientIdForFile(FileInfo clientIdFile)
    {
        var id = clientIdFile.Name[FilenamePrefix.Length..^FilenameExtension.Length];
        return Guid.Parse(id);
    }

    private static void WriteCommit(StreamWriter stream, Commit commit)
    {
        JsonSerializer.Serialize(stream.BaseStream, commit, JsonSerializerOptions.Web);
        stream.Write('\n'); // Don't use WriteLine() as that could send "\r\n" on Windows
    }

    private static IAsyncEnumerable<Commit?> ReadCommits(FileInfo file)
    {
        return JsonSerializer.DeserializeAsyncEnumerable<Commit>(file.OpenRead(), topLevelValues: true, JsonSerializerOptions.Web);
        // TODO: Find out if DeserializeAsyncEnumerable disposes of the Stream on completion, otherwise we need to do it in here
    }
}
