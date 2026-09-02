namespace Editor.Domain;

/// <summary>A collaboratively edited document.</summary>
public sealed class Document
{
    public Guid Id { get; set; }

    public Guid OwnerId { get; set; }

    public required string Title { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Soft deletion. The operation log is retained.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
