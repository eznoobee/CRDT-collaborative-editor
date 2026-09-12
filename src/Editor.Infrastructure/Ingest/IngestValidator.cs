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
            // The run cap goes into the decoder rather than being checked
            // after it: a run is expanded during decoding, so a cap applied
            // afterwards would have already paid for the expansion it exists to
            // prevent (§6, §7).
            operations = OperationBinary.Decode(encoded.Span, _limits.MaxRunCodePoints);
        }
        catch (RunLengthExceededException)
        {
            // Its own code: a client that pasted too much at once needs to know
            // to split it, which is a different fix from the one a client
            // sending malformed bytes needs.
            return IngestResult.Reject(IngestRejection.RunTooLong);
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

        // §5's readiness, enforced at ingest rather than buffered. See below for
        // why the server has no pending set at all.
        var missing = await MissingOriginsAsync(documentId, operations, cancellationToken)
            .ConfigureAwait(false);

        if (missing.Count > 0)
        {
            return IngestResult.Reject(
                await ClassifyAsync(documentId, missing, cancellationToken).ConfigureAwait(false));
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

    /// <summary>
    /// Whether every id the batch references already exists, or is created
    /// earlier within the batch itself.
    /// </summary>
    /// <remarks>
    /// <b>The server rejects a non-ready operation; it does not buffer one.</b>
    /// §5 describes a bounded pending set because "origins are client-supplied",
    /// and that is true of a peer receiving a broadcast. It is not true here. A
    /// client can only reference an element it knows about, and it knows about
    /// exactly two kinds: its own earlier operations, which density already
    /// guarantees the server holds, and other replicas' operations, which it
    /// learned from a broadcast the server sent only after committing them.
    /// <para>
    /// So a non-ready operation arriving at ingest is a bug or an attack, never
    /// a legitimate race — and buffering one is buffering an id that may never
    /// arrive, which is the denial of service §5 warns about. Rejecting removes
    /// the vector rather than bounding it, and leaves the server with no
    /// pending set to bound.
    /// </para>
    /// </remarks>
    private async Task<HashSet<ElementId>> MissingOriginsAsync(
        Guid documentId,
        IReadOnlyList<Operation> operations,
        CancellationToken cancellationToken)
    {
        var created = new HashSet<ElementId>();
        var referenced = new HashSet<ElementId>();

        foreach (var operation in operations)
        {
            switch (operation)
            {
                case InsertOperation insert:
                    // Order matters: an operation may reference one created
                    // earlier in the same batch, which is the ordinary case for
                    // a run or for typing, but never one created after it.
                    Require(insert.Parent);
                    Require(insert.RightOrigin);
                    created.Add(insert.Id);
                    break;

                case DeleteOperation delete:
                    Require(delete.Target);
                    break;

                default:
                    break;
            }
        }

        if (referenced.Count == 0)
        {
            return referenced;
        }

        var known = await _state.KnownElementsAsync(documentId, referenced, cancellationToken)
            .ConfigureAwait(false);

        referenced.ExceptWith(known);
        return referenced;

        void Require(ElementId? id)
        {
            if (id is { } value && !created.Contains(value))
            {
                referenced.Add(value);
            }
        }
    }

    /// <summary>
    /// Which refusal an unresolvable reference earns (§9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>resync_required</c> is the one refusal in this system that legitimately
    /// destroys a user's unsent work, so it is emitted only where the server can
    /// say the referenced element <em>existed and was collected</em> — below
    /// §5's frontier, for an author the frontier knows, with no operation of that
    /// sequence number in the log at all. Everything else stays
    /// <c>unknown_origin</c>.
    /// </para><para>
    /// Two narrowings beyond the frontier comparison, both of which the table in
    /// §9 originally missed. An id at or above the frontier is not evidence of
    /// collection, because not everyone held it. And an id naming an operation
    /// that <em>does</em> exist but is a delete is a client bug, not a
    /// collection: deletes consume sequence numbers, so such an id sits below
    /// the frontier and would otherwise be answered with an instruction to throw
    /// state away.
    /// </para><para>
    /// The frontier is read here rather than on every submission. It is a query,
    /// and this path runs only when a reference failed to resolve — which for a
    /// correct client is never.
    /// </para>
    /// </remarks>
    private async Task<string> ClassifyAsync(
        Guid documentId,
        HashSet<ElementId> missing,
        CancellationToken cancellationToken)
    {
        var frontier = await _state.FrontierAsync(documentId, cancellationToken)
            .ConfigureAwait(false);

        if (frontier.Count == 0)
        {
            return IngestRejection.UnknownOrigin;
        }

        var below = new HashSet<ElementId>();
        foreach (var id in missing)
        {
            if (frontier.TryGetValue(id.Replica, out var count) && id.Seq < count)
            {
                below.Add(id);
            }
        }

        if (below.Count == 0)
        {
            return IngestRejection.UnknownOrigin;
        }

        // Of the ids below the frontier, any that name an operation the log
        // still holds are references to a delete, not to something collected.
        var operations = await _state.KnownOperationsAsync(documentId, below, cancellationToken)
            .ConfigureAwait(false);

        below.ExceptWith(operations);

        return below.Count > 0
            ? IngestRejection.ResyncRequired
            : IngestRejection.UnknownOrigin;
    }
}
