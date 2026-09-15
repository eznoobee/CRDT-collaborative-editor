using System.Text;
using Crdt.Core.Tests.Simulation;

namespace Crdt.Core.Tests;

/// <summary>
/// Direct tests for <see cref="Replica"/>'s surface.
/// </summary>
/// <remarks>
/// The invariants exercise the algorithm through generated scenarios, which is
/// where the real confidence comes from. These cover the edges a generator will
/// not reach on purpose: argument validation, delivery deltas, and the
/// collection rules from §5, whose conditions are easier to state than to hit
/// by chance.
/// </remarks>
public sealed class ReplicaTests
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

    [Fact]
    public void A_new_replica_is_empty()
    {
        var replica = New(1);

        Assert.Equal(string.Empty, replica.Text);
        Assert.Empty(replica.Values);
        Assert.Empty(replica.VisibleIds);
        Assert.Empty(replica.AllIds);
        Assert.Empty(replica.VersionVector);
        Assert.Equal(0, replica.PendingCount);
        Assert.Equal(ReplicaIds.Numbered(1), replica.Id);
    }

    [Fact]
    public void Inserts_at_the_requested_index()
    {
        var replica = New(1);
        replica.Insert(0, R('b'));
        replica.Insert(0, R('a'));
        replica.Insert(2, R('c'));

        Assert.Equal("abc", replica.Text);
        Assert.Equal(3, replica.VisibleIds.Count);
    }

    [Fact]
    public void Deleting_tombstones_rather_than_removing()
    {
        var replica = New(1);
        replica.Insert(0, R('a'));
        replica.Insert(1, R('b'));
        replica.Delete(0);

        Assert.Equal("b", replica.Text);
        Assert.Single(replica.VisibleIds);

        // The tombstone keeps its place in the full order: it is still an
        // element, and still a valid anchor (§5).
        Assert.Equal(2, replica.AllIds.Count);
    }

    [Fact]
    public void Rejects_out_of_range_indexes()
    {
        var replica = New(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => replica.Insert(1, R('a')));
        Assert.Throws<ArgumentOutOfRangeException>(() => replica.Insert(-1, R('a')));
        Assert.Throws<ArgumentOutOfRangeException>(() => replica.Delete(0));

        replica.Insert(0, R('a'));
        Assert.Throws<ArgumentOutOfRangeException>(() => replica.Delete(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => replica.Delete(-1));
    }

    [Fact]
    public void Rejects_a_null_operation()
    {
        Assert.Throws<ArgumentNullException>(() => New(1).Apply(null!));
        Assert.Throws<ArgumentNullException>(() => New(1).OperationsSince(null!));
        Assert.Throws<ArgumentNullException>(() => New(1).Collect(null!));
    }

    [Fact]
    public void Counts_operations_not_elements_in_the_version_vector()
    {
        var replica = New(1);
        replica.Insert(0, R('a'));
        replica.Delete(0);

        // Deletes consume a Seq too (§5), so the count is two despite one
        // element existing.
        Assert.Equal(2UL, replica.VersionVector[replica.Id]);
    }

    [Fact]
    public void Sends_only_what_the_peer_is_missing()
    {
        var a = New(1);
        var b = New(2);

        a.Insert(0, R('x'));
        Assert.Single(a.OperationsSince(b.VersionVector));

        Deliver(a, b);
        Assert.Empty(a.OperationsSince(b.VersionVector));

        a.Insert(1, R('y'));
        Assert.Single(a.OperationsSince(b.VersionVector));
    }

    [Fact]
    public void Applying_the_same_operation_twice_changes_nothing()
    {
        var a = New(1);
        var b = New(2);
        var op = a.Insert(0, R('a'));

        b.Apply(op);
        b.Apply(op);

        Assert.Equal("a", b.Text);
        Assert.Equal(0, b.PendingCount);
    }

    [Fact]
    public void Buffers_an_operation_whose_dependency_has_not_arrived()
    {
        var a = New(1);
        var b = New(2);

        var first = a.Insert(0, R('a'));
        var second = a.Insert(1, R('b'));

        // Deliver out of order: the second insert depends on the first.
        b.Apply(second);
        Assert.Equal(1, b.PendingCount);
        Assert.Equal(string.Empty, b.Text);

        b.Apply(first);
        Assert.Equal(0, b.PendingCount);
        Assert.Equal("ab", b.Text);
    }

    [Fact]
    public void Collect_keeps_tombstones_that_are_not_causally_stable()
    {
        var replica = New(1);
        replica.Insert(0, R('a'));
        replica.Insert(1, R('b'));
        replica.Delete(0);

        // An empty frontier means nothing is known to be stable anywhere.
        Assert.Equal(0, replica.Collect(new Dictionary<ReplicaId, ulong>()));
        Assert.Equal(2, replica.AllIds.Count);
    }

    [Fact]
    public void Collect_retains_the_leading_tombstone_of_a_run()
    {
        var replica = New(1);
        foreach (var c in "abcd")
        {
            replica.Insert(replica.Values.Count, R(c));
        }

        // Delete b, c and d, leaving a run of three consecutive tombstones.
        replica.Delete(1);
        replica.Delete(1);
        replica.Delete(1);

        var before = replica.AllIds.Count;
        var collected = replica.Collect(replica.VersionVector);

        // Only the leading tombstone of the run can still be named as a future
        // right origin, so it stays and the other two go (§5). Reaching that
        // takes a fixpoint: forward typing makes each element its successor's
        // parent, so the run is collected from the tail inwards.
        Assert.Equal(2, collected);
        Assert.Equal(before - 2, replica.AllIds.Count);
        Assert.Equal("a", replica.Text);
    }

    [Fact]
    public void Collect_treats_the_frontier_as_strictly_exclusive()
    {
        var replica = New(1);
        foreach (var c in "abcd")
        {
            replica.Insert(replica.Values.Count, R(c));
        }

        // Elements a..d hold seqs 0..3; the three deletes take 4..6.
        replica.Delete(1);
        replica.Delete(1);
        replica.Delete(1);

        // A frontier of 3 means "operations 0,1,2 are stable everywhere", so
        // element d at seq 3 is not yet stable. d is the only leaf of the
        // tombstone run, so nothing can go.
        Assert.Equal(0, replica.Collect(new Dictionary<ReplicaId, ulong> { [replica.Id] = 3 }));

        // At 4, d becomes stable and collectable, and c becomes a leaf behind
        // it. The boundary is exactly one operation wide.
        Assert.Equal(2, replica.Collect(new Dictionary<ReplicaId, ulong> { [replica.Id] = 4 }));
        Assert.Equal("a", replica.Text);
    }

    [Fact]
    public void Collect_ignores_replicas_absent_from_the_frontier()
    {
        var a = New(1);
        var b = New(2);
        a.Insert(0, R('a'));
        a.Insert(1, R('b'));
        Deliver(a, b);
        b.Delete(1);
        Deliver(b, a);

        // The frontier says nothing about replica 2, whose delete created the
        // tombstone; an unknown replica is not a stable one.
        Assert.Equal(0, a.Collect(new Dictionary<ReplicaId, ulong> { [a.Id] = 99 }));
    }

    [Fact]
    public void Collect_does_not_change_the_visible_text()
    {
        var replica = New(1);
        foreach (var c in "hello")
        {
            replica.Insert(replica.Values.Count, R(c));
        }

        replica.Delete(1);
        replica.Delete(1);

        var before = replica.Text;
        replica.Collect(replica.VersionVector);

        Assert.Equal(before, replica.Text);
    }
}
