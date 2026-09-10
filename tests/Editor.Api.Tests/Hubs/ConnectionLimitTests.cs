using System.Net;
using System.Net.Http.Json;
using Editor.Domain;
using Editor.Infrastructure.Tickets;
using Microsoft.Extensions.DependencyInjection;
using IRedis = StackExchange.Redis.IConnectionMultiplexer;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// §7's per-user connection cap, which is not the per-document replica cap.
/// </summary>
/// <remarks>
/// The vacuity risks, named before these were written.
/// <para>
/// <b>First, and the one §7 names: the per-document replica cap already
/// exists, and against one user on one document the two limits are
/// indistinguishable.</b> Both refuse the next connection with a status. So
/// every test here opens connections across <i>different</i> documents, where
/// only a per-user limit can refuse — and one test does the opposite, opening
/// the same number of connections with the per-user cap raised, because "four
/// connections were refused" is satisfied by a document cap of three.
/// </para><para>
/// <b>Second: the resumption path returns before the replica cap ever runs.</b>
/// Predicted before the code was written and it is why the admission sits above
/// that branch. A test that only opens fresh sessions cannot see it: every
/// assertion below would hold while every reloaded tab in the system went
/// uncounted (§13.32).
/// </para><para>
/// <b>Third: a cap that never gives slots back is a lifetime quota that passes
/// every "the fourth one is refused" test.</b> Closing and reopening is
/// asserted, and so is the count itself, because a slot released twice or never
/// is invisible in a pass/fail on the next connection.
/// </para><para>
/// Each test gets its own key prefix — the fixture's Redis is shared with the
/// rest of the collection and a cap of three is small enough that a stray entry
/// would decide the result.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class ConnectionLimitTests
{
    private readonly EditorFixture _fixture;

    public ConnectionLimitTests(EditorFixture fixture) => _fixture = fixture;

    private static Dictionary<string, string?> Cap(string prefix, int perUser) => new()
    {
        ["ConnectionLimits:KeyPrefix"] = $"conn:{prefix}:{Guid.NewGuid():N}:",
        ["ConnectionLimits:MaxPerUser"] =
            perUser.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    [Fact]
    public async Task A_user_at_their_cap_is_refused_on_a_document_they_have_never_opened()
    {
        // §7: connection limits per user, across documents. Three documents,
        // one connection each, and the fourth document refuses — no replica cap
        // can produce that, because no document here holds more than one.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, settings: Cap("cap", perUser: 3));

        var documents = await DocumentsAsync(factory, "spread", 4);
        var open = new List<DocumentClient>();

        try
        {
            foreach (var documentId in documents[..3])
            {
                open.Add(await DocumentClient.JoinAsync(factory, "spread", documentId));
            }

            Assert.Equal(
                HttpStatusCode.TooManyRequests,
                await NegotiateStatusAsync(factory, "spread", documents[3]));
        }
        finally
        {
            foreach (var client in open)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task The_same_four_documents_are_fine_when_the_per_user_cap_allows_them()
    {
        // The pair, and without it the test above is satisfied by any limit that
        // refuses a fourth connection — including the per-document replica cap
        // misconfigured, or a bug that refuses everyone's fourth. Same four
        // documents, same user, one number changed.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, settings: Cap("headroom", perUser: 8));

        var documents = await DocumentsAsync(factory, "roomy", 4);
        var open = new List<DocumentClient>();

        try
        {
            foreach (var documentId in documents)
            {
                open.Add(await DocumentClient.JoinAsync(factory, "roomy", documentId));
            }

            Assert.Equal(4, open.Count);
        }
        finally
        {
            foreach (var client in open)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_resumed_connection_holds_a_slot_like_any_other()
    {
        // The predicted defect, asserted directly. negotiate's resumption branch
        // returns before the per-document replica cap, so the obvious placement
        // for this check — beside that cap — leaves every reloaded tab
        // uncounted.
        //
        // Cap of one. A fresh connection takes the slot, closes and gives it
        // back, then RESUMES the replica it owned. If resumption is counted, the
        // second document is refused; if it is not, the second document is
        // admitted and this line is green with the cap doing nothing for
        // reloads.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, settings: Cap("resume", perUser: 1));

        var documents = await DocumentsAsync(factory, "reloader", 2);

        Guid replicaId;
        await using (var first = await DocumentClient.JoinAsync(factory, "reloader", documents[0]))
        {
            replicaId = first.Negotiated.ReplicaId;

            // See ReleasedAsync: a connection disposed before the hub finishes
            // OnConnectedAsync never binds, so nothing releases its claim and
            // the resumption below has nothing to resume. One invocation makes
            // the teardown orderly.
            Assert.Null((await first.CatchUpAsync()).Code);
        }

        // Disposing the client returns before the server has finished tearing
        // the connection down, so a resumption attempted immediately loses the
        // race against the outgoing session's own claim and quietly becomes a
        // fresh replica. Waiting for the release is what makes this a test of
        // the connection cap rather than of that race (§13.16).
        await ReleasedAsync(factory, documents[0], replicaId);

        await using var resumed = await DocumentClient.JoinAsync(
            factory, "reloader", documents[0], resume: replicaId);

        Assert.True(resumed.Negotiated.Resumed);

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            await NegotiateStatusAsync(factory, "reloader", documents[1]));
    }

    [Fact]
    public async Task A_tab_that_reconnects_all_afternoon_holds_one_slot()
    {
        // Keyed on the replica rather than on the connection, so a reconnect
        // storm from one client costs one slot. Asserted on the count itself:
        // "the next connection is still allowed" would pass at four slots held
        // under a cap of eight, and the leak would only appear under load.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, settings: Cap("storm", perUser: 8));

        var documents = await DocumentsAsync(factory, "flapper", 1);
        var userId = await factory.CreateUserAsync("flapper", TestContext.Current.CancellationToken);

        Guid replicaId;
        await using (var first = await DocumentClient.JoinAsync(factory, "flapper", documents[0]))
        {
            replicaId = first.Negotiated.ReplicaId;
            Assert.Null((await first.CatchUpAsync()).Code);
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await ReleasedAsync(factory, documents[0], replicaId);

            await using var again = await DocumentClient.JoinAsync(
                factory, "flapper", documents[0], resume: replicaId);

            Assert.True(again.Negotiated.Resumed);

            // A round trip before closing, and it is load-bearing rather than
            // decorative. StartAsync returns once the handshake is done, which
            // is BEFORE the hub has finished OnConnectedAsync — so disposing
            // straight away can cancel the ticket redemption mid-flight, leaving
            // a connection that never bound and therefore never releases
            // anything on the way out. An invocation is dispatched only after
            // OnConnectedAsync completes, so this makes the teardown orderly and
            // the loop a test of slots rather than of that race.
            Assert.Null((await again.CatchUpAsync()).Code);
        }

        var registry = factory.Services.GetRequiredService<IUserConnections>();

        // One slot held while a connection is open, after five negotiates for
        // the same replica. Asserted with the last one still live rather than
        // after it closes: zero-when-closed is also what a cap that never
        // counted anything reports.
        await ReleasedAsync(factory, documents[0], replicaId);
        await using var live = await DocumentClient.JoinAsync(
            factory, "flapper", documents[0], resume: replicaId);

        Assert.Equal(
            1, await registry.HeldAsync(userId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_closed_connection_gives_its_slot_back()
    {
        // Without this the cap is a lifetime quota, and every refusal test above
        // passes against one. A user who has opened and closed their cap's worth
        // of connections has to be able to open another.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, settings: Cap("recycle", perUser: 2));

        var documents = await DocumentsAsync(factory, "closer", 3);
        var userId = await factory.CreateUserAsync("closer", TestContext.Current.CancellationToken);

        await using (var first = await DocumentClient.JoinAsync(factory, "closer", documents[0]))
        await using (var second = await DocumentClient.JoinAsync(factory, "closer", documents[1]))
        {
            Assert.Null((await first.CatchUpAsync()).Code);
            Assert.Null((await second.CatchUpAsync()).Code);

            Assert.Equal(
                HttpStatusCode.TooManyRequests,
                await NegotiateStatusAsync(factory, "closer", documents[2]));
        }

        // Both closed — but disposing a client returns before the server has
        // run its disconnect, so this waits for the slots rather than assuming
        // them. Joining straight away is a coin flip, and one that comes up
        // heads often enough to look green in isolation.
        await SettlesToAsync(factory, userId, 0);

        // The third document is now reachable, on a cap that has already
        // refused it once.
        await using var third = await DocumentClient.JoinAsync(factory, "closer", documents[2]);
        Assert.False(third.Negotiated.Resumed);
    }

    [Fact]
    public async Task A_refused_connection_is_not_charged_for_the_slot_it_did_not_get()
    {
        // The document is at its replica cap, so negotiate refuses with 409 —
        // after the connection slot was already taken. Holding it would charge
        // the caller for a connection the server declined to give them, and the
        // effect is a user whose cap silently shrinks every time they open a
        // full document.
        _fixture.RequireBoth();
        var settings = Cap("refunded", perUser: 4);
        settings["Ingest:MaxReplicasPerDocument"] = "1";

        await using var factory = new EditorApiFactory(_fixture, settings: settings);

        var documents = await DocumentsAsync(factory, "unlucky", 2);
        var userId = await factory.CreateUserAsync("unlucky", TestContext.Current.CancellationToken);
        var registry = factory.Services.GetRequiredService<IUserConnections>();

        await using var holder = await DocumentClient.JoinAsync(factory, "unlucky", documents[0]);

        var before = await registry.HeldAsync(userId, TestContext.Current.CancellationToken);

        Assert.Equal(
            HttpStatusCode.Conflict,
            await NegotiateStatusAsync(factory, "unlucky", documents[0]));

        Assert.Equal(
            before, await registry.HeldAsync(userId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cap_reached_on_one_instance_refuses_on_the_other()
    {
        // §7's limits hold across instances, and §8 forbids sticky sessions, so
        // landing elsewhere is the normal case rather than an attack. Same
        // construction as the rate limits: two factories share this process, so
        // a static counter would be shared by accident and every assertion here
        // would still hold — the counters are deleted out of Redis at the end
        // and the refusal has to lift.
        _fixture.RequireBoth();
        var settings = Cap("cross", perUser: 2);

        await using var here = new EditorApiFactory(_fixture, settings: settings);
        await using var there = new EditorApiFactory(_fixture, settings: settings);

        var documents = await DocumentsAsync(here, "roamer", 3);
        var open = new List<DocumentClient>();

        try
        {
            open.Add(await DocumentClient.JoinAsync(here, "roamer", documents[0]));
            open.Add(await DocumentClient.JoinAsync(here, "roamer", documents[1]));

            Assert.Equal(
                HttpStatusCode.TooManyRequests,
                await NegotiateStatusAsync(there, "roamer", documents[2]));

            var removed = await ClearAsync(there, settings["ConnectionLimits:KeyPrefix"]!);
            Assert.NotEqual(0, removed);

            Assert.Equal(
                HttpStatusCode.OK,
                await NegotiateStatusAsync(there, "roamer", documents[2]));
        }
        finally
        {
            foreach (var client in open)
            {
                await client.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Waits for the outgoing session to let go of its replica claim.
    /// </summary>
    /// <remarks>
    /// Disposing a client returns before the server has run its disconnect, so
    /// a resumption attempted straight afterwards races the session it is
    /// replacing — and loses silently, becoming a fresh replica rather than
    /// failing. Every test here that resumes waits for this first, or it is
    /// testing the race instead of the cap.
    /// </remarks>
    private static async Task ReleasedAsync(
        EditorApiFactory factory, Guid documentId, Guid replicaId)
    {
        // Note for anyone extending this file: every session whose claim is
        // waited on here must make at least one hub invocation before it is
        // disposed. StartAsync returns when the handshake completes, which is
        // before OnConnectedAsync has redeemed the ticket, so a client that
        // connects and closes immediately can have its redemption cancelled —
        // it never binds, OnDisconnectedAsync finds nothing to release, and the
        // claim then sits until its own lifetime expires. That is by design
        // (the lifetime is what stops a dead process stranding a replica) and
        // it outlasts this deadline by a long way.

        var claims = factory.Services.GetRequiredService<IReplicaClaims>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline
            && await claims.IsHeldAsync(documentId, replicaId, TestContext.Current.CancellationToken))
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.False(
            await claims.IsHeldAsync(documentId, replicaId, TestContext.Current.CancellationToken),
            "the previous session never released its replica claim");
    }

    /// <summary>Waits for this user's held slots to reach <paramref name="expected"/>.</summary>
    /// <remarks>
    /// The connection-slot twin of <see cref="ReleasedAsync"/>, and needed for
    /// the same reason: a disposed client has closed its socket, not finished
    /// being torn down on the server. Every "and then it works again" assertion
    /// in this file waits here first.
    /// </remarks>
    private static async Task SettlesToAsync(
        EditorApiFactory factory, Guid userId, long expected)
    {
        var registry = factory.Services.GetRequiredService<IUserConnections>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        long held;

        do
        {
            held = await registry.HeldAsync(userId, TestContext.Current.CancellationToken);
            if (held == expected)
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Fail($"expected {expected} connection slots held, found {held}");
    }

    /// <summary>Creates <paramref name="count"/> documents the subject may edit.</summary>
    private static async Task<Guid[]> DocumentsAsync(
        EditorApiFactory factory, string subject, int count)
    {
        var documents = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            documents[i] = await DocumentSetup.DocumentAsync(factory, $"owner-{subject}-{i}");
            await DocumentSetup.GrantAsync(factory, documents[i], subject, Role.Editor);
        }

        return documents;
    }

    /// <summary>Negotiates and answers with the status, whatever it is.</summary>
    private static async Task<HttpStatusCode> NegotiateStatusAsync(
        EditorApiFactory factory, string subject, Guid documentId)
    {
        using var http = factory.ClientFor(subject);
        using var response = await http.PostAsJsonAsync(
            new Uri($"/documents/{documentId}/negotiate", UriKind.Relative),
            new { replicaId = (Guid?)null },
            TestContext.Current.CancellationToken);

        return response.StatusCode;
    }

    private static async Task<int> ClearAsync(EditorApiFactory factory, string prefix)
    {
        var redis = factory.Services.GetRequiredService<IRedis>();
        var database = redis.GetDatabase();
        var cleared = 0;

        foreach (var endpoint in redis.GetEndPoints())
        {
            foreach (var key in redis.GetServer(endpoint).Keys(pattern: prefix + "*"))
            {
                if (await database.KeyDeleteAsync(key))
                {
                    cleared++;
                }
            }
        }

        return cleared;
    }
}
