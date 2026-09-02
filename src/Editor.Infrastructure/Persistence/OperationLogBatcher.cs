using System.Collections.Concurrent;
using System.Threading.Channels;
using Crdt.Core;

namespace Editor.Infrastructure.Persistence;

/// <summary>How long to accumulate before writing (PROJECT_SPEC.md §8).</summary>
/// <param name="Window">Maximum time a submission waits for company.</param>
/// <param name="MaxOperations">Flush as soon as this many have accumulated.</param>
public readonly record struct BatchingPolicy(TimeSpan Window, int MaxOperations)
{
    /// <summary>The §8 default: 50 ms or 100 operations, whichever comes first.</summary>
    public static BatchingPolicy Default => new(TimeSpan.FromMilliseconds(50), 100);
}

/// <summary>
/// Coalesces submissions per document into single writes (PROJECT_SPEC.md §8).
/// </summary>
/// <remarks>
/// <para>
/// Twenty people typing produce twenty tiny transactions a second each, and each
/// one takes the document's advisory lock. Buffering for up to 50 ms or 100
/// operations turns that into one transaction that takes the lock once, which is
/// what makes per-document serialisation affordable at all.
/// </para>
/// <para>
/// One consumer loop per document, so operations from a single document are
/// never written concurrently with each other. Documents do not contend.
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

            // Take everything already queued, then hold the window open for
            // stragglers — that is where the coalescing comes from.
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
