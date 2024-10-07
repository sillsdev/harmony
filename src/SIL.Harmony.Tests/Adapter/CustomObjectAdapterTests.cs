using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using SIL.Harmony.Entities;
using SIL.Harmony.Linq2db;

namespace SIL.Harmony.Tests.Adapter;

public class CustomObjectAdapterTests
{
    public class MyDbContext(DbContextOptions<MyDbContext> options, IOptions<CrdtConfig> crdtConfig) : DbContext(options), ICrdtDbContext
    {
        public DbSet<MyClass> MyClasses { get; set; } = null!;
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.UseCrdt(crdtConfig.Value);
        }
    }

    public class MyClass
    {
        public Guid Identifier { get; set; }
        public long? DeletedTime { get; set; }
        public string? MyString { get; set; }
    }

    public class CreateMyClassChange : CreateChange<MyClass>, ISelfNamedType<CreateMyClassChange>
    {
        private readonly MyClass _entity;

        public CreateMyClassChange(MyClass entity) : base(entity.Identifier)
        {
            _entity = entity;
        }

        public override ValueTask<MyClass> NewEntity(Commit commit, ChangeContext context)
        {
            return ValueTask.FromResult(_entity);
        }
    }

    [Fact]
    public async Task CanAdaptACustomObject()
    {
        var services = new ServiceCollection()
            .AddDbContext<MyDbContext>(builder => builder.UseSqlite("Data Source=:memory:"))
            .AddCrdtData<MyDbContext>(config =>
            {
                config.ChangeTypeListBuilder.Add<CreateMyClassChange>();
                config.ObjectTypeListBuilder
                    .CustomAdapter()
                    .Add<MyClass>(
                        "MyClassTypeName",
                        o => o.Identifier,
                        o => o.DeletedTime.HasValue ? DateTimeOffset.FromUnixTimeSeconds(o.DeletedTime.Value) : null,
                        (o, deletedAt) => o.DeletedTime = deletedAt?.ToUnixTimeSeconds(),
                        o => [],
                        (o, id, commit) => { },
                        o => new MyClass
                        {
                            Identifier = o.Identifier,
                            DeletedTime = o.DeletedTime,
                            MyString = o.MyString
                        },
                        builder => builder.HasKey(o => o.Identifier)
                    );
            }).BuildServiceProvider();
        var myDbContext = services.GetRequiredService<MyDbContext>();
        await myDbContext.Database.OpenConnectionAsync();
        await myDbContext.Database.EnsureCreatedAsync();
        var dataModel = services.GetRequiredService<DataModel>();
        var objectId = Guid.NewGuid();
        await dataModel.AddChange(Guid.NewGuid(), new CreateMyClassChange(new MyClass
        {
            Identifier = objectId,
            MyString = "Hello"
        }));
        var snapshot = await dataModel.GetLatestSnapshotByObjectId(objectId);
        snapshot.Should().NotBeNull();
        snapshot.Entity.Should().NotBeNull();
        var myClass = snapshot.Entity.Is<MyClass>();
        myClass.Should().NotBeNull();
        myClass.Identifier.Should().Be(objectId);
        myClass.MyString.Should().Be("Hello");
        myClass.DeletedTime.Should().BeNull();
    }
}