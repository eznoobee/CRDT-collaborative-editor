using System.Net;
using System.Net.Http.Json;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Ingest;

namespace Editor.Api.Tests.Documents;

/// <summary>
/// §7's rate limits on the document API (register row 22).
/// </summary>
/// <remarks>
/// The vacuity risks, named before these were written.
/// <para>
/// <b>First: a limit applied to the endpoints someone remembered.</b> This is
/// 5b.4's shape and the reason the filter is on the route group rather than on
/// three handlers. A test that exercises <c>POST /documents</c> proves nothing
/// about the two membership routes, and an unlimited endpoint looks exactly
/// like an endpoint that does not need limiting. So the write routes are
/// enumerated and each is driven past the budget in turn — and the enumeration
/// is written out here rather than derived from the endpoint table, because a
/// list derived from what is mapped cannot notice a route that was mapped
/// somewhere else.
/// </para><para>
/// <b>Second: reads are deliberately unbilled, which is indistinguishable from
/// reads being forgotten.</b> §7's abuse list names the writes. One test drives
/// a read well past the write budget and asserts it still answers, so the
/// exclusion is a decision this suite records rather than a gap it hides.
/// </para><para>
/// <b>Third: the budget is per user, and a test using one identity cannot tell
/// that from a global one.</b> A second user writes freely while the first is
/// refused.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class DocumentApiRateLimitTests
{
    private readonly EditorFixture _fixture;

    public DocumentApiRateLimitTests(EditorFixture fixture) => _fixture = fixture;

    private static Dictionary<string, string?> Budget(string prefix, int writes) => new()
    {
        ["DocumentApiRateLimits:KeyPrefix"] = $"apirate:{prefix}:{Guid.NewGuid():N}:",
        ["DocumentApiRateLimits:WritesPerUser"] =
            writes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["DocumentApiRateLimits:Interval"] = "00:01:00",
    };

    /// <summary>Every route on the document API that writes, and how to call it.</summary>
    public static TheoryData<string, string> WriteRoutes()
    {
        var document = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();

        return new TheoryData<string, string>
        {
            { "POST", "/documents" },
            { "PUT", $"/documents/{document}/members/{member}" },
            { "DELETE", $"/documents/{document}/members/{member}" },
        };
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task Every_write_route_is_limited(string method, string route)
    {
        // The ids in the route belong to nothing, so each call answers 404 —
        // and that is the point rather than a compromise. §7's limit has to
        // charge before the handler decides, or the cheapest abuse (a loop of
        // calls that will all be refused) is the one thing left unbounded.
        // Every call here still costs a role lookup and a Postgres round trip.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(
            _fixture, settings: Budget($"route-{method}", writes: 3));

        using var client = factory.ClientFor($"writer-{method}-{Guid.NewGuid():N}");

        for (var i = 0; i < 3; i++)
        {
            using var allowed = await SendAsync(client, method, route);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        using var refused = await SendAsync(client, method, route);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains(
            IngestRejection.RateLimited,
            await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        // The delay is the server's, and in seconds because this is HTTP.
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.InRange(refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
    }

    [Fact]
    public async Task The_budget_is_shared_across_the_write_routes_rather_than_per_route()
    {
        // Three budgets of three would pass the test above for every route and
        // still leave a caller nine writes a minute. §7 limits the caller, not
        // the URL.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, settings: Budget("shared", writes: 2));

        var document = Guid.CreateVersion7();
        using var client = factory.ClientFor("spender");

        using (var first = await SendAsync(client, "POST", "/documents"))
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        }

        using (var second = await SendAsync(
            client, "DELETE", $"/documents/{document}/members/{Guid.CreateVersion7()}"))
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        }

        // Two spent across two different routes, and the third is refused
        // wherever it goes.
        using var third = await SendAsync(
            client, "PUT", $"/documents/{document}/members/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Reading_is_not_billed_against_the_write_budget()
    {
        // §7's abuse list names the writes. Asserted rather than assumed,
        // because "reads are excluded on purpose" and "reads were forgotten"
        // look identical in the code and only one of them is a decision.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, settings: Budget("reads", writes: 2));

        using var client = factory.ClientFor("reader");

        for (var i = 0; i < 8; i++)
        {
            using var listed = await SendAsync(client, "GET", "/documents");
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        }

        // And the write budget is still whole, so the reads did not spend it.
        using var written = await SendAsync(client, "POST", "/documents");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, written.StatusCode);
    }

    [Fact]
    public async Task One_user_spending_their_budget_leaves_another_users_alone()
    {
        // Per user, not global. A limiter keyed on nothing passes every test
        // above and takes the whole deployment down when one account misbehaves.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, settings: Budget("peruser", writes: 2));

        using var loud = factory.ClientFor("loud");
        using var quiet = factory.ClientFor("quiet");

        for (var i = 0; i < 3; i++)
        {
            using var _ = await SendAsync(loud, "POST", "/documents");
        }

        using var refused = await SendAsync(loud, "POST", "/documents");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        using var allowed = await SendAsync(quiet, "POST", "/documents");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    [Fact]
    public async Task A_budget_spent_on_one_instance_is_refused_on_the_other()
    {
        // §7's limits hold across instances, and the document API is reached
        // through the same load balancer as everything else.
        _fixture.RequireBoth();
        var settings = Budget("cross", writes: 2);

        await using var here = new EditorApiFactory(_fixture, settings: settings);
        await using var there = new EditorApiFactory(_fixture, settings: settings);

        using var first = here.ClientFor("roamer-api");
        using var second = there.ClientFor("roamer-api");

        for (var i = 0; i < 2; i++)
        {
            using var _ = await SendAsync(first, "POST", "/documents");
        }

        using var refused = await SendAsync(second, "POST", "/documents");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, string method, string route)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);

        if (method is "POST")
        {
            request.Content = JsonContent.Create(new { title = "rate limited" });
        }
        else if (method is "PUT")
        {
            request.Content = JsonContent.Create(new { role = Role.Editor });
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
