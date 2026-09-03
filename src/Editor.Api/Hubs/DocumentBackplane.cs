using System.Buffers.Binary;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Editor.Api.Hubs;

/// <summary>
/// Carries a document's broadcasts between instances (PROJECT_SPEC.md §8).
/// </summary>
/// <remarks>
/// <para>
/// §8 forbids sticky sessions, so two people editing the same document
/// routinely hold connections on different servers. Without this, each server
/// fans out to the connections it happens to hold and a document is
/// collaborative only among the clients that landed on the same box — which
/// looks completely correct in any single-instance test.
/// </para>
/// <para>
/// SignalR's own Redis backplane would deliver a group send across instances,
/// and is deliberately not what carries this. A group send arrives on the
/// remote instance as a write into each member's channel with no timeout, so
/// one slow client there stalls that instance's backplane consumer — the exact
/// stall §8's per-connection deadline exists to prevent, reintroduced one hop
/// away and invisible from here. Publishing the batch instead lets every
/// instance fan out to its own connections under its own
/// <see cref="DocumentBroadcaster"/>, so §8's rule holds uniformly rather than
/// only on the instance the sender happened to reach.
/// </para>
/// <para>
/// Subscriptions are per document and taken only while this instance holds a
/// connection for it. One shared channel would be less code and would make
/// every instance decode every document's traffic, which is work proportional
/// to the whole deployment rather than to what this instance is serving.
/// </para>
/// </remarks>
public sealed partial class DocumentBackplane : IAsyncDisposable
{
    private readonly Guid _instanceId = Guid.CreateVersion7();
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _subscribed = [];

    private readonly IConnectionMultiplexer _redis;
    private readonly DocumentConnections _connections;
    private readonly DocumentBroadcaster _broadcaster;
    private readonly IHubContext<EditorHub> _hub;
    private readonly ILogger<DocumentBackplane> _logger;
    private long _received;

    public DocumentBackplane(
        IConnectionMultiplexer redis,
        DocumentConnections connections,
        DocumentBroadcaster broadcaster,
        IHubContext<EditorHub> hub,
        ILogger<DocumentBackplane> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(logger);

        _redis = redis;
        _connections = connections;
        _broadcaster = broadcaster;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>This instance's identity, used to ignore its own publications.</summary>
    public Guid InstanceId => _instanceId;

    /// <summary>
    /// Batches this instance has taken off the backplane and fanned out.
    /// </summary>
    /// <remarks>
    /// §13.15. Two clients on two instances converge whether the batch crossed
    /// the backplane or the far client resynced from Postgres on its next
    /// catch-up, and the documents are identical either way. This count is what
    /// separates them, and without it "scale-out works" is a claim no test can
    /// make.
    /// </remarks>
    public long Received => Interlocked.Read(ref _received);

    /// <summary>
    /// How many documents this instance currently subscribes to.
    /// </summary>
    /// <remarks>
    /// §13.15. An instance that never unsubscribes behaves identically to one
    /// that does — every message still arrives, every document still converges
    /// — while accumulating subscriptions for documents nobody here is reading,
    /// for as long as the process lives. Nothing but this number distinguishes
    /// the two.
    /// </remarks>
    public int Carrying
    {
        get
        {
            lock (_gate)
            {
                return _subscribed.Count;
            }
        }
    }

    /// <summary>Starts carrying <paramref name="documentId"/>, if not already.</summary>
    public async Task JoinAsync(Guid documentId)
    {
        lock (_gate)
        {
            if (!_subscribed.Add(documentId))
            {
                return;
            }
        }

        try
        {
            await _redis.GetSubscriber()
                .SubscribeAsync(Channel(documentId), (_, value) => Deliver(value))
                .ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            // Undone, so the next connection on this document tries again
            // rather than believing a subscription exists.
            lock (_gate)
            {
                _subscribed.Remove(documentId);
            }

            Log.SubscribeFailed(_logger, documentId, exception);
        }
    }

    /// <summary>Stops carrying <paramref name="documentId"/> once nothing here holds it.</summary>
    public async Task LeaveAsync(Guid documentId)
    {
        lock (_gate)
        {
            if (!_subscribed.Remove(documentId))
            {
                return;
            }
        }

        try
        {
            await _redis.GetSubscriber().UnsubscribeAsync(Channel(documentId)).ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            Log.UnsubscribeFailed(_logger, documentId, exception);
        }
    }

    /// <summary>Offers a batch to the other instances holding this document.</summary>
    /// <remarks>
    /// Best effort, and deliberately so: the operations are already committed
    /// (§8 fans out only after the write), so a lost publication costs a remote
    /// client latency until its next catch-up rather than an operation. Failing
    /// the submission here would turn a backplane hiccup into a rejected
    /// keystroke for an operation the server already holds.
    /// </remarks>
    public async Task PublishAsync(OperationBroadcast broadcast)
    {
        ArgumentNullException.ThrowIfNull(broadcast);

        try
        {
            await _redis.GetSubscriber()
                .PublishAsync(Channel(broadcast.DocumentId), Encode(_instanceId, broadcast))
                .ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            Log.PublishFailed(_logger, broadcast.DocumentId, exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Guid[] documents;
        lock (_gate)
        {
            documents = [.. _subscribed];
            _subscribed.Clear();
        }

        foreach (var documentId in documents)
        {
            try
            {
                await _redis.GetSubscriber().UnsubscribeAsync(Channel(documentId)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is RedisException or ObjectDisposedException)
            {
                // Shutting down. A multiplexer already disposed underneath this
                // one has closed the subscription more thoroughly than the
                // unsubscribe would have.
            }
        }
    }

    private static RedisChannel Channel(Guid documentId) =>
        RedisChannel.Literal($"editor:ops:{documentId:N}");

    /// <summary>
    /// The inter-instance frame: origin, document, server_seq, then the §6 batch.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than handed to a serialiser. §13.13a's constraint
    /// is that §6 stays the sole authoritative encoding and nothing else
    /// acquires canonical-form rules of its own; a fixed 40-byte header in front
    /// of an opaque payload has no object model to disagree about.
    /// </remarks>
    internal static byte[] Encode(Guid origin, OperationBroadcast broadcast)
    {
        var frame = new byte[40 + broadcast.Operations.Length];
        origin.TryWriteBytes(frame.AsSpan(0, 16));
        broadcast.DocumentId.TryWriteBytes(frame.AsSpan(16, 16));
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(32, 8), broadcast.ServerSeq);
        broadcast.Operations.CopyTo(frame.AsSpan(40));
        return frame;
    }

    internal static (Guid Origin, OperationBroadcast Broadcast)? Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 40)
        {
            return null;
        }

        return (
            new Guid(frame[..16]),
            new OperationBroadcast(
                new Guid(frame[16..32]),
                frame[40..].ToArray(),
                BinaryPrimitives.ReadInt64LittleEndian(frame[32..40])));
    }

    private void Deliver(RedisValue value)
    {
        if (Decode((byte[]?)value ?? []) is not { } decoded)
        {
            return;
        }

        var (origin, broadcast) = decoded;
        if (origin == _instanceId)
        {
            // Its own publication, already fanned out locally by the hub before
            // it was published.
            return;
        }

        Interlocked.Increment(ref _received);

        // Not awaited: this runs on the Redis subscription callback, and
        // blocking it holds up every other document this instance carries.
        _ = FanOutAsync(broadcast);
    }

    private async Task FanOutAsync(OperationBroadcast broadcast)
    {
        try
        {
            await _broadcaster.FanOutAsync(
                _connections.All(broadcast.DocumentId),
                (connection, token) =>
                    _hub.Clients.Client(connection).SendAsync(EditorHub.Broadcast, broadcast, token),
                connection =>
                {
                    Log.BackpressureDrop(_logger, broadcast.DocumentId, connection);
                    _connections.Abort(broadcast.DocumentId, connection);
                    return Task.CompletedTask;
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Nothing above this to catch it: an unobserved failure on a
            // subscription callback is a document that quietly stops updating
            // on this instance only.
            Log.FanOutFailed(_logger, broadcast.DocumentId, exception);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 3410,
            Level = LogLevel.Warning,
            Message = "Could not subscribe to the backplane for document {DocumentId}.")]
        public static partial void SubscribeFailed(ILogger logger, Guid documentId, Exception exception);

        [LoggerMessage(
            EventId = 3411,
            Level = LogLevel.Warning,
            Message = "Could not unsubscribe from the backplane for document {DocumentId}.")]
        public static partial void UnsubscribeFailed(ILogger logger, Guid documentId, Exception exception);

        [LoggerMessage(
            EventId = 3412,
            Level = LogLevel.Warning,
            Message = "Could not publish a batch for document {DocumentId} to the backplane.")]
        public static partial void PublishFailed(ILogger logger, Guid documentId, Exception exception);

        [LoggerMessage(
            EventId = 3413,
            Level = LogLevel.Error,
            Message = "Fanning out a backplane batch for document {DocumentId} failed.")]
        public static partial void FanOutFailed(ILogger logger, Guid documentId, Exception exception);

        [LoggerMessage(
            EventId = 3414,
            Level = LogLevel.Warning,
            Message = "Dropped connection {ConnectionId} on document {DocumentId} for backpressure.")]
        public static partial void BackpressureDrop(ILogger logger, Guid documentId, string connectionId);
    }
}
