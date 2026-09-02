using Crdt.Core;
using Editor.Infrastructure.Serialization;

namespace Editor.Infrastructure.Ingest;

/// <summary>The outcome of validating a batch.</summary>
/// <param name="Rejection">
/// <see langword="null"/> when accepted, otherwise a code from
/// <see cref="IngestRejection"/>.
/// </param>
/// <param name="Operations">The decoded batch, when accepted.</param>
public readonly record struct IngestResult(string? Rejection, IReadOnlyList<Operation>? Operations)
{
    public static IngestResult Reject(string code) => new(code, null);

    public static IngestResult Accept(IReadOnlyList<Operation> operations) => new(null, operations);
}

/// <summary>
/// Everything PROJECT_SPEC.md §7 requires of an operation batch before any of
/// it is applied.
/// </summary>
/// <remarks>
/// All or nothing. A batch that fails any check is rejected whole, and nothing
/// in it is written: applying the valid prefix of an invalid batch would leave
/// the replica's sequence dense on the server and gapped on the client, and §5
/// makes density a correctness property rather than a convention.
/// <para>
/// The checks are ordered by cost. Size and count are arithmetic on bytes; the
/// decode allocates; the sequence check may query Postgres once for a replica
/// this instance has not seen. An expensive check that runs before a cheap one
/// that would have rejected the message is a denial-of-service with the
/// server's own validation as the amplifier.
/// </para>
/// </remarks>
public sealed class IngestValidator
{
    private readonly DocumentIngestState _state;
    private readonly IngestLimits _limits;

    public IngestValidator(DocumentIngestState state, IngestLimits limits)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(limits);

        _state = state;
        _limits = limits;
    }

    /// <summary>Validates a batch submitted by a bound connection.</summary>
    public async Task<IngestResult> ValidateAsync(
        Guid documentId,
        ReplicaId replicaId,
        ReadOnlyMemory<byte> encoded,
        CancellationToken cancellationToken)
    {
        if (encoded.Length > _limits.MaxMessageBytes)
        {
            return IngestResult.Reject(IngestRejection.MessageTooLarge);
        }

        IReadOnlyList<Operation> operations;
        try
        {
            operations = OperationBinary.Decode(encoded.Span);
        }
        catch (BinaryFormatException)
        {
            // §6's decoder is the UTF-8 and lone-surrogate check as well as the
            // structural one: it reads a value as a single code point and
            // refuses anything else, so "exactly 1 code point, at most 4 bytes"
            // is enforced by there being no way to encode anything else.
            return IngestResult.Reject(IngestRejection.Malformed);
        }

        if (operations.Count > _limits.MaxOperationsPerBatch)
        {
            return IngestResult.Reject(IngestRejection.BatchTooLarge);
        }

        if (operations.Count == 0)
        {
            // Not an error worth a code of its own, and not something to write
            // either: an empty batch would take a document lock and a round
            // trip to append nothing.
            return IngestResult.Accept(operations);
        }

        // §7: an operation whose replica id is not the connection's is a forged
        // attribution. Checked per operation, not per batch: a batch whose
        // first operation is honest says nothing about its fifth.
        foreach (var operation in operations)
        {
            if (!operation.Id.Replica.Equals(replicaId))
            {
                return IngestResult.Reject(IngestRejection.ReplicaMismatch);
            }
        }

        var expected = await _state.NextSequenceAsync(documentId, replicaId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var operation in operations)
        {
            if (operation.Id.Seq != expected)
            {
                // Both directions. A gap forward breaks §5's density; a repeat
                // or a step backwards is a replay, and the log's primary key
                // would silently swallow it as a duplicate.
                return IngestResult.Reject(IngestRejection.SequenceGap);
            }

            expected++;
        }

        var live = await _state.LiveBytesAsync(documentId, cancellationToken).ConfigureAwait(false);
        var added = 0L;

        foreach (var operation in operations)
        {
            if (operation is InsertOperation insert)
            {
                added += insert.Value.Utf8SequenceLength;
            }
        }

        if (live + added > _limits.MaxDocumentBytes)
        {
            return IngestResult.Reject(IngestRejection.DocumentFull);
        }

        return IngestResult.Accept(operations);
    }
}
