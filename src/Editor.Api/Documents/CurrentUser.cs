using System.Security.Claims;
using Editor.Domain;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Editor.Api.Documents;

/// <summary>Maps an OIDC principal to the user row it names.</summary>
public sealed class CurrentUser
{
    private readonly EditorDbContext _context;
    private readonly TimeProvider _time;

    public CurrentUser(EditorDbContext context, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(time);

        _context = context;
        _time = time;
    }

    /// <summary>
    /// The user id for <paramref name="principal"/>, provisioning a row the
    /// first time an issuer and subject are seen.
    /// </summary>
    /// <remarks>
    /// Identity is the pair (issuer, subject), not the subject: §6 says so
    /// because a subject is unique per issuer and nothing else. Keying on the
    /// subject alone would let a second issuer's user collide with a first
    /// issuer's — and inherit their documents.
    /// </remarks>
    public async Task<Guid?> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // MapInboundClaims is off (§7's registration), so these are the JWT's
        // own claim names rather than the framework's SOAP-era aliases.
        var issuer = principal.FindFirstValue("iss");
        var subject = principal.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var existing = await _context.Users
            .AsNoTracking()
            .Where(user => user.OidcIssuer == issuer && user.OidcSubject == subject)
            .Select(user => (Guid?)user.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        var created = new User
        {
            // Version 7, so the primary key is time-ordered and inserts do not
            // scatter across the index the way a v4 key does.
            Id = Guid.CreateVersion7(),
            OidcIssuer = issuer,
            OidcSubject = subject,
            DisplayName = principal.FindFirstValue("name") ?? subject,
            CreatedAt = _time.GetUtcNow(),
        };

        _context.Users.Add(created);

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return created.Id;
        }
        catch (DbUpdateException)
        {
            // Two connections for a first-time user race here, and the unique
            // index on (issuer, subject) is what decides it. The loser reads
            // the winner's row rather than failing the request.
            _context.Entry(created).State = EntityState.Detached;

            return await _context.Users
                .AsNoTracking()
                .Where(user => user.OidcIssuer == issuer && user.OidcSubject == subject)
                .Select(user => (Guid?)user.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
