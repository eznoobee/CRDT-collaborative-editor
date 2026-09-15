using Editor.Api.Hubs;
using Microsoft.Extensions.Time.Testing;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// §8's backpressure rule: a client that cannot keep up is dropped to
/// catch-up rather than waited for.
/// </summary>
/// <remarks>
/// The fan-out is tested directly rather than through a real slow client. The
/// property is "a connection that never drains does not hold up the others",
/// which is a statement about this loop, and a test driving it here can hold a
/// send open indefinitely and advance a fake clock — where an end-to-end
/// version would have to genuinely fill a 64 KB transport buffer and then wait
/// out a real five-second timeout, which is a test nobody runs and a race when
/// they do.
/// <para>
/// The named vacuity risk: a "slow" client that is not actually slower than the
/// producer never fills anything, and every assertion below would pass against
/// a fan-out with no timeout at all. So the slow send here never completes —
/// not "completes late" — and the fast ones complete immediately, which no
/// timing accident can reverse.
/// </para>
/// </remarks>
public sealed class BackpressureTests
{
    private static DocumentBroadcaster Broadcaster(FakeTimeProvider time, TimeSpan? sendTimeout = null) =>
        new(
            new BackpressureOptions { SendTimeout = sendTimeout ?? TimeSpan.FromSeconds(5) },
            time);

    [Fact]
    public async Task A_connection_that_never_drains_does_not_hold_up_the_others()
    {
        // The whole point of §8's rule. Without the timeout, this fan-out never
        // returns and every other client on the document stops receiving.
        var time = new FakeTimeProvider();
        var broadcaster = Broadcaster(time);

        var stuck = new TaskCompletionSource();
        var delivered = new List<string>();
        var closed = new List<string>();

        var fanOut = broadcaster.FanOutAsync(
            ["fast-one", "slow", "fast-two"],
            async (connection, token) =>
            {
                if (connection == "slow")
                {
                    // Never completes on its own. Only the timeout can end it.
                    await using var registration = token.Register(() => stuck.TrySetCanceled(token));
                    await stuck.Task;
                    return;
                }

                lock (delivered)
                {
                    delivered.Add(connection);
                }
            },
            connection =>
            {
                closed.Add(connection);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(6));
        var result = await fanOut;

        Assert.Equal(2, result.Delivered);
        Assert.Equal(1, result.Dropped);
        Assert.Equal(["fast-one", "fast-two"], [.. delivered.Order()]);
        Assert.Equal(["slow"], closed);
    }

    [Fact]
    public async Task A_connection_within_the_timeout_is_not_dropped()
    {
        // The other half. Without it, "drops the slow one" would be satisfied
        // by a fan-out that drops everyone, and every assertion above would
        // still hold.
        var time = new FakeTimeProvider();
        var broadcaster = Broadcaster(time);
        var closed = new List<string>();

        var result = await broadcaster.FanOutAsync(
            ["a", "b", "c"],
            (_, _) => Task.CompletedTask,
            connection =>
            {
                closed.Add(connection);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Delivered);
        Assert.Equal(0, result.Dropped);
        Assert.Empty(closed);
        Assert.Equal(0, broadcaster.DroppedForBackpressure);
    }

    [Fact]
    public async Task Dropping_is_counted()
    {
        // §13.15. A fan-out that drops slow clients and one that never drops
        // any produce the same document, so the count is the only thing that
        // tells them apart — and a client quietly reconnecting in a loop is
        // invisible until someone complains.
        var time = new FakeTimeProvider();
        var broadcaster = Broadcaster(time);

        for (var round = 0; round < 3; round++)
        {
            var stuck = new TaskCompletionSource();
            var fanOut = broadcaster.FanOutAsync(
                ["slow"],
                async (_, token) =>
                {
                    await using var registration = token.Register(() => stuck.TrySetCanceled(token));
                    await stuck.Task;
                },
                _ => Task.CompletedTask,
                TestContext.Current.CancellationToken);

            time.Advance(TimeSpan.FromSeconds(6));
            await fanOut;
        }

        Assert.Equal(3, broadcaster.DroppedForBackpressure);
    }

    [Fact]
    public async Task A_send_that_fails_outright_does_not_stop_the_rest()
    {
        // A connection that has already gone away throws rather than hanging.
        // It is not delivered and the fan-out continues, because one client
        // closing its laptop is not a reason for everyone else to stop
        // receiving.
        var time = new FakeTimeProvider();
        var broadcaster = Broadcaster(time);
        var delivered = new List<string>();

        var result = await broadcaster.FanOutAsync(
            ["gone", "here"],
            (connection, _) =>
            {
                if (connection == "gone")
                {
                    return Task.FromException(new IOException("connection reset"));
                }

                delivered.Add(connection);
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Delivered);
        Assert.Equal(1, result.Dropped);
        Assert.Equal(["here"], delivered);
    }

    [Fact]
    public async Task Sends_are_started_together_rather_than_one_after_another()
    {
        // Awaiting each send before starting the next would make every
        // connection wait for the slowest one ahead of it — the same stall,
        // moved from one client to all the ones behind it in the list. The slow
        // connection is first here, so a sequential implementation would leave
        // the other two unstarted until the timeout expired.
        var time = new FakeTimeProvider();
        var broadcaster = Broadcaster(time);
        var started = new List<string>();

        var stuck = new TaskCompletionSource();
        var fanOut = broadcaster.FanOutAsync(
            ["slow", "b", "c"],
            async (connection, token) =>
            {
                lock (started)
                {
                    started.Add(connection);
                }

                if (connection == "slow")
                {
                    await using var registration = token.Register(() => stuck.TrySetCanceled(token));
                    await stuck.Task;
                }
            },
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        // Every send has been started before the clock moves at all.
        Assert.Equal(3, started.Count);

        time.Advance(TimeSpan.FromSeconds(6));
        var result = await fanOut;

        Assert.Equal(2, result.Delivered);
    }
}
