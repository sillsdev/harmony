using Microsoft.Extensions.DependencyInjection;
using SIL.Harmony.Changes;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Tests;

public class PersistExtraDataTests
{
    private DataModelTestBase _dataModelTestBase;

    public class CreateExtraDataModelChange(Guid entityId) : CreateChange<ExtraDataModel>(entityId), ISelfNamedType<CreateExtraDataModelChange>
    {
        public override ValueTask<ExtraDataModel> NewEntity(Commit commit, IChangeContext context)
        {
            return ValueTask.FromResult(new ExtraDataModel()
            {
                Id = EntityId,
            });
        }
    }

    public class ExtraDataModel : IObjectBase<ExtraDataModel>
    {
        public Guid Id { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public Guid CommitId { get; set; }
        public DateTimeOffset? DateTime { get; set; }
        public long Counter { get; set; }

        public Guid[] GetReferences()
        {
            return [];
        }

        public void RemoveReference(Guid id, Commit commit)
        {
        }

        public IObjectBase Copy()
        {
            return new ExtraDataModel()
            {
                Id = Id,
                DeletedAt = DeletedAt,
                CommitId = CommitId,
                DateTime = DateTime,
                Counter = Counter
            };
        }
    }

    public PersistExtraDataTests()
    {
        _dataModelTestBase = new DataModelTestBase(configure: services =>
        {
            services.Configure<CrdtConfig>(config =>
            {
                config.ObjectTypeListBuilder.DefaultAdapter().Add<ExtraDataModel>();
                config.ChangeTypeListBuilder.Add<CreateExtraDataModelChange>();
                config.BeforeSaveObject = (obj, snapshot) =>
                {
                    if (obj is ExtraDataModel extraDataModel)
                    {
                        extraDataModel.CommitId = snapshot.CommitId;
                        extraDataModel.DateTime = snapshot.Commit.HybridDateTime.DateTime;
                        extraDataModel.Counter = snapshot.Commit.HybridDateTime.Counter;
                    }
                    return ValueTask.CompletedTask;
                };
            });
        });
    }

    [Fact]
    public async Task CanPersistExtraData()
    {
        var entityId = Guid.NewGuid();
        var commit = await _dataModelTestBase.WriteNextChange(new CreateExtraDataModelChange(entityId));
        var extraDataModel = _dataModelTestBase.DataModel.QueryLatest<ExtraDataModel>().Should().ContainSingle().Subject;
        extraDataModel.Id.Should().Be(entityId);
        extraDataModel.CommitId.Should().Be(commit.Id);
        extraDataModel.DateTime.Should().Be(commit.HybridDateTime.DateTime);
        extraDataModel.Counter.Should().Be(commit.HybridDateTime.Counter);
    }
}