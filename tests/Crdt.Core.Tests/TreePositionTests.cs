using System.Text;
using Crdt.Core.Tests.Simulation;

namespace Crdt.Core.Tests;

/// <summary>
/// Exercises right-sibling ordering where the right origins being compared sit
/// at different depths, or where one is an ancestor of the other.
/// </summary>
/// <remarks>
/// Ordering right siblings means comparing their right origins by position, and
/// that comparison has to normalise depth and detect an ancestor relationship
/// before it can compare siblings. Ordinary scenarios never reach those branches:
/// they compare nodes that happen to sit at the same depth. Deep trees come from
/// backward typing, which chains left children, so these tests build documents
/// that way and then merge concurrent edits into them.
/// </remarks>
public sealed class TreePositionTests
{
    private static Replica New(int n) => new(ReplicaIds.Numbered(n));

    private static Rune R(char c) => new(c);

    private static void Deliver(Replica from, Replica to)
    {
        foreach (var op in from.OperationsSince(to.VersionVector))
        {
            to.Apply(op);
        }
    }

    private static void Sync(params Replica[] replicas)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var from in replicas)
            {
                foreach (var to in replicas)
                {
                    if (!ReferenceEquals(from, to))
                    {
                        foreach (var op in from.OperationsSince(to.VersionVector))
                        {
                            to.Apply(op);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Types text right to left, which nests each character deeper.</summary>
    private static void TypeBackwards(Replica replica, string text, int at = 0)
    {
        foreach (var c in text)
        {
            replica.Insert(at, R(c));
        }
    }

    [Fact]
    public void Converges_when_concurrent_edits_land_in_a_deeply_nested_tree()
    {
        var a = New(1);
        var b = New(2);
        var c = New(3);

        TypeBackwards(a, "abcdefgh");
        Sync(a, b, c);

        // Each replica edits at a different depth of the nesting, concurrently.
        b.Insert(4, R('X'));
        c.Insert(1, R('Y'));
        TypeBackwards(a, "zy", at: 6);

        Sync(a, b, c);

        Assert.Equal(a.Text, b.Text);
        Assert.Equal(a.Text, c.Text);
        Assert.Equal(0, a.PendingCount);
        Assert.Equal(12, a.Values.Count);
    }

    [Fact]
    public void Converges_when_right_origins_are_nested_inside_one_another()
    {
        var a = New(1);
        var b = New(2);
        var c = New(3);

        a.Insert(0, R('a'));
        Sync(a, b, c);

        // Build nesting on one replica, then have two others insert
        // concurrently at the same visible position. Their right origins are at
        // different depths of that nesting.
        TypeBackwards(a, "mnop", at: 1);
        Sync(a, b, c);

        b.Insert(3, R('1'));
        c.Insert(3, R('2'));
        a.Insert(3, R('3'));

        Sync(a, b, c);

        Assert.Equal(a.Text, b.Text);
        Assert.Equal(b.Text, c.Text);
        Assert.Equal(8, a.Values.Count);
    }

    [Fact]
    public void Converges_with_interleaved_forward_and_backward_typing_at_depth()
    {
        var a = New(1);
        var b = New(2);

        TypeBackwards(a, "12345");
        Sync(a, b);

        // Forward run on one replica, backward run on the other, both landing
        // inside the nested region, concurrently.
        for (var i = 0; i < 4; i++)
        {
            a.Insert(2 + i, R((char)('p' + i)));
        }

        TypeBackwards(b, "wxyz", at: 2);

        Sync(a, b);

        Assert.Equal(a.Text, b.Text);
        Assert.Equal(13, a.Values.Count);
        Assert.Equal(0, a.PendingCount);
        Assert.Equal(0, b.PendingCount);
    }

    [Fact]
    public void Orders_right_siblings_whose_right_origins_are_ancestor_and_descendant()
    {
        // The case that drives the comparator's depth normalisation and its
        // ancestor detection. Two right children of the same parent only ever
        // have DIFFERENT right origins when their authors disagreed about what
        // follows that parent, and the smallest such disagreement is a second
        // left child of Q that one replica has and the other does not. The two
        // right origins are then Q and a child of Q — an ancestor pair.
        var r1 = New(1);
        var r2 = New(2);
        var r3 = New(3);

        r1.Insert(0, R('Q'));
        Sync(r1, r2, r3);

        // P and Z both become left children of Q, concurrently, so Q ends up
        // with two left children ordered by element id.
        r1.Insert(0, R('P'));
        r2.Insert(0, R('Z'));

        // r3 learns of both; r1 never learns of Z before it types again.
        Deliver(r1, r3);
        Deliver(r2, r3);
        Assert.Equal("PZQ", r3.Text);
        Assert.Equal("PQ", r1.Text);

        // Both insert immediately after P, so both become right children of P.
        // r1 believes P is followed by Q; r3 knows it is followed by Z.
        r1.Insert(1, R('x'));
        r3.Insert(1, R('y'));

        Sync(r1, r2, r3);

        Assert.Equal(r1.Text, r3.Text);
        Assert.Equal(r1.Text, r2.Text);
        Assert.Equal(5, r1.Values.Count);
        Assert.StartsWith("P", r1.Text, StringComparison.Ordinal);
        Assert.EndsWith("Q", r1.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Deletion_inside_a_nested_region_does_not_disturb_ordering()
    {
        var a = New(1);
        var b = New(2);

        TypeBackwards(a, "abcdef");
        Sync(a, b);

        a.Delete(2);
        a.Delete(2);
        b.Insert(3, R('Q'));

        Sync(a, b);

        Assert.Equal(a.Text, b.Text);
        Assert.Equal(5, a.Values.Count);

        // Tombstones stay in the full order as anchors.
        Assert.Equal(7, a.AllIds.Count);
    }
}
