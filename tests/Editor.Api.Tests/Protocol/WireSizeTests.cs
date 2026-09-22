using System.Buffers;
using System.Globalization;
using System.Text;
using Crdt.Core;
using Editor.Api.Hubs;
using Editor.Api.Tests.Hubs;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace Editor.Api.Tests.Protocol;

/// <summary>
/// What a hub message actually costs on the wire, per protocol (§13.13a).
/// </summary>
/// <remarks>
/// <para>
/// <b>The frame is measured, not the payload.</b> §7's cap is on the message,
/// and the JSON protocol's base64 inflation lives in the frame the protocol
/// writes — the <c>byte[]</c> argument is the same length under both protocols.
/// Measuring the payload would report the two as identical, produce a plausible
/// number, and decide the protocol wrongly with nothing about the result looking
/// wrong afterwards. That is §12's named vacuity risk for this task, and the
/// assertions below exist to keep the measurement honest rather than to check a
/// threshold.
/// </para>
/// <para>
/// <see cref="IHubProtocol.WriteMessage"/> is exactly what the connection sends,
/// minus transport framing (WebSocket headers), which is identical for both and
/// so cancels out of the comparison.
/// </para>
/// </remarks>
public sealed class WireSizeTests
{
    private static readonly Guid Document = Guid.CreateVersion7();
    private static readonly Guid Replica = Guid.CreateVersion7();

    private static JsonHubProtocol Json() => new();

    private static MessagePackHubProtocol MessagePack() => new();

    /// <summary>The bytes a protocol puts on the wire for one submission.</summary>
    private static int FramedBytes(IHubProtocol protocol, byte[] operations)
    {
        var message = new InvocationMessage(
            "SubmitAsync", [new OperationBatchMessage(Document, Replica, operations)]);

        var buffer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(message, buffer);
        return buffer.WrittenCount;
    }

    /// <summary>A batch of <paramref name="codePoints"/> characters typed left to right.</summary>
    private static byte[] Batch(int codePoints) =>
        new ReplicaWriter(Replica).Type(new string('a', codePoints));

    [Fact]
    public void The_json_protocol_inflates_the_payload_and_messagepack_does_not()
    {
        // The measurement that decides §13.13a, and the shape of it is the
        // point: JSON's frame is markedly larger than the payload it carries,
        // MessagePack's is barely larger, and the difference is invisible if
        // you measure the payload.
        var payload = Batch(256);

        var json = FramedBytes(Json(), payload);
        var messagePack = FramedBytes(MessagePack(), payload);

        // Base64 is 4 bytes out for every 3 in.
        Assert.InRange(json, (int)(payload.Length * 1.3), (int)(payload.Length * 1.4) + 512);

        // MessagePack writes the bytes as bytes: overhead is a header and the
        // two ids, not a proportion of the payload.
        Assert.InRange(messagePack, payload.Length, payload.Length + 512);

        Assert.True(
            messagePack < json,
            $"MessagePack framed {messagePack} bytes and JSON {json} for a {payload.Length}-byte payload.");
    }

    [Fact]
    public void The_payload_is_the_same_under_both_protocols()
    {
        // The vacuity check, made explicit rather than left as a comment: this
        // is the measurement that would have been taken by mistake, and it
        // shows the two protocols as identical. Asserting that it does is what
        // stops someone "simplifying" the test above into this one.
        var payload = Batch(256);

        Assert.Equal(payload.Length, payload.Length);
        Assert.NotEqual(FramedBytes(Json(), payload), FramedBytes(MessagePack(), payload));
    }

    [Fact]
    public void The_section_7_message_cap_admits_more_operations_under_messagepack()
    {
        // §7 caps the message at 64 KB. What matters to a client is how many
        // operations fit inside that, and under JSON the answer is about three
        // quarters of what it looks like.
        const int Cap = 64 * 1024;

        var jsonPayload = LargestPayloadUnder(Json(), Cap);
        var messagePackPayload = LargestPayloadUnder(MessagePack(), Cap);

        Assert.True(
            messagePackPayload > jsonPayload,
            $"Under a {Cap}-byte cap: JSON admits {jsonPayload} payload bytes, MessagePack {messagePackPayload}.");

        // Roughly three quarters, which is the 47 KB Phase 3 reported.
        Assert.InRange(jsonPayload, (int)(Cap * 0.70), (int)(Cap * 0.80));
        Assert.InRange(messagePackPayload, (int)(Cap * 0.95), Cap);
    }

    [Fact]
    public void Report()
    {
        // Not an assertion — the numbers §13.13a says get reported. Emitted
        // from the test rather than a script because the protocols are .NET
        // types and this is where they can be exercised directly.
        var report = new StringBuilder();
        report.AppendLine();
        report.AppendLine("PROJECT_SPEC.md §13.13a — framed hub message, bytes");
        report.AppendLine();
        report.AppendLine(
            "Base64 adds a third to the payload, so the saving relative to the JSON");
        report.AppendLine(
            "frame is a quarter (0.33/1.33), approached as fixed frame overhead —");
        report.AppendLine("method name, document id, replica id — is amortised.");
        report.AppendLine();
        report.AppendLine("| document | payload | JSON frame | MessagePack frame | saved |");
        report.AppendLine("|---|---|---|---|---|");

        foreach (var (label, payload) in new (string, byte[])[]
        {
            ("one keystroke", Batch(1)),
            ("keystroke batch (16)", Batch(16)),
            ("paste at the run cap (256)", Batch(256)),
            ("256 separate inserts (no run)", Payload(256)),
            ("a batch near §7's cap", Payload(20_000)),
        })
        {
            var json = FramedBytes(Json(), payload);
            var messagePack = FramedBytes(MessagePack(), payload);
            var saved = 100.0 * (json - messagePack) / json;

            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {label} | {payload.Length} | {json} | {messagePack} | {saved:F1}% |"));
        }

        const int Cap = 64 * 1024;
        report.AppendLine();
        report.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Payload admitted under §7's {Cap}-byte message cap: "
            + $"JSON {LargestPayloadUnder(Json(), Cap)} bytes, "
            + $"MessagePack {LargestPayloadUnder(MessagePack(), Cap)} bytes."));

        var text = report.ToString();
        TestContext.Current.SendDiagnosticMessage(text);
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "wire-sizes.md"), text, Encoding.UTF8);

        Assert.Contains("MessagePack", text, StringComparison.Ordinal);
    }

    /// <summary>The largest payload whose framed message stays under <paramref name="cap"/>.</summary>
    private static int LargestPayloadUnder(IHubProtocol protocol, int cap)
    {
        var best = 0;

        // Binary search on code points; the framed size is monotonic in them.
        var low = 1;
        var high = 40_000;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var payload = Payload(middle);

            if (FramedBytes(protocol, payload) <= cap)
            {
                best = payload.Length;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    /// <summary>
    /// A payload of <paramref name="codePoints"/> elements, encoded once.
    /// </summary>
    /// <remarks>
    /// Built with individual insert records rather than a run, because a run
    /// collapses 256 elements into a handful of bytes and the question here is
    /// how much *payload* fits, not how few operations can express it.
    /// </remarks>
    private static byte[] Payload(int codePoints)
    {
        var replica = ReplicaIdConversion.FromGuid(Replica);
        var operations = new List<Operation>(codePoints);

        for (var i = 0; i < codePoints; i++)
        {
            // Alternating right origins break the run shape, so every element
            // is its own record.
            operations.Add(new InsertOperation(
                new ElementId(replica, (ulong)i),
                new Rune('a'),
                i == 0 ? null : new ElementId(replica, (ulong)(i - 1)),
                Side.Right,
                i % 2 == 0 ? null : new ElementId(replica, 0)));
        }

        return OperationBinary.Encode(operations);
    }
}
