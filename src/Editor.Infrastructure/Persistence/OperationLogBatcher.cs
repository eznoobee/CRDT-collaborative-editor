using System.Collections.Concurrent;
using System.Threading.Channels;
using Crdt.Core;

namespace Editor.Infrastructure.Persistence;

/// <summary>How long to accumulate before writing (PROJECT_SPEC.md §8).</summary>
/// <param name="Window">
/// Extra time a drained batch waits for company before being written. Zero is
/// the default and means adaptive — see <see cref="Default"/>.
/// </param>
/// <param name="MaxOperations">Flush as soon as this many have accumulated.</param>
public readonly record struct BatchingPolicy(TimeSpan Window, int MaxOperations)
{
    /// <summary>
    /// Adaptive: write immediately, and batch only what arrives during a write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §8 originally said "buffer for up to 50 ms or 100 ops, whichever comes
    /// first", and the reasoning was sound: twenty people typing produce twenty
    /// tiny transactions a second each, and each one takes the document's
    /// advisory lock. What the number did not account for is that <b>the wait
    /// happens inside the segment §8's own latency target measures</b>. 7b.4
    /// measured the contradiction: <c>editor.persist</c> never dropped below
    /// 28 ms and sat at a p50 of 57-59 ms, against a target of 25 ms for the
    /// whole of receive to broadcast. The p50 alone was more than twice the p99
    /// target, so no amount of tail work reached it.
    /// </para><para>
    /// <b>A window of zero is not "no batching".</b> The consumer loop drains
    /// everything queued, writes it, and only then looks again — so every
    /// submission that arrives <i>while a write is in flight</i> is waiting in
    /// the channel when that write returns, and goes out in the next batch.
    /// The batch size therefore tunes itself to load: one operation when the
    /// system is idle, and as many as arrive in a write's duration when it is
    /// busy. The amortisation the window was for is still there; what is gone
    /// is paying for it when there is no contention to amortise.
    /// </para><para>
    /// This is why there is no new code path and no mode switch. A scheme that
    /// chose between "immediate" and "batched" would have a threshold, and a
    /// threshold has a latency cliff at the crossing; this has neither, because
    /// the queue depth at the moment a write finishes <i>is</i> the load
    /// measurement, taken continuously and for free.
    /// </para><para>
    /// <c>MaxOperations</c> still caps a batch, so a long burst becomes several
    /// bounded writes rather than one unbounded one.
    /// </para>
    /// </remarks>
    public static BatchingPolicy Default => new(TimeSpan.Zero, 100);

    /// <summary>§8's original fixed window, kept for measuring against.</summary>
    /// <remarks>
    /// Not dead code: <c>docs/phase-8-measurements.md</c> compares the two, and
    /// a figure with nothing to compare it to cannot be shown to have improved.
    /// A deployment whose write latency is dominated by lock contention rather
    /// than by round trips can also still choose it.
    /// </remarks>
    public static BatchingPolicy FixedWindow => new(TimeSpan.FromMilliseconds(50), 100);
}

/// <summary>
/// Coalesces submissions per document into single writes (PROJECT_SPEC.md §8).
/// </summary>
/// <remarks>
/// <para>
/// Twenty people typing produce twenty tiny transactions a second each, and each
/// one takes the document's advisory lock. Coalescing turns that into one
/// transaction that takes the lock once, which is what makes per-document
/// serialisation affordable at all.
/// </para>
/// <para>
/// <b>One consumer loop per document, and it awaits each write before looking
/// for more.</b> That single fact carries two things §8 depends on, and it is
/// worth separating them because they are usually conflated.
/// </para>
/// <para>
/// <b>First, correctness.</b> <c>server_seq</c> is assigned inside
/// <see cref="OperationLogWriter.AppendAsync"/> under the document's advisory
/// lock, by reading the high-water mark and inserting above it. Two overlapping
/// writes for one document would each be correct in isolation — the lock
/// serialises them in the database — but the <i>completion order</i> would then
/// be whichever transaction committed first, so a client could be told about
/// sequence 12 before sequence 11 existed. The loop removes that case by
/// construction rather than by locking: there is never a second write for the
/// same document to race with. Note what this does <b>not</b> rest on: it is
/// per document, and different documents do overlap freely, which is the whole
/// point of a queue per document rather than one global writer.
/// </para>
/// <para>
/// <b>Second, and consequently, "no write in flight" is a property this loop
/// already has.</b> Adaptive flushing is not a new mechanism here — it is what
/// a zero window means given the loop: drain, write, and whatever arrived
/// meanwhile is the next batch. See <see cref="BatchingPolicy.Default"/>.
/// </para>
/// </remarks>
public sealed class OperationLogBatcher(
    OperationLogWriter writer,
    BatchingPolicy policy,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private sealed record Submission(
        IReadOnlyList<Operation> Operations,
        TaskCompletionSource<AppendResult> Completion);

    private sealed class DocumentQueue
    {
        public required Channel<Submission> Channel { get; init; }

        public required Task Consumer { get; init; }
    }

    private readonly ConcurrentDictionary<Guid, DocumentQueue> _queues = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private int _flushes;

    /// <summary>Writes performed, so coalescing can be observed rather than assumed.</summary>
    public int Flushes => Volatile.Read(ref _flushes);

    /// <summary>Queues operations, completing when they are durable.</summary>
    public Task<AppendResult> SubmitAsync(Guid documentId, IReadOnlyList<Operation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);

        var submission = new Submission(
            operations,
            new TaskCompletionSource<AppendResult>(TaskCreationOptions.RunContinuationsAsynchronously));

        var queue = _queues.GetOrAdd(documentId, Start);
        if (!queue.Channel.Writer.TryWrite(submission))
        {
            submission.Completion.TrySetException(
                new InvalidOperationException($"The queue for document {documentId} is closed."));
        }

        return submission.Completion.Task;
    }

    private DocumentQueue Start(Guid documentId)
    {
        var channel = Channel.CreateUnbounded<Submission>(
            new UnboundedChannelOptions { SingleReader = true });

        return new DocumentQueue
        {
            Channel = channel,
            Consumer = ConsumeAsync(documentId, channel),
        };
    }

    private async Task ConsumeAsync(Guid documentId, Channel<Submission> channel)
    {
        var reader = channel.Reader;
        var pending = new List<Submission>();

        while (await reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
        {
            pending.Clear();
            var count = 0;

            // Everything already queued. Under the adaptive default that is
            // where the coalescing comes from: what is sitting here now is
            // exactly what arrived while the previous write was in flight.
            // A non-zero window then holds the batch open for stragglers on
            // top of that, which is §8's original arrangement.
            while (count < policy.MaxOperations && reader.TryRead(out var first))
            {
                pending.Add(first);
                count += first.Operations.Count;
            }

            var deadline = _time.GetUtcNow() + policy.Window;
            while (count < policy.MaxOperations)
            {
                var remaining = deadline - _time.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                using var window = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                using var timer = _time.CreateTimer(
                    static state => ((CancellationTokenSource)state!).Cancel(),
                    window,
                    remaining,
                    Timeout.InfiniteTimeSpan);

                try
                {
                    if (!await reader.WaitToReadAsync(window.Token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
                {
                    break;
                }

                while (count < policy.MaxOperations && reader.TryRead(out var next))
                {
                    pending.Add(next);
                    count += next.Operations.Count;
                }
            }

            await FlushAsync(documentId, pending).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(Guid documentId, List<Submission> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var operations = pending.SelectMany(s => s.Operations).ToArray();

        try
        {
            var result = await writer
                .AppendAsync(documentId, operations, _shutdown.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _flushes);

            foreach (var submission in pending)
            {
                submission.Completion.TrySetResult(result);
            }
        }
        catch (Exception ex)
        {
            // Every submission in the batch shares its fate: they shared a
            // transaction, so reporting success for any of them would be a lie.
            foreach (var submission in pending)
            {
                submission.Completion.TrySetException(ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        foreach (var queue in _queues.Values)
        {
            queue.Channel.Writer.TryComplete();
            try
            {
                await queue.Consumer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: shutdown cancels the consumer loop.
            }
        }

        _shutdown.Dispose();
    }
}
