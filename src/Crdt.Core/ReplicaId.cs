using System.Buffers.Binary;
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
/// <para>
/// Stored as two big-endian halves so that comparing <c>_high</c> then
/// <c>_low</c> as unsigned integers is, by construction, lexicographic
/// comparison of the 16 bytes.
/// </para>
/// </remarks>
public readonly struct ReplicaId : IEquatable<ReplicaId>, IComparable<ReplicaId>
{
    /// <summary>Number of bytes in a replica identifier.</summary>
    public const int Size = 16;

    private readonly ulong _high;
    private readonly ulong _low;

    /// <summary>Creates a replica id from 16 bytes in RFC 4122 big-endian order.</summary>
    public ReplicaId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"A replica id is {Size} bytes.", nameof(bytes));
        }

        _high = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        _low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
    }

    /// <summary>Parses the canonical lowercase hyphenated form.</summary>
    public static ReplicaId Parse(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);

        var hex = canonical.Replace("-", string.Empty, StringComparison.Ordinal);
        if (hex.Length != Size * 2)
        {
            throw new FormatException($"'{canonical}' is not a canonical UUID.");
        }

        Span<byte> bytes = stackalloc byte[Size];
        for (var i = 0; i < Size; i++)
        {
            bytes[i] = byte.Parse(
                hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return new ReplicaId(bytes);
    }

    /// <summary>Writes the 16 bytes in RFC 4122 big-endian order.</summary>
    public void WriteBytes(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, _high);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _low);
    }

    /// <summary>Renders the canonical lowercase hyphenated form.</summary>
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Size];
        WriteBytes(bytes);
        var hex = Convert.ToHexStringLower(bytes);
        return string.Create(36, hex, static (span, source) =>
        {
            source.AsSpan(0, 8).CopyTo(span);
            span[8] = '-';
            source.AsSpan(8, 4).CopyTo(span[9..]);
            span[13] = '-';
            source.AsSpan(12, 4).CopyTo(span[14..]);
            span[18] = '-';
            source.AsSpan(16, 4).CopyTo(span[19..]);
            span[23] = '-';
            source.AsSpan(20, 12).CopyTo(span[24..]);
        });
    }

    /// <inheritdoc />
    public int CompareTo(ReplicaId other)
    {
        var high = _high.CompareTo(other._high);
        return high != 0 ? high : _low.CompareTo(other._low);
    }

    /// <inheritdoc />
    public bool Equals(ReplicaId other) => _high == other._high && _low == other._low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ReplicaId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_high, _low);

    public static bool operator ==(ReplicaId left, ReplicaId right) => left.Equals(right);

    public static bool operator !=(ReplicaId left, ReplicaId right) => !left.Equals(right);

    public static bool operator <(ReplicaId left, ReplicaId right) => left.CompareTo(right) < 0;

    public static bool operator >(ReplicaId left, ReplicaId right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReplicaId left, ReplicaId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReplicaId left, ReplicaId right) => left.CompareTo(right) >= 0;
}
