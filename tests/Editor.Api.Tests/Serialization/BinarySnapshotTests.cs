using System.Text;
using Crdt.Core;
using Editor.Infrastructure.Serialization;

namespace Editor.Api.Tests.Serialization;

/// <summary>
/// The binary snapshot codec against PROJECT_SPEC.md §6's layout.
/// </summary>
/// <remarks>
/// Round-tripping proves the codec agrees with itself, which is the weakest
/// thing worth asserting: a codec that writes and reads its own mistakes
/// consistently passes it. The cross-implementation checks in the conformance
/// corpus are what pin it to §9's normative form; these cover the shapes and the
/// refusals, which a corpus of whole documents reaches only by accident.
/// </remarks>
public sealed class BinarySnapshotTests
{
    private static ReplicaId R(int n)
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        bytes[^1] = (byte)n;
        return new ReplicaId(bytes);
    }

    private static Rune V(char c) => new(c);

    private static byte[] Encode(params ElementState[] elements) =>
        SnapshotBinary.Encode(elements, new Dictionary<ReplicaId, ulong>());

    private static IReadOnlyList<ElementState> RoundTrip(params ElementState[] elements)
    {
        var (decoded, _) = SnapshotBinary.DecodeParts(Encode(elements));
        return decoded;
    }

    [Fact]
    public void An_empty_document_round_trips()
    {
        var replica = new Replica(R(1));
        var restored = SnapshotBinary.Decode(R(1), SnapshotBinary.Encode(replica));

        Assert.Equal(string.Empty, restored.Text);
        Assert.Empty(restored.AllIds);
    }

    [Fact]
    public void A_typed_document_round_trips_with_its_version_vector()
    {
        var replica = new Replica(R(1));
        foreach (var c in "hello")
        {
            replica.Insert(replica.Values.Count, V(c));
        }

        replica.Delete(1);

        var restored = SnapshotBinary.Decode(R(1), SnapshotBinary.Encode(replica));

        Assert.Equal("hllo", restored.Text);
        Assert.Equal(replica.AllIds, restored.AllIds);
        Assert.Equal(replica.VersionVector, restored.VersionVector);
    }

    [Fact]
    public void The_four_shapes_the_encoding_must_carry_round_trip()
    {
        // The same four as conformance trace 0050: a left child, a right child
        // with an explicit right origin, a right child at end of document, and a
        // deleted element. The last two both have no right-origin id, and
        // conflating them is the encoding bug that would reach the client.
        var root = new ElementState(new ElementId(R(1), 0), V('a'), null, Side.Right, null, false);
        var left = new ElementState(
            new ElementId(R(2), 0), V('b'), root.Id, Side.Left, null, false);
        var rightWithOrigin = new ElementState(
            new ElementId(R(2), 1), V('c'), root.Id, Side.Right, new ElementId(R(1), 0), false);
        var deleted = new ElementState(
            new ElementId(R(3), 7), V('d'), root.Id, Side.Right, null, true);

        var decoded = RoundTrip(root, left, rightWithOrigin, deleted);

        Assert.Equal(4, decoded.Count);
        Assert.Equal(left, decoded[1]);
        Assert.Equal(rightWithOrigin, decoded[2]);
        Assert.Equal(deleted, decoded[3]);

        // Shape, not a flag value: a left child has no right-origin field, so
        // "absent because left child" cannot be written as "absent because end
        // of document" or the reverse.
        Assert.Null(decoded[1].RightOrigin);
        Assert.Equal(Side.Left, decoded[1].Side);
        Assert.Null(decoded[3].RightOrigin);
        Assert.Equal(Side.Right, decoded[3].Side);
    }

    [Fact]
    public void A_forward_chain_becomes_one_run()
    {
        const int Length = 64;
        var elements = new List<ElementState>(Length);
        for (var i = 0; i < Length; i++)
        {
            elements.Add(new ElementState(
                new ElementId(R(1), (ulong)i),
                V((char)('a' + (i % 26))),
                i == 0 ? null : new ElementId(R(1), (ulong)(i - 1)),
                Side.Right,
                null,
                IsDeleted: i % 5 == 0));
        }

        var encoded = SnapshotBinary.Encode(elements, new Dictionary<ReplicaId, ulong>());
        var (decoded, _) = SnapshotBinary.DecodeParts(encoded);
        Assert.Equal(elements, decoded);

        // One run record, so the whole chain costs its bitmap plus its text
        // rather than a record each. Asserted as a bound rather than an exact
        // size: the point is the order of magnitude, not the byte count.
        Assert.True(
            encoded.Length < Length * 3,
            $"A pure forward chain of {Length} took {encoded.Length} bytes; the run form is not "
            + "being used.");
    }

    [Fact]
    public void Tombstones_in_a_run_cost_a_bit_each()
    {
        const int Length = 256;
        static List<ElementState> Chain(bool allDeleted)
        {
            var elements = new List<ElementState>(Length);
            for (var i = 0; i < Length; i++)
            {
                elements.Add(new ElementState(
                    new ElementId(R(1), (ulong)i),
                    V('x'),
                    i == 0 ? null : new ElementId(R(1), (ulong)(i - 1)),
                    Side.Right,
                    null,
                    allDeleted));
            }

            return elements;
        }

        var empty = new Dictionary<ReplicaId, ulong>();
        var live = SnapshotBinary.Encode(Chain(allDeleted: false), empty);
        var dead = SnapshotBinary.Encode(Chain(allDeleted: true), empty);

        // §8's stress case is 500k tombstones and §5 says they cannot be
        // collected on stability alone, so what they cost is load-bearing.
        Assert.Equal(live.Length, dead.Length);
    }

    [Fact]
    public void A_run_of_one_is_written_as_an_element()
    {
        // Canonical form: two spellings of one document would make the
        // byte-identity check a check of whichever the writer chose.
        var single = new ElementState(new ElementId(R(1), 0), V('a'), null, Side.Right, null, false);
        var encoded = Encode(single);

        // Offset of the first record, walked by hand from §6's layout rather
        // than taken from the decoder: 4 magic + 1 version + 1 kind, then a
        // one-entry replica table (1 + 16), an empty version vector (1) and an
        // element count of one (1).
        const int FirstRecord = 6 + 1 + ReplicaId.Size + 1 + 1;
        Assert.Equal(BinaryFormat.TagElement, encoded[FirstRecord]);
        Assert.Equal([single], RoundTrip(single));
    }

    [Fact]
    public void A_run_does_not_fold_onto_an_element_that_carries_a_right_origin()
    {
        // Found by trying to break the cross-implementation check (§13.11). An
        // element with an explicit right origin can neither start a run nor sit
        // inside one, so what follows it begins a new record however well it
        // would otherwise continue. A canonical rule that ignores the earlier
        // element's right origin makes the decoder reject its own encoder's
        // output — which both implementations did, having been written from the
        // same wrong sentence.
        //
        // Ordinary typing cannot reach this shape: a right origin records what
        // followed at insert time and tombstones keep it there. Garbage
        // collection (§5) and a directly built snapshot can.
        var first = new ElementState(new ElementId(R(1), 0), V('a'), null, Side.Right, null, false);
        var withOrigin = new ElementState(
            new ElementId(R(2), 0), V('x'), first.Id, Side.Right, first.Id, false);
        var continuation = new ElementState(
            new ElementId(R(2), 1), V('c'), withOrigin.Id, Side.Right, null, false);

        Assert.Equal(
            [first, withOrigin, continuation],
            RoundTrip(first, withOrigin, continuation));
    }

    [Fact]
    public void Encoding_is_stable_across_calls()
    {
        var replica = new Replica(R(1));
        foreach (var c in "the quick brown fox")
        {
            replica.Insert(replica.Values.Count, V(c));
        }

        Assert.Equal(SnapshotBinary.Encode(replica), SnapshotBinary.Encode(replica));
    }
}
