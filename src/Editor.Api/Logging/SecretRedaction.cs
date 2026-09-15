using System.Text.RegularExpressions;

namespace Editor.Api.Logging;

/// <summary>
/// Removes credentials from text on its way to a log sink (PROJECT_SPEC.md §7).
/// </summary>
/// <remarks>
/// §7 requires that a ticket or token is never written to a log. Two of the
/// three places one can appear are outside this application's control: ASP.NET
/// Core logs "Request starting ... ?access_token=…" from inside the hosting
/// layer before any middleware runs, and an exception's own message may quote
/// the URL that produced it. So redaction happens at the logging seam rather
/// than in a middleware, where it covers every category, every provider and
/// every exception path at once.
/// <para>
/// This is a blocklist, and a blocklist is a weaker thing than a design that
/// never puts the secret where it can be logged. It is here because the ticket
/// has to travel in a URL (§7: browsers cannot set headers on a WebSocket
/// handshake) and URLs get logged by code this project does not own.
/// </para>
/// </remarks>
public static partial class SecretRedaction
{
    /// <summary>What replaces a redacted value.</summary>
    public const string Placeholder = "[redacted]";

    /// <summary>
    /// The ticket in a query string, wherever a URL is logged.
    /// </summary>
    [GeneratedRegex(
        @"((?:access_token|ticket)=)[^&\s""'>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueryCredential();

    /// <summary>
    /// A bearer token, whether logged as a header line or on its own.
    /// </summary>
    [GeneratedRegex(
        @"\bBearer\s+[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    /// <summary>
    /// An <c>Authorization</c> header value in any of the shapes a logger might
    /// render one: <c>Authorization: x</c>, <c>Authorization=x</c>, or a
    /// structured pair rendered as <c>Authorization = x</c>.
    /// </summary>
    [GeneratedRegex(
        // The value runs to the end of the field, not to the first space: an
        // Authorization value is "Bearer <token>", and stopping at the space
        // redacts the word "Bearer" and leaves the token.
        // The whitespace after the separator is matched atomically so it cannot
        // be given back: with an ordinary \s* the engine backtracks to match
        // zero spaces, the lookahead then sees " [redacted]" rather than
        // "[redacted]", and a second pass redacts its own placeholder.
        @"(Authorization""?\s*[:=](?>[ \t]*))(?!\[redacted\])[^\r\n,;}\]""]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    /// <summary>
    /// A bare connect ticket: 43 base64url characters, which is exactly 32
    /// bytes and exactly what this application issues.
    /// </summary>
    /// <remarks>
    /// Deliberately over-broad. It will also redact a 43-character content
    /// hash, and a log line that loses a hash is a smaller loss than one that
    /// keeps a live credential. The bounded alternative — redacting only where
    /// a known parameter name appears — misses any component that logs the
    /// value on its own, which is the case the other patterns cannot cover.
    /// </remarks>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9\-_])[A-Za-z0-9\-_]{43}(?![A-Za-z0-9\-_=])",
        RegexOptions.CultureInvariant)]
    private static partial Regex BareTicket();

    /// <summary>
    /// Names whose value is a credential whatever it looks like.
    /// </summary>
    /// <remarks>
    /// Text patterns cannot cover structured logging. A scope carrying
    /// <c>["Ticket"] = "abc"</c> renders as a key and a value that match
    /// nothing — the value has no <c>access_token=</c> in front of it and no
    /// particular shape — so the pattern list leaves it alone and a JSON
    /// provider writes the credential to disk. The name is the only signal
    /// available, so the name is what this matches.
    /// </remarks>
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token",
        "accesstoken",
        "authorization",
        "bearer",
        "connect_ticket",
        "connectticket",
        "id_token",
        "password",
        "refresh_token",
        "secret",
        "ticket",
        "token",
    };

    /// <summary>Whether a structured log field's name makes its value a credential.</summary>
    public static bool IsSensitiveName(string? name) =>
        name is not null && SensitiveNames.Contains(name);

    /// <summary>Redacts every credential shape from <paramref name="text"/>.</summary>
    public static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // Order matters: the query and header patterns keep their parameter
        // name, which tells whoever reads the log what was removed. Running the
        // bare-ticket pattern first would erase the value without that context.
        var redacted = QueryCredential().Replace(text, $"$1{Placeholder}");
        redacted = AuthorizationHeader().Replace(redacted, $"$1{Placeholder}");
        redacted = BearerToken().Replace(redacted, $"Bearer {Placeholder}");
        redacted = BareTicket().Replace(redacted, Placeholder);

        return redacted;
    }

    /// <summary>Whether redaction would change <paramref name="text"/>.</summary>
    public static bool Contains(string? text) =>
        !string.IsNullOrEmpty(text) && !string.Equals(text, Apply(text), StringComparison.Ordinal);
}
