namespace Crdt.Core.Tests;

/// <summary>
/// Direct tests for the identifier ordering PROJECT_SPEC.md §5 makes normative.
/// </summary>
/// <remarks>
/// This surface is exercised end to end by the conformance corpus, but that
/// lives in another project and proves it only indirectly. Sibling ties are
/// broken here, so a defect reorders user text; it deserves tests that name the
/// rule rather than tests that happen to depend on it.
/// </remarks>
public sealed class ReplicaIdTests
{
    private static ReplicaId FromBytes(params byte[] bytes)
    {
        var full = new byte[ReplicaId.Size];
        bytes.CopyTo(full, 0);
        return new ReplicaId(full);
    }

    [Fact]
    public void Parses_and_renders_the_canonical_form()
    {
        const string Canonical = "0123456789abcdef-fedc-ba98-7654-3210abcdef01";
        var id = ReplicaId.Parse("01234567-89ab-cdef-fedc-ba9876543210");

        Assert.Equal("01234567-89ab-cdef-fedc-ba9876543210", id.ToString());
        Assert.NotEqual(Canonical, id.ToString());
    }

    [Fact]
    public void Round_trips_through_bytes()
    {
        var original = ReplicaId.Parse("00112233-4455-6677-8899-aabbccddeeff");

        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        original.WriteBytes(bytes);

        Assert.Equal(0x00, bytes[0]);
        Assert.Equal(0xff, bytes[15]);
        Assert.Equal(original, new ReplicaId(bytes));
    }

    [Fact]
    public void Rejects_input_that_is_not_a_canonical_uuid()
    {
        Assert.Throws<FormatException>(() => ReplicaId.Parse("not-a-uuid"));
        Assert.Throws<ArgumentException>(() => new ReplicaId(new byte[4]));
    }

    [Theory]
    [InlineData("7fffffff-0000-0000-0000-000000000000", "80000000-0000-0000-0000-000000000000")]
    [InlineData("00000000-7fff-0000-0000-000000000000", "00000000-8000-0000-0000-000000000000")]
    [InlineData("00000000-0000-7fff-0000-000000000000", "00000000-0000-8000-0000-000000000000")]
    [InlineData("01000000-0000-0000-0000-000000000000", "ff000000-0000-0000-0000-000000000000")]
    [InlineData("00000000-0000-0000-0000-0000000000ff", "00000000-0000-0000-0001-000000000000")]
    public void Orders_as_unsigned_big_endian_bytes_across_signed_boundaries(
        string lowText, string highText)
    {
        // Every pair here straddles a boundary where a signed field comparison
        // would give the opposite answer. §5 requires unsigned byte order.
        var low = ReplicaId.Parse(lowText);
        var high = ReplicaId.Parse(highText);

        Assert.True(low < high, $"{lowText} must sort before {highText}");
        Assert.True(high > low);

        Span<byte> lowBytes = stackalloc byte[ReplicaId.Size];
        Span<byte> highBytes = stackalloc byte[ReplicaId.Size];
        low.WriteBytes(lowBytes);
        high.WriteBytes(highBytes);

        Assert.True(lowBytes.SequenceCompareTo(highBytes) < 0);
    }

    [Fact]
    public void Orders_by_the_first_differing_byte()
    {
        Assert.True(FromBytes(0x00, 0x01) < FromBytes(0x01, 0x00));
        Assert.True(FromBytes(0x00, 0x00, 0xff) < FromBytes(0x00, 0x01));
    }

    [Fact]
    public void Compares_the_low_half_when_the_high_half_matches()
    {
        var a = ReplicaId.Parse("00000000-0000-0000-0000-000000000001");
        var b = ReplicaId.Parse("00000000-0000-0000-0000-000000000002");

        Assert.True(a < b);
        Assert.True(a <= b);
        Assert.False(a >= b);
        Assert.Equal(-1, Math.Sign(a.CompareTo(b)));
    }

    [Fact]
    public void Strict_operators_are_false_for_equal_ids()
    {
        // The difference between < and <=. Without this the two are
        // indistinguishable, and a comparator that called equal ids "less than"
        // would order same-id siblings arbitrarily.
        var a = ReplicaId.Parse("00000000-0000-0000-0000-00000000000a");
        var same = ReplicaId.Parse("00000000-0000-0000-0000-00000000000a");

        Assert.False(a < same);
        Assert.False(a > same);
        Assert.True(a <= same);
        Assert.True(a >= same);
    }

    [Fact]
    public void Parse_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => ReplicaId.Parse(null!));
    }

    [Fact]
    public void Errors_name_what_was_wrong()
    {
        // The messages are part of the contract: a malformed replica id arrives
        // over the wire (§7), and "invalid" alone is not a diagnosis.
        var format = Assert.Throws<FormatException>(() => ReplicaId.Parse("nope"));
        Assert.Contains("nope", format.Message, StringComparison.Ordinal);

        var argument = Assert.Throws<ArgumentException>(() => new ReplicaId(new byte[4]));
        Assert.Contains("16", argument.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        var a = ReplicaId.Parse("00000000-0000-0000-0000-0000000000aa");
        var b = ReplicaId.Parse("00000000-0000-0000-0000-0000000000aa");
        var c = ReplicaId.Parse("00000000-0000-0000-0000-0000000000ab");

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a != c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(0, a.CompareTo(b));
        Assert.True(a.Equals((object)b));
        Assert.False(a.Equals("not an id"));
    }
}
