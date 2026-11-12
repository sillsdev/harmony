namespace SIL.Harmony.Config;

/// <summary>
/// Controls how <c>PeekThenConcreteChangeConverter</c> handles an <see cref="Changes.IChange"/>
/// whose <c>$type</c> is not registered on this client, or whose JSON fails to deserialize to its registered type.
/// </summary>
public enum UnknownChangeHandling
{
    /// <summary>Throw a <see cref="System.Text.Json.JsonException"/> on an unknown $type (default).</summary>
    Throw,
    /// <summary>
    /// Fall back to <see cref="Changes.OpaqueChange"/>, preserving the raw JSON so it round-trips.
    /// Replay throws on an unreadable registered type unless a newer model version authored it.
    /// </summary>
    Fallback,
}
