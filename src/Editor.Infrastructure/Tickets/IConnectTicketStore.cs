using Editor.Domain;

namespace Editor.Infrastructure.Tickets;

/// <summary>Issues and redeems the short-lived connect tickets §7 requires.</summary>
public interface IConnectTicketStore
{
    /// <summary>
    /// Issues a single-use ticket for <paramref name="binding"/>.
    /// </summary>
    /// <returns>The opaque ticket value to put in the <c>access_token</c> query parameter.</returns>
    Task<string> IssueAsync(ConnectionBinding binding, CancellationToken cancellationToken);

    /// <summary>
    /// Redeems <paramref name="ticket"/>, atomically and at most once.
    /// </summary>
    /// <returns>The binding it carried, or <see langword="null"/> if the ticket
    /// was unknown, already redeemed, or expired. The three are deliberately
    /// indistinguishable to the caller.</returns>
    Task<ConnectionBinding?> RedeemAsync(string ticket, CancellationToken cancellationToken);
}
