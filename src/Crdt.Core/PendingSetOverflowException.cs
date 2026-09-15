namespace Crdt.Core;

/// <summary>
/// The pending set exceeded the bound its connection set (§5).
/// </summary>
/// <remarks>
/// A protocol violation rather than a resource problem to absorb. §5 requires
/// reject, log and close, and the close has to be distinguishable by the client
/// from the connection simply dropping (§13.13) — so this carries the numbers
/// that explain it rather than being a bare failure.
/// </remarks>
public sealed class PendingSetOverflowException : Exception
{
    public PendingSetOverflowException(int pending, int bound)
        : base($"The pending set holds {pending} operations and the bound is {bound} (§5).")
    {
        Pending = pending;
        Bound = bound;
    }

    public PendingSetOverflowException()
        : base("The pending set exceeded its bound (§5).")
    {
    }

    public PendingSetOverflowException(string message)
        : base(message)
    {
    }

    public PendingSetOverflowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Operations waiting when the bound was hit.</summary>
    public int Pending { get; }

    /// <summary>The bound that was exceeded.</summary>
    public int Bound { get; }
}
