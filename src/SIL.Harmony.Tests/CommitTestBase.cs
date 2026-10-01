using SIL.Harmony.Changes;
using SIL.Harmony.Config;
using SIL.Harmony.Sample;
using SIL.Harmony.Sample.Changes;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests;

/// <summary>builds commits and changes in memory; nothing here needs a database</summary>
public class CommitTestBase
{
    private static int _instanceCount;

    protected readonly Guid _localClientId = Guid.NewGuid();
    //each instance starts at its own hour, so commits from two instances in one test never share a timestamp
    private DateTimeOffset _currentDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(_instanceCount++);

    private static readonly Lazy<HarmonyConfig> LazySampleConfig = new(() =>
    {
        var config = new HarmonyConfig();
        CrdtSampleKernel.ConfigureSample(config);
        return config;
    });

    /// <summary>the sample model's config as the sample app registers it; shared, so don't mutate it</summary>
    protected static HarmonyConfig SampleConfig => LazySampleConfig.Value;

    public DateTimeOffset NextDate() => _currentDate = _currentDate.AddDays(1);

    public void SetCurrentDate(DateTimeOffset dateTime) => _currentDate = dateTime;

    protected DateTimeOffset CurrentDate => _currentDate;

    public Commit NextCommit(params IEnumerable<IChange> changes) => CommitAt(NextDate(), changes);

    public Commit CommitAt(DateTimeOffset dateTime, params IEnumerable<IChange> changes) => BuildCommit(_localClientId, dateTime, changes);

    public Commit CommitBefore(Commit before, params IEnumerable<IChange> changes) => CommitAt(before.DateTime.AddHours(-1), changes);

    public static Commit BuildCommit(Guid clientId, DateTimeOffset dateTime, IEnumerable<IChange> changes)
    {
        var commit = new Commit { ClientId = clientId, HybridDateTime = new HybridDateTime(dateTime, 0) };
        commit.ChangeEntities.AddRange(changes.Select((change, index) => new ChangeEntity<IChange>
        {
            Change = change,
            Index = index,
            CommitId = commit.Id,
            EntityId = change.EntityId
        }));
        return commit;
    }

    public IChange SetWord(Guid entityId, string value)
    {
        return new SetWordTextChange(entityId, value);
    }

    public IChange SetWordNote(Guid entityId, string note)
    {
        return new SetWordNoteChange(entityId, note);
    }

    public IChange DeleteWord(Guid entityId)
    {
        return new DeleteChange<Word>(entityId);
    }

    public IChange SetTag(Guid entityId, string value)
    {
        return new SetTagChange(entityId, value);
    }

    public IChange TagWord(Guid wordId, Guid tagId, Guid entityId = default)
    {
        return new TagWordChange(new WordTag { Id = entityId, WordId = wordId, TagId = tagId });
    }

    public IChange DeleteTag(Guid entityId)
    {
        return new DeleteChange<Tag>(entityId);
    }

    public IChange NewDefinition(Guid wordId,
        string text,
        string partOfSpeech,
        double order = 0,
        Guid? definitionId = default)
    {
        return new NewDefinitionChange(definitionId ?? Guid.NewGuid())
        {
            WordId = wordId,
            Text = text,
            PartOfSpeech = partOfSpeech,
            Order = order
        };
    }
}
