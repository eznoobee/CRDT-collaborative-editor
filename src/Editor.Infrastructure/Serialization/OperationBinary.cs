using System.Text;
using Crdt.Core;

namespace Editor.Infrastructure.Serialization;

/// <summary>
/// The binary operation batch of PROJECT_SPEC.md §6, kind <c>0x02</c>.
/// </summary>
/// <remarks>
/// The wire form. §6's reserved run form is deliberately absent: expanding runs
/// on ingest replays the placement rule per element and is Phase 3's work, and a
/// format nothing writes or reads would be a stub in the codec as much as in the
/// specification.
/// </remarks>
public static class OperationBinary
{
    /// <summary>Encodes a batch in canonical form.</summary>
    public static byte[] Encode(IReadOnlyList<Operation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var table = BuildTable(operations);
        var index = new Dictionary<ReplicaId, int>(table.Length);
        for (var i = 0; i < table.Length; i++)
        {
            index[table[i]] = i;
        }

        var bytes = new List<byte>(32 + (operations.Count * 8));
        BinaryFormat.WriteHeader(bytes, BinaryFormat.KindOperations);

        BinaryFormat.WriteVarint(bytes, (ulong)table.Length);
        foreach (var id in table)
        {
            BinaryFormat.WriteReplicaId(bytes, id);
        }

        // Records, not operations: a run of n elements is one record. Written
        // into a buffer first, because the record count is only known once the
        // grouping is done.
        var records = new List<byte>(operations.Count * 8);
        var recordCount = 0;

        ElementId? previousInsert = null;
        for (var i = 0; i < operations.Count;)
        {
            switch (operations[i])
            {
                case InsertOperation insert:
                    {
                        // Maximal by construction. §6 forbids both a run that
                        // could have been longer and a lone insert that could
                        // have joined one, so the encoder takes the longest run
                        // available and never has a choice to make — which is
                        // what keeps canonical form a property of the encoder
                        // rather than a rule it has to be checked against.
                        var length = RunLength(operations, i);
                        if (length >= 2)
                        {
                            WriteRun(records, operations, i, length, index, previousInsert);
                            previousInsert = new ElementId(
                                insert.Id.Replica, insert.Id.Seq + (ulong)(length - 1));
                            i += length;
                        }
                        else
                        {
                            WriteInsert(records, insert, index, previousInsert);
                            previousInsert = insert.Id;
                            i++;
                        }

                        recordCount++;
                        break;
                    }

                case DeleteOperation delete:
                    records.Add(BinaryFormat.OpDelete);
                    WriteElementId(records, delete.Id, index);
                    WriteElementId(records, delete.Target, index);
                    previousInsert = null;
                    recordCount++;
                    i++;
                    break;

                default:
                    throw new BinaryFormatException(
                        $"Unknown operation type {operations[i].GetType().Name}.");
            }
        }

        BinaryFormat.WriteVarint(bytes, (ulong)recordCount);
        bytes.AddRange(records);

        return [.. bytes];
    }

    /// <summary>Decodes a batch, or throws without applying any of it.</summary>
    public static IReadOnlyList<Operation> Decode(
        ReadOnlySpan<byte> encoded, int maxRunCodePoints = BinaryFormat.MaxRunCodePoints)
    {
        var cursor = new BinaryCursor(encoded);
        SnapshotBinary.ReadHeader(ref cursor, BinaryFormat.KindOperations);

        var table = SnapshotBinary.ReadTable(ref cursor);
        var count = cursor.ReadCount("A record count");
        var operations = new List<Operation>(count);

        // Where each record's operations start, so maximality can be checked
        // across record boundaries once everything is expanded.
        var recordStarts = new List<int>(count);

        ElementId? previousInsert = null;
        for (var i = 0; i < count; i++)
        {
            recordStarts.Add(operations.Count);
            var tag = cursor.ReadByte();
            switch (tag)
            {
                case BinaryFormat.OpInsert:
                    {
                        var insert = ReadInsert(ref cursor, table, previousInsert);
                        operations.Add(insert);
                        previousInsert = insert.Id;
                        break;
                    }

                case BinaryFormat.OpRun:
                    {
                        var expanded = ReadRun(ref cursor, table, previousInsert, maxRunCodePoints);
                        operations.AddRange(expanded);
                        previousInsert = expanded[^1].Id;
                        break;
                    }

                case BinaryFormat.OpDelete:
                    {
                        var id = SnapshotBinary.ReadElementId(ref cursor, table);
                        var target = SnapshotBinary.ReadElementId(ref cursor, table);
                        operations.Add(new DeleteOperation(id, target));
                        previousInsert = null;
                        break;
                    }

                default:
                    throw new BinaryFormatException($"Unknown operation tag 0x{tag:X2} (§6).");
            }
        }

        if (cursor.Remaining != 0)
        {
            throw new BinaryFormatException(
                $"{cursor.Remaining} bytes remain after the declared {count} records (§6).");
        }

        // Maximality, checked over the whole batch rather than record by
        // record: a lone insert that could have continued the record before it
        // is exactly as non-canonical as a run that stopped early, and both
        // read the same way here.
        var ceiling = Math.Min(maxRunCodePoints, BinaryFormat.MaxRunCodePoints);
        for (var record = 1; record < recordStarts.Count; record++)
        {
            var start = recordStarts[record];
            var previousLength = start - recordStarts[record - 1];

            // A run already at the cap is the one place a boundary is allowed
            // to fall mid-run: a paste of 300 code points has to be encodable,
            // and it is a run of 256 followed by a record that continues it.
            if (previousLength >= ceiling)
            {
                continue;
            }

            if (start < operations.Count && Continues(operations[start - 1], operations[start]))
            {
                throw new BinaryFormatException(
                    "Non-canonical: a record begins with an element that continues the previous "
                    + "record's last element, so the two should be one run (§6).");
            }
        }

        return operations;
    }

    /// <summary>Encodes one operation, so a single message need not build a list.</summary>
    public static byte[] EncodeOne(Operation operation) => Encode([operation]);

    /// <summary>Decodes a batch that must hold exactly one operation.</summary>
    public static Operation DecodeOne(ReadOnlySpan<byte> encoded)
    {
        var operations = Decode(encoded);
        if (operations.Count != 1)
        {
            throw new BinaryFormatException(
                $"Expected exactly one operation but the batch holds {operations.Count}.");
        }

        return operations[0];
    }

    /// <summary>
    /// Whether <paramref name="later"/> continues <paramref name="earlier"/>'s run.
    /// </summary>
    /// <remarks>
    /// §6's rule for an operation batch, and the whole of maximality. It is
    /// one-sided, unlike the snapshot form's: nothing is required of the
    /// earlier element, because a run's first element may carry a right origin.
    /// A snapshot run record has nowhere to put one and so must refuse it; this
    /// record has somewhere, and the case it serves — a paste into the middle
    /// of a document — is the common one.
    /// </remarks>
    private static bool Continues(Operation earlier, Operation later) =>
        earlier is InsertOperation previous
        && later is InsertOperation next
        && next.RightOrigin is null
        && next.Side == Side.Right
        && next.Parent is { } parent
        && parent.Equals(previous.Id)
        && next.Id.Replica.Equals(previous.Id.Replica)
        && next.Id.Seq == previous.Id.Seq + 1;

    /// <summary>How many operations from <paramref name="start"/> form one run.</summary>
    private static int RunLength(IReadOnlyList<Operation> operations, int start)
    {
        var length = 1;
        while (start + length < operations.Count
            && length < BinaryFormat.MaxRunCodePoints
            && Continues(operations[start + length - 1], operations[start + length]))
        {
            length++;
        }

        return length;
    }

    private static List<InsertOperation> ReadRun(
        ref BinaryCursor cursor, ReplicaId[] table, ElementId? previousInsert, int maxRunCodePoints)
    {
        // Same reader as an insert record, because the body is one.
        var first = ReadInsert(ref cursor, table, previousInsert);
        var count = cursor.ReadCount("A run length");

        if (count < 2)
        {
            throw new BinaryFormatException(
                $"Non-canonical: a run of {count} is an insert record (§6).");
        }

        var ceiling = Math.Min(maxRunCodePoints, BinaryFormat.MaxRunCodePoints);
        if (count > ceiling)
        {
            // Before the allocation, not after: a run naming four billion code
            // points is one varint, and expanding it first would make the cap a
            // denial of service rather than a defence against one (§6, §7).
            throw new RunLengthExceededException(
                $"A run of {count} code points exceeds the cap of {ceiling} (§7).");
        }

        var expanded = new List<InsertOperation>(count) { first };

        for (var i = 1; i < count; i++)
        {
            // §6 and §5: each element chains onto the one before it. Giving
            // them all the same parent and side would make them siblings and
            // reintroduce exactly the interleaving invariant 8 forbids — which
            // is the reason expansion replays the placement rule instead of
            // copying the record's fields n times.
            var previous = expanded[^1];
            expanded.Add(new InsertOperation(
                new ElementId(previous.Id.Replica, previous.Id.Seq + 1),
                cursor.ReadValue(),
                previous.Id,
                Side.Right,
                RightOrigin: null));
        }

        return expanded;
    }

    private static void WriteRun(
        List<byte> bytes,
        IReadOnlyList<Operation> operations,
        int start,
        int length,
        IReadOnlyDictionary<ReplicaId, int> index,
        ElementId? previousInsert)
    {
        var first = (InsertOperation)operations[start];

        // The body is an insert record's, tag included, and the tag is then
        // overwritten. Sharing the writer rather than copying it is what keeps
        // the two records from drifting apart field by field — the run form is
        // an insert plus a count plus values, and it has to stay that.
        var tagAt = bytes.Count;
        WriteInsert(bytes, first, index, previousInsert);
        bytes[tagAt] = BinaryFormat.OpRun;

        BinaryFormat.WriteVarint(bytes, (ulong)length);

        // The first element's value went out with its body; the rest follow in
        // the order they were placed.
        for (var i = 1; i < length; i++)
        {
            WriteValue(bytes, ((InsertOperation)operations[start + i]).Value);
        }
    }

    private static InsertOperation ReadInsert(
        ref BinaryCursor cursor, ReplicaId[] table, ElementId? previousInsert)
    {
        var flags = cursor.ReadByte();
        SnapshotBinary.ValidateFlags(flags, isRun: false);

        var id = SnapshotBinary.ReadElementId(ref cursor, table);
        var side = (flags & BinaryFormat.FlagSideRight) != 0 ? Side.Right : Side.Left;

        ElementId? parent;
        switch (flags & BinaryFormat.ParentMask)
        {
            case BinaryFormat.ParentRoot:
                parent = null;
                break;

            case BinaryFormat.ParentPrevious:
                parent = previousInsert
                    ?? throw new BinaryFormatException(
                        "Parent flag 1 names the element inserted by the previous operation, and "
                        + "there is none (§6).");
                break;

            default:
                parent = SnapshotBinary.ReadElementId(ref cursor, table);
                if (previousInsert is { } previous && parent.Value.Equals(previous))
                {
                    throw new BinaryFormatException(
                        "Non-canonical: the parent is the previous operation's element, which "
                        + "flag 1 already says in no bytes (§6).");
                }

                break;
        }

        ElementId? rightOrigin = null;
        if ((flags & BinaryFormat.FlagRightOriginExplicit) != 0)
        {
            if (side == Side.Left)
            {
                throw new BinaryFormatException(
                    "A left child has no right origin, so bit 4 must be clear (§6).");
            }

            rightOrigin = SnapshotBinary.ReadElementId(ref cursor, table);
        }

        return new InsertOperation(id, cursor.ReadValue(), parent, side, rightOrigin);
    }

    private static void WriteInsert(
        List<byte> bytes,
        InsertOperation insert,
        IReadOnlyDictionary<ReplicaId, int> index,
        ElementId? previousInsert)
    {
        var parentFlag = insert.Parent switch
        {
            null => BinaryFormat.ParentRoot,
            { } id when previousInsert is { } previous && id.Equals(previous) =>
                BinaryFormat.ParentPrevious,
            _ => BinaryFormat.ParentExplicit,
        };

        var flags = (byte)(parentFlag
            | (insert.Side == Side.Right ? BinaryFormat.FlagSideRight : 0)
            | (insert.Side == Side.Right && insert.RightOrigin is not null
                ? BinaryFormat.FlagRightOriginExplicit
                : 0));

        bytes.Add(BinaryFormat.OpInsert);
        bytes.Add(flags);
        WriteElementId(bytes, insert.Id, index);

        if (parentFlag == BinaryFormat.ParentExplicit)
        {
            WriteElementId(bytes, insert.Parent!.Value, index);
        }

        if ((flags & BinaryFormat.FlagRightOriginExplicit) != 0)
        {
            WriteElementId(bytes, insert.RightOrigin!.Value, index);
        }

        WriteValue(bytes, insert.Value);
    }

    private static void WriteValue(List<byte> bytes, Rune value)
    {
        Span<byte> utf8 = stackalloc byte[4];
        var written = value.EncodeToUtf8(utf8);
        BinaryFormat.WriteVarint(bytes, (ulong)written);
        bytes.AddRange(utf8[..written]);
    }

    private static void WriteElementId(
        List<byte> bytes, ElementId id, IReadOnlyDictionary<ReplicaId, int> index)
    {
        BinaryFormat.WriteVarint(bytes, (ulong)index[id.Replica]);
        BinaryFormat.WriteVarint(bytes, id.Seq);
    }

    private static ReplicaId[] BuildTable(IReadOnlyList<Operation> operations)
    {
        var seen = new SortedSet<ReplicaId>();
        foreach (var operation in operations)
        {
            seen.Add(operation.Id.Replica);
            switch (operation)
            {
                case InsertOperation insert:
                    if (insert.Parent is { } parent)
                    {
                        seen.Add(parent.Replica);
                    }

                    if (insert.RightOrigin is { } origin)
                    {
                        seen.Add(origin.Replica);
                    }

                    break;

                case DeleteOperation delete:
                    seen.Add(delete.Target.Replica);
                    break;
            }
        }

        return [.. seen];
    }
}
