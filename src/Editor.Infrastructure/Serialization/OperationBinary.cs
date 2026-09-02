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

        BinaryFormat.WriteVarint(bytes, (ulong)operations.Count);

        ElementId? previousInsert = null;
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case InsertOperation insert:
                    WriteInsert(bytes, insert, index, previousInsert);
                    previousInsert = insert.Id;
                    break;

                case DeleteOperation delete:
                    bytes.Add(BinaryFormat.OpDelete);
                    WriteElementId(bytes, delete.Id, index);
                    WriteElementId(bytes, delete.Target, index);
                    previousInsert = null;
                    break;

                default:
                    throw new BinaryFormatException(
                        $"Unknown operation type {operation.GetType().Name}.");
            }
        }

        return [.. bytes];
    }

    /// <summary>Decodes a batch, or throws without applying any of it.</summary>
    public static IReadOnlyList<Operation> Decode(ReadOnlySpan<byte> encoded)
    {
        var cursor = new BinaryCursor(encoded);
        SnapshotBinary.ReadHeader(ref cursor, BinaryFormat.KindOperations);

        var table = SnapshotBinary.ReadTable(ref cursor);
        var count = cursor.ReadCount("An operation count");
        var operations = new List<Operation>(count);

        ElementId? previousInsert = null;
        for (var i = 0; i < count; i++)
        {
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
                $"{cursor.Remaining} bytes remain after the declared {count} operations (§6).");
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

        Span<byte> utf8 = stackalloc byte[4];
        var written = insert.Value.EncodeToUtf8(utf8);
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
