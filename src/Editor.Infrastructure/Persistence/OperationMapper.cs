using System.Text;
using Crdt.Core;

namespace Editor.Infrastructure.Persistence;

/// <summary>
/// Maps <c>Crdt.Core</c> operations to and from log rows.
/// </summary>
/// <remarks>
/// <para>
/// This is the mapping PROJECT_SPEC.md §6 calls a second implementation of the
/// encoding, the first being the TypeScript serialiser. The two must agree, and
/// §9's serialised round-trip trace is what holds them together.
/// </para>
/// <para>
/// It lives here rather than on the operation types because <c>Crdt.Core</c>
/// references nothing but the BCL (§4).
/// </para>
/// </remarks>
public static class OperationMapper
{
    public const string InsertType = "insert";
    public const string DeleteType = "delete";

    /// <summary>Encodes an operation as a log row.</summary>
    public static DocumentOperationRow ToRow(Guid documentId, Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var row = new DocumentOperationRow
        {
            DocumentId = documentId,
            ReplicaId = ReplicaIdConversion.ToGuid(operation.Id.Replica),
            Seq = ReplicaIdConversion.ToInt64(operation.Id.Seq),
            OpType = operation is InsertOperation ? InsertType : DeleteType,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        switch (operation)
        {
            case InsertOperation insert:
                row.Value = insert.Value.ToString();
                row.Side = insert.Side == Crdt.Core.Side.Left ? "L" : "R";

                if (insert.Parent is { } parent)
                {
                    row.ParentReplica = ReplicaIdConversion.ToGuid(parent.Replica);
                    row.ParentSeq = ReplicaIdConversion.ToInt64(parent.Seq);
                }

                if (insert.Side == Crdt.Core.Side.Right)
                {
                    if (insert.RightOrigin is { } origin)
                    {
                        row.RightOriginReplica = ReplicaIdConversion.ToGuid(origin.Replica);
                        row.RightOriginSeq = ReplicaIdConversion.ToInt64(origin.Seq);
                    }
                    else
                    {
                        // A right child with no right origin sits at the end of
                        // the document. Left children carry no right origin at
                        // all, and the two must stay distinguishable.
                        row.RightOriginIsEnd = true;
                    }
                }

                break;

            case DeleteOperation delete:
                row.TargetReplica = ReplicaIdConversion.ToGuid(delete.Target.Replica);
                row.TargetSeq = ReplicaIdConversion.ToInt64(delete.Target.Seq);
                break;

            default:
                throw new ArgumentException($"Unknown operation {operation}.", nameof(operation));
        }

        return row;
    }

    /// <summary>Decodes a log row back into an operation.</summary>
    public static Operation FromRow(DocumentOperationRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var id = new ElementId(
            ReplicaIdConversion.FromGuid(row.ReplicaId),
            ReplicaIdConversion.ToUInt64(row.Seq));

        if (string.Equals(row.OpType, DeleteType, StringComparison.Ordinal))
        {
            return new DeleteOperation(id, RequireId(row.TargetReplica, row.TargetSeq, "target"));
        }

        if (!string.Equals(row.OpType, InsertType, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unknown op_type '{row.OpType}'.", nameof(row));
        }

        var side = row.Side switch
        {
            "L" => Crdt.Core.Side.Left,
            "R" => Crdt.Core.Side.Right,
            _ => throw new ArgumentException($"Unknown side '{row.Side}'.", nameof(row)),
        };

        var runes = (row.Value ?? throw new ArgumentException("An insert needs a value.", nameof(row)))
            .EnumerateRunes().ToArray();
        if (runes.Length != 1)
        {
            throw new ArgumentException(
                $"An insert carries exactly one code point, got '{row.Value}' (§7).", nameof(row));
        }

        ElementId? parent = row.ParentReplica is null
            ? null
            : RequireId(row.ParentReplica, row.ParentSeq, "parent");

        ElementId? rightOrigin = null;
        if (side == Crdt.Core.Side.Right && !row.RightOriginIsEnd && row.RightOriginReplica is not null)
        {
            rightOrigin = RequireId(row.RightOriginReplica, row.RightOriginSeq, "right origin");
        }

        return new InsertOperation(id, runes[0], parent, side, rightOrigin);
    }

    private static ElementId RequireId(Guid? replica, long? seq, string what)
    {
        if (replica is null || seq is null)
        {
            throw new ArgumentException($"Incomplete {what} identifier.", nameof(replica));
        }

        return new ElementId(
            ReplicaIdConversion.FromGuid(replica.Value),
            ReplicaIdConversion.ToUInt64(seq.Value));
    }
}
