using System.Net.Http.Json;
using Editor.Api.Documents;
using Editor.Api.Hubs;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Editor.Api.Tests.Documents;

/// <summary>
/// §5's replica retirement, and the heartbeat that keeps live readers out of it.
/// </summary>
/// <remarks>
/// The vacuity risks, named before these were written.
/// <para>
/// <b>First: a retirement job with nothing to retire passes every test.</b> A
/// <c>WHERE</c> clause matching nothing returns zero and logs nothing, and that
/// is indistinguishable from a job that never ran. It matters more here than
/// anywhere else in this project because of what it composes with: a frontier
/// that never advances also passes every correctness test — convergence still
/// holds, no document is wrong — so a dead retirement job and a stalled frontier
/// produce a fully green suite over a GC that reclaims nothing, forever. Hence
/// the counters, and hence a test that asserts a replica just under the
/// threshold is <i>not</i> retired: without it, "retire everything" passes.
/// </para><para>
/// <b>Second, and the one predicted before the code: <c>last_seen_at</c> is not
/// what it sounds like.</b> It was written by <c>negotiate</c> and by nothing
/// else, so it recorded when a replica connected, not when it was last active. A
/// person reading a document for longer than <c>T_retire</c> would be retired
/// underneath a live socket. That is §13.37's shape arriving in this phase's own
/// new number — the retirement tests all pass at any <c>T_retire</c>, and only a
/// test phrased as the <i>use</i> ("someone keeps a tab open longer than the
/// threshold") can see it. It is register row 27's test for <c>T_retire</c>, and
/// it is in this file rather than deferred because a <c>T_retire</c> that
/// retires a live replica is data loss.
/// </para><para>
/// <b>Third: retirement and the replica claim are two ideas of liveness.</b> A
/// retired replica must not be resumable, or a returning client continues under
/// an id the frontier has already moved past.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class ReplicaRetirementTests
{
    private readonly EditorFixture _fixture;

    public ReplicaRetirementTests(EditorFixture fixture) => _fixture = fixture;

    private static readonly DateTimeOffset Now =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A factory whose clock the test drives.</summary>
    private static EditorApiFactory Frozen(EditorFixture fixture, FakeTimeProvider clock) =>
        new(fixture, configure: services =>
            services.AddSingleton<TimeProvider>(clock));

    [Fact]
    public async Task A_replica_idle_past_T_retire_is_retired_and_one_under_it_is_not()
    {
        // Both halves in one test on purpose. "Retired something" is satisfied
        // by a job that retires everything, and the pair is what pins the
        // threshold to the number rather than to the existence of a WHERE
        // clause.
        _fixture.RequireBoth();
        var clock = new FakeTimeProvider(Now);
        await using var factory = Frozen(_fixture, clock);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-retire");
        var stale = await ReplicaAsync(factory, documentId, "stale", Now - TimeSpan.FromDays(8));
        var recent = await ReplicaAsync(factory, documentId, "recent", Now - TimeSpan.FromDays(6));

        var job = factory.Services.GetRequiredService<ReplicaRetirement>();
        var retired = await job.RetireAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, retired);
        Assert.NotNull(await RetiredAtAsync(factory, stale));
        Assert.Null(await RetiredAtAsync(factory, recent));

        // The counters §13.15 requires, and the reason they are not optional:
        // zero retirements with zero sweeps is a job that never ran, zero with
        // many is a job with nothing to do, and one counter cannot say which.
        Assert.Equal(1, job.Retired);
        Assert.Equal(1, job.Sweeps);
    }

    [Fact]
    public async Task A_sweep_that_retires_nothing_still_reports_that_it_ran()
    {
        // The other half of the counter argument, and the one that catches a
        // job whose timer never fires. Without this, "Retired == 0" is
        // ambiguous in exactly the direction that hides a dead GC.
        _fixture.RequireBoth();
        var clock = new FakeTimeProvider(Now);
        await using var factory = Frozen(_fixture, clock);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-quiet");
        await ReplicaAsync(factory, documentId, "fresh", Now - TimeSpan.FromHours(1));

        var job = factory.Services.GetRequiredService<ReplicaRetirement>();

        Assert.Equal(0, await job.RetireAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, job.Retired);
        Assert.Equal(1, job.Sweeps);
    }

    [Fact]
    public async Task Someone_who_keeps_a_document_open_for_longer_than_T_retire_is_not_retired()
    {
        // REGISTER ROW 27's test for T_retire, phrased as the use rather than
        // the number: a person opens a document and reads it for nine days.
        // Nothing here mentions the threshold, so it keeps working if the
        // threshold moves — and it fails if last_seen_at ever stops meaning
        // "recently active", which is what it meant before the heartbeat
        // existed and what every other test in this file would tolerate.
        _fixture.RequireBoth();
        var clock = new FakeTimeProvider(Now);
        await using var factory = Frozen(_fixture, clock);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-reader");
        await DocumentSetup.GrantAsync(factory, documentId, "reader", Role.Viewer);

        await using var reading = await DocumentClient.JoinAsync(factory, "reader", documentId);
        Assert.Null((await reading.CatchUpAsync()).Code);

        var heartbeat = factory.Services.GetRequiredService<ReplicaHeartbeat>();
        var job = factory.Services.GetRequiredService<ReplicaRetirement>();

        // Nine days of reading and never typing. The heartbeat is driven
        // directly rather than waited for; what is under test is that it keeps
        // the replica alive, not that PeriodicTimer fires.
        for (var day = 0; day < 9; day++)
        {
            clock.Advance(TimeSpan.FromDays(1));
            Assert.Equal(1, await heartbeat.BeatAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await job.RetireAsync(TestContext.Current.CancellationToken));
        }

        Assert.Null(await RetiredAtAsync(factory, reading.Negotiated.ReplicaId));

        // No counterfactual here, deliberately. "And without the heartbeat it
        // WOULD be retired" cannot be shown from inside this test: the
        // heartbeat is a hosted service, it is running, and the connection is
        // still open — advancing the clock only makes it beat again. The
        // counterfactual belongs to the sabotage run, where the heartbeat is
        // removed and this test is the one that goes red.
    }

    [Fact]
    public async Task The_sweep_runs_on_its_own_timer_without_anyone_calling_it()
    {
        // Every other test in this file drives RetireAsync directly, which
        // proves the sweep works and says nothing about whether it ever runs —
        // they would all pass with the hosted service unregistered, and the
        // symptom in production would be no GC at all, silently, forever. This
        // is the §13.19 hole in this file's own tests: the mechanism is correct
        // and unreachable.
        //
        // So: nobody calls anything. The clock moves, the timer fires, and the
        // counter has to move on its own.
        _fixture.RequireBoth();
        var clock = new FakeTimeProvider(Now);
        await using var factory = Frozen(_fixture, clock);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-timer");
        var stale = await ReplicaAsync(factory, documentId, "timer", Now - TimeSpan.FromDays(8));

        var job = factory.Services.GetRequiredService<ReplicaRetirement>();
        Assert.Equal(0, job.Sweeps);

        clock.Advance(TimeSpan.FromHours(2));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && job.Sweeps == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(job.Sweeps > 0, "the retirement service never swept on its own timer");
        Assert.NotNull(await RetiredAtAsync(factory, stale));
    }

    [Fact]
    public async Task The_heartbeat_touches_only_connections_this_instance_holds()
    {
        // A heartbeat that refreshed every row would keep the whole table out
        // of retirement and produce exactly the frontier that never advances.
        // Two replicas, one connected.
        _fixture.RequireBoth();
        var clock = new FakeTimeProvider(Now);
        await using var factory = Frozen(_fixture, clock);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-scope");
        await DocumentSetup.GrantAsync(factory, documentId, "connected", Role.Editor);
        var absent = await ReplicaAsync(factory, documentId, "absent", Now - TimeSpan.FromDays(8));

        await using var live = await DocumentClient.JoinAsync(factory, "connected", documentId);
        Assert.Null((await live.CatchUpAsync()).Code);

        clock.Advance(TimeSpan.FromMinutes(10));
        var heartbeat = factory.Services.GetRequiredService<ReplicaHeartbeat>();

        Assert.Equal(1, await heartbeat.BeatAsync(TestContext.Current.CancellationToken));

        var job = factory.Services.GetRequiredService<ReplicaRetirement>();
        Assert.Equal(1, await job.RetireAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(await RetiredAtAsync(factory, absent));
        Assert.Null(await RetiredAtAsync(factory, live.Negotiated.ReplicaId));
    }

    [Fact]
    public async Task A_retired_replica_cannot_be_resumed()
    {
        // §5: a retired replica that comes back is told to resync from a
        // snapshot, because the frontier may have moved past state it holds.
        // Resumption is the path that would let it continue as though nothing
        // happened, and negotiate's resumability check is what refuses.
        _fixture.RequireBoth();
        var clock = new FakeTimeProvider(Now);
        await using var factory = Frozen(_fixture, clock);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-return");
        await DocumentSetup.GrantAsync(factory, documentId, "returner", Role.Editor);

        Guid replicaId;
        await using (var before = await DocumentClient.JoinAsync(factory, "returner", documentId))
        {
            replicaId = before.Negotiated.ReplicaId;
            Assert.Null((await before.CatchUpAsync()).Code);
        }

        // Disposing returns before the server has finished tearing the
        // connection down, and until it has, the heartbeat still counts this
        // replica as live — so advancing the clock here would refresh the row
        // this test is trying to age out. Wait for the connection to be gone
        // from the instance's own registry first.
        await ClosedAsync(factory, replicaId);

        // Advancing eight days also fires the hosted retirement service's own
        // timer, so the row may already be retired by the time this sweep runs.
        // Assert the state rather than this sweep's return: a manual sweep
        // racing the background one is a fact about the test, and "how many did
        // THIS call retire" is the wrong question when both are working.
        clock.Advance(TimeSpan.FromDays(8));
        await factory.Services.GetRequiredService<ReplicaRetirement>()
            .RetireAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(await RetiredAtAsync(factory, replicaId));

        await using var after = await DocumentClient.JoinAsync(
            factory, "returner", documentId, resume: replicaId);

        // A fresh replica, not a refusal: §7 makes a declined resumption mint a
        // new id rather than fail, because a client whose replica was retired
        // needs a working session rather than a status it cannot act on.
        Assert.False(after.Negotiated.Resumed);
        Assert.NotEqual(replicaId, after.Negotiated.ReplicaId);
    }

    [Fact]
    public async Task The_declined_resumption_reaches_the_client_in_the_shape_it_reads()
    {
        // §9's offline-window discard, at the seam. The client's whole discard
        // is keyed on two fields of negotiate's JSON: `resumed` being false and
        // `replicaId` differing from what it asked for. Every other test of
        // this path — the C# ones through a typed record, the TypeScript ones
        // through a fake transport — asserts against a shape it declared
        // itself, so a rename or a casing change on either side would leave all
        // of them green and the discard silently dead in the browser.
        //
        // So this reads the raw body. It is the one assertion in the pair that
        // neither side could satisfy alone.
        _fixture.RequireBoth();
        var clock = new FakeTimeProvider(Now);
        await using var factory = Frozen(_fixture, clock);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-shape");
        await DocumentSetup.GrantAsync(factory, documentId, "shape", Role.Editor);

        Guid replicaId;
        await using (var before = await DocumentClient.JoinAsync(factory, "shape", documentId))
        {
            replicaId = before.Negotiated.ReplicaId;
            Assert.Null((await before.CatchUpAsync()).Code);
        }

        await ClosedAsync(factory, replicaId);

        clock.Advance(TimeSpan.FromDays(8));
        await factory.Services.GetRequiredService<ReplicaRetirement>()
            .RetireAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(await RetiredAtAsync(factory, replicaId));

        using var http = factory.ClientFor("shape");
        using var response = await http.PostAsJsonAsync(
            new Uri($"/documents/{documentId}/negotiate", UriKind.Relative),
            new { replicaId },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var json = System.Text.Json.JsonDocument.Parse(body);

        // The field names the client reads, verbatim, and their values.
        Assert.False(json.RootElement.GetProperty("resumed").GetBoolean());
        Assert.NotEqual(
            replicaId, json.RootElement.GetProperty("replicaId").GetGuid());
    }

    /// <summary>Waits until this instance no longer holds the replica's connection.</summary>
    /// <remarks>
    /// The heartbeat refreshes whatever <see cref="DocumentConnections"/> says
    /// is live, so a test that ages a replica out has to know the connection is
    /// really gone rather than merely disposed on the client side.
    /// </remarks>
    private static async Task ClosedAsync(EditorApiFactory factory, Guid replicaId)
    {
        var connections = factory.Services.GetRequiredService<DocumentConnections>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline
            && connections.Held().Any(held => held.ReplicaId == replicaId))
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.DoesNotContain(connections.Held(), held => held.ReplicaId == replicaId);
    }

    /// <summary>Inserts a replica row with a chosen <c>last_seen_at</c>.</summary>
    private static async Task<Guid> ReplicaAsync(
        EditorApiFactory factory, Guid documentId, string subject, DateTimeOffset lastSeen)
    {
        var userId = await factory.CreateUserAsync(subject, TestContext.Current.CancellationToken);
        var replicaId = Guid.CreateVersion7();

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        context.DocumentReplicas.Add(new DocumentReplica
        {
            DocumentId = documentId,
            ReplicaId = replicaId,
            UserId = userId,
            LastSeenAt = lastSeen,
            OperationCount = 0,
            RetiredAt = null,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return replicaId;
    }

    private static async Task<DateTimeOffset?> RetiredAtAsync(
        EditorApiFactory factory, Guid replicaId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        return await context.DocumentReplicas
            .Where(replica => replica.ReplicaId == replicaId)
            .Select(replica => replica.RetiredAt)
            .SingleAsync(TestContext.Current.CancellationToken);
    }
}
