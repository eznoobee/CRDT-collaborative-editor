using System.Text;
using Crdt.Core;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// Builds the batches a client would send, in §6's wire form.
/// </summary>
/// <remarks>
/// Real encoded operations rather than arbitrary bytes, because §7's ingest
/// checks are about what the operations say — the replica they claim, the
/// sequence they occupy — and a test that submitted four bytes would only ever
/// exercise the decoder's refusal.
/// <para>
/// It types left to right into an empty document: each element's parent is the
/// previous one, on its right, with no right origin. That is the shape §13.10
/// singled out, and it is what a person typing produces.
/// </para>
/// </remarks>
public sealed class ReplicaWriter
{
    private readonly ReplicaId _replica;
    private ElementId? _previous;

    public ReplicaWriter(Guid replicaId, ulong startSeq = 0)
    {
        _replica = ReplicaIdConversion.FromGuid(replicaId);
        NextSeq = startSeq;
    }

    /// <summary>The sequence number the next operation will carry.</summary>
    public ulong NextSeq { get; private set; }

    /// <summary>Encodes the next <paramref name="text"/> as one batch, advancing the sequence.</summary>
    public byte[] Type(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var operations = new List<Operation>();
        foreach (var rune in text.EnumerateRunes())
        {
            var id = new ElementId(_replica, NextSeq++);
            operations.Add(new InsertOperation(id, rune, _previous, Side.Right, RightOrigin: null));
            _previous = id;
        }

        return OperationBinary.Encode(operations);
    }

    /// <summary>
    /// A batch built as if from a different replica, keeping this writer's
    /// sequence numbers.
    /// </summary>
    public static byte[] TypeAs(Guid replicaId, ulong seq, string text) =>
        new ReplicaWriter(replicaId, seq).Type(text);

    /// <summary>A batch whose first operation skips a sequence number.</summary>
    public byte[] TypeWithGap(string text)
    {
        NextSeq++;
        return Type(text);
    }
}
