using System.Text.Json.Serialization.Metadata;
using SIL.Harmony.Entities;
using SIL.Harmony.Helpers;
using SIL.Harmony.Sample.Models;

namespace SIL.Harmony.Tests.Helpers;

public class DerivedTypeHelperTests
{
    [Fact]
    public void AddDerivedType_Duplicate_Throws()
    {
        var types = new Dictionary<Type, List<JsonDerivedType>>();
        types.AddDerivedType(typeof(IObjectBase), typeof(Word), "Word");

        //different discriminator, same DerivedType: proves the guard rejects the duplicate type, not the discriminator
        var act = () => types.AddDerivedType(typeof(IObjectBase), typeof(Word), "WordAgain");

        act.Should().Throw<InvalidOperationException>().WithMessage("*already added*");
    }

    [Fact]
    public void AddDerivedType_DifferentTypesUnderSameBase_Succeeds()
    {
        var types = new Dictionary<Type, List<JsonDerivedType>>();
        types.AddDerivedType(typeof(IObjectBase), typeof(Word), "Word");
        types.AddDerivedType(typeof(IObjectBase), typeof(Definition), "Definition");

        types[typeof(IObjectBase)].Should().BeEquivalentTo([
            new JsonDerivedType(typeof(Word), "Word"),
            new JsonDerivedType(typeof(Definition), "Definition"),
        ]);
    }

    [Fact]
    public void GetEntityDiscriminator_WhenInstanceTypeIsNotAssignableToBase_Throws()
    {
        var act = () => DerivedTypeHelper.GetEntityDiscriminator<IObjectBase>(typeof(string));

        act.Should().Throw<ArgumentException>().WithMessage("*must implement IObjectBase*");
    }
}
