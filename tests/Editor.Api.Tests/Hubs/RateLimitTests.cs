using Editor.Domain;
using Editor.Infrastructure.Ingest;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using IRedis = StackExchange.Redis.IConnectionMultiplexer;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// §7's submission rate limits: code points per interval, across instances.
/// </summary>
/// <remarks>
/// The vacuity risks, named before these were written.
/// <para>
/// <b>First, and the one §7 forbids by name: a limiter that counts messages
/// passes every test written with single-character operations.</b> Every other
/// test in this project types one character at a time, so a budget of N is
/// indistinguishable from N messages against all of them. The first test here
/// spends one budget as a single run and the same budget as one-character
/// batches, and asserts they cost the same — which is false under a message
/// counter, where the run user's second message is still well inside a cap of
/// 32.
/// </para><para>
/// <b>Second: "across instances" is satisfied by a shared Redis that nothing
/// reads back.</b> A limiter that writes counters to Redis and decides from a
/// field on itself passes a two-instance test in this process, because two
/// <see cref="EditorApiFactory"/> instances share a CLR — a static counter
/// would be shared by accident and look like a working backplane. So the
/// cross-instance test deletes the counters out of Redis and asserts the
/// refusal <i>lifts</i>: state the server does not read cannot be removed by
/// deleting it.
/// </para><para>
/// <b>Third: a refusal is not a refusal if the write happened anyway.</b> §7
/// says return a structured throttle response, and a limiter charged after the
/// append would return exactly that response while still writing every
/// operation. The rows are counted.
/// </para><para>
/// Each test gets its own key prefix. Not tidiness: the fixture's Redis is
/// shared with every other test in the collection, and a budget of 32 is small
/// enough that a stray counter from elsewhere would decide the result.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class RateLimitTests
{
    private readonly EditorFixture _fixture;

    public RateLimitTests(EditorFixture fixture) => _fixture = fixture;

    private static Dictionary<string, string?> Budget(
        string prefix, int perUser, int perConnection, string interval = "00:00:10") =>
        new()
        {
            ["RateLimits:KeyPrefix"] = $"rate:{prefix}:{Guid.NewGuid():N}:",
            ["RateLimits:CodePointsPerUser"] = perUser.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["RateLimits:CodePointsPerConnection"] = perConnection.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["RateLimits:Interval"] = interval,
        };

    [Fact]
    public async Task A_run_and_the_same_text_typed_one_character_at_a_time_cost_the_same()
    {
        // §7: "a single 256-code-point run and 256 single-character inserts
        // must consume the same budget". 32 rather than 256 so the typing side
        // is 32 round trips instead of 256; the number is not what is under
        // test, the unit is.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(
            _fixture, settings: Budget("unit", perUser: 32, perConnection: 32));

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-unit");
        await DocumentSetup.GrantAsync(factory, documentId, "paster", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "typist", Role.Editor);

        await using var paster = await DocumentClient.JoinAsync(factory, "paster", documentId);
        await using var typist = await DocumentClient.JoinAsync(factory, "typist", documentId);

        // One message, one record, 32 code points.
        var pasted = await paster.SubmitAsync(paster.Writer.Type(new string('a', 32)));
        Assert.Null(pasted.Code);
        Assert.Equal(32, pasted.Accepted);

        // The same 32 code points, one message each.
        for (var i = 0; i < 32; i++)
        {
            Assert.Null((await typist.SubmitAsync(typist.Writer.Type("b"))).Code);
        }

        // Both are now exactly at budget, and the next code point costs each of
        // them the same thing. Under a message counter the paster has spent 1
        // of 32 and this line is green; that is the bypass.
        Assert.Equal(IngestRejection.RateLimited, (await paster.SubmitAsync()).Code);
        Assert.Equal(IngestRejection.RateLimited, (await typist.SubmitAsync()).Code);
    }

    [Fact]
    public async Task A_throttled_batch_is_refused_with_a_code_a_delay_and_no_write()
    {
        // §7: return a structured throttle response, do not silently drop. All
        // three parts: the code, the delay the client is told to wait, and the
        // absence of the write — a limiter charged after the append returns the
        // same code while persisting everything it claims to have refused.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(
            _fixture, settings: Budget("structured", perUser: 8, perConnection: 8));

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-structured");
        await DocumentSetup.GrantAsync(factory, documentId, "over", Role.Editor);
        await using var client = await DocumentClient.JoinAsync(factory, "over", documentId);

        Assert.Null((await client.SubmitAsync(client.Writer.Type(new string('a', 8)))).Code);

        var refused = await client.SubmitAsync(client.Writer.Type(new string('b', 4)));

        Assert.Equal(IngestRejection.RateLimited, refused.Code);
        Assert.Equal(0, refused.Accepted);

        // A delay inside the window it belongs to. Zero would leave the client
        // inventing one, and anything past the interval would be a number the
        // limiter cannot have measured.
        Assert.InRange(refused.RetryAfterMs, 1, (long)TimeSpan.FromSeconds(10).TotalMilliseconds);

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();
        var rows = await context.DocumentOperations
            .CountAsync(row => row.DocumentId == documentId, TestContext.Current.CancellationToken);

        // The eight that were accepted, and not one of the four that were not.
        Assert.Equal(8, rows);
    }

    [Fact]
    public async Task The_window_rolls_over_and_the_same_client_writes_again()
    {
        // Without this the limiter could be a permanent ban wearing a throttle's
        // clothes, and every other test here would still pass — none of them
        // waits.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(
            _fixture, settings: Budget("rollover", perUser: 4, perConnection: 4, interval: "00:00:02"));

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-rollover");
        await DocumentSetup.GrantAsync(factory, documentId, "waiter", Role.Editor);
        await using var client = await DocumentClient.JoinAsync(factory, "waiter", documentId);

        Assert.Null((await client.SubmitAsync(client.Writer.Type(new string('a', 4)))).Code);

        // Kept, because the retry is the same bytes. A refused batch has
        // already spent its sequence numbers on the client, and a client that
        // built a fresh batch instead would leave a hole the server refuses
        // with sequence_gap — which is what §9's "the same batch, later"
        // recovery means and what SyncController does, holding the batch at the
        // head of the outbox rather than rebuilding it.
        var again = client.Writer.Type("b");

        var refused = await client.SubmitAsync(again);
        Assert.Equal(IngestRejection.RateLimited, refused.Code);

        // The server's own number, not a guess. Waiting a fixed three seconds
        // would pass against a limiter whose delay is nonsense.
        await Task.Delay(
            TimeSpan.FromMilliseconds(refused.RetryAfterMs + 250), TestContext.Current.CancellationToken);

        // Accepted on the retry, and accepted whole: the refusal cost the
        // client nothing but time, which is the difference between a throttle
        // and a drop.
        var accepted = await client.SubmitAsync(again);
        Assert.Null(accepted.Code);
        Assert.Equal(1, accepted.Accepted);
    }

    [Fact]
    public async Task The_user_budget_holds_across_two_connections_that_are_each_inside_their_own()
    {
        // The per-user limit, isolated: neither connection comes near its own
        // budget of 30, and together they pass the user's 40. A limiter with
        // only a per-connection counter — the easy implementation, since the
        // connection id is right there — passes every other test in this file.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(
            _fixture, settings: Budget("per-user", perUser: 40, perConnection: 30));

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-peruser");
        await DocumentSetup.GrantAsync(factory, documentId, "twotabs", Role.Editor);

        await using var first = await DocumentClient.JoinAsync(factory, "twotabs", documentId);
        await using var second = await DocumentClient.JoinAsync(factory, "twotabs", documentId);

        Assert.Null((await first.SubmitAsync(first.Writer.Type(new string('a', 25)))).Code);

        var refused = await second.SubmitAsync(second.Writer.Type(new string('b', 25)));

        Assert.Equal(IngestRejection.RateLimited, refused.Code);
    }

    [Fact]
    public async Task A_connection_over_its_own_budget_leaves_the_users_other_connection_writing()
    {
        // The other direction, and the reason §7 names two limits rather than
        // one: a single runaway tab is stopped without taking the same person's
        // other sessions down with it. A limiter keyed only on the user refuses
        // here, which is the failure this asserts against.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(
            _fixture, settings: Budget("per-connection", perUser: 10_000, perConnection: 16));

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-perconn");
        await DocumentSetup.GrantAsync(factory, documentId, "runaway", Role.Editor);

        await using var loud = await DocumentClient.JoinAsync(factory, "runaway", documentId);
        await using var quiet = await DocumentClient.JoinAsync(factory, "runaway", documentId);

        Assert.Null((await loud.SubmitAsync(loud.Writer.Type(new string('a', 16)))).Code);
        Assert.Equal(IngestRejection.RateLimited, (await loud.SubmitAsync()).Code);

        Assert.Null((await quiet.SubmitAsync(quiet.Writer.Type("b"))).Code);
    }

    [Fact]
    public async Task A_budget_spent_on_one_instance_is_refused_on_the_other()
    {
        // §7: "this cannot be marked done from a single-instance run — the
        // budget is exhausted on one instance and the refusal observed on
        // another, or nothing has been shown." Third occurrence of 3b.2's
        // shape.
        _fixture.RequireBoth();
        var settings = Budget("cross-instance", perUser: 24, perConnection: 10_000);

        await using var here = new EditorApiFactory(_fixture, settings: settings);
        await using var there = new EditorApiFactory(_fixture, settings: settings);

        var documentId = await DocumentSetup.DocumentAsync(here, "owner-cross");
        await DocumentSetup.GrantAsync(here, documentId, "roamer", Role.Editor);

        await using var near = await DocumentClient.JoinAsync(here, "roamer", documentId);
        await using var far = await DocumentClient.JoinAsync(there, "roamer", documentId);

        // Spent entirely on this instance.
        Assert.Null((await near.SubmitAsync(near.Writer.Type(new string('a', 24)))).Code);

        // Refused on the other one, which has seen nothing from this user and
        // whose own per-connection budget is untouched. Reconnecting elsewhere
        // is how a per-process limiter is escaped, and §8 forbids sticky
        // sessions, so landing here is the normal case rather than an attack.
        var blocked = far.Writer.Type("z");
        Assert.Equal(IngestRejection.RateLimited, (await far.SubmitAsync(blocked)).Code);

        // And the counters are what decided it. Two factories share this
        // process, so a static field would be shared by accident and every
        // assertion above would still hold; deleting the keys out of Redis
        // separates a limiter that reads them from one that merely writes them.
        var removed = await ClearAsync(here, settings["RateLimits:KeyPrefix"]!);
        Assert.NotEqual(0, removed);

        Assert.Null((await far.SubmitAsync(blocked)).Code);
    }

    /// <summary>Deletes every counter under a prefix; answers how many.</summary>
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
