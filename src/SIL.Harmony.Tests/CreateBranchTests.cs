using SIL.Harmony.Refs;
using SIL.Harmony.Refs.Changes;
using SIL.Harmony.Refs.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace SIL.Harmony.Tests;

public class CreateBranchTests : DataModelTestBase
{
    private readonly RefsDataModel _refs;

    public CreateBranchTests() : base(configure: services =>
    {
        services.Configure<CrdtConfig>(config => config.AddHarmonyRefs());
        services.AddHarmonyRefsDataModel();
    })
    {
        _refs = _services.GetRequiredService<RefsDataModel>();
    }

    [Fact]
    public async Task CanCreateAndReadBranch()
    {
        var branchId = Guid.NewGuid();
        await _refs.CreateBranch(_localClientId, branchId, "feature-x");

        var branch = await DataModel.GetLatest<Branch>(branchId);
        branch.Should().NotBeNull();
        branch!.Id.Should().Be(branchId);
        branch.Name.Should().Be("feature-x");
    }

    [Fact]
    public async Task DuplicateBranchNamesAreAllowed()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await _refs.CreateBranch(_localClientId, firstId, "dup");
        await _refs.CreateBranch(_localClientId, secondId, "dup");

        var first = await DataModel.GetLatest<Branch>(firstId);
        var second = await DataModel.GetLatest<Branch>(secondId);
        first!.Name.Should().Be("dup");
        second!.Name.Should().Be("dup");
        first.Id.Should().NotBe(second.Id);

        var both = _refs.ListBranches()
            .ToBlockingEnumerable(TestContext.Current.CancellationToken)
            .ToArray();
        both.Should().HaveCount(2);
    }
}
