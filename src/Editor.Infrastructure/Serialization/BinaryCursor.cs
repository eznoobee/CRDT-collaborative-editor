using System.Text;
using Crdt.Core;

namespace Editor.Infrastructure.Serialization;

/// <summary>
/// A bounds-checked forward reader over an encoded body (PROJECT_SPEC.md §6).
/// </summary>
/// <remarks>
/// Every read either succeeds or throws <see cref="BinaryFormatException"/>.
/// There is deliberately no "try" variant and no way to read past the end: a
/// partial decode is the outcome §6 forbids, so it is not reachable from here.
/// </remarks>
public ref struct BinaryCursor(ReadOnlySpan<byte> source)
{
    private readonly ReadOnlySpan<byte> _source = source;
    private int _position;

    /// <summary>Bytes not yet consumed.</summary>
    public readonly int Remaining => _source.Length - _position;

    /// <summary>Consumes one byte.</summary>
    public byte ReadByte()
    {
        if (_position >= _source.Length)
        {
            throw new BinaryFormatException("Input ended in the middle of a record.");
        }

        return _source[_position++];
    }

    /// <summary>Consumes <paramref name="count"/> bytes.</summary>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0 || Remaining < count)
        {
            throw new BinaryFormatException(
                $"Input ended after {Remaining} bytes with {count} expected.");
        }

        var slice = _source.Slice(_position, count);
        _position += count;
        return slice;
    }

    /// <summary>
    /// Consumes an unsigned LEB128 varint, rejecting a non-minimal encoding.
    /// </summary>
    /// <remarks>
    /// §6 requires minimal encoding because canonical form requires exactly one
    /// spelling per document, and a padded varint is a second spelling.
    /// </remarks>
    public ulong ReadVarint()
    {
        ulong value = 0;
        var shift = 0;

        while (true)
        {
            if (shift > 63)
            {
                throw new BinaryFormatException("Varint is longer than 64 bits.");
            }

            var b = ReadByte();
            value |= (ulong)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
            {
                // A final byte of zero is padding unless it is the only byte.
                if (b == 0 && shift > 0)
                {
                    throw new BinaryFormatException(
                        "Varint is not minimally encoded (trailing zero group).");
                }

                return value;
            }

            shift += 7;
        }
    }

    /// <summary>Consumes a varint that must fit a non-negative <see cref="int"/>.</summary>
    public int ReadCount(string what)
    {
        var value = ReadVarint();
        if (value > int.MaxValue)
        {
            throw new BinaryFormatException($"{what} of {value} is larger than this build accepts.");
        }

        return (int)value;
    }

    /// <summary>Consumes a value: a byte length, then exactly one code point.</summary>
    public Rune ReadValue()
    {
        var length = ReadCount("A value byte length");
        if (length is < 1 or > 4)
        {
            throw new BinaryFormatException(
                $"A value is one code point, so 1 to 4 UTF-8 bytes, not {length} (§7).");
        }

        var bytes = ReadBytes(length);
        if (Rune.DecodeFromUtf8(bytes, out var rune, out var consumed) != System.Buffers.OperationStatus.Done
            || consumed != length)
        {
            throw new BinaryFormatException(
                "A value is not exactly one well-formed UTF-8 code point (§7 rejects lone surrogates).");
        }

        return rune;
    }
}
