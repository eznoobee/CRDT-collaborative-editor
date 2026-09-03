using System.Text;

namespace Crdt.Core.Tests;

/// <summary>
/// The coupling between readiness and duplicate detection (§5).
/// </summary>
public sealed class CausalReadinessTests
{
    private static ReplicaId R(int n)
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        bytes[^1] = (byte)n;
        return new ReplicaId(bytes);
    }

    [Fact]
    public void A_sequence_gap_buffers_even_when_the_structural_dependencies_are_present()
    {
        // This is what makes duplicate detection complete. Readiness requires
        // the exact next sequence number for the replica, so a replica's
        // operations are applied strictly in order and the applied set never
        // has a per-replica gap — which is why "sequence below the watermark"
        // is a complete test for "already applied" rather than an approximation.
        //
        // The third operation below depends structurally on the root alone, so
        // nothing but the density rule can be holding it back.
        var author = R(1);
        var first = new ElementId(author, 0);

        var op0 = new InsertOperation(first, new Rune('a'), null, Side.Right, null);
        var op1 = new InsertOperation(new ElementId(author, 1), new Rune('b'), first, Side.Right, null);
        var op2 = new InsertOperation(new ElementId(author, 2), new Rune('c'), null, Side.Right, null);

        var replica = new Replica(R(2));
        replica.Apply(op0);
        replica.Apply(op2);

        Assert.Equal(1, replica.PendingCount);
        Assert.DoesNotContain("c", replica.Text, StringComparison.Ordinal);

        // The gap closes and the buffered operation cascades out.
        replica.Apply(op1);

        Assert.Equal(0, replica.PendingCount);
        Assert.Contains("c", replica.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_applied_operation_is_below_the_watermark()
    {
        // The consequence of the test above, stated as the property duplicate
        // detection actually relies on. If readiness is ever relaxed to apply an
        // operation whose structural dependencies exist despite a sequence gap,
        // this stops holding and the watermark check silently becomes an
        // approximation — so it is pinned here rather than left implicit in the
        // interaction between two private methods.
        var author = R(1);
        var replica = new Replica(R(2));

        ElementId? previous = null;
        var operations = new List<Operation>();
        for (var i = 0; i < 8; i++)
        {
            var id = new ElementId(author, (ulong)i);
            operations.Add(new InsertOperation(id, new Rune('a'), previous, Side.Right, null));
            previous = id;
        }

        // Applied in reverse, so all but the first spend time in the pending
        // set: the property has to hold after a cascade, not only after an
        // in-order delivery that never buffers anything.
        foreach (var operation in Enumerable.Reverse(operations))
        {
            replica.Apply(operation);
        }

        var watermark = replica.VersionVector[author];

        Assert.Equal(8UL, watermark);
        Assert.Equal(0, replica.PendingCount);
        Assert.All(operations, operation => Assert.True(operation.Id.Seq < watermark));
    }

    [Fact]
    public void Applying_the_same_operation_twice_changes_nothing()
    {
        var author = R(1);
        var replica = new Replica(R(2));
        var operation = new InsertOperation(
            new ElementId(author, 0), new Rune('a'), null, Side.Right, null);

        replica.Apply(operation);
        var once = replica.Text;
        replica.Apply(operation);

        Assert.Equal(once, replica.Text);
        Assert.Equal(0, replica.PendingCount);
    }

    [Fact]
    public void A_duplicate_arriving_while_a_cascade_is_pending_changes_nothing()
    {
        // The case a watermark test cannot catch on its own: the operation is
        // not applied yet, so the watermark says nothing about it, and the
        // pending set is what has to notice. Buffering it twice would apply it
        // twice when the gap closes.
        var author = R(1);
        var first = new ElementId(author, 0);

        var op0 = new InsertOperation(first, new Rune('a'), null, Side.Right, null);
        var op2 = new InsertOperation(new ElementId(author, 2), new Rune('c'), null, Side.Right, null);
        var op1 = new InsertOperation(new ElementId(author, 1), new Rune('b'), first, Side.Right, null);

        var replica = new Replica(R(2));
        replica.Apply(op0);
        replica.Apply(op2);
        replica.Apply(op2);

        Assert.Equal(1, replica.PendingCount);

        replica.Apply(op1);

        Assert.Equal(0, replica.PendingCount);
        Assert.Equal(1, replica.Text.Count(c => c == 'c'));
    }
}
