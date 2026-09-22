using System.Text;
using Crdt.Core;
using Editor.Infrastructure.Serialization;

namespace Editor.Api.Tests.Serialization;

/// <summary>
/// Hand-written bodies that §6 says are VALID, built from the specification
/// rather than by the encoder (PROJECT_SPEC.md §12).
/// </summary>
/// <remarks>
/// <para>
/// Round-trip testing defines codec correctness as encoder-decoder agreement,
/// which is circular: an encoder that never emits a legal shape and a decoder
/// that rejects it agree perfectly and are both wrong. The property that
/// matters is that <b>a decoder accepts every document the format admits</b>,
/// and only input written by hand from the specification can test it — by
/// construction the encoder cannot produce the cases that would expose the gap.
/// </para>
/// <para>
/// Each fixture is also re-encoded and compared, because §6 requires exactly
/// one encoding per document: accepting a hand-written body and then emitting
/// different bytes for the same document would mean the fixture was not
/// canonical, or that the encoder is not.
/// </para>
/// </remarks>
public sealed class BinaryFixtureTests
{
    private static ReplicaId R(int n)
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        bytes[^1] = (byte)n;
        return new ReplicaId(bytes);
    }

    /// <summary>Builds a snapshot body header and replica table by hand.</summary>
    private static List<byte> Body(params ReplicaId[] table)
    {
        var bytes = new List<byte>();
        BinaryFormat.WriteHeader(bytes, BinaryFormat.KindSnapshot);
        BinaryFormat.WriteVarint(bytes, (ulong)table.Length);
        foreach (var id in table)
        {
            BinaryFormat.WriteReplicaId(bytes, id);
        }

        return bytes;
    }

    private static void Utf8(List<byte> bytes, string value)
    {
        var encoded = Encoding.UTF8.GetBytes(value);
        BinaryFormat.WriteVarint(bytes, (ulong)encoded.Length);
        bytes.AddRange(encoded);
    }

    /// <summary>Decodes, then re-encodes and requires the same bytes back.</summary>
    private static IReadOnlyList<ElementState> AcceptsAndRoundTrips(List<byte> body)
    {
        var original = body.ToArray();
        var (elements, vector) = SnapshotBinary.DecodeParts(original);

        Assert.Equal(original, SnapshotBinary.Encode(elements, vector));
        return elements;
    }

    [Fact]
    public void A_run_beginning_at_a_left_child()
    {
        // §6: "a run may begin at a left child, and every element after it is a
        // right child regardless." Reachable in principle and not produced by
        // any document the corpus builds, so nothing else exercises the branch
        // where a run's side bit is clear.
        var body = Body(R(1), R(2));

        BinaryFormat.WriteVarint(body, 0);   // no version vector
        BinaryFormat.WriteVarint(body, 3);   // three elements

        // An anchor for the left child to hang from.
        body.Add(BinaryFormat.TagElement);
        body.Add(BinaryFormat.FlagSideRight | BinaryFormat.ParentRoot);
        BinaryFormat.WriteVarint(body, 0);
        BinaryFormat.WriteVarint(body, 0);
        Utf8(body, "z");

        // A run of two whose first element is a LEFT child of that anchor.
        body.Add(BinaryFormat.TagRun);
        BinaryFormat.WriteVarint(body, 2);
        body.Add(BinaryFormat.ParentPrevious);  // side bit clear: left child
        BinaryFormat.WriteVarint(body, 1);      // replica index 1
        BinaryFormat.WriteVarint(body, 0);      // seq 0
        body.Add(0b0000_0000);                  // bitmap: neither deleted
        Utf8(body, "ab");

        var elements = AcceptsAndRoundTrips(body);

        Assert.Equal(Side.Left, elements[1].Side);
        Assert.Equal(Side.Right, elements[2].Side);
        Assert.Equal(elements[1].Id, elements[2].Parent);
    }

    [Fact]
    public void A_sequence_number_at_the_top_of_the_range()
    {
        // ulong.MaxValue is a ten-byte varint — the longest this format can
        // carry. No generated document comes close, so nothing else reaches the
        // final shift in the varint reader.
        var body = Body(R(1));

        BinaryFormat.WriteVarint(body, 1);
        BinaryFormat.WriteVarint(body, 0);
        BinaryFormat.WriteVarint(body, ulong.MaxValue);   // vector count
        BinaryFormat.WriteVarint(body, 1);                // one element

        body.Add(BinaryFormat.TagElement);
        body.Add(BinaryFormat.FlagSideRight | BinaryFormat.ParentRoot);
        BinaryFormat.WriteVarint(body, 0);
        BinaryFormat.WriteVarint(body, ulong.MaxValue - 1);
        Utf8(body, "x");

        var elements = AcceptsAndRoundTrips(body);
        Assert.Equal(ulong.MaxValue - 1, elements[0].Id.Seq);
    }

    [Fact]
    public void A_replica_index_that_needs_two_varint_bytes()
    {
        // Indices past 127 are where a replica reference stops being one byte.
        // Real documents have a handful of replicas, so no generated body has
        // ever reached it.
        //
        // Every one of the 130 must actually be referenced: §6's canonical form
        // says the table holds exactly the replicas the body names and no more,
        // so a large table over a small document is not a valid fixture. The
        // first draft of this test made that mistake and the re-encode check
        // caught it, which is the argument for having the check.
        const int Replicas = 130;
        var table = Enumerable.Range(1, Replicas).Select(R).ToArray();
        var body = Body(table);

        BinaryFormat.WriteVarint(body, 0);
        BinaryFormat.WriteVarint(body, Replicas);

        for (var i = 0; i < Replicas; i++)
        {
            // One element per replica, each a right child of the one before, so
            // every table entry is named and no two elements can form a run.
            body.Add(BinaryFormat.TagElement);
            body.Add((byte)(BinaryFormat.FlagSideRight
                | (i == 0 ? BinaryFormat.ParentRoot : BinaryFormat.ParentPrevious)));
            BinaryFormat.WriteVarint(body, (ulong)i);
            BinaryFormat.WriteVarint(body, 0);
            Utf8(body, "q");
        }

        var elements = AcceptsAndRoundTrips(body);

        Assert.Equal(Replicas, elements.Count);
        Assert.Equal(table[129], elements[129].Id.Replica);
        Assert.Equal(elements[128].Id, elements[129].Parent);
    }

    [Fact]
    public void A_four_byte_code_point()
    {
        // §7 works in code points, and an astral character is four UTF-8 bytes
        // and two UTF-16 units. The corpus is ASCII, so the value reader's
        // upper length bound is otherwise untested.
        var body = Body(R(1));

        BinaryFormat.WriteVarint(body, 0);
        BinaryFormat.WriteVarint(body, 1);

        body.Add(BinaryFormat.TagElement);
        body.Add(BinaryFormat.FlagSideRight | BinaryFormat.ParentRoot);
        BinaryFormat.WriteVarint(body, 0);
        BinaryFormat.WriteVarint(body, 0);
        Utf8(body, "\U0001F600");

        var elements = AcceptsAndRoundTrips(body);
        Assert.Equal(0x1F600, elements[0].Value.Value);
    }

    [Fact]
    public void A_run_whose_bitmap_ends_mid_byte()
    {
        // Five elements is one bitmap byte with three bits past the end, which
        // §6 requires to be zero. The fixture is the positive half; the
        // rejection of a non-zero spare bit is asserted below.
        var body = Body(R(1));

        BinaryFormat.WriteVarint(body, 0);
        BinaryFormat.WriteVarint(body, 5);

        body.Add(BinaryFormat.TagRun);
        BinaryFormat.WriteVarint(body, 5);
        body.Add(BinaryFormat.FlagSideRight | BinaryFormat.ParentRoot);
        BinaryFormat.WriteVarint(body, 0);
        BinaryFormat.WriteVarint(body, 0);
        body.Add(0b0001_0101);   // elements 0, 2 and 4 deleted
        Utf8(body, "abcde");

        var elements = AcceptsAndRoundTrips(body);

        Assert.Equal([true, false, true, false, true], elements.Select(e => e.IsDeleted));
    }

    [Fact]
    public void A_non_zero_bit_past_the_last_element_of_a_bitmap_is_refused()
    {
        // The mirror of the fixture above, and the reason §6 now says the spare
        // bits must be zero. Before that rule the two bodies decoded to the same
        // document, which is a canonical-form violation: one document, several
        // spellings, and byte-identity becomes a check of whichever the writer
        // happened to choose. Found by writing these fixtures (§12).
        var body = Body(R(1));

        BinaryFormat.WriteVarint(body, 0);
        BinaryFormat.WriteVarint(body, 5);

        body.Add(BinaryFormat.TagRun);
        BinaryFormat.WriteVarint(body, 5);
        body.Add(BinaryFormat.FlagSideRight | BinaryFormat.ParentRoot);
        BinaryFormat.WriteVarint(body, 0);
        BinaryFormat.WriteVarint(body, 0);
        body.Add(0b1110_0000);   // no element deleted; only the spare bits set
        Utf8(body, "abcde");

        var error = Assert.Throws<BinaryFormatException>(
            () => SnapshotBinary.DecodeParts(body.ToArray()));
        Assert.Contains("past its last element", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_document_that_still_carries_a_version_vector()
    {
        // Everything collected: no elements, but the replica has seen operations
        // and must not forget. Reachable only after GC (§5) takes the last
        // tombstone, which no test document does.
        var body = Body(R(1), R(2));

        BinaryFormat.WriteVarint(body, 2);
        BinaryFormat.WriteVarint(body, 0);
        BinaryFormat.WriteVarint(body, 41);
        BinaryFormat.WriteVarint(body, 1);
        BinaryFormat.WriteVarint(body, 7);
        BinaryFormat.WriteVarint(body, 0);   // no elements

        var original = body.ToArray();
        var (elements, vector) = SnapshotBinary.DecodeParts(original);

        Assert.Empty(elements);
        Assert.Equal(41ul, vector[R(1)]);
        Assert.Equal(7ul, vector[R(2)]);
        Assert.Equal(original, SnapshotBinary.Encode(elements, vector));
    }
}
