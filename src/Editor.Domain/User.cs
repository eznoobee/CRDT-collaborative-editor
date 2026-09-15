namespace Editor.Domain;

/// <summary>An authenticated principal.</summary>
/// <remarks>
/// An OIDC subject is unique per issuer, not globally, so identity is the pair
/// (§6). Nothing here is PII beyond the display name, which §10 keeps out of
/// logs.
/// </remarks>
public sealed class User
{
    public Guid Id { get; set; }

    public required string OidcIssuer { get; set; }

    public required string OidcSubject { get; set; }

    public required string DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
