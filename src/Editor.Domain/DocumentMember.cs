namespace Editor.Domain;

/// <summary>A user's membership of a document.</summary>
/// <remarks>
/// §7 checks this per operation against a cache with a five-second TTL, so a
/// change here must be observable within that bound.
/// </remarks>
public sealed class DocumentMember
{
    public Guid DocumentId { get; set; }

    public Guid UserId { get; set; }

    public Role Role { get; set; }

    public DateTimeOffset GrantedAt { get; set; }

    public Guid GrantedBy { get; set; }
}
