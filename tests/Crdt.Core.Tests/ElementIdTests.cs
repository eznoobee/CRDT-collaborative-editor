namespace Crdt.Core.Tests;

/// <summary>
/// Direct tests for the sibling tie-break: lexicographic on (replica, seq).
/// </summary>
public sealed class ElementIdTests
{
    private static ElementId Id(int replica, ulong seq) =>
        new(Simulation.ReplicaIds.Numbered(replica), seq);

    [Fact]
    public void Orders_by_replica_before_seq()
    {
        // A higher seq must not outrank a lower replica id: replica is the
        // primary component (§5).
        Assert.True(Id(1, 999) < Id(2, 0));
        Assert.True(Id(2, 0) > Id(1, 999));
    }

    [Fact]
    public void Orders_by_seq_within_one_replica()
    {
        Assert.True(Id(1, 0) < Id(1, 1));
        Assert.True(Id(1, 2) >= Id(1, 2));
        Assert.True(Id(1, 2) <= Id(1, 2));
        Assert.Equal(0, Id(1, 5).CompareTo(Id(1, 5)));
    }

    [Fact]
    public void Renders_replica_and_seq()
    {
        var text = Id(3, 7).ToString();

        Assert.Contains("00000000-0000-0000-0000-000000000003", text, StringComparison.Ordinal);
        Assert.EndsWith(":7", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(Id(1, 1), Id(1, 1));
        Assert.NotEqual(Id(1, 1), Id(1, 2));
        Assert.NotEqual(Id(1, 1), Id(2, 1));
    }
}
