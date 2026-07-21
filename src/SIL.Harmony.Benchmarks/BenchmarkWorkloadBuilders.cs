using SIL.Harmony.Changes;
using SIL.Harmony.Tests;

namespace SIL.Harmony.Benchmarks;

/// <summary>
/// Shared commit-building helpers for the benchmark suites so <see cref="DataModelSyncBenchmarks"/> (full sync
/// pipeline) and <see cref="AddSnapshotsBenchmarks"/> (isolated snapshot persist) exercise the same domain scenarios.
/// Builders are pure: they only use the source <see cref="DataModelTestBase"/> for its change factories and
/// <see cref="DataModelTestBase.NextDate"/> counter and never touch the database.
/// </summary>
public static class BenchmarkWorkloadBuilders
{
    public static Commit NewCommit(Guid clientId, DateTimeOffset dateTime, params IChange[] changes)
    {
        var commit = new Commit(Guid.NewGuid())
        {
            ClientId = clientId,
            HybridDateTime = new HybridDateTime(dateTime, 0)
        };
        for (var i = 0; i < changes.Length; i++)
        {
            commit.ChangeEntities.Add(DataModel.ToChangeEntity(changes[i], i, commit.Id));
        }
        return commit;
    }

    /// <summary>Create many distinct words (one SetWord per commit).</summary>
    public static List<Commit> BuildCreateWords(DataModelTestBase src, Guid clientId, int count)
    {
        var commits = new List<Commit>(count);
        for (var i = 0; i < count; i++)
        {
            commits.Add(NewCommit(clientId, src.NextDate(),
                src.SetWord(Guid.NewGuid(), $"entity {i}")));
        }
        return commits;
    }

    /// <summary>Create words each with a new definition in the same commit.</summary>
    public static List<Commit> BuildWordsWithDefinitions(DataModelTestBase src, Guid clientId, int count)
    {
        var commits = new List<Commit>(count);
        for (var i = 0; i < count; i++)
        {
            var wordId = Guid.NewGuid();
            commits.Add(NewCommit(clientId, src.NextDate(),
                src.SetWord(wordId, $"entity {i}"),
                src.NewDefinition(wordId, $"definition {i}", "noun")));
        }
        return commits;
    }

    /// <summary>Create words each with a tag and WordTag link in the same commit.</summary>
    public static List<Commit> BuildWordsWithTags(DataModelTestBase src, Guid clientId, int count)
    {
        var commits = new List<Commit>(count);
        for (var i = 0; i < count; i++)
        {
            var wordId = Guid.NewGuid();
            var tagId = Guid.NewGuid();
            commits.Add(NewCommit(clientId, src.NextDate(),
                src.SetWord(wordId, $"entity {i}"),
                src.SetTag(tagId, $"tag {i}"),
                src.TagWord(wordId, tagId)));
        }
        return commits;
    }

    /// <summary>Create one word, then modify that same word repeatedly.</summary>
    public static List<Commit> BuildModifySameWord(DataModelTestBase src, Guid clientId, int count)
    {
        var wordId = Guid.NewGuid();
        var commits = new List<Commit>(count);
        commits.Add(NewCommit(clientId, src.NextDate(),
            src.SetWord(wordId, "entity 0")));
        for (var i = 1; i < count; i++)
        {
            commits.Add(NewCommit(clientId, src.NextDate(),
                src.SetWord(wordId, $"entity {i}")));
        }
        return commits;
    }

    /// <summary>Create words then delete them.</summary>
    public static List<Commit> BuildCreateThenDelete(DataModelTestBase src, Guid clientId, int count)
    {
        var commits = new List<Commit>(count * 2);
        for (var i = 0; i < count; i++)
        {
            var wordId = Guid.NewGuid();
            commits.Add(NewCommit(clientId, src.NextDate(),
                src.SetWord(wordId, $"entity {i}")));
            commits.Add(NewCommit(clientId, src.NextDate(),
                src.DeleteWord(wordId)));
        }
        return commits;
    }

    /// <summary>Create, delete, then modify (apply-after-delete) for each word.</summary>
    public static List<Commit> BuildCreateDeleteModify(DataModelTestBase src, Guid clientId, int count)
    {
        var commits = new List<Commit>(count * 3);
        for (var i = 0; i < count; i++)
        {
            var wordId = Guid.NewGuid();
            commits.Add(NewCommit(clientId, src.NextDate(),
                src.SetWord(wordId, $"entity {i}")));
            commits.Add(NewCommit(clientId, src.NextDate(),
                src.DeleteWord(wordId)));
            // SetWordNote supports apply-on-existing (including deleted) without undeleting
            commits.Add(NewCommit(clientId, src.NextDate(),
                src.SetWordNote(wordId, $"note {i}")));
        }
        return commits;
    }

    /// <summary>
    /// Local already has create + late modify; sync inserts a mid-history modify for each word
    /// (forces stale snapshot deletion / rebuild). Returns the seed commits and the mid-history commits to sync.
    /// </summary>
    public static (List<Commit> seed, List<Commit> toSync) BuildOutOfOrderInsert(DataModelTestBase src, Guid clientId, int count)
    {
        var seed = new List<Commit>(count * 2);
        var toSync = new List<Commit>(count);
        for (var i = 0; i < count; i++)
        {
            var wordId = Guid.NewGuid();
            var createTime = src.NextDate();
            var midTime = src.NextDate();
            var lateTime = src.NextDate();

            seed.Add(NewCommit(clientId, createTime,
                src.SetWord(wordId, $"entity {i}")));
            // Mid-history change is what gets synced after local already has create + late
            toSync.Add(NewCommit(clientId, midTime,
                src.SetWordNote(wordId, $"note {i}")));
            seed.Add(NewCommit(clientId, lateTime,
                src.SetWord(wordId, $"entity {i} late")));
        }

        return (seed, toSync);
    }

    /// <summary>
    /// Seed a set of created words, then modify each one exactly once. The updates are the measured batch;
    /// their snapshots must update the already-projected rows (FindAsync per snapshot in the slow path).
    /// </summary>
    public static (List<Commit> seed, List<Commit> measured) BuildUpdateExisting(DataModelTestBase src, Guid clientId, int count)
    {
        var wordIds = new Guid[count];
        var seed = new List<Commit>(count);
        for (var i = 0; i < count; i++)
        {
            wordIds[i] = Guid.NewGuid();
            seed.Add(NewCommit(clientId, src.NextDate(),
                src.SetWord(wordIds[i], $"entity {i}")));
        }

        var measured = new List<Commit>(count);
        for (var i = 0; i < count; i++)
        {
            measured.Add(NewCommit(clientId, src.NextDate(),
                src.SetWord(wordIds[i], $"entity {i} updated")));
        }
        return (seed, measured);
    }
}
