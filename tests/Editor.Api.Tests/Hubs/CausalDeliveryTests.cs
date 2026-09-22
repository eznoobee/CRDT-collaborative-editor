using Crdt.Core;
using Editor.Api.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// Causal delivery over the wire (§5, §8).
/// </summary>
/// <remarks>
/// §8 makes broadcast unordered on purpose: Redis pub/sub does not promise
/// cross-instance order, and building on the assumption that it does would make
/// correctness depend on a property nothing provides. So these tests apply what
/// arrives in orders the network is allowed to produce — reversed, interleaved,
/// duplicated — rather than in the order this particular run happened to deliver.
/// <para>
/// The vacuity risk named in the breakdown: a delivery order that is causal most
/// of the time never exercises the pending set, and would pass against a replica
/// with no buffering at all. Every order below is chosen to be hostile, and the
/// scale case exists because §13.10 is about exactly this — a property that holds
/// at every size the suite tried.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class CausalDeliveryTests
{
    private readonly EditorFixture _fixture;

    public CausalDeliveryTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Operations_applied_in_reverse_order_still_converge()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-reverse");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-reverse", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "reader-reverse", Role.Editor);

        await using var writer = await DocumentClient.JoinAsync(factory, "writer-reverse", documentId);
        await using var reader = await DocumentClient.JoinAsync(factory, "reader-reverse", documentId);

        // Each character is its own batch, so the reader has eleven independent
        // broadcasts to reorder. One batch would arrive atomically and prove
        // nothing about ordering.
        var broadcasts = new List<OperationBroadcast>();
        foreach (var character in "hello world")
        {
            var batch = writer.Writer.Type(character.ToString());
            Assert.Null((await writer.SubmitAsync(batch)).Code);
            writer.ApplyLocal(batch);
            broadcasts.Add(await reader.NextAsync());
        }

        // The hostile order. Every operation but the first arrives before its
        // parent, so all of them pass through the pending set.
        foreach (var broadcast in Enumerable.Reverse(broadcasts))
        {
            reader.Apply(broadcast);
        }

        Assert.Equal(0, reader.Replica.PendingCount);
        Assert.Equal("hello world", reader.Replica.Text);

        // §8: convergence is asserted on normalised state, tombstones included,
        // not on the visible text. Two replicas can render the same characters
        // while disagreeing about the tree underneath.
        Assert.Equal(writer.Normalised, reader.Normalised);
    }

    [Fact]
    public async Task A_duplicated_delivery_changes_nothing_and_is_counted()
    {
        // §5: duplicate delivery is guaranteed, not incidental. The count is
        // the observable part, and asserting it is what stops the dedupe from
        // being quietly removed later — convergence alone would still pass.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-dupe");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-dupe", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "reader-dupe", Role.Editor);

        await using var writer = await DocumentClient.JoinAsync(factory, "writer-dupe", documentId);
        await using var reader = await DocumentClient.JoinAsync(factory, "reader-dupe", documentId);

        var batch = writer.Writer.Type("abc");
        Assert.Null((await writer.SubmitAsync(batch)).Code);
        writer.ApplyLocal(batch);

        var broadcast = await reader.NextAsync();

        reader.Apply(broadcast);
        var afterOnce = reader.Normalised;
        Assert.Equal(0, reader.Replica.DuplicatesDropped);

        reader.Apply(broadcast);
        reader.Apply(broadcast);

        Assert.Equal(afterOnce, reader.Normalised);
        Assert.Equal(6, reader.Replica.DuplicatesDropped);
        Assert.Equal(writer.Normalised, reader.Normalised);
    }

    [Fact]
    public async Task Two_writers_interleaved_adversarially_converge()
    {
        // The case that actually needs the pending set: two replicas editing
        // concurrently, each receiving the other's operations out of order and
        // interleaved with its own.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-interleaved");
        await DocumentSetup.GrantAsync(factory, documentId, "left", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "right", Role.Editor);

        await using var left = await DocumentClient.JoinAsync(factory, "left", documentId);
        await using var right = await DocumentClient.JoinAsync(factory, "right", documentId);

        var fromLeft = new List<OperationBroadcast>();
        var fromRight = new List<OperationBroadcast>();

        for (var round = 0; round < 6; round++)
        {
            var leftBatch = left.Writer.Type("L");
            Assert.Null((await left.SubmitAsync(leftBatch)).Code);
            left.ApplyLocal(leftBatch);
            fromLeft.Add(await right.NextAsync());

            var rightBatch = right.Writer.Type("R");
            Assert.Null((await right.SubmitAsync(rightBatch)).Code);
            right.ApplyLocal(rightBatch);
            fromRight.Add(await left.NextAsync());
        }

        // Each side applies the other's operations backwards.
        foreach (var broadcast in Enumerable.Reverse(fromRight))
        {
            left.Apply(broadcast);
        }

        foreach (var broadcast in Enumerable.Reverse(fromLeft))
        {
            right.Apply(broadcast);
        }

        Assert.Equal(0, left.Replica.PendingCount);
        Assert.Equal(0, right.Replica.PendingCount);
        Assert.Equal(left.Normalised, right.Normalised);
    }

    [Fact]
    public void A_delete_for_an_unknown_element_buffers_rather_than_applying()
    {
        // §5 names this explicitly: deletes buffer on the same rules as
        // inserts, and a delete for an unknown id is not applied. Silently
        // dropping it would tombstone nothing and diverge the moment the
        // insert arrived.
        var author = ReplicaIdConversion.FromGuid(Guid.CreateVersion7());
        var target = new ElementId(author, 0);

        var insert = new InsertOperation(target, new System.Text.Rune('a'), null, Side.Right, null);
        var delete = new DeleteOperation(new ElementId(author, 1), target);

        var replica = new Replica(ReplicaIdConversion.FromGuid(Guid.CreateVersion7()));
        replica.Apply(delete);

        Assert.Equal(1, replica.PendingCount);

        replica.Apply(insert);

        Assert.Equal(0, replica.PendingCount);
        Assert.Empty(replica.Text);
        Assert.Single(replica.AllIds);
    }

    [Fact]
    public async Task Reordering_still_converges_at_a_size_the_suite_would_not_otherwise_reach()
    {
        // §13.10. Correct at every tested size and fatal at real ones is the
        // class of bug this project has already shipped once, and a pending set
        // is exactly the sort of structure whose cost is invisible at ten
        // operations. Two hundred is not production scale but it is two orders
        // of magnitude past the tests above, and it is the size at which a
        // quadratic drain becomes visible as a timeout rather than a wrong
        // answer.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-scale");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-scale", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "reader-scale", Role.Editor);

        await using var writer = await DocumentClient.JoinAsync(factory, "writer-scale", documentId);
        await using var reader = await DocumentClient.JoinAsync(factory, "reader-scale", documentId);

        const int Batches = 200;
        var broadcasts = new List<OperationBroadcast>(Batches);

        for (var i = 0; i < Batches; i++)
        {
            var batch = writer.Writer.Type("x");
            Assert.Null((await writer.SubmitAsync(batch)).Code);
            writer.ApplyLocal(batch);
            broadcasts.Add(await reader.NextAsync());
        }

        // Reversed: every operation waits for the one before it, so the pending
        // set holds all 200 and drains in a single cascade.
        foreach (var broadcast in Enumerable.Reverse(broadcasts))
        {
            reader.Apply(broadcast);
        }

        Assert.Equal(0, reader.Replica.PendingCount);
        Assert.Equal(Batches, reader.Replica.Text.Length);
        Assert.Equal(writer.Normalised, reader.Normalised);
    }
}
