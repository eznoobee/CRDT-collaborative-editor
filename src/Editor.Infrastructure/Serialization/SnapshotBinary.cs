using System.Text;
using Crdt.Core;

namespace Editor.Infrastructure.Serialization;

/// <summary>
/// The binary snapshot body of PROJECT_SPEC.md §6, kind <c>0x01</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the storage form. The normative form is the normalised JSON of §9 and
/// stays so: §9 requires <c>binary → JSON → binary</c> and
/// <c>JSON → binary → JSON</c> to be byte-identical on both implementations, so
/// what this codec is allowed to do is pinned by a format it does not define.
/// </para>
/// <para>
/// Written from §6's layout, not from the TypeScript codec. Two implementations
/// derived from one description disagree loudly; a second derived from the first
/// inherits its mistakes silently.
/// </para>
/// </remarks>
public static class SnapshotBinary
{
    /// <summary>Encodes a replica's state in canonical form.</summary>
    public static byte[] Encode(Replica replica)
    {
        ArgumentNullException.ThrowIfNull(replica);
        return Encode(replica.Export(), replica.VersionVector);
    }

    /// <summary>Encodes exported elements and a version vector.</summary>
    public static byte[] Encode(
        IReadOnlyList<ElementState> elements,
        IReadOnlyDictionary<ReplicaId, ulong> versionVector)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(versionVector);

        var table = BuildTable(elements, versionVector);
        var index = new Dictionary<ReplicaId, int>(table.Length);
        for (var i = 0; i < table.Length; i++)
        {
            index[table[i]] = i;
        }

        var bytes = new List<byte>(64 + (elements.Count * 2));
        BinaryFormat.WriteHeader(bytes, BinaryFormat.KindSnapshot);

        BinaryFormat.WriteVarint(bytes, (ulong)table.Length);
        foreach (var id in table)
        {
            BinaryFormat.WriteReplicaId(bytes, id);
        }

        // Ascending by table index, which is ascending by replica id.
        var vector = versionVector.OrderBy(pair => index[pair.Key]).ToArray();
        BinaryFormat.WriteVarint(bytes, (ulong)vector.Length);
        foreach (var (replicaId, count) in vector)
        {
            BinaryFormat.WriteVarint(bytes, (ulong)index[replicaId]);
            BinaryFormat.WriteVarint(bytes, count);
        }

        BinaryFormat.WriteVarint(bytes, (ulong)elements.Count);

        var position = 0;
        while (position < elements.Count)
        {
            var length = RunLengthAt(elements, position);
            if (length >= 2)
            {
                WriteRun(bytes, elements, position, length, index);
                position += length;
            }
            else
            {
                WriteElement(bytes, elements, position, index);
                position++;
            }
        }

        return [.. bytes];
    }

    /// <summary>Decodes a snapshot into a replica owned by <paramref name="id"/>.</summary>
    public static Replica Decode(ReplicaId id, ReadOnlySpan<byte> encoded)
    {
        var (elements, vector) = DecodeParts(encoded);
        return Replica.Import(id, elements, vector);
    }

    /// <summary>Decodes without rebuilding a replica, for round-trip checks.</summary>
    public static (IReadOnlyList<ElementState> Elements, IReadOnlyDictionary<ReplicaId, ulong> VersionVector)
        DecodeParts(ReadOnlySpan<byte> encoded)
    {
        var cursor = new BinaryCursor(encoded);
        ReadHeader(ref cursor, BinaryFormat.KindSnapshot);

        var table = ReadTable(ref cursor);

        var vectorCount = cursor.ReadCount("A version vector entry count");
        var vector = new Dictionary<ReplicaId, ulong>(vectorCount);
        var previousIndex = -1;
        for (var i = 0; i < vectorCount; i++)
        {
            var replicaIndex = ReadIndex(ref cursor, table.Length);
            if (replicaIndex <= previousIndex)
            {
                throw new BinaryFormatException(
                    "Version vector entries must ascend by replica index and not repeat (§6).");
            }

            previousIndex = replicaIndex;
            vector[table[replicaIndex]] = cursor.ReadVarint();
        }

        var elementCount = cursor.ReadCount("An element count");
        var elements = new List<ElementState>(elementCount);

        while (elements.Count < elementCount)
        {
            var tag = cursor.ReadByte();
            var firstOfRecord = elements.Count;

            switch (tag)
            {
                case BinaryFormat.TagElement:
                    ReadElement(ref cursor, table, elements);
                    break;
                case BinaryFormat.TagRun:
                    ReadRun(ref cursor, table, elements, elementCount);
                    break;
                default:
                    throw new BinaryFormatException($"Unknown record tag 0x{tag:X2} (§6).");
            }

            // Canonical form, the one local rule that gives maximality: the
            // first element of a record must not be able to continue the
            // element before it, or the two records should have been one.
            if (firstOfRecord > 0
                && CanFollow(elements[firstOfRecord - 1], elements[firstOfRecord]))
            {
                throw new BinaryFormatException(
                    "Non-canonical: a record starts with an element that continues the previous "
                    + "one, so they should have been a single run (§6).");
            }
        }

        if (cursor.Remaining != 0)
        {
            throw new BinaryFormatException(
                $"{cursor.Remaining} bytes remain after the declared {elementCount} elements (§6).");
        }

        return (elements, vector);
    }

    internal static void ReadHeader(ref BinaryCursor cursor, byte expectedKind)
    {
        var magic = cursor.ReadBytes(BinaryFormat.Magic.Length);
        if (!magic.SequenceEqual(BinaryFormat.Magic))
        {
            throw new BinaryFormatException("Not a CRDT binary body: the magic bytes do not match.");
        }

        var version = cursor.ReadByte();
        if (version != BinaryFormat.Version)
        {
            // §9: never a best-effort parse. Naming the supported versions is
            // what makes the refusal actionable rather than merely safe.
            throw new BinaryFormatException(
                $"Binary format version {version} is not supported. This build reads version "
                + $"{BinaryFormat.Version}.");
        }

        var kind = cursor.ReadByte();
        if (kind != expectedKind)
        {
            throw new BinaryFormatException(
                $"Expected body kind 0x{expectedKind:X2} but found 0x{kind:X2}.");
        }
    }

    internal static ReplicaId[] ReadTable(ref BinaryCursor cursor)
    {
        var count = cursor.ReadCount("A replica table size");
        var table = new ReplicaId[count];
        for (var i = 0; i < count; i++)
        {
            table[i] = BinaryFormat.ReadReplicaId(cursor.ReadBytes(ReplicaId.Size));
            if (i > 0 && table[i].CompareTo(table[i - 1]) <= 0)
            {
                throw new BinaryFormatException(
                    "The replica table must ascend in §5 order and not repeat (§6).");
            }
        }

        return table;
    }

    internal static int ReadIndex(ref BinaryCursor cursor, int tableLength)
    {
        var index = cursor.ReadCount("A replica index");
        if (index >= tableLength)
        {
            throw new BinaryFormatException(
                $"Replica index {index} is past the end of a {tableLength}-entry table (§6).");
        }

        return index;
    }

    internal static void ValidateFlags(byte flags, bool isRun)
    {
        if ((flags & BinaryFormat.ReservedMask) != 0)
        {
            throw new BinaryFormatException(
                "Reserved flag bits are set. A version that assigns them is a version bump, and "
                + "this build must refuse rather than ignore what it cannot see (§6).");
        }

        if ((flags & BinaryFormat.ParentMask) == BinaryFormat.ParentInvalid)
        {
            throw new BinaryFormatException("Parent kind 3 is not a value (§6).");
        }

        if (isRun && (flags & BinaryFormat.FlagRightOriginExplicit) != 0)
        {
            throw new BinaryFormatException(
                "A run record may not carry an explicit right origin (§6).");
        }

        if (isRun && (flags & BinaryFormat.FlagDeleted) != 0)
        {
            throw new BinaryFormatException(
                "A run record carries deleted state in its bitmap, so flags bit 1 must be zero (§6).");
        }
    }

    internal static ElementId? ReadParent(
        ref BinaryCursor cursor, byte flags, ReplicaId[] table, IReadOnlyList<ElementState> sofar)
    {
        switch (flags & BinaryFormat.ParentMask)
        {
            case BinaryFormat.ParentRoot:
                return null;

            case BinaryFormat.ParentPrevious:
                if (sofar.Count == 0)
                {
                    throw new BinaryFormatException(
                        "The first record cannot name the previous element as its parent (§6).");
                }

                return sofar[^1].Id;

            default:
                {
                    var parent = ReadElementId(ref cursor, table);
                    if (sofar.Count > 0 && parent.Equals(sofar[^1].Id))
                    {
                        throw new BinaryFormatException(
                            "Non-canonical: the parent is the previous element, which flag 1 "
                            + "already says in no bytes (§6).");
                    }

                    return parent;
                }
        }
    }

    internal static ElementId ReadElementId(ref BinaryCursor cursor, ReplicaId[] table)
    {
        var replicaIndex = ReadIndex(ref cursor, table.Length);
        return new ElementId(table[replicaIndex], cursor.ReadVarint());
    }

    private static void ReadElement(
        ref BinaryCursor cursor, ReplicaId[] table, List<ElementState> elements)
    {
        var flags = cursor.ReadByte();
        ValidateFlags(flags, isRun: false);

        var id = ReadElementId(ref cursor, table);
        var side = (flags & BinaryFormat.FlagSideRight) != 0 ? Side.Right : Side.Left;
        var parent = ReadParent(ref cursor, flags, table, elements);

        ElementId? rightOrigin = null;
        if (side == Side.Right && (flags & BinaryFormat.FlagRightOriginExplicit) != 0)
        {
            rightOrigin = ReadElementId(ref cursor, table);
        }
        else if (side == Side.Left && (flags & BinaryFormat.FlagRightOriginExplicit) != 0)
        {
            throw new BinaryFormatException(
                "A left child has no right origin, so bit 4 must be clear (§6).");
        }

        elements.Add(new ElementState(
            id, cursor.ReadValue(), parent, side, rightOrigin, (flags & BinaryFormat.FlagDeleted) != 0));
    }

    private static void ReadRun(
        ref BinaryCursor cursor, ReplicaId[] table, List<ElementState> elements, int elementCount)
    {
        var count = cursor.ReadCount("A run length");
        if (count < 2)
        {
            throw new BinaryFormatException(
                $"A run is two or more elements, not {count}; one element is an element record (§6).");
        }

        if (count > elementCount - elements.Count)
        {
            throw new BinaryFormatException(
                $"A run of {count} overruns the declared element count (§6).");
        }

        var flags = cursor.ReadByte();
        ValidateFlags(flags, isRun: true);

        var first = ReadElementId(ref cursor, table);
        var side = (flags & BinaryFormat.FlagSideRight) != 0 ? Side.Right : Side.Left;
        var parent = ReadParent(ref cursor, flags, table, elements);

        var bitmap = cursor.ReadBytes((count + 7) / 8).ToArray();
        var valueBytes = cursor.ReadBytes(cursor.ReadCount("A run value length")).ToArray();

        var runes = new List<Rune>(count);
        var offset = 0;
        while (offset < valueBytes.Length)
        {
            if (Rune.DecodeFromUtf8(valueBytes.AsSpan(offset), out var rune, out var consumed)
                != System.Buffers.OperationStatus.Done)
            {
                throw new BinaryFormatException("A run's values are not well-formed UTF-8 (§7).");
            }

            runes.Add(rune);
            offset += consumed;
        }

        if (runes.Count != count)
        {
            throw new BinaryFormatException(
                $"A run of {count} carries {runes.Count} code points (§6).");
        }

        for (var i = 0; i < count; i++)
        {
            var deleted = (bitmap[i / 8] & (1 << (i % 8))) != 0;
            elements.Add(new ElementState(
                new ElementId(first.Replica, first.Seq + (ulong)i),
                runes[i],
                i == 0 ? parent : new ElementId(first.Replica, first.Seq + (ulong)(i - 1)),
                i == 0 ? side : Side.Right,
                null,
                deleted));
        }
    }

    private static ReplicaId[] BuildTable(
        IReadOnlyList<ElementState> elements,
        IReadOnlyDictionary<ReplicaId, ulong> versionVector)
    {
        var seen = new SortedSet<ReplicaId>();
        foreach (var element in elements)
        {
            seen.Add(element.Id.Replica);
            if (element.Parent is { } parent)
            {
                seen.Add(parent.Replica);
            }

            if (element.RightOrigin is { } origin)
            {
                seen.Add(origin.Replica);
            }
        }

        foreach (var replicaId in versionVector.Keys)
        {
            seen.Add(replicaId);
        }

        return [.. seen];
    }

    /// <summary>
    /// True when <paramref name="current"/> is the next element of a run begun
    /// by <paramref name="previous"/>.
    /// </summary>
    /// <remarks>
    /// A condition on both elements, which §6 is explicit about because an
    /// earlier draft was not: <paramref name="previous"/> must be able to be in
    /// a run at all — no right origin — and <paramref name="current"/> must be a
    /// right child of it with the next sequence number on the same replica and
    /// no right origin of its own.
    ///
    /// Dropping the first half makes a decoder reject documents its own encoder
    /// produces: an element with an explicit right origin followed by a
    /// consecutive right child, where no run can begin and so two records are
    /// written (§13.11).
    /// </remarks>
    internal static bool CanFollow(ElementState previous, ElementState current) =>
        previous.RightOrigin is null
        && current.Id.Replica.Equals(previous.Id.Replica)
        && previous.Id.Seq != ulong.MaxValue
        && current.Id.Seq == previous.Id.Seq + 1
        && current.Side == Side.Right
        && current.RightOrigin is null
        && current.Parent is { } parent
        && parent.Equals(previous.Id);

    private static int RunLengthAt(IReadOnlyList<ElementState> elements, int start)
    {
        // A run's first element may sit on either side, but it must have no
        // right origin: the run form has no room for one.
        if (elements[start].RightOrigin is not null)
        {
            return 1;
        }

        var length = 1;
        while (start + length < elements.Count
               && CanFollow(elements[start + length - 1], elements[start + length]))
        {
            length++;
        }

        return length;
    }

    private static byte ParentFlag(
        IReadOnlyList<ElementState> elements, int position, ElementId? parent) => parent switch
        {
            null => BinaryFormat.ParentRoot,
            { } id when position > 0 && id.Equals(elements[position - 1].Id) =>
                BinaryFormat.ParentPrevious,
            _ => BinaryFormat.ParentExplicit,
        };

    private static void WriteElementId(
        List<byte> bytes, ElementId id, IReadOnlyDictionary<ReplicaId, int> index)
    {
        BinaryFormat.WriteVarint(bytes, (ulong)index[id.Replica]);
        BinaryFormat.WriteVarint(bytes, id.Seq);
    }

    private static void WriteElement(
        List<byte> bytes,
        IReadOnlyList<ElementState> elements,
        int position,
        IReadOnlyDictionary<ReplicaId, int> index)
    {
        var element = elements[position];
        var parentFlag = ParentFlag(elements, position, element.Parent);

        var flags = (byte)(parentFlag
            | (element.Side == Side.Right ? BinaryFormat.FlagSideRight : 0)
            | (element.IsDeleted ? BinaryFormat.FlagDeleted : 0)
            | (element.Side == Side.Right && element.RightOrigin is not null
                ? BinaryFormat.FlagRightOriginExplicit
                : 0));

        bytes.Add(BinaryFormat.TagElement);
        bytes.Add(flags);
        WriteElementId(bytes, element.Id, index);

        if (parentFlag == BinaryFormat.ParentExplicit)
        {
            WriteElementId(bytes, element.Parent!.Value, index);
        }

        if ((flags & BinaryFormat.FlagRightOriginExplicit) != 0)
        {
            WriteElementId(bytes, element.RightOrigin!.Value, index);
        }

        WriteValue(bytes, element.Value);
    }

    private static void WriteRun(
        List<byte> bytes,
        IReadOnlyList<ElementState> elements,
        int position,
        int length,
        IReadOnlyDictionary<ReplicaId, int> index)
    {
        var first = elements[position];
        var parentFlag = ParentFlag(elements, position, first.Parent);
        var flags = (byte)(parentFlag
            | (first.Side == Side.Right ? BinaryFormat.FlagSideRight : 0));

        bytes.Add(BinaryFormat.TagRun);
        BinaryFormat.WriteVarint(bytes, (ulong)length);
        bytes.Add(flags);
        WriteElementId(bytes, first.Id, index);

        if (parentFlag == BinaryFormat.ParentExplicit)
        {
            WriteElementId(bytes, first.Parent!.Value, index);
        }

        var bitmap = new byte[(length + 7) / 8];
        var values = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var element = elements[position + i];
            if (element.IsDeleted)
            {
                bitmap[i / 8] |= (byte)(1 << (i % 8));
            }

            values.Append(element.Value.ToString());
        }

        bytes.AddRange(bitmap);

        var encoded = Encoding.UTF8.GetBytes(values.ToString());
        BinaryFormat.WriteVarint(bytes, (ulong)encoded.Length);
        bytes.AddRange(encoded);
    }

    private static void WriteValue(List<byte> bytes, Rune value)
    {
        Span<byte> utf8 = stackalloc byte[4];
        var written = value.EncodeToUtf8(utf8);
        BinaryFormat.WriteVarint(bytes, (ulong)written);
        bytes.AddRange(utf8[..written]);
    }
}
