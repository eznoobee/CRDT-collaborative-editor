namespace Editor.Api.Logging;

/// <summary>
/// Stands in for an exception whose text carried a credential.
/// </summary>
/// <remarks>
/// An exception's message cannot be rewritten in place, and a log provider that
/// serialises the original would defeat the redaction. Replacing it costs the
/// concrete type in the log line — the redacted text still names it — and keeps
/// the stack trace, which is the part anyone debugging actually needs.
/// </remarks>
public sealed class RedactedException : Exception
{
    public RedactedException()
        : base("An exception was redacted.")
    {
    }

    public RedactedException(string message)
        : base(message)
    {
    }

    public RedactedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The redacted rendering, in place of the original's.</summary>
    public override string ToString() => Message;
}
