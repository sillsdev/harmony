using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Text.Json.Serialization;

namespace Crdt.Core;

/// <summary>
/// most basic commit, does not contain any change data, that's stored in <see cref="CommitBase{TChange}"/>
/// this class is not meant to be inherited from directly, use <see cref="ServerCommit"/> or <see cref="Crdt.Commit"/> instead
/// </summary>
public abstract class CommitBase
{
    public const string NullParentHash = "0000";
    [JsonConstructor]
    protected internal CommitBase(Guid id, string hash, string parentHash, HybridDateTime hybridDateTime)
    {
        Id = id;
        Hash = hash;
        ParentHash = parentHash;
        HybridDateTime = hybridDateTime;
    }

    internal CommitBase(Guid id)
    {
        Id = id;
        Hash = GenerateHash(NullParentHash);
        ParentHash = NullParentHash;
    }

    public (DateTimeOffset, long, Guid) CompareKey => (HybridDateTime.DateTime, HybridDateTime.Counter, Id);
    public Guid Id { get; }
    public required HybridDateTime HybridDateTime { get; init; }
    public DateTimeOffset DateTime => HybridDateTime.DateTime;
    [JsonIgnore]
    public string Hash { get; private set; }

    [JsonIgnore]
    public string ParentHash { get; private set; }
    public CommitMetadata Metadata { get; init; } = new();

    public void SetParentHash(string parentHash)
    {
        Hash = GenerateHash(parentHash);
        ParentHash = parentHash;
    }

    public string GenerateHash(string parentHash)
    {
        var idBytes = Id.ToByteArray();
        var parentHashBytes = Convert.FromHexString(parentHash);
        Span<byte> hashBytes = stackalloc byte[idBytes.Length + parentHashBytes.Length];
        idBytes.AsSpan().CopyTo(hashBytes);
        parentHashBytes.AsSpan().CopyTo(hashBytes[idBytes.Length..]);
        return Convert.ToHexString(XxHash64.Hash(hashBytes));
    }

    public required Guid ClientId { get; init; }

    public override string ToString()
    {
        return $"{Id} [{DateTime}]";
    }
}

/// <inheritdoc cref="CommitBase"/>
public abstract class CommitBase<TChange> : CommitBase
{
    internal CommitBase(Guid id, string hash, string parentHash, HybridDateTime hybridDateTime) : base(id, hash, parentHash, hybridDateTime)
    {
    }

    internal CommitBase(Guid id) : base(id)
    {
    }

    public List<ChangeEntity<TChange>> ChangeEntities { get; init; } = new();
}
