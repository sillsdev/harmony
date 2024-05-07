using Crdt.Tests;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Crdt.Tests;

public class DbContextTests: DataModelTestBase
{
    [Fact]
    public async Task VerifyModel()
    {
        await Verify(DbContext.Model.ToDebugString(MetadataDebugStringOptions.LongDefault));
    }
}