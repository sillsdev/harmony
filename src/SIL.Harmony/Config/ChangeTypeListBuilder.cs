using SIL.Harmony.Changes;
using SIL.Harmony.Entities;

namespace SIL.Harmony.Config;

public readonly record struct RegisteredChangeType(Type Type, string Discriminator);

public class ChangeTypeListBuilder
{
    private bool _frozen;

    /// <summary>
    /// we call freeze when the builder is used to create a json serializer options, as it is not possible to add new types after that.
    /// </summary>
    internal void Freeze()
    {
        _frozen = true;
    }

    private void CheckFrozen()
    {
        if (_frozen) throw new InvalidOperationException($"{nameof(ChangeTypeListBuilder)} is frozen");
    }

    private readonly List<RegisteredChangeType> _types = [];
    public IReadOnlyList<RegisteredChangeType> Types => _types.AsReadOnly();

    public ChangeTypeListBuilder Add<TDerived>() where TDerived : IChange, IPolyType
    {
        CheckFrozen();
        if (_types.Any(t => t.Type == typeof(TDerived))) return this;
        _types.Add(new RegisteredChangeType(typeof(TDerived), TDerived.TypeName));
        return this;
    }
}
