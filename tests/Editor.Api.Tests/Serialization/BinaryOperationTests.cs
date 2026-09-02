using System.Text;
using Crdt.Core;
using Editor.Infrastructure.Serialization;

namespace Editor.Api.Tests.Serialization;

/// <summary>The binary operation batch of PROJECT_SPEC.md §6, kind <c>0x02</c>.</summary>
public sealed class BinaryOperationTests
{
    private static ReplicaId R(int n)
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        bytes[^1] = (byte)n;
        return new ReplicaId(bytes);
    }

    private static Rune V(char c) => new(c);

    [Fact]
    public void Typing_round_trips_as_a_batch()
    {
        var replica = new Replica(R(1));
        var operations = new List<Operation>();
        foreach (var c in "hello")
        {
            operations.Add(replica.Insert(replica.Values.Count, V(c)));
        }

        operations.Add(replica.Delete(0));

        Assert.Equal(operations, OperationBinary.Decode(OperationBinary.Encode(operations)));
    }

    [Fact]
    public void The_four_shapes_round_trip_one_at_a_time()
    {
        // As trace 0050: a left child, a right child with an explicit right
        // origin, a right child at end of document, and a delete. The middle
        // two are the pair that must not be conflated.
        Operation[] operations =
        [
            new InsertOperation(new ElementId(R(1), 0), V('a'), null, Side.Right, null),
            new InsertOperation(
                new ElementId(R(2), 0), V('b'), new ElementId(R(1), 0), Side.Left, null),
            new InsertOperation(
                new ElementId(R(2), 1), V('c'), new ElementId(R(1), 0), Side.Right,
                new ElementId(R(1), 0)),
            new DeleteOperation(new ElementId(R(3), 4), new ElementId(R(1), 0)),
        ];

        foreach (var operation in operations)
        {
            Assert.Equal(operation, OperationBinary.DecodeOne(OperationBinary.EncodeOne(operation)));
        }
    }

    [Fact]
    public void A_right_child_at_end_of_document_is_not_confused_with_a_left_child()
    {
        var atEnd = new InsertOperation(
            new ElementId(R(1), 1), V('x'), new ElementId(R(1), 0), Side.Right, null);
        var leftChild = new InsertOperation(
            new ElementId(R(1), 1), V('x'), new ElementId(R(1), 0), Side.Left, null);

        // Both carry no right-origin id. If the encoding conflated them the
        // decoded pair would be equal, and the divergence would surface as
        // reordered text on the client rather than as a failure here.
        Assert.NotEqual(
            OperationBinary.EncodeOne(atEnd),
            OperationBinary.EncodeOne(leftChild));
        Assert.Equal(atEnd, OperationBinary.DecodeOne(OperationBinary.EncodeOne(atEnd)));
        Assert.Equal(leftChild, OperationBinary.DecodeOne(OperationBinary.EncodeOne(leftChild)));
    }

    [Fact]
    public void A_chain_of_inserts_names_its_parent_in_no_bytes()
    {
        var replica = new Replica(R(1));
        var operations = new List<Operation>();
        for (var i = 0; i < 32; i++)
        {
            operations.Add(replica.Insert(i, V('a')));
        }

        // Parent flag 1 means each insert after the first spends nothing on its
        // parent, which is most of what an insert would otherwise carry.
        var batch = OperationBinary.Encode(operations);
        var separately = operations.Sum(op => OperationBinary.EncodeOne(op).Length);

        Assert.True(
            batch.Length < separately / 2,
            $"A batch of 32 chained inserts took {batch.Length} bytes against {separately} "
            + "encoded one at a time; parent flag 1 is not being used.");
        Assert.Equal(operations, OperationBinary.Decode(batch));
    }

    [Fact]
    public void An_unknown_operation_tag_is_refused()
    {
        var encoded = OperationBinary.EncodeOne(
            new DeleteOperation(new ElementId(R(1), 0), new ElementId(R(1), 1)));

        // 6 header + one-entry table (1 + 16) + op count (1) = the tag.
        const int Tag = 6 + 1 + ReplicaId.Size + 1;
        Assert.Equal(BinaryFormat.OpDelete, encoded[Tag]);
        encoded[Tag] = 0x55;

        Assert.Throws<BinaryFormatException>(() => OperationBinary.Decode(encoded));
    }

    [Fact]
    public void A_snapshot_body_is_not_accepted_as_an_operation_batch()
    {
        var replica = new Replica(R(1));
        replica.Insert(0, V('a'));

        Assert.Throws<BinaryFormatException>(
            () => OperationBinary.Decode(SnapshotBinary.Encode(replica)));
    }

    [Fact]
    public void Parent_flag_one_after_a_delete_is_refused()
    {
        // Flag 1 names the element inserted by the previous operation, and a
        // delete inserts nothing.
        var bytes = new List<byte>();
        BinaryFormat.WriteHeader(bytes, BinaryFormat.KindOperations);
        BinaryFormat.WriteVarint(bytes, 1);
        BinaryFormat.WriteReplicaId(bytes, R(1));
        BinaryFormat.WriteVarint(bytes, 2);

        bytes.Add(BinaryFormat.OpDelete);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 1);

        bytes.Add(BinaryFormat.OpInsert);
        bytes.Add(BinaryFormat.FlagSideRight | BinaryFormat.ParentPrevious);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 2);
        BinaryFormat.WriteVarint(bytes, 1);
        bytes.Add((byte)'a');

        var error = Assert.Throws<BinaryFormatException>(
            () => OperationBinary.Decode(bytes.ToArray()));
        Assert.Contains("previous operation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Encoding_is_stable_across_calls()
    {
        var replica = new Replica(R(1));
        var operations = new List<Operation>();
        foreach (var c in "stable")
        {
            operations.Add(replica.Insert(replica.Values.Count, V(c)));
        }

        Assert.Equal(OperationBinary.Encode(operations), OperationBinary.Encode(operations));
    }
}
