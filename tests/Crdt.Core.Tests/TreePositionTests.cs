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
