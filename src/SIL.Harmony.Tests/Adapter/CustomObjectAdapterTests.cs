using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SIL.Harmony.Adapters;
using SIL.Harmony.Changes;
using SIL.Harmony.Db;
using SIL.Harmony.Entities;
using SIL.Harmony.Linq2db;

namespace SIL.Harmony.Tests.Adapter;

public class CustomObjectAdapterTests
{
    public class MyDbContext(DbContextOptions<MyDbContext> options, IOptions<CrdtConfig> crdtConfig)
        : DbContext(options), ICrdtDbContext
    {
        public DbSet<MyClass> MyClasses { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.UseCrdt(crdtConfig.Value);
        }
    }

    [JsonPolymorphic]
    [JsonDerivedType(typeof(MyClass), MyClass.ObjectTypeName)]
    [JsonDerivedType(typeof(MyClass2), MyClass2.ObjectTypeName)]
    public interface IMyCustomInterface
    {
        Guid Identifier { get; set; }
        long? DeletedTime { get; set; }
        string TypeName { get; }
        IMyCustomInterface Copy();
    }

    public class MyClass : IMyCustomInterface
    {
        public const string ObjectTypeName = "MyClassTypeName";
        string IMyCustomInterface.TypeName => ObjectTypeName;

        public IMyCustomInterface Copy()
        {
            return new MyClass
            {
                Identifier = Identifier,
                DeletedTime = DeletedTime,
                MyString = MyString
            };
        }

        public Guid Identifier { get; set; }
        public long? DeletedTime { get; set; }
        public string? MyString { get; set; }
    }

    public class MyClass2 : IMyCustomInterface
    {
        public const string ObjectTypeName = "MyClassTypeName2";
        string IMyCustomInterface.TypeName => ObjectTypeName;

        public IMyCustomInterface Copy()
        {
            return new MyClass2()
            {
                Identifier = Identifier,
                DeletedTime = DeletedTime,
                MyNumber = MyNumber
            };
        }

        public Guid Identifier { get; set; }
        public long? DeletedTime { get; set; }
        public decimal MyNumber { get; set; }
    }

    public class MyClassAdapter : ICustomAdapter<MyClassAdapter, IMyCustomInterface>
    {
        public static string AdapterTypeName => "MyClassAdapter";

        public static MyClassAdapter Create(IMyCustomInterface obj)
        {
            return new MyClassAdapter(obj);
        }

        public IMyCustomInterface Obj { get; }

        [JsonConstructor]
        public MyClassAdapter(IMyCustomInterface obj)
        {
            Obj = obj;
        }

        [JsonIgnore]
        public Guid Id => Obj.Identifier;

        [JsonIgnore]
        public DateTimeOffset? DeletedAt
        {
            get => Obj.DeletedTime.HasValue ? DateTimeOffset.FromUnixTimeSeconds(Obj.DeletedTime.Value) : null;
            set => Obj.DeletedTime = value?.ToUnixTimeSeconds();
        }

        public string GetObjectTypeName() => Obj.TypeName;

        [JsonIgnore]
        public object DbObject => Obj;

        public Guid[] GetReferences() => [];

        public void RemoveReference(Guid id, Commit commit)
        {
        }

        public IObjectBase Copy() => new MyClassAdapter(Obj.Copy());
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

    public class CreateMyClass2Change : CreateChange<MyClass2>, ISelfNamedType<CreateMyClass2Change>
    {
        private readonly MyClass2 _entity;

        public CreateMyClass2Change(MyClass2 entity) : base(entity.Identifier)
        {
            _entity = entity;
        }

        public override ValueTask<MyClass2> NewEntity(Commit commit, ChangeContext context)
        {
            return ValueTask.FromResult(_entity);
        }
    }

    [Fact]
    public async Task CanAdaptACustomObject()
    {
        var services = new ServiceCollection()
            .AddDbContext<MyDbContext>(builder => builder.UseSqlite("Data Source=test.db"))
            .AddCrdtData<MyDbContext>(config =>
            {
                config.ChangeTypeListBuilder.Add<CreateMyClassChange>().Add<CreateMyClass2Change>();
                config.ObjectTypeListBuilder
                    .CustomAdapter<IMyCustomInterface, MyClassAdapter>()
                    .Add<MyClass>(builder => builder.HasKey(o => o.Identifier))
                    .Add<MyClass2>(builder => builder.HasKey(o => o.Identifier));
            }).BuildServiceProvider();
        var myDbContext = services.GetRequiredService<MyDbContext>();
        await myDbContext.Database.OpenConnectionAsync();
        await myDbContext.Database.EnsureCreatedAsync();
        var dataModel = services.GetRequiredService<DataModel>();
        var objectId = Guid.NewGuid();
        var objectId2 = Guid.NewGuid();
        await dataModel.AddChange(Guid.NewGuid(),
            new CreateMyClassChange(new MyClass
            {
                Identifier = objectId,
                MyString = "Hello"
            }));
        await dataModel.AddChange(Guid.NewGuid(),
            new CreateMyClass2Change(new MyClass2
            {
                Identifier = objectId2,
                MyNumber = 123.45m
            }));

        var snapshot = await dataModel.GetLatestSnapshotByObjectId(objectId);
        snapshot.Should().NotBeNull();
        snapshot.Entity.Should().NotBeNull();
        var myClass = snapshot.Entity.Is<MyClass>();
        myClass.Should().NotBeNull();
        myClass.Identifier.Should().Be(objectId);
        myClass.MyString.Should().Be("Hello");
        myClass.DeletedTime.Should().BeNull();

        var snapshot2 = await dataModel.GetLatestSnapshotByObjectId(objectId2);
        snapshot2.Should().NotBeNull();
        snapshot2.Entity.Should().NotBeNull();
        var myClass2 = snapshot2.Entity.Is<MyClass2>();
        myClass2.Should().NotBeNull();
        myClass2.Identifier.Should().Be(objectId2);
        myClass2.MyNumber.Should().Be(123.45m);
        myClass2.DeletedTime.Should().BeNull();

        dataModel.GetLatestObjects<MyClass>().Should().NotBeEmpty();
    }
}