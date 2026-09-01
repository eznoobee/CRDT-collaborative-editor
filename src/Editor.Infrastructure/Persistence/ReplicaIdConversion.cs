using Crdt.Core;

namespace Editor.Infrastructure.Persistence;

/// <summary>
/// Converts between <see cref="ReplicaId"/> and <see cref="Guid"/> for storage.
/// </summary>
/// <remarks>
/// Both are 16 bytes, but they do not agree on layout: .NET lays a
/// <see cref="Guid"/>'s first three groups out little-endian in memory, while
/// PROJECT_SPEC.md §5 defines a replica id as its RFC 4122 big-endian bytes.
/// Converting through the byte-order-explicit overloads keeps the database
/// representation identical to the wire representation, so an id read back from
/// Postgres compares the same way it did before it was written.
/// </remarks>
public static class ReplicaIdConversion
{
    /// <summary>To a <see cref="Guid"/> preserving big-endian byte order.</summary>
    public static Guid ToGuid(ReplicaId id)
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        id.WriteBytes(bytes);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>From a <see cref="Guid"/> preserving big-endian byte order.</summary>
    public static ReplicaId FromGuid(Guid value)
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        return new ReplicaId(bytes);
    }

    /// <summary>
    /// A sequence number as a signed <see cref="long"/> for Postgres, which has
    /// no unsigned <c>bigint</c>.
    /// </summary>
    public static long ToInt64(ulong seq) => checked((long)seq);

    /// <summary>A sequence number back from storage.</summary>
    public static ulong ToUInt64(long seq) =>
        seq < 0
            ? throw new ArgumentOutOfRangeException(nameof(seq), seq, "Sequence numbers are non-negative.")
            : (ulong)seq;
}
