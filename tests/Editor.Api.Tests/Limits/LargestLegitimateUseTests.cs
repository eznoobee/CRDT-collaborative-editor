using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Editor.Api.Hubs;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Ingest;
using Editor.Infrastructure.Tickets;
using Microsoft.Extensions.DependencyInjection;
using IRedis = StackExchange.Redis.IConnectionMultiplexer;

namespace Editor.Api.Tests.Limits;

/// <summary>
/// §13.37's standing technique, for every configured limit (register row 27).
/// </summary>
/// <remarks>
/// <para>
/// Every other test of a limit in this repository proves the limit
/// <b>enforces</b>: over the number is refused, under it is accepted. All of
/// them pass when the number is in the wrong place, because they are written in
/// terms of the number. <b>The mechanism being right and the number being right
/// are two claims</b>, and until this file only the first was tested.
/// </para><para>
/// So: one test per limit, performing the largest thing a real user
/// legitimately does, asserting it succeeds. The numbers come from
/// <see cref="Use"/> — a paragraph, a workshop, a long weekend — and from the
/// client's own batching, never from the server's configuration. A test that
/// computed its input from <c>MaxDocumentBytes</c> would be comparing the
/// number to itself and would pass at any setting.
/// </para><para>
/// <b>The mechanical check that keeps them honest.</b> §13.37: if a test here
/// would still pass after the configured number is halved, it is testing the
/// wrong thing. The halving pass and what each limit did under it are recorded
/// in <c>docs/limit-headroom.md</c>, run through <c>scripts/sabotage.sh</c>.
/// Two limits survive halving for real reasons, and the document says which and
/// why rather than leaving them looking checked.
/// </para><para>
/// <b>Nothing here configures the limit it is about.</b> Two tests configure a
/// <i>different</i> limit out of the way, both with a stated reason, which is
/// §13.37's second half: raising a limit so a red test goes green is how a
/// control is quietly disabled, and doing it in a test that is about another
/// cap, in writing, with the limit's own tests untouched, is scoping. Key
/// prefixes are randomised per test — the fixture's Redis is shared with the
/// whole collection and a stray counter would decide a result — which is
/// isolation rather than a change to any number.
/// </para>
/// <para>
/// The vacuity risks, named before these were written.
/// </para><para>
/// <b>First: a use test that stays under half the limit tells you nothing.</b>
/// That is the whole failure mode §13.37 describes, arriving in a new costume.
/// The halving pass is the check, and where a limit has so much headroom that
/// halving changes nothing a person does, the honest answer is to record that
/// the number is untested by use rather than to invent a user who needs it.
/// </para><para>
/// <b>Second: "the server accepted it" can mean the server wrote nothing.</b>
/// Every submission here asserts the accepted operation count, not just the
/// absence of a rejection code — a limiter charged after the append returns
/// the same null code while persisting or dropping whatever it likes.
/// </para><para>
/// <b>Third: a paste split into 256s by the test is a test of the test.</b> The
/// chunk size below is the <i>client's</i>, taken from
/// <c>DocumentSession.MAX_OPERATIONS_PER_BATCH</c>, because the question these
/// ask is whether the server admits what the shipped client actually sends. It
/// is the one number here that comes from code rather than from a person, and
/// it comes from the other implementation, which is why halving the server's
/// cap still turns these red.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class LargestLegitimateUseTests
{
    private readonly EditorFixture _fixture;

    public LargestLegitimateUseTests(EditorFixture fixture) => _fixture = fixture;

    /// <summary>
    /// What the largest legitimate user does, in the units people do it in.
    /// </summary>
    /// <remarks>
    /// The same set as <c>client/src/limits/use.ts</c>, restated rather than
    /// shared because there is no mechanism to share a constant across the two
    /// languages that is cheaper than writing it twice — and §6's conformance
    /// corpus, which is that mechanism for the format, exists because the
    /// format has to agree byte for byte. These do not: they are two suites
    /// asking the same question of two implementations, and a number that
    /// drifted would be a disagreement about people, visible in review.
    /// </remarks>
    private static class Use
    {
        /// <summary>A pasted paragraph of prose, in code points (~150 words).</summary>
        public const int Paragraph = 900;

        /// <summary>Three pages of prose, in code points. §13.37's own figure.</summary>
        public const int ThreePages = 3_000;

        /// <summary>Tabs one person has the same document open in while pasting.</summary>
        public const int Tabs = 3;

        /// <summary>People in a workshop who all open the one document they were sent.</summary>
        public const int Workshop = 40;

        /// <summary>People a new document is shared with in one sitting.</summary>
        public const int Team = 30;

        /// <summary>Documents one person has open at once.</summary>
        public const int OpenDocuments = 12;

        /// <summary>Seconds a cold first load takes before the ticket is redeemed.</summary>
        public const int ColdLoadSeconds = 20;

        /// <summary>Seconds a reload across a slow network takes before the claim is reused.</summary>
        public const int SlowReloadSeconds = 45;

        /// <summary>Code points in a long document appended to over months (~4,000 words).</summary>
        public const int LongDocument = 25_000;
    }

    /// <summary>
    /// Operations the shipped client puts in one submission.
    /// </summary>
    /// <remarks>
    /// <c>DocumentSession.MAX_OPERATIONS_PER_BATCH</c>. Not the server's
    /// <c>MaxOperationsPerBatch</c>, though they are equal today and have to
    /// be: the client splits here, so this is the size of the batch the server
    /// will actually be asked to accept. Taking the number from the server's
    /// own options would make every test below pass at any setting, which is
    /// the thing §13.37 is about.
    /// </remarks>
    private const int ClientBatch = 256;

    [Fact]
    public async Task A_pasted_paragraph_is_accepted()
    {
        // The action: someone copies a paragraph and pastes it in. The client
        // splits it; the server has to take every piece.
        await using var session = await SessionAsync("paragraph");

        var accepted = await PasteAsync(session, Use.Paragraph);

        Assert.Equal(Use.Paragraph, accepted);
    }

    [Fact]
    public async Task A_three_page_paste_is_accepted_in_one_go()
    {
        // §13.37's own case, and the one that found the rate limit's first
        // default in the wrong place. "In one go" is the requirement: the
        // batches go back to back at the speed the round trips allow, with no
        // pacing, because that is what a paste does.
        await using var session = await SessionAsync("three-pages");

        var accepted = await PasteAsync(session, Use.ThreePages);

        Assert.Equal(Use.ThreePages, accepted);
    }

    [Fact]
    public async Task Pasting_into_three_tabs_at_once_is_accepted()
    {
        // The per-user budget, which the per-connection budget cannot see. One
        // person, three tabs, the same three pages in each — the fan-out one
        // account legitimately produces.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, settings: Isolate("tabs"));

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-tabs");
        await DocumentSetup.GrantAsync(factory, documentId, "tabs", Role.Editor);

        var tabs = new List<DocumentClient>();
        try
        {
            for (var tab = 0; tab < Use.Tabs; tab++)
            {
                tabs.Add(await DocumentClient.JoinAsync(factory, "tabs", documentId));
            }

            // Interleaved rather than one tab after another. A person switching
            // windows produces exactly this, and it is the arrangement in which
            // a per-user budget bites soonest.
            long accepted = 0;
            var text = Prose(Use.ThreePages);
            for (var at = 0; at < text.Length; at += ClientBatch)
            {
                var chunk = text[at..Math.Min(at + ClientBatch, text.Length)];
                foreach (var tab in tabs)
                {
                    var result = await tab.SubmitAsync(tab.Writer.Type(chunk));
                    Assert.Null(result.Code);
                    accepted += result.Accepted;
                }
            }

            Assert.Equal((long)Use.ThreePages * Use.Tabs, accepted);
        }
        finally
        {
            foreach (var tab in tabs)
            {
                await tab.DisposeAsync();
            }

            // Nine thousand un-snapshotted operations, removed for the reason
            // Session.DisposeAsync gives: a laggard this large outranks every
            // other test's document in SnapshotSweeper's global ranking.
            using var owner = factory.ClientFor("owner-tabs");
            using var _ = await owner.DeleteAsync(
                new Uri($"/documents/{documentId}", UriKind.Relative),
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task The_largest_batch_the_client_can_build_is_accepted()
    {
        // The message byte cap. Its case is not a long paste — it is an
        // expensive one: a full client batch whose every code point costs four
        // bytes. A cap reasoned about from ASCII keystrokes is met by a paste
        // of emoji, which is §13.37's shape exactly.
        await using var session = await SessionAsync("largest-batch");

        var expensive = string.Concat(Enumerable.Repeat("\U0001F600", ClientBatch));
        var result = await session.SubmitAsync(session.Writer.Type(expensive));

        Assert.Null(result.Code);
        Assert.Equal(ClientBatch, result.Accepted);
    }

    [Fact]
    public async Task A_workshop_can_all_join_one_document()
    {
        // A class, a team, a workshop: everyone opens the one document they
        // were sent, and everyone is still connected when the last person
        // arrives.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, settings: Isolate("workshop"));

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-workshop");

        var replicas = new HashSet<Guid>();
        for (var person = 0; person < Use.Workshop; person++)
        {
            var subject = $"workshop-{person}";
            await DocumentSetup.GrantAsync(factory, documentId, subject, Role.Editor);

            using var client = factory.ClientFor(subject);
            using var response = await client.PostAsync(
                new Uri($"/documents/{documentId}/negotiate", UriKind.Relative),
                null,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var negotiated = await response.Content.ReadFromJsonAsync<Negotiated>(
                TestContext.Current.CancellationToken);

            Assert.NotNull(negotiated);

            // Distinct ids, so "forty succeeded" cannot be one replica
            // negotiated forty times — which is what a cap counting something
            // other than live replicas would let through.
            Assert.True(replicas.Add(negotiated.ReplicaId));
        }

        Assert.Equal(Use.Workshop, replicas.Count);
    }

    [Fact]
    public async Task Sharing_a_new_document_with_a_team_is_accepted()
    {
        // §13.37's own case for the document API's budget. Creating documents
        // is a slow human act; granting is not — someone sets up a document and
        // invites their team as fast as they can click, and a budget reasoned
        // about from "how often does someone make a document" refuses it.
        //
        // Through HTTP, every call, because the limiter is a route filter: a
        // grant written through IDocumentRoleWriter is not charged and would
        // make this test green against no limiter at all.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, settings: Isolate("team"));

        using var owner = factory.ClientFor("team-owner");

        using var created = await owner.PostAsJsonAsync(
            new Uri("/documents", UriKind.Relative),
            new { title = "Team plan" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var document = await created.Content.ReadFromJsonAsync<CreatedDocument>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(document);

        for (var member = 0; member < Use.Team; member++)
        {
            var memberId = await factory.CreateUserAsync(
                $"team-{member}", TestContext.Current.CancellationToken);

            using var granted = await owner.PutAsJsonAsync(
                new Uri($"/documents/{document.Id}/members/{memberId}", UriKind.Relative),
                new { role = Role.Editor },
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        }
    }

    [Fact]
    public async Task A_dozen_open_documents_survive_a_wake_from_sleep_reconnect()
    {
        // §7's own reasoning for the connection cap, tested: a dozen documents
        // open in a dozen tabs is ordinary work, and a machine waking from
        // sleep reconnects all of them before the server has noticed the old
        // sockets died. So the peak is twice the tabs, not the tabs — which is
        // the half a cap reasoned about from "how many tabs does someone have"
        // misses.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, settings: Isolate("wake"));

        var documents = new List<Guid>();
        for (var i = 0; i < Use.OpenDocuments; i++)
        {
            var documentId = await DocumentSetup.DocumentAsync(factory, $"owner-wake-{i}");
            await DocumentSetup.GrantAsync(factory, documentId, "wake", Role.Editor);
            documents.Add(documentId);
        }

        var open = new List<DocumentClient>();
        try
        {
            // The dozen tabs, before the machine slept.
            foreach (var documentId in documents)
            {
                open.Add(await DocumentClient.JoinAsync(factory, "wake", documentId));
            }

            // The wake. The old sockets are still registered — that is the
            // point — and every tab negotiates again.
            foreach (var documentId in documents)
            {
                open.Add(await DocumentClient.JoinAsync(factory, "wake", documentId));
            }

            Assert.Equal(Use.OpenDocuments * 2, open.Count);

            // And the reconnected tabs can actually work, which "negotiate
            // returned 200" does not establish on its own.
            var last = open[^1];
            var result = await last.SubmitAsync(last.Writer.Type("a"));
            Assert.Null(result.Code);
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
    public async Task A_ticket_outlives_a_cold_first_load()
    {
        // §7 issues the ticket at negotiate and the socket opens after the
        // page has loaded, the bundle has parsed and the token has been
        // acquired. On a cold stack on a slow phone that is a long way from
        // instant, and a ticket that expired in between is a sign-in that ends
        // in a connection failure for no reason the user can see.
        //
        // Measured as the life Redis gave the key rather than by waiting
        // twenty seconds. What that proves is exactly what it says — the ticket
        // is still redeemable that far into the load — and the limitation is
        // recorded in docs/limit-headroom.md rather than dressed up.
        _fixture.RequireBoth();
        var prefix = $"ticket:use:{Guid.NewGuid():N}:";
        await using var factory = new EditorApiFactory(_fixture, settings: new Dictionary<string, string?>
        {
            ["ConnectTicket:KeyPrefix"] = prefix,
        });

        var store = factory.Services.GetRequiredService<IConnectTicketStore>();
        var ticket = await store.IssueAsync(
            new ConnectionBinding(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Role.Editor,
                Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        var redis = factory.Services.GetRequiredService<IRedis>();
        var remaining = await redis.GetDatabase().KeyTimeToLiveAsync(prefix + ticket);

        Assert.NotNull(remaining);
        Assert.True(
            remaining.Value >= TimeSpan.FromSeconds(Use.ColdLoadSeconds),
            $"A cold first load takes about {Use.ColdLoadSeconds}s and the ticket had {remaining.Value}.");

        // And it really does redeem, so the reading above is about a live
        // ticket rather than about a key that happens to carry a TTL.
        Assert.NotNull(await store.RedeemAsync(ticket, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_replica_claim_outlives_a_reload_across_a_slow_network()
    {
        // The claim is taken at negotiate and renewed by the live connection.
        // A reload kills the connection at the moment the page goes away, so
        // the claim has to survive the whole gap on its own — and a claim that
        // lapsed would let the replica be taken, which costs the user the
        // outbox they reloaded with.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var claims = factory.Services.GetRequiredService<IReplicaClaims>();
        var documentId = Guid.CreateVersion7();
        var replicaId = Guid.CreateVersion7();

        var token = await claims.TryClaimAsync(
            documentId, replicaId, TestContext.Current.CancellationToken);

        Assert.NotNull(token);

        var redis = factory.Services.GetRequiredService<IRedis>();
        var remaining = await redis.GetDatabase()
            .KeyTimeToLiveAsync($"editor:replica-claim:{documentId:N}:{replicaId:N}");

        Assert.NotNull(remaining);
        Assert.True(
            remaining.Value >= TimeSpan.FromSeconds(Use.SlowReloadSeconds),
            $"A reload across a slow network takes about {Use.SlowReloadSeconds}s "
                + $"and the claim had {remaining.Value}.");
    }

    [Fact]
    public async Task A_long_document_still_accepts_an_edit()
    {
        // §7's per-document byte cap, approached the way a document reaches it:
        // appended to, over months, until it is a book chapter. The assertion
        // that matters is the last one — that the next ordinary keystroke is
        // still accepted — because a cap set where real documents live would
        // refuse it.
        await using var session = await SessionAsync("long-document", settings: new Dictionary<string, string?>
        {
            // Out of the way, and stated rather than tuned into silence
            // (§13.37's second half). This test writes a book chapter as fast
            // as the round trips allow, which is not a burst §7's submission
            // budget should tolerate and is not what is under test here; the
            // budget's own cases are the paste tests above, and they run with
            // it in force. Halving MaxDocumentBytes still turns this red, which
            // is the property the scoping has to preserve.
            ["RateLimits:CodePointsPerUser"] = "10000000",
            ["RateLimits:CodePointsPerConnection"] = "10000000",
        });

        var written = await PasteAsync(session, Use.LongDocument);
        Assert.Equal(Use.LongDocument, written);

        var next = await session.SubmitAsync(session.Writer.Type("."));
        Assert.Null(next.Code);
        Assert.Equal(1, next.Accepted);
    }

    /// <summary>Submits <paramref name="codePoints"/> of prose the way the client would.</summary>
    /// <returns>The operations the server said it accepted.</returns>
    private static async Task<long> PasteAsync(Session session, int codePoints)
    {
        var text = Prose(codePoints);
        long accepted = 0;

        for (var at = 0; at < text.Length; at += ClientBatch)
        {
            var chunk = text[at..Math.Min(at + ClientBatch, text.Length)];
            var result = await session.SubmitAsync(session.Writer.Type(chunk));

            // Asserted per batch, not at the end: a run that stopped being
            // accepted halfway through would otherwise show up only as a total,
            // and the total is what a retry would eventually reach anyway.
            Assert.Null(result.Code);
            accepted += result.Accepted;
        }

        return accepted;
    }

    /// <summary>Prose of a given length in code points, as a clipboard holds it.</summary>
    private static string Prose(int codePoints)
    {
        // Words rather than one repeated character: run coalescing is
        // length-aware and a string of identical characters is the friendliest
        // possible input to it. Real prose is what has to fit.
        const string Source = "the quick brown fox jumps over a lazy dog and then keeps going ";

        var text = new System.Text.StringBuilder(codePoints + Source.Length);
        while (text.Length < codePoints)
        {
            text.Append(Source);
        }

        return text.ToString(0, codePoints);
    }

    /// <summary>
    /// Redis key prefixes of their own, and not one limit changed.
    /// </summary>
    /// <remarks>
    /// The fixture's Redis is shared with every test in the collection, and a
    /// counter left behind by another test would decide these results. Every
    /// budget stays at its configured default, which is the whole point of the
    /// file.
    /// </remarks>
    private static Dictionary<string, string?> Isolate(string prefix) => new()
    {
        ["RateLimits:KeyPrefix"] = $"rate:use-{prefix}:{Guid.NewGuid():N}:",
        ["DocumentApiRateLimits:KeyPrefix"] = $"apirate:use-{prefix}:{Guid.NewGuid():N}:",
        ["ConnectionLimits:KeyPrefix"] = $"conn:use-{prefix}:{Guid.NewGuid():N}:",
    };

    private async Task<Session> SessionAsync(
        string subject, Dictionary<string, string?>? settings = null)
    {
        _fixture.RequireBoth();

        var configuration = Isolate(subject);
        foreach (var (key, value) in settings ?? [])
        {
            configuration[key] = value;
        }

        var factory = new EditorApiFactory(_fixture, settings: configuration);
        var documentId = await DocumentSetup.DocumentAsync(factory, $"owner-{subject}");
        await DocumentSetup.GrantAsync(factory, documentId, subject, Role.Editor);

        var client = await DocumentClient.JoinAsync(factory, subject, documentId);
        return new Session(factory, client, documentId, $"owner-{subject}");
    }

    /// <summary>
    /// A connected editor that removes its document when it is done.
    /// </summary>
    /// <remarks>
    /// <b>The cleanup is not tidiness.</b> These tests write the largest
    /// documents in the suite — a book chapter, three pages, a paragraph — into
    /// a Postgres shared with every other test in the collection, and
    /// <c>SnapshotSweeper</c> ranks laggards <i>globally</i> and sweeps the top
    /// N. A document left here with twenty-five thousand un-snapshotted
    /// operations outranks every document any other test creates, so it takes a
    /// slot in every sweep from then on and pushes somebody else's document out
    /// of its own batch. <c>PeriodicSnapshotTests</c> went red exactly once that
    /// way while this file was being written, and passed on a re-run, which is
    /// the shape of a test made to depend on what its neighbours left behind.
    /// <para>
    /// Removed through <c>DELETE /documents/{id}</c> — the product's own path,
    /// added in 7.6 — because the laggard query filters on <c>deleted_at</c>
    /// and a row deleted any other way would not be the thing the sweep skips.
    /// The underlying fragility is not fixed by this and is not this task's to
    /// fix: it is register row 37. Widening the sweep's batch until the red
    /// went away was the other option, and that is tuning a control into
    /// silence.
    /// </para>
    /// </remarks>
    private sealed class Session(
        EditorApiFactory factory, DocumentClient client, Guid documentId, string ownerSubject)
        : IAsyncDisposable
    {
        public ReplicaWriter Writer => client.Writer;

        public Task<SubmitResult> SubmitAsync(byte[]? operations = null) =>
            client.SubmitAsync(operations);

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();

            using (var owner = factory.ClientFor(ownerSubject))
            {
                // Best effort: a failure here must not turn a green test red or
                // mask the assertion that actually ran.
                try
                {
                    using var _ = await owner.DeleteAsync(
                        new Uri($"/documents/{documentId}", UriKind.Relative),
                        TestContext.Current.CancellationToken);
                }
                catch (HttpRequestException)
                {
                }
            }

            await factory.DisposeAsync();
        }
    }

    /// <summary>What <c>POST /documents</c> answers with.</summary>
    private sealed record CreatedDocument(Guid Id, string Title);
}
