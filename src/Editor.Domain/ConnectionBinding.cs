namespace Editor.Domain;

/// <summary>
/// What a SignalR connection is allowed to be, decided at <c>negotiate</c>.
/// </summary>
/// <remarks>
/// Every field here is chosen by the server (§7, §13.12). The replica id
/// especially: §5 makes it the tie-break that orders concurrent insertions, and
/// §7 rejects operations whose replica id does not match the connection's. If
/// the client supplied the binding, that comparison would check a value against
/// itself, and a client naming another live replica's id would author
/// operations attributed to that replica — with every other replica converging
/// on the forgery, because convergence is what the algorithm guarantees.
/// </remarks>
/// <param name="UserId">The authenticated user the OIDC token named.</param>
/// <param name="DocumentId">The one document this connection may submit into.</param>
/// <param name="ReplicaId">The replica id the server assigned to this connection.</param>
/// <param name="Role">The role held when the ticket was issued.</param>
public readonly record struct ConnectionBinding(
    Guid UserId,
    Guid DocumentId,
    Guid ReplicaId,
    Role Role);
