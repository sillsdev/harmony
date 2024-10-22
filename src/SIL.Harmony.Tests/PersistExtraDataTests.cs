using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SIL.Harmony.Changes;
using SIL.Harmony.Core;
using SIL.Harmony.Entities;
using SIL.Harmony.Sample;

namespace SIL.Harmony.Tests;

public class PersistExtraDataTests
{
    private DataModelTestBase _dataModelTestBase;

    public class CreateExtraDataModelChange(Guid entityId) : CreateChange<ExtraDataModel>(entityId), ISelfNamedType<CreateExtraDataModelChange>
    {
        public override ValueTask<ExtraDataModel> NewEntity(Commit commit, ChangeContext context)
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
        public HybridDateTime? HybridDateTime { get; set; }

        public Guid[] GetReferences()
        {
            return [];
        }

        public void RemoveReference(Guid id, Commit commit)
        {
        }

        public IObjectBase Copy()
        {
            return new ExtraDataModel();
        }
    }

    public PersistExtraDataTests()
    {
        _dataModelTestBase = new DataModelTestBase(configure: services =>
        {
            services.AddOptions<CrdtConfig>().Configure(config =>
            {
                config.ObjectTypeListBuilder.DefaultAdapter().Add<ExtraDataModel>();
                config.ChangeTypeListBuilder.Add<CreateExtraDataModelChange>();
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
        extraDataModel.HybridDateTime.Should().Be(commit.HybridDateTime);
    }
}