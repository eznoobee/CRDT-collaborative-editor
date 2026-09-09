using System.Net;
using System.Net.Http.Json;
using Editor.Api.Documents;
using Editor.Api.Tests.Hubs;

namespace Editor.Api.Tests.Documents;

/// <summary>
/// <c>GET /me</c>: the input §9's grant needs and nothing in the product
/// produced.
/// </summary>
/// <remarks>
/// A grant names a user id. There is no directory, so an owner learns one by
/// being given it, and the person being invited had no way to read their own —
/// which makes the grant path unusable rather than merely inconvenient. That is
/// register row 15's shape a second time, and it was found by trying to use the
/// path rather than by reading it.
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class IdentityEndpointTests
{
    private static readonly Uri Me = new("/me", UriKind.Relative);

    private readonly EditorFixture _fixture;

    public IdentityEndpointTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_anonymous_call_is_refused_by_the_real_scheme()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, testAuthentication: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Me, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task It_answers_the_id_that_a_grant_names()
    {
        // Asserted against the id the rest of the API uses, not against itself.
        // An endpoint returning a fresh guid each call would satisfy "returns a
        // user id" and be useless for the only thing it is for.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("me-owner");
        using var invitee = factory.ClientFor("me-invitee");

        var mine = await invitee.GetFromJsonAsync<Identity>(Me, TestContext.Current.CancellationToken);
        Assert.NotNull(mine);

        using var created = await owner.PostAsJsonAsync(
            new Uri("/documents", UriKind.Relative),
            new CreateDocumentRequest("Shared"),
            TestContext.Current.CancellationToken);

        var document = await created.Content.ReadFromJsonAsync<DocumentSummary>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(document);

        using var granted = await owner.PutAsJsonAsync(
            new Uri($"/documents/{document.Id}/members/{mine.UserId}", UriKind.Relative),
            new GrantRoleRequest(Editor.Domain.Role.Editor),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);

        // The grant landed on the person who read that id, which is the whole
        // claim: they can now reach the document.
        var reachable = await invitee.GetFromJsonAsync<List<DocumentSummary>>(
            new Uri("/documents", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.NotNull(reachable);
        Assert.Contains(reachable, entry => entry.Id == document.Id);
    }

    [Fact]
    public async Task Two_subjects_are_two_identities()
    {
        // §6: identity is (issuer, subject). Two callers sharing one id would
        // make every grant in the system a grant to both.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var first = factory.ClientFor("me-first");
        using var second = factory.ClientFor("me-second");

        var one = await first.GetFromJsonAsync<Identity>(Me, TestContext.Current.CancellationToken);
        var two = await second.GetFromJsonAsync<Identity>(Me, TestContext.Current.CancellationToken);

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.NotEqual(one.UserId, two.UserId);
    }

    [Fact]
    public async Task The_same_subject_is_the_same_identity_across_calls()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var client = factory.ClientFor("me-stable");

        var first = await client.GetFromJsonAsync<Identity>(Me, TestContext.Current.CancellationToken);
        var again = await client.GetFromJsonAsync<Identity>(Me, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(again);
        Assert.Equal(first.UserId, again.UserId);
    }
}
