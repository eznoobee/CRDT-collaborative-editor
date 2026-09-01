using System.Globalization;

namespace Crdt.Core;

/// <summary>
/// A 128-bit replica identifier, compared as unsigned big-endian bytes.
/// </summary>
/// <remarks>
/// <para>
/// PROJECT_SPEC.md §5 makes this ordering normative. It is the first component
/// of the <see cref="ElementId"/> comparison that breaks sibling ties, so a
/// disagreement between the C# and TypeScript implementations reorders user
/// text rather than producing some harmless internal difference.
/// </para>
/// <para>
/// This type deliberately does not wrap <see cref="Guid"/>.
/// <see cref="Guid.CompareTo(Guid)"/> reads the first three groups as signed
/// integers, so <c>ff000000-…</c> sorts before <c>01000000-…</c> — the opposite
/// of the byte order specified. The <c>replica-id-byte-ordering</c> conformance
/// trace exists to catch exactly that regression.
/// </para>
/// </remarks>
public readonly struct ReplicaId : IEquatable<ReplicaId>, IComparable<ReplicaId>
{
    /// <summary>Number of bytes in a replica identifier.</summary>
    public const int Size = 16;

    /// <summary>Creates a replica id from 16 bytes in RFC 4122 big-endian order.</summary>
    public ReplicaId(ReadOnlySpan<byte> bytes) => throw new NotImplementedException();

    /// <summary>Parses the canonical lowercase hyphenated form.</summary>
    public static ReplicaId Parse(string canonical) => throw new NotImplementedException();

    /// <summary>Writes the 16 bytes in RFC 4122 big-endian order.</summary>
    public void WriteBytes(Span<byte> destination) => throw new NotImplementedException();

    /// <summary>Renders the canonical lowercase hyphenated form.</summary>
    public override string ToString() => throw new NotImplementedException();

    /// <inheritdoc />
    public int CompareTo(ReplicaId other) => throw new NotImplementedException();

    /// <inheritdoc />
    public bool Equals(ReplicaId other) => throw new NotImplementedException();

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ReplicaId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => throw new NotImplementedException();

    public static bool operator ==(ReplicaId left, ReplicaId right) => left.Equals(right);

    public static bool operator !=(ReplicaId left, ReplicaId right) => !left.Equals(right);

    public static bool operator <(ReplicaId left, ReplicaId right) => left.CompareTo(right) < 0;

    public static bool operator >(ReplicaId left, ReplicaId right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReplicaId left, ReplicaId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReplicaId left, ReplicaId right) => left.CompareTo(right) >= 0;
}
