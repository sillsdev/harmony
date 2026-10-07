using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SIL.Harmony;

public interface IJsonSyncableFactory
{
    /// <summary>
    /// A syncable that stores commits in <paramref name="dir"/> as one JSON Lines file (one commit per line) per client, so the folder can be shared through Dropbox, Git or a flash drive.
    /// </summary>
    ISyncable Create(DirectoryInfo dir);
}

internal class JsonSyncableFactory(JsonSerializerOptions serializerOptions, SyncService syncService, ILogger<JsonSyncable> logger) : IJsonSyncableFactory
{
    public ISyncable Create(DirectoryInfo dir) => new JsonSyncable(dir, serializerOptions, syncService, logger);
}
