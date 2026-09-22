namespace Editor.Api.Hubs;

/// <summary>
/// The error codes a hub method fails with (PROJECT_SPEC.md §7).
/// </summary>
/// <remarks>
/// §7's 404-versus-403 rule is about not leaking document existence, and a hub
/// method has no status code, so it carries the same distinction as a code. The
/// distinction is not cosmetic: answering <c>forbidden</c> for a document the
/// caller cannot see leaks its existence exactly as a 403 would.
/// <para>
/// These strings reach the client verbatim and are the entire message. Anything
/// added — an id, a reason, the name of a document — is the leak the rule
/// exists to prevent.
/// </para>
/// </remarks>
public static class HubErrors
{
    /// <summary>
    /// The caller has no role on this document, so as far as they are concerned
    /// it does not exist. Covers no such document, a deleted document, and one
    /// they were never a member of.
    /// </summary>
    public const string NotFound = "not_found";

    /// <summary>
    /// The caller can see the document and may not do this — a viewer writing.
    /// There is nothing to conceal, because they already know it exists.
    /// </summary>
    public const string Forbidden = "forbidden";

    /// <summary>The connection is not bound to a document.</summary>
    public const string Unauthenticated = "unauthenticated";
}
