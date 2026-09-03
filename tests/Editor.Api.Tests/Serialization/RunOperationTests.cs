using System.Text;
using Crdt.Core;
using Editor.Infrastructure.Serialization;

namespace Editor.Api.Tests.Serialization;

/// <summary>
/// §6's run insert, and the expansion §6 requires of it.
/// </summary>
public sealed class RunOperationTests
{
    private static ReplicaId R(int n)
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        bytes[^1] = (byte)n;
        return new ReplicaId(bytes);
    }

    /// <summary>A hand-written run record, built from §6 rather than by the encoder.</summary>
    private static byte[] Fixture(
        string text, bool explicitRightOrigin = false, ulong? declaredCount = null)
    {
        var replica = R(1);
        var bytes = new List<byte>();
        BinaryFormat.WriteHeader(bytes, BinaryFormat.KindOperations);

        // Two replicas when a right origin is present, because §6 requires the
        // table to hold exactly the replicas the body refers to.
        var table = explicitRightOrigin ? new[] { replica, R(2) } : [replica];
        BinaryFormat.WriteVarint(bytes, (ulong)table.Length);
        foreach (var id in table)
        {
            BinaryFormat.WriteReplicaId(bytes, id);
        }

        BinaryFormat.WriteVarint(bytes, 1);

        bytes.Add(BinaryFormat.OpRun);
        bytes.Add((byte)(BinaryFormat.ParentRoot
            | BinaryFormat.FlagSideRight
            | (explicitRightOrigin ? BinaryFormat.FlagRightOriginExplicit : 0)));

        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 0);

        if (explicitRightOrigin)
        {
            BinaryFormat.WriteVarint(bytes, 1);
            BinaryFormat.WriteVarint(bytes, 7);
        }

        // The first element's value belongs to the insert body; the count and
        // the remaining values follow it (§6).
        var runes = text.EnumerateRunes().ToList();
        var utf8 = new byte[4];

        void Value(Rune rune)
        {
            var written = rune.EncodeToUtf8(utf8);
            BinaryFormat.WriteVarint(bytes, (ulong)written);
            bytes.AddRange(utf8.AsSpan(0, written).ToArray());
        }

        Value(runes[0]);
        BinaryFormat.WriteVarint(bytes, declaredCount ?? (ulong)runes.Count);

        foreach (var rune in runes.Skip(1))
        {
            Value(rune);
        }

        return [.. bytes];
    }

    [Fact]
    public void A_run_expands_into_a_chain_and_not_into_siblings()
    {
        // §6 and §5. Assigning every element in the run the same parent and
        // side would make them siblings, and invariant 8 forbids exactly that
        // — a concurrent insertion at the same position would interleave
        // through the middle of a paste.
        var operations = OperationBinary.Decode(Fixture("hello"));

        Assert.Equal(5, operations.Count);

        var inserts = operations.Cast<InsertOperation>().ToList();
        Assert.Equal("hello", string.Concat(inserts.Select(op => op.Value.ToString())));

        Assert.Null(inserts[0].Parent);

        for (var i = 1; i < inserts.Count; i++)
        {
            Assert.Equal(inserts[i - 1].Id, inserts[i].Parent);
            Assert.Equal(Side.Right, inserts[i].Side);
            Assert.Null(inserts[i].RightOrigin);
            Assert.Equal((ulong)i, inserts[i].Id.Seq);
            Assert.Equal(inserts[0].Id.Replica, inserts[i].Id.Replica);
        }
    }

    [Fact]
    public void The_first_element_of_a_run_may_carry_a_right_origin()
    {
        // The case a snapshot run cannot express and this one can: a paste into
        // the middle of a document, which is the common case rather than an
        // edge one.
        var operations = OperationBinary.Decode(Fixture("ab", explicitRightOrigin: true));

        var inserts = operations.Cast<InsertOperation>().ToList();

        Assert.NotNull(inserts[0].RightOrigin);
        Assert.Equal(new ElementId(R(2), 7), inserts[0].RightOrigin!.Value);

        // And only the first: every later element is placed against the one
        // before it, so it has no right origin of its own.
        Assert.Null(inserts[1].RightOrigin);
    }

    [Fact]
    public void A_hand_written_run_re_encodes_to_the_same_bytes()
    {
        // §12's rule, and the half that makes the fixture worth having: the
        // decoder accepting a body the encoder never produces proves nothing if
        // the encoder then writes it differently. §6 admits exactly one
        // encoding, so decode-then-encode has to be the identity.
        foreach (var fixture in new[] { Fixture("hello"), Fixture("ab", explicitRightOrigin: true) })
        {
            Assert.Equal(fixture, OperationBinary.Encode(OperationBinary.Decode(fixture)));
        }
    }

    [Fact]
    public void Typing_left_to_right_encodes_as_one_run()
    {
        // The transport win §6 is after. Without it, a paste is one record per
        // code point and the batch cap turns a 300-character paste into two
        // messages for no reason.
        var replica = R(1);
        var operations = new List<Operation>();
        ElementId? previous = null;

        foreach (var rune in "hello world".EnumerateRunes())
        {
            var id = new ElementId(replica, (ulong)operations.Count);
            operations.Add(new InsertOperation(id, rune, previous, Side.Right, null));
            previous = id;
        }

        var encoded = OperationBinary.Encode(operations);

        // One record, and it is a run.
        Assert.Equal(Fixture("hello world"), encoded);
        Assert.Equal(operations, OperationBinary.Decode(encoded));
    }

    [Fact]
    public void A_run_of_one_is_refused()
    {
        // §6: one element is an insert record. A run of one would be a second
        // spelling of the same batch, and canonical form admits exactly one.
        var error = Assert.Throws<BinaryFormatException>(
            () => OperationBinary.Decode(Fixture("a", declaredCount: 1)));

        Assert.Contains("insert record", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_longer_than_the_cap_is_refused_without_expanding_it()
    {
        // §7's cap, and §6's insistence that it is checked before the
        // allocation. The fixture declares a thousand code points and carries
        // two: a decoder that expanded first would try to read the rest.
        var error = Assert.Throws<RunLengthExceededException>(
            () => OperationBinary.Decode(Fixture("ab", declaredCount: 1000)));

        Assert.Contains("exceeds the cap", error.Message, StringComparison.Ordinal);

        // An absurd count never reaches the cap check, because the cursor's own
        // count guard refuses it first. Both refusals matter and this pins that
        // the earlier one is still there.
        Assert.Throws<BinaryFormatException>(
            () => OperationBinary.Decode(Fixture("ab", declaredCount: 4_000_000_000)));
    }

    [Fact]
    public void A_configured_cap_below_the_ceiling_is_honoured()
    {
        Assert.Throws<RunLengthExceededException>(
            () => OperationBinary.Decode(Fixture("hello"), maxRunCodePoints: 4));

        Assert.Equal(5, OperationBinary.Decode(Fixture("hello"), maxRunCodePoints: 5).Count);
    }

    [Fact]
    public void A_batch_that_stops_a_run_early_is_refused()
    {
        // Maximality. Two adjacent records that could have been one are a
        // second spelling of the same batch.
        var replica = R(1);
        var first = new ElementId(replica, 0);
        var second = new ElementId(replica, 1);
        var third = new ElementId(replica, 2);

        var split = new List<byte>();
        BinaryFormat.WriteHeader(split, BinaryFormat.KindOperations);
        BinaryFormat.WriteVarint(split, 1);
        BinaryFormat.WriteReplicaId(split, replica);
        BinaryFormat.WriteVarint(split, 2);

        // A run of two, then a lone insert that continues it.
        split.Add(BinaryFormat.OpRun);
        split.Add(BinaryFormat.ParentRoot | BinaryFormat.FlagSideRight);
        BinaryFormat.WriteVarint(split, 0);
        BinaryFormat.WriteVarint(split, 0);
        Value(split, 'a');
        BinaryFormat.WriteVarint(split, 2);
        Value(split, 'b');

        split.Add(BinaryFormat.OpInsert);
        split.Add(BinaryFormat.ParentPrevious | BinaryFormat.FlagSideRight);
        BinaryFormat.WriteVarint(split, 0);
        BinaryFormat.WriteVarint(split, 2);
        Value(split, 'c');

        var error = Assert.Throws<BinaryFormatException>(() => OperationBinary.Decode([.. split]));

        Assert.Contains("one run", error.Message, StringComparison.Ordinal);
        Assert.Equal([first, second, third], new[] { first, second, third });
    }

    [Fact]
    public void A_run_at_the_cap_may_be_continued_by_the_next_record()
    {
        // The one exception to maximality, and it has to exist: a paste longer
        // than the cap is a run at the cap plus a record that continues it, and
        // a rule with no exception would make that batch unencodable.
        var replica = R(1);
        var operations = new List<Operation>();
        ElementId? previous = null;

        for (var i = 0; i < BinaryFormat.MaxRunCodePoints + 3; i++)
        {
            var id = new ElementId(replica, (ulong)i);
            operations.Add(new InsertOperation(id, new Rune('a'), previous, Side.Right, null));
            previous = id;
        }

        var encoded = OperationBinary.Encode(operations);

        Assert.Equal(operations, OperationBinary.Decode(encoded));
        Assert.Equal(encoded, OperationBinary.Encode(OperationBinary.Decode(encoded)));
    }

    private static void Value(List<byte> bytes, char value)
    {
        BinaryFormat.WriteVarint(bytes, 1);
        bytes.Add((byte)value);
    }
}
