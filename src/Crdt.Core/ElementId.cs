namespace Crdt.Core;

/// <summary>
/// Identifies one operation, and for an insert the element it creates.
/// </summary>
/// <remarks>
/// <para>
/// Ordered lexicographically as the pair <c>(Replica, Seq)</c>, per
/// PROJECT_SPEC.md §5. This is the sibling tie-break: right-side siblings fall
/// back to it when their right origins are equal, and left-side siblings use it
/// outright.
/// </para>
/// <para>
/// It is an identity comparison, not a causal clock. Nothing about it needs to
/// respect happens-before, which is why a dense per-replica counter is enough
/// here where RGA would have needed a Lamport timestamp. See §13.2.
/// </para>
/// </remarks>
/// <param name="Replica">The replica that generated the operation.</param>
/// <param name="Seq">The replica's dense operation counter, starting at 0.</param>
public readonly record struct ElementId(ReplicaId Replica, ulong Seq)
    : IComparable<ElementId>
{
    /// <inheritdoc />
    public int CompareTo(ElementId other)
    {
        var replica = Replica.CompareTo(other.Replica);
        return replica != 0 ? replica : Seq.CompareTo(other.Seq);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Replica}:{Seq.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static bool operator <(ElementId left, ElementId right) => left.CompareTo(right) < 0;

    public static bool operator >(ElementId left, ElementId right) => left.CompareTo(right) > 0;

    public static bool operator <=(ElementId left, ElementId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ElementId left, ElementId right) => left.CompareTo(right) >= 0;
}
