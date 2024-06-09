using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Argon;
using Crdt.Core;
using Crdt.Db;
using Crdt.Sample;
using Microsoft.Extensions.DependencyInjection;

namespace Crdt.Tests;

public class ModuleInit
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var services = new ServiceCollection()
            .AddCrdtDataSample(":memory:")
            .BuildServiceProvider();
        var model = services.GetRequiredService<SampleDbContext>().Model;
        VerifyEntityFramework.Initialize(model);
        VerifierSettings.AddExtraSettings(s =>
        {
            s.TypeNameHandling = TypeNameHandling.Objects;
        });
        AddHashConverter<Commit>(c => c.Hash);
        AddHashConverter<Commit>(c => c.ParentHash);
        VerifyDiffPlex.Initialize();
    }

    private static readonly AsyncLocal<List<string>?> HashList = new();

    private static void AddHashConverter<T>(Expression<Func<T, string?>> expression)
    {
        VerifierSettings.MemberConverter(expression,
            (target, memberValue) =>
            {
                if (memberValue is null) return null;
                if (memberValue == CommitBase.NullParentHash) return "Hash_Empty";
                HashList.Value ??= [];

                var list = HashList.Value;
                var index = list.IndexOf(memberValue);
                if (index == -1)
                {
                    index = list.Count;
                    list.Add(memberValue);
                }
                return $"Hash_{index + 1}";
            });
    }
}