using Editor.Api.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// Two instances behind one Redis and one Postgres (§8).
/// </summary>
/// <remarks>
/// §8 forbids sticky sessions, so the people editing one document are routinely
/// spread across servers. Every test above this one runs a single instance, and
/// a single instance cannot tell a working backplane from no backplane at all —
/// which is why 3b.2's fan-out was not actually verified until here.
/// <para>
/// The vacuity risks, named before these were written. First and sharpest:
/// <b>convergence is not evidence of delivery</b>. A remote client that missed
/// every broadcast converges perfectly well on its next catch-up, because
/// catch-up reads Postgres and Postgres is shared — so a two-instance test that
/// ends in "both documents match" would pass against a server with the
/// backplane deleted. So no test here calls catch-up before asserting delivery,
/// and each asserts the backplane's own counter, which is the only thing that
/// separates the two paths (§13.15). Second: an instance that re-delivers its
/// own publication produces duplicates, and §5 makes duplicates harmless, so
/// that too is invisible in the document — asserted by counting what arrives,
/// not by comparing state. Third: "two instances" is only true if the clients
/// really landed on different ones, so each client is built against its own
/// factory rather than trusting a load balancer that is not there.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class ScaleOutTests
{
    private readonly EditorFixture _fixture;

    public ScaleOutTests(EditorFixture fixture) => _fixture = fixture;

    private static DocumentBackplane Backplane(EditorApiFactory factory) =>
        factory.Services.GetRequiredService<DocumentBackplane>();

    private static DocumentConnections Connections(EditorApiFactory factory) =>
        factory.Services.GetRequiredService<DocumentConnections>();

    [Fact]
    public async Task A_batch_submitted_on_one_instance_reaches_a_client_on_the_other()
    {
        _fixture.RequireBoth();
        await using var first = new EditorApiFactory(_fixture);
        await using var second = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(first, "owner-scaleout");
        await DocumentSetup.GrantAsync(first, documentId, "near", Role.Editor);
        await DocumentSetup.GrantAsync(first, documentId, "far", Role.Editor);

        await using var near = await DocumentClient.JoinAsync(first, "near", documentId);
        await using var far = await DocumentClient.JoinAsync(second, "far", documentId);

        var batch = near.Writer.Type("across");
        Assert.Null((await near.SubmitAsync(batch)).Code);
        near.ApplyLocal(batch);

        var arrived = await far.NextAsync();

        // The mechanism, not the outcome. Without this the assertions below are
        // satisfied by any route that gets the bytes there, and the route that
        // would silently take over is catch-up from the shared Postgres.
        Assert.Equal(1, Backplane(second).Received);

        // The publishing instance ignores its own message. Acting on it would
        // send every local client a second copy, which §5 absorbs and no
        // comparison of documents would ever show.
        Assert.Equal(0, Backplane(first).Received);

        far.Apply(arrived);

        Assert.Equal("across", far.Replica.Text);
        Assert.Equal(near.Normalised, far.Normalised);
    }

    [Fact]
    public async Task The_fan_out_reaches_every_other_connection_and_not_the_sender()
    {
        // 3b.2's actual verification. On one instance the property "everyone
        // else receives it" is indistinguishable from "everyone else on this
        // box receives it", and the second is what the code did.
        _fixture.RequireBoth();
        await using var first = new EditorApiFactory(_fixture);
        await using var second = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(first, "owner-fanout");
        await DocumentSetup.GrantAsync(first, documentId, "sender", Role.Editor);
        await DocumentSetup.GrantAsync(first, documentId, "same-box", Role.Editor);
        await DocumentSetup.GrantAsync(first, documentId, "other-box", Role.Editor);

        await using var sender = await DocumentClient.JoinAsync(first, "sender", documentId);
        await using var sameBox = await DocumentClient.JoinAsync(first, "same-box", documentId);
        await using var otherBox = await DocumentClient.JoinAsync(second, "other-box", documentId);

        var batch = sender.Writer.Type("one");
        Assert.Null((await sender.SubmitAsync(batch)).Code);
        sender.ApplyLocal(batch);

        sameBox.Apply(await sameBox.NextAsync());
        otherBox.Apply(await otherBox.NextAsync());

        // Exactly once each. A sending instance that also acted on its own
        // publication would deliver twice to the client sharing its box, and
        // the document would be identical either way.
        await sameBox.NothingArrivesAsync(TimeSpan.FromMilliseconds(400));
        await otherBox.NothingArrivesAsync(TimeSpan.FromMilliseconds(400));
        Assert.Single(sameBox.Received);
        Assert.Single(otherBox.Received);

        // The sender already has these operations and is excluded on purpose.
        await sender.NothingArrivesAsync(TimeSpan.FromMilliseconds(400));

        Assert.Equal(sender.Normalised, sameBox.Normalised);
        Assert.Equal(sender.Normalised, otherBox.Normalised);
    }

    [Fact]
    public async Task Edits_cross_in_both_directions()
    {
        // One direction working proves the publish on one instance and the
        // subscribe on the other. It does not prove the reverse pair, and the
        // two are separate code paths on separate objects.
        _fixture.RequireBoth();
        await using var first = new EditorApiFactory(_fixture);
        await using var second = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(first, "owner-both-ways");
        await DocumentSetup.GrantAsync(first, documentId, "left-box", Role.Editor);
        await DocumentSetup.GrantAsync(first, documentId, "right-box", Role.Editor);

        await using var left = await DocumentClient.JoinAsync(first, "left-box", documentId);
        await using var right = await DocumentClient.JoinAsync(second, "right-box", documentId);

        for (var round = 0; round < 4; round++)
        {
            var fromLeft = left.Writer.Type("L");
            Assert.Null((await left.SubmitAsync(fromLeft)).Code);
            left.ApplyLocal(fromLeft);
            right.Apply(await right.NextAsync());

            var fromRight = right.Writer.Type("R");
            Assert.Null((await right.SubmitAsync(fromRight)).Code);
            right.ApplyLocal(fromRight);
            left.Apply(await left.NextAsync());
        }

        Assert.Equal(4, Backplane(first).Received);
        Assert.Equal(4, Backplane(second).Received);
        Assert.Equal(0, left.Replica.PendingCount);
        Assert.Equal(0, right.Replica.PendingCount);
        Assert.Equal(left.Normalised, right.Normalised);
    }

    [Fact]
    public async Task An_instance_dying_mid_session_costs_its_clients_a_reconnect_and_nothing_else()
    {
        _fixture.RequireBoth();
        var doomed = new EditorApiFactory(_fixture);
        await using var survivor = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(survivor, "owner-kill");
        await DocumentSetup.GrantAsync(survivor, documentId, "stranded", Role.Editor);
        await DocumentSetup.GrantAsync(survivor, documentId, "kept", Role.Editor);

        var stranded = await DocumentClient.JoinAsync(doomed, "stranded", documentId);
        await using var kept = await DocumentClient.JoinAsync(survivor, "kept", documentId);

        var before = stranded.Writer.Type("before");
        Assert.Null((await stranded.SubmitAsync(before)).Code);
        stranded.ApplyLocal(before);
        kept.Apply(await kept.NextAsync());

        // The kill.
        await stranded.DisposeAsync();
        await doomed.DisposeAsync();

        // The survivor keeps taking writes. A publish to a channel nobody is
        // listening on has to be a no-op rather than a failed submission —
        // otherwise losing an instance takes the remaining ones down with it.
        var after = kept.Writer.Type("after");
        Assert.Null((await kept.SubmitAsync(after)).Code);
        kept.ApplyLocal(after);

        // What a stranded client actually does: reconnect somewhere else and
        // catch up. The ticket was one-shot and the replica id with it, so this
        // is a new replica reading the document, which is what a real reconnect
        // produces.
        await using var rejoined = await DocumentClient.JoinAsync(survivor, "stranded", documentId);
        var caught = await rejoined.CatchUpAsync();

        Assert.Null(caught.Code);
        rejoined.ApplyCatchUp(caught);

        // Named because the failure mode is a stale answer that still looks
        // right: everything written before the kill, and everything written
        // after it.
        Assert.Equal("beforeafter", rejoined.Replica.Text);
        Assert.Equal(0, rejoined.Replica.PendingCount);
        Assert.Equal(kept.Normalised, rejoined.Normalised);
    }

    [Fact]
    public async Task A_departing_connection_does_not_strand_the_others_on_its_instance()
    {
        // The subscription is per document, not per connection, so it has to
        // outlive any one of them and end with the last.
        //
        // This was first written as two assertions on the subscription count,
        // and the sabotage that unsubscribes on the *first* departure went
        // straight through. The wait was `Carrying == 1`, which is already true
        // the instant the client is disposed — before the server has processed
        // the disconnect at all — so it returned immediately and asserted
        // against a transition that had not happened. A condition that holds
        // before the mechanism runs is not a test of the mechanism.
        //
        // So the middle assertion is now the thing that actually matters —
        // the remaining client still receives cross-instance broadcasts — and
        // the wait is on a transition that genuinely has to occur.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        await using var other = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-carry");
        await DocumentSetup.GrantAsync(factory, documentId, "carry-leaving", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "carry-staying", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "carry-remote", Role.Editor);

        Assert.Equal(0, Backplane(factory).Carrying);

        var leaving = await DocumentClient.JoinAsync(factory, "carry-leaving", documentId);
        var staying = await DocumentClient.JoinAsync(factory, "carry-staying", documentId);
        await using var remote = await DocumentClient.JoinAsync(other, "carry-remote", documentId);

        // One subscription for the document, not one per connection.
        Assert.Equal(1, Backplane(factory).Carrying);

        await leaving.DisposeAsync();

        // The transition, waited for rather than assumed: two connections down
        // to one. Submitting before this lands would let the message arrive
        // while the instance is still subscribed for reasons that have nothing
        // to do with the rule under test.
        await WaitFor(() => Connections(factory).Count(documentId) == 1);

        var batch = remote.Writer.Type("still here");
        Assert.Null((await remote.SubmitAsync(batch)).Code);
        remote.ApplyLocal(batch);

        staying.Apply(await staying.NextAsync());

        Assert.Equal(1, Backplane(factory).Carrying);
        Assert.Equal(remote.Normalised, staying.Normalised);

        await staying.DisposeAsync();

        // §13.15. An instance that never unsubscribes delivers every message
        // correctly and converges every client, while decoding traffic for
        // documents nobody on it is reading, for the life of the process.
        await WaitFor(() => Backplane(factory).Carrying == 0);
        Assert.Equal(0, Backplane(factory).Carrying);
    }

    /// <summary>
    /// Waits for a server-side state change a client cannot observe directly.
    /// </summary>
    /// <remarks>
    /// Disconnect handling runs after the client's dispose returns, so there is
    /// no reply to await. Polling with a deadline is honest about that; a fixed
    /// sleep would be either flaky or slow, and asserting immediately would test
    /// the scheduler.
    /// </remarks>
    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
