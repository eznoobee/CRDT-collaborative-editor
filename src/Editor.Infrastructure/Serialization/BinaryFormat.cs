using System.Buffers.Binary;

namespace Editor.Infrastructure.Serialization;

/// <summary>
/// Shared constants and primitives of the binary encoding (PROJECT_SPEC.md §6).
/// </summary>
/// <remarks>
/// One copy of the header, the tags and the varint rules, used by both bodies.
/// The TypeScript side has the same file for the same reason; both were written
/// from §6 rather than from each other.
/// </remarks>
public static class BinaryFormat
{
    /// <summary>The four magic bytes every body starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "CRDT"u8;

    /// <summary>The only format version this build reads or writes.</summary>
    public const byte Version = 1;

    public const byte KindSnapshot = 0x01;
    public const byte KindOperations = 0x02;

    public const byte TagElement = 0x00;
    public const byte TagRun = 0x01;

    public const byte OpInsert = 0x00;
    public const byte OpDelete = 0x01;
    public const byte OpRun = 0x02;

    /// <summary>
    /// §7's cap on the code points one run may name, applied while decoding.
    /// </summary>
    /// <remarks>
    /// A run naming four billion code points is one varint. Expanding it and
    /// then checking the cap is a denial of service written into the format, so
    /// the check happens before the allocation and the ceiling lives here, next
    /// to the reader that enforces it. A deployment may configure a smaller cap
    /// (§7); it cannot configure a larger one.
    /// </remarks>
    public const int MaxRunCodePoints = 256;

    public const byte FlagSideRight = 0b0000_0001;
    public const byte FlagDeleted = 0b0000_0010;
    public const byte ParentMask = 0b0000_1100;
    public const byte ParentRoot = 0b0000_0000;
    public const byte ParentPrevious = 0b0000_0100;
    public const byte ParentExplicit = 0b0000_1000;
    public const byte ParentInvalid = 0b0000_1100;
    public const byte FlagRightOriginExplicit = 0b0001_0000;
    public const byte ReservedMask = 0b1110_0000;

    /// <summary>Appends an unsigned LEB128 varint.</summary>
    public static void WriteVarint(List<byte> destination, ulong value)
    {
        ArgumentNullException.ThrowIfNull(destination);

        while (value >= 0x80)
        {
            destination.Add((byte)(value | 0x80));
            value >>= 7;
        }

        destination.Add((byte)value);
    }

    /// <summary>Bytes a value occupies as a varint. Used to predict sizes.</summary>
    public static int VarintSize(ulong value)
    {
        var size = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }

        return size;
    }

    /// <summary>Appends sixteen raw big-endian bytes of a replica id.</summary>
    public static void WriteReplicaId(List<byte> destination, Crdt.Core.ReplicaId id)
    {
        ArgumentNullException.ThrowIfNull(destination);

        Span<byte> bytes = stackalloc byte[Crdt.Core.ReplicaId.Size];
        id.WriteBytes(bytes);
        destination.AddRange(bytes);
    }

    /// <summary>Reads sixteen raw bytes as a replica id.</summary>
    public static Crdt.Core.ReplicaId ReadReplicaId(ReadOnlySpan<byte> source) => new(source);

    /// <summary>Writes the six header bytes.</summary>
    public static void WriteHeader(List<byte> destination, byte kind)
    {
        ArgumentNullException.ThrowIfNull(destination);

        destination.AddRange(Magic);
        destination.Add(Version);
        destination.Add(kind);
    }

    internal static uint ReadUInt32BigEndian(ReadOnlySpan<byte> source) =>
        BinaryPrimitives.ReadUInt32BigEndian(source);
}

/// <summary>Raised when a body does not decode. Never partially applied.</summary>
/// <remarks>
/// §6 and §9: a codec that guesses at input it does not understand produces a
/// document that is wrong but well-formed, and every replica then agrees on it.
/// The whole point of the system is that they agree; agreeing on corruption is
/// the failure it exists to prevent.
/// </remarks>
public class BinaryFormatException : Exception
{
    public BinaryFormatException(string message)
        : base(message)
    {
    }

    public BinaryFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BinaryFormatException()
    {
    }
}

/// <summary>
/// A run named more code points than the cap allows.
/// </summary>
/// <remarks>
/// Distinct from a structural refusal because §7 gives it a distinct answer.
/// A client that pasted too much at once needs to know to split it; a client
/// sending bytes that are not a batch has a different bug, and telling them
/// apart is the difference between a fix and a mystery.
/// </remarks>
public sealed class RunLengthExceededException : BinaryFormatException
{
    public RunLengthExceededException(string message)
        : base(message)
    {
    }

    public RunLengthExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RunLengthExceededException()
        : base("A run exceeds the code-point cap (§7).")
    {
    }
}
