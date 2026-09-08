using SIL.Harmony.Changes;

namespace SIL.Harmony.Tests.PropertyBased;

/// <summary>
/// The projected value we compare across engines/replays. Deterministic across runs and
/// across models: per-entity content signature (including deleted flag) plus the canonical
/// commit-hash chain. Deliberately excludes <c>ObjectSnapshot.Id</c>, which is
/// <c>Guid.NewGuid()</c> per run and would otherwise make identical projections compare unequal.
/// </summary>
public sealed record Projection(IReadOnlyDictionary<Guid, string> Entities, string? LastCommitHash);

/// <summary>
/// Bridges generated <see cref="Schedule"/>s to a real Harmony <see cref="DataModel"/>.
/// Each helper builds fresh in-memory engines (via <see cref="DataModelTestBase"/>, which
/// wires an in-memory SQLite <c>SampleDbContext</c>, a deterministic <c>MockTimeProvider</c>,
/// and <c>AlwaysValidateCommits = true</c> so the hash-chain check runs for free after every
/// ingest) and disposes them. Commits are minted fresh per feed so no engine ever mutates a
/// commit another engine also holds.
/// </summary>
internal static class HarmonyEngineHarness
{
    private static readonly Guid ClientId = new("00000000-0000-0000-0000-0000000000AA");

    /// <summary>Mint a fresh <see cref="Commit"/> from an immutable spec. Fresh per call because the engine mutates commit hashes during ingest.</summary>
    public static Commit ToCommit(CommitSpec spec)
    {
        var commit = new Commit(spec.Id)
        {
            ClientId = ClientId,
            HybridDateTime = spec.Time,
        };
        for (var i = 0; i < spec.Changes.Count; i++)
        {
            var change = spec.Changes[i];
            commit.ChangeEntities.Add(new ChangeEntity<IChange>
            {
                Change = change.ToChange(),
                Index = i,
                CommitId = spec.Id,
                EntityId = change.EntityId,
            });
        }
        return commit;
    }

    /// <summary>Ingest each arrival batch in order via the sync path (the straggler/rollback entry point).</summary>
    public static async Task Feed(DataModel model, IEnumerable<Arrival> arrivals)
    {
        foreach (var arrival in arrivals)
        {
            var batch = arrival.Batch.Select(ToCommit).ToArray();
            await ((ISyncable)model).AddRangeFromSync(batch);
        }
    }

    /// <summary>Ingest a flat set of commits as a single batch.</summary>
    public static async Task Feed(DataModel model, IEnumerable<CommitSpec> specs)
    {
        var batch = specs.Select(ToCommit).ToArray();
        await ((ISyncable)model).AddRangeFromSync(batch);
    }

    /// <summary>
    /// Read the projected state as a comparable value. Iterates the current snapshot of every
    /// entity (words and definitions, including deleted ones), capturing a deleted flag and a
    /// type-specific content signature, plus the canonical commit-hash chain.
    /// </summary>
    public static async Task<Projection> Read(DataModel model)
    {
        var entities = new Dictionary<Guid, string>();
        await foreach (var snapshot in model.GetLatestSnapshots())
        {
            var marker = snapshot.EntityIsDeleted ? "X:" : "-:";
            entities[snapshot.EntityId] = marker + EntitySignature.Of(snapshot.Entity.DbObject);
        }

        var projectSnapshot = await model.GetProjectSnapshot();
        return new Projection(entities, projectSnapshot.LastCommitHash);
    }

    /// <summary>Build a fresh in-memory engine. Caller must dispose the returned harness.</summary>
    public static async Task<DataModelTestBase> NewEngine()
    {
        var engine = new DataModelTestBase();
        await engine.InitializeAsync();
        return engine;
    }
}
