using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;

namespace Editor.Api.Hubs;

/// <summary>How long a slow connection may hold up a fan-out (§8).</summary>
public sealed class BackpressureOptions
{
    public const string Section = "Backpressure";

    /// <summary>
    /// Bytes buffered for one connection before the transport stops accepting
    /// more.
    /// </summary>
    /// <remarks>
    /// §8 bounds this in bytes rather than messages because buffered payload is
    /// what exhausts an app server, and a one-code-point insert and a
    /// 256-code-point paste are not the same amount of memory.
    /// </remarks>
    [Range(4 * 1024, 1024 * 1024)]
    public int MaxOutboundBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// How long a single send may take before the connection is dropped to
    /// catch-up.
    /// </summary>
    /// <remarks>
    /// Once the outbound buffer is full, a send does not fail — it waits. That
    /// wait is the thing that has to be bounded: without it one slow client
    /// holds up the fan-out to everyone else on the document, which turns one
    /// bad network into a stalled document.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:30")]
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>The outcome of one fan-out.</summary>
/// <param name="Delivered">Connections the batch reached.</param>
/// <param name="Dropped">Connections closed for not keeping up.</param>
public readonly record struct FanOutResult(int Delivered, int Dropped);

/// <summary>
/// Fans a batch out to a document's connections, dropping the ones that cannot
/// keep up rather than waiting for them (PROJECT_SPEC.md §8).
/// </summary>
/// <remarks>
/// Separated from the hub because the property worth testing is not "the
/// message arrives" but "a connection that never reads does not hold up the
/// others", and that is a statement about this loop rather than about SignalR.
/// The hub supplies the send and the close; this decides how long to wait and
/// what to do when the wait expires.
/// </remarks>
public sealed class DocumentBroadcaster
{
    private readonly BackpressureOptions _options;
    private readonly TimeProvider _time;
    private long _dropped;

    public DocumentBroadcaster(BackpressureOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _options = options;
        _time = time;
    }

    /// <summary>
    /// Connections dropped for backpressure since this process started.
    /// </summary>
    /// <remarks>
    /// §13.15: dropping a slow client and never dropping one produce the same
    /// document, so the count is the only thing that distinguishes them. A rate
    /// above zero is a network or a client problem that is otherwise invisible
    /// until someone complains their editor keeps reconnecting.
    /// </remarks>
    public long DroppedForBackpressure => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Sends to every connection, dropping those that exceed the send timeout.
    /// </summary>
    /// <param name="connections">Connection ids to send to.</param>
    /// <param name="send">Sends to one connection; may block on a full buffer.</param>
    /// <param name="close">Closes one connection that could not keep up.</param>
    /// <param name="cancellationToken">Cancels the whole fan-out, not one send.</param>
    public async Task<FanOutResult> FanOutAsync(
        IReadOnlyCollection<string> connections,
        Func<string, CancellationToken, Task> send,
        Func<string, Task> close,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(close);

        if (connections.Count == 0)
        {
            return new FanOutResult(0, 0);
        }

        // Started together rather than awaited one at a time. Sequential sends
        // would make every connection wait for the slowest one ahead of it,
        // which is the stall this exists to prevent — just moved from one slow
        // client to all of them.
        var attempts = new List<(string Connection, Task Task)>(connections.Count);
        var timeouts = new List<CancellationTokenSource>(connections.Count);

        try
        {
            foreach (var connection in connections)
            {
                // Constructed with the TimeProvider so a test can advance the
                // clock rather than wait out a real five seconds — the timeout
                // is the thing under test, and a test that sleeps for it is a
                // test nobody runs.
                var timeout = new CancellationTokenSource(_options.SendTimeout, _time);
                timeouts.Add(timeout);

                var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    timeout.Token, cancellationToken);
                timeouts.Add(linked);

                attempts.Add((connection, send(connection, linked.Token)));
            }

            var delivered = 0;
            var dropped = new List<string>();

            foreach (var (connection, task) in attempts)
            {
                try
                {
                    await task.ConfigureAwait(false);
                    delivered++;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    dropped.Add(connection);
                }
                catch (Exception)
                {
                    // A connection that has already gone away is not a
                    // backpressure drop and is not counted as one, but it is
                    // still not delivered.
                    dropped.Add(connection);
                }
            }

            foreach (var connection in dropped)
            {
                Interlocked.Increment(ref _dropped);
                await close(connection).ConfigureAwait(false);
            }

            return new FanOutResult(delivered, dropped.Count);
        }
        finally
        {
            foreach (var timeout in timeouts)
            {
                timeout.Dispose();
            }
        }
    }
}

/// <summary>
/// Which connections are on which document.
/// </summary>
/// <remarks>
/// SignalR's own groups would do the fan-out, and do not expose their members,
/// so a per-connection send timeout is not expressible through them. This
/// registry is what makes "drop the slow one, keep the others" possible at all.
/// It is per instance, which is correct: an instance can only send to the
/// connections it holds, and §8's backplane is what reaches the rest.
/// </remarks>
public sealed class DocumentConnections
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, Entry>> _byDocument = new();

    /// <summary>What this instance knows about one connection it holds.</summary>
    /// <param name="ReplicaId">The replica this connection authors as (§7).</param>
    /// <param name="ClaimToken">This session's proof that it holds that replica.</param>
    /// <param name="Abort">
    /// Closes it. Carried per connection because a hub can only abort
    /// <em>itself</em> — <c>Context.Abort()</c> ends the connection that invoked
    /// the method, and there is no abort-by-id anywhere in the hub API. A
    /// registry of bare ids produced a fan-out that dropped the slow client by
    /// disconnecting the fast one that happened to be sending.
    /// </param>
    private readonly record struct Entry(Guid ReplicaId, Guid ClaimToken, Action Abort);

    /// <summary>One connection this instance holds, as the renewal loop sees it.</summary>
    public readonly record struct HeldConnection(
        Guid DocumentId, string ConnectionId, Guid ReplicaId, Guid ClaimToken);

    /// <summary>
    /// Registers a connection along with the means to close it and the claim it
    /// holds.
    /// </summary>
    public void Add(Guid documentId, string connectionId, Guid replicaId, Guid claimToken, Action abort) =>
        _byDocument.GetOrAdd(documentId, _ => new ConcurrentDictionary<string, Entry>())[connectionId] =
            new Entry(replicaId, claimToken, abort);

    /// <summary>Every connection this instance holds, across all documents.</summary>
    /// <remarks>
    /// §7's claims are renewed from here rather than from activity: a client
    /// reading without typing sends nothing for minutes, and its claim lapsing
    /// while its socket is open is what would let a second session resume a
    /// replica that still has a live author.
    /// </remarks>
    public IReadOnlyList<HeldConnection> Held()
    {
        var held = new List<HeldConnection>();
        foreach (var (documentId, connections) in _byDocument)
        {
            foreach (var (connectionId, entry) in connections)
            {
                held.Add(new HeldConnection(
                    documentId, connectionId, entry.ReplicaId, entry.ClaimToken));
            }
        }

        return held;
    }

    /// <summary>
    /// Deregisters a connection, reporting whether it was the last one here.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this instance now holds no connection for
    /// the document, which is what lets the backplane subscription be dropped.
    /// </returns>
    public bool Remove(Guid documentId, string connectionId)
    {
        if (!_byDocument.TryGetValue(documentId, out var connections))
        {
            return false;
        }

        connections.TryRemove(connectionId, out _);
        return connections.IsEmpty;
    }

    /// <summary>How many connections this instance holds for the document.</summary>
    /// <remarks>
    /// A synchronisation point for tests, and the honest one: disconnect
    /// handling runs with no reply to await, so a test that asserts immediately
    /// after a client goes away is asserting against a transition that has not
    /// happened yet — and passes for that reason rather than for the intended
    /// one.
    /// </remarks>
    public int Count(Guid documentId) =>
        _byDocument.TryGetValue(documentId, out var connections) ? connections.Count : 0;

    /// <summary>Closes one connection, if it is still registered.</summary>
    public void Abort(Guid documentId, string connectionId)
    {
        if (_byDocument.TryGetValue(documentId, out var connections)
            && connections.TryGetValue(connectionId, out var held))
        {
            held.Abort();
        }
    }

    /// <summary>Connections on this document, other than <paramref name="except"/>.</summary>
    public IReadOnlyCollection<string> Others(Guid documentId, string except) =>
        _byDocument.TryGetValue(documentId, out var connections)
            ? [.. connections.Keys.Where(id => !string.Equals(id, except, StringComparison.Ordinal))]
            : [];

    /// <summary>
    /// Every connection this instance holds for the document.
    /// </summary>
    /// <remarks>
    /// The backplane path has no sender to leave out: the instance that took
    /// the submission excluded it before publishing, and on any other instance
    /// every local connection is a recipient.
    /// </remarks>
    public IReadOnlyCollection<string> All(Guid documentId) =>
        _byDocument.TryGetValue(documentId, out var connections) ? [.. connections.Keys] : [];
}
