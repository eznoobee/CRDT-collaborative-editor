namespace Editor.Infrastructure.Ingest;

/// <summary>§7's rate limits on the document API (register row 22).</summary>
public interface IDocumentApiRateLimiter
{
    /// <summary>Charges one writing request against this user's budget.</summary>
    Task<RateLimitDecision> ChargeWriteAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// The document API's budget, over the same Redis window as §7's other limits.
/// </summary>
/// <remarks>
/// This limit exists because §7 was incomplete rather than because the
/// implementation was: for five phases the abuse list spoke only to operation
/// submission and connections, which left <c>POST /documents</c> an unbounded
/// write path that nothing in the specification forbade. Register row 22.
/// </remarks>
public sealed class RedisDocumentApiRateLimiter : IDocumentApiRateLimiter
{
    private readonly RedisFixedWindow _window;
    private readonly DocumentApiRateLimitOptions _options;

    public RedisDocumentApiRateLimiter(
        RedisFixedWindow window, DocumentApiRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(options);

        _window = window;
        _options = options;
    }

    public async Task<RateLimitDecision> ChargeWriteAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var charge = await _window
            .ChargeAsync($"{_options.KeyPrefix}write:{userId:N}", 1, _options.Interval)
            .ConfigureAwait(false);

        return charge.Total > _options.WritesPerUser
            ? new RateLimitDecision(false, charge.Ttl)
            : RateLimitDecision.Ok();
    }
}
