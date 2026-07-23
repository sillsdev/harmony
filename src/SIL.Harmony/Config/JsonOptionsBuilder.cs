using System.Text.Json;

namespace SIL.Harmony.Config;

internal class JsonOptionsBuilder
{
    private bool _frozen;
    private readonly List<Action<JsonSerializerOptions>> _configurations = [];

    public void Configure(Action<JsonSerializerOptions> configure)
    {
        CheckFrozen();
        _configurations.Add(configure);
    }

    internal void ApplyTo(JsonSerializerOptions options)
    {
        foreach (var configure in _configurations)
            configure(options);
        Freeze();
    }

    private void Freeze() => _frozen = true;

    private void CheckFrozen()
    {
        if (_frozen) throw new InvalidOperationException($"{nameof(JsonOptionsBuilder)} is frozen");
    }
}
