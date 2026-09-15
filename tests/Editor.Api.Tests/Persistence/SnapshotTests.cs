using System.Text;
using Crdt.Core;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;

namespace Editor.Api.Tests.Persistence;

/// <summary>Snapshots and document loading (PROJECT_SPEC.md §6).</summary>
[Collection(nameof(PostgresTests))]
public sealed class SnapshotTests(PostgresFixture fixture)
{
    private static ReplicaId Replica(int n)
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        bytes[^1] = (byte)n;
        return new ReplicaId(bytes);
    }

    private static List<Operation> Type(Replica replica, string text, int at = 0)
    {
        var operations = new List<Operation>();
        foreach (var rune in text.EnumerateRunes())
        {
            operations.Add(replica.Insert(at++, rune));
        }

        return operations;
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 499, false)]
    [InlineData(0, 500, true)]
    [InlineData(500, 999, false)]
    [InlineData(499, 501, true)]
    public void Snapshot_policy_fires_on_crossing_a_multiple(long before, long after, bool due)
    {
        // Batches move the sequence by more than one, so an equality test would
        // step over the threshold and never fire.
        Assert.Equal(due, SnapshotPolicy.Default.IsDue(before, after));

        // new() and default bypass a primary constructor's defaults on a record
        // struct, so an unconfigured policy must read as "disabled" rather than
        // pretending to be the §6 default.
        Assert.Equal(0, new SnapshotPolicy().OperationsPerSnapshot);
        Assert.False(new SnapshotPolicy().IsDue(before, after));
    }

    [Fact]
    public async Task Loads_a_document_with_no_snapshot_from_the_log_alone()
    {
        fixture.RequireDatabase();

        var documentId = PostgresFixture.NewDocumentId();
        var writer = new OperationLogWriter(fixture.DataSource);
        var store = new DocumentStore(fixture.DataSource);

        var source = new Replica(Replica(1));
        await writer.AppendAsync(documentId, Type(source, "hello"), TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(documentId, Replica(9), TestContext.Current.CancellationToken);

        Assert.Equal("hello", loaded.Text);
    }

    [Fact]
    public async Task Loads_a_document_as_a_snapshot_plus_later_operations()
    {
        fixture.RequireDatabase();

        var documentId = PostgresFixture.NewDocumentId();
        var writer = new OperationLogWriter(fixture.DataSource);
        var store = new DocumentStore(fixture.DataSource);

        var source = new Replica(Replica(1));
        var first = await writer.AppendAsync(
            documentId, Type(source, "hello"), TestContext.Current.CancellationToken);

        await store.SaveSnapshotAsync(
            documentId, source, first.HighestServerSeq, TestContext.Current.CancellationToken);

        // Operations after the snapshot must still be applied on load.
        await writer.AppendAsync(
            documentId, Type(source, " world", at: 5), TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(documentId, Replica(9), TestContext.Current.CancellationToken);

        Assert.Equal("hello world", loaded.Text);
        Assert.Equal(source.Text, loaded.Text);
        Assert.Equal(0, loaded.PendingCount);
    }

    [Fact]
    public async Task A_snapshot_preserves_tombstones_so_later_operations_still_attach()
    {
        fixture.RequireDatabase();

        // The reason a snapshot cannot be the visible text: an operation
        // arriving afterwards may name a tombstone as its origin (§5).
        var documentId = PostgresFixture.NewDocumentId();
        var writer = new OperationLogWriter(fixture.DataSource);
        var store = new DocumentStore(fixture.DataSource);

        var source = new Replica(Replica(1));
        var operations = Type(source, "abcdef");
        operations.Add(source.Delete(2));

        var appended = await writer.AppendAsync(
            documentId, operations, TestContext.Current.CancellationToken);
        await store.SaveSnapshotAsync(
            documentId, source, appended.HighestServerSeq, TestContext.Current.CancellationToken);

        await writer.AppendAsync(
            documentId, [source.Insert(2, new Rune('X'))], TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(documentId, Replica(9), TestContext.Current.CancellationToken);

        Assert.Equal(source.Text, loaded.Text);
        Assert.Equal(0, loaded.PendingCount);
    }

    [Fact]
    public void Snapshot_serialisation_round_trips()
    {
        var source = new Replica(Replica(1));
        Type(source, "the quick brown fox");
        source.Delete(3);

        var json = SnapshotSerializer.Serialize(source);
        var restored = SnapshotSerializer.Deserialize(Replica(1), json);

        Assert.Equal(source.Text, restored.Text);
        Assert.Equal(source.AllIds, restored.AllIds);

        // Re-serialising must be byte-identical, or the format is not normalised.
        Assert.Equal(json, SnapshotSerializer.Serialize(restored));
    }

    [Fact]
    public void Snapshot_serialisation_keeps_non_ascii_literal()
    {
        // §9: non-ASCII is emitted literally, never as \uXXXX. C# escapes it by
        // default and JavaScript does not, so the two would diverge silently.
        var source = new Replica(Replica(1));
        Type(source, "naïve 日本 🎉");

        var json = SnapshotSerializer.Serialize(source);

        Assert.Contains("naïve", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u00ef", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(source.Text, SnapshotSerializer.Deserialize(Replica(1), json).Text);
    }
}
