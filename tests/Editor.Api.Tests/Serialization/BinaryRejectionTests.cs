using System.Text;
using Crdt.Core;
using Editor.Infrastructure.Serialization;

namespace Editor.Api.Tests.Serialization;

/// <summary>
/// Every refusal PROJECT_SPEC.md §6 enumerates, exercised.
/// </summary>
/// <remarks>
/// <para>
/// These are the most load-bearing tests of the codec, and the least obvious.
/// A decoder that accepts malformed input does not fail — it succeeds, quietly,
/// producing a document that is wrong but well-formed, which every replica then
/// converges on. §9 states the principle; this is where it is enforced.
/// </para>
/// <para>
/// The canonical-form refusals matter for a second reason: §9 requires
/// <c>binary → JSON → binary</c> to be byte-identical, and a reader that accepts
/// two spellings of one document turns that into a check of whichever spelling
/// the writer chose.
/// </para>
/// </remarks>
public sealed class BinaryRejectionTests
{
    private static ReplicaId R(int n)
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        bytes[^1] = (byte)n;
        return new ReplicaId(bytes);
    }

    private static Rune V(char c) => new(c);

    /// <summary>A two-element forward chain, which the encoder writes as one run.</summary>
    private static byte[] Chain() => SnapshotBinary.Encode(
        [
            new ElementState(new ElementId(R(1), 0), V('a'), null, Side.Right, null, false),
            new ElementState(
                new ElementId(R(1), 1), V('b'), new ElementId(R(1), 0), Side.Right, null, false),
        ],
        new Dictionary<ReplicaId, ulong>());

    private static BinaryFormatException Rejects(byte[] encoded) =>
        Assert.Throws<BinaryFormatException>(() => SnapshotBinary.DecodeParts(encoded));

    /// <summary>Offset of the version byte: after the four magic bytes.</summary>
    private const int VersionOffset = 4;

    /// <summary>Offset of the body kind byte.</summary>
    private const int KindOffset = 5;

    [Fact]
    public void An_unknown_version_is_refused_by_name()
    {
        var encoded = Chain();
        encoded[VersionOffset] = 99;

        var error = Rejects(encoded);

        // Naming the supported version is what makes the refusal actionable.
        // "Cannot read this" sends someone to a hex dump; "version 99, this
        // build reads 1" sends them to the writer.
        Assert.Contains("99", error.Message, StringComparison.Ordinal);
        Assert.Contains("1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_future_version_is_refused_rather_than_parsed_leniently()
    {
        // Version 2 would very likely be readable by a version 1 parser for a
        // while — that is exactly the trap. A codec that guesses produces a
        // corrupt document every replica agrees on (§9).
        var encoded = Chain();
        encoded[VersionOffset] = BinaryFormat.Version + 1;

        Rejects(encoded);
    }

    [Fact]
    public void The_wrong_body_kind_is_refused()
    {
        var encoded = Chain();
        encoded[KindOffset] = BinaryFormat.KindOperations;

        Rejects(encoded);
    }

    [Fact]
    public void A_body_that_is_not_ours_is_refused()
    {
        var encoded = Chain();
        encoded[0] = (byte)'X';

        Assert.Contains("magic", Rejects(encoded).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Truncated_input_is_refused()
    {
        var encoded = Chain();
        for (var length = 0; length < encoded.Length; length++)
        {
            Assert.Throws<BinaryFormatException>(
                () => SnapshotBinary.DecodeParts(encoded.AsSpan(0, length)));
        }
    }

    [Fact]
    public void Trailing_bytes_are_refused()
    {
        // Not pedantry: bytes after the declared element count mean the writer
        // and reader disagree about the document, and the reader cannot tell
        // which of them is right.
        Assert.Contains("remain", Rejects([.. Chain(), 0]).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reserved_flag_bits_are_refused()
    {
        var encoded = Chain();
        var flags = FindRunFlagsOffset(encoded);
        encoded[flags] |= BinaryFormat.ReservedMask;

        Assert.Contains("Reserved", Rejects(encoded).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_right_origin_on_a_run_is_refused()
    {
        var encoded = Chain();
        encoded[FindRunFlagsOffset(encoded)] |= BinaryFormat.FlagRightOriginExplicit;

        Rejects(encoded);
    }

    [Fact]
    public void A_deleted_flag_on_a_run_is_refused()
    {
        // The bitmap already carries it, so a set bit here is a second spelling.
        var encoded = Chain();
        encoded[FindRunFlagsOffset(encoded)] |= BinaryFormat.FlagDeleted;

        Rejects(encoded);
    }

    [Fact]
    public void Parent_kind_three_is_refused()
    {
        var encoded = Chain();
        encoded[FindRunFlagsOffset(encoded)] |= BinaryFormat.ParentInvalid;

        Rejects(encoded);
    }

    [Fact]
    public void A_replica_index_past_the_table_is_refused()
    {
        // The table has one entry, so index 1 is out of range. It sits
        // immediately after the run's flags byte.
        var encoded = Chain();
        encoded[FindRunFlagsOffset(encoded) + 1] = 1;

        Assert.Contains("past the end", Rejects(encoded).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_shorter_than_two_is_refused()
    {
        // One element is an element record. Accepting a run of one would give
        // the same document two encodings.
        var encoded = Chain();
        encoded[FindRunCountOffset(encoded)] = 1;

        Rejects(encoded);
    }

    [Fact]
    public void A_non_minimal_varint_is_refused()
    {
        // 0x80 0x00 is zero written in two bytes: a second spelling of the same
        // number, and therefore of the same document.
        var encoded = Chain();
        var countOffset = FindRunCountOffset(encoded);
        var padded = encoded.Take(countOffset)
            .Concat<byte>([(byte)(encoded[countOffset] | 0x80), 0x00])
            .Concat(encoded.Skip(countOffset + 1))
            .ToArray();

        Assert.Contains("minimally", Rejects(padded).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_replica_table_out_of_order_is_refused()
    {
        var elements = new List<ElementState>
        {
            new(new ElementId(R(1), 0), V('a'), null, Side.Right, null, false),
            new(new ElementId(R(2), 0), V('b'), new ElementId(R(1), 0), Side.Left, null, false),
        };

        var encoded = SnapshotBinary.Encode(elements, new Dictionary<ReplicaId, ulong>());

        // Swap the two 16-byte ids so the table descends.
        var first = encoded.AsSpan(7, ReplicaId.Size).ToArray();
        var second = encoded.AsSpan(7 + ReplicaId.Size, ReplicaId.Size).ToArray();
        second.CopyTo(encoded, 7);
        first.CopyTo(encoded, 7 + ReplicaId.Size);

        Assert.Contains("ascend", Rejects(encoded).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_records_that_should_have_been_one_run_are_refused()
    {
        // Maximality, which §6 reduces to one local rule: the first element of
        // a record must not be able to continue the element before it. Built by
        // hand because the encoder will not produce it.
        var bytes = new List<byte>();
        BinaryFormat.WriteHeader(bytes, BinaryFormat.KindSnapshot);
        BinaryFormat.WriteVarint(bytes, 1);
        BinaryFormat.WriteReplicaId(bytes, R(1));
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 2);

        // Two element records that form a run.
        bytes.Add(BinaryFormat.TagElement);
        bytes.Add(BinaryFormat.FlagSideRight | BinaryFormat.ParentRoot);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 1);
        bytes.Add((byte)'a');

        bytes.Add(BinaryFormat.TagElement);
        bytes.Add(BinaryFormat.FlagSideRight | BinaryFormat.ParentPrevious);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 1);
        BinaryFormat.WriteVarint(bytes, 1);
        bytes.Add((byte)'b');

        Assert.Contains("single run", Rejects([.. bytes]).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_parent_that_is_the_previous_element_is_refused()
    {
        // Flag 1 says the same thing in no bytes, so the explicit form is a
        // second spelling.
        var bytes = new List<byte>();
        BinaryFormat.WriteHeader(bytes, BinaryFormat.KindSnapshot);
        BinaryFormat.WriteVarint(bytes, 1);
        BinaryFormat.WriteReplicaId(bytes, R(1));
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 2);

        bytes.Add(BinaryFormat.TagElement);
        bytes.Add(BinaryFormat.FlagSideRight | BinaryFormat.ParentRoot);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 1);
        bytes.Add((byte)'a');

        // A left child of the previous element, written with an explicit parent
        // so that the maximality rule does not fire first.
        bytes.Add(BinaryFormat.TagElement);
        bytes.Add(BinaryFormat.ParentExplicit);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 5);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 1);
        bytes.Add((byte)'b');

        Assert.Contains("flag 1", Rejects([.. bytes]).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_is_not_one_code_point_is_refused()
    {
        var bytes = new List<byte>();
        BinaryFormat.WriteHeader(bytes, BinaryFormat.KindSnapshot);
        BinaryFormat.WriteVarint(bytes, 1);
        BinaryFormat.WriteReplicaId(bytes, R(1));
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 1);

        bytes.Add(BinaryFormat.TagElement);
        bytes.Add(BinaryFormat.FlagSideRight | BinaryFormat.ParentRoot);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 2);
        bytes.Add((byte)'a');
        bytes.Add((byte)'b');

        Rejects([.. bytes]);
    }

    [Fact]
    public void A_lone_surrogate_is_refused()
    {
        // §7: validate UTF-8, reject lone surrogates, normalize nothing.
        var bytes = new List<byte>();
        BinaryFormat.WriteHeader(bytes, BinaryFormat.KindSnapshot);
        BinaryFormat.WriteVarint(bytes, 1);
        BinaryFormat.WriteReplicaId(bytes, R(1));
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 1);

        bytes.Add(BinaryFormat.TagElement);
        bytes.Add(BinaryFormat.FlagSideRight | BinaryFormat.ParentRoot);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 3);
        bytes.AddRange([0xED, 0xA0, 0x80]);

        Rejects([.. bytes]);
    }

    [Fact]
    public void Parent_flag_one_on_the_first_record_is_refused()
    {
        var bytes = new List<byte>();
        BinaryFormat.WriteHeader(bytes, BinaryFormat.KindSnapshot);
        BinaryFormat.WriteVarint(bytes, 1);
        BinaryFormat.WriteReplicaId(bytes, R(1));
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 1);

        bytes.Add(BinaryFormat.TagElement);
        bytes.Add(BinaryFormat.FlagSideRight | BinaryFormat.ParentPrevious);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 0);
        BinaryFormat.WriteVarint(bytes, 1);
        bytes.Add((byte)'a');

        Assert.Contains("first record", Rejects([.. bytes]).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_record_tag_is_refused()
    {
        var encoded = Chain();
        encoded[FindRunCountOffset(encoded) - 1] = 0x7F;

        Assert.Contains("tag", Rejects(encoded).Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Offset of the run record's count byte in <see cref="Chain"/>: 6 header,
    /// a one-entry table (1 + 16), an empty vector (1), an element count (1),
    /// then the tag.
    /// </summary>
    private static int FindRunCountOffset(byte[] encoded)
    {
        const int Tag = 6 + 1 + ReplicaId.Size + 1 + 1;
        Assert.Equal(BinaryFormat.TagRun, encoded[Tag]);
        return Tag + 1;
    }

    /// <summary>Offset of the run record's flags byte, one varint past the count.</summary>
    private static int FindRunFlagsOffset(byte[] encoded) => FindRunCountOffset(encoded) + 1;
}
