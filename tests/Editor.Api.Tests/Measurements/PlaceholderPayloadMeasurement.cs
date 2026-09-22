using System.Globalization;
using System.Text;
using Crdt.Core;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;

namespace Editor.Api.Tests.Measurements;

/// <summary>
/// What a retained tombstone's payload costs, across edit shapes (row 29).
/// </summary>
/// <remarks>
/// <para>
/// 7.3 found that §5's collection reclaims nothing from a mid-document
/// deletion: forward typing builds a chain of right children, so an interior
/// tombstone always has a visible child, is never a leaf, and is never
/// collected. It stays as a structural placeholder, which is correct — right
/// origins can name tombstones, so the position has to survive. The open
/// question row 29 carries is whether the <em>payload</em> has to: a deleted
/// element's character is never rendered and never affects ordering, which is
/// decided entirely by <see cref="ElementId"/>.
/// </para><para>
/// <b>The vacuity risk, and it is the whole reason this file enumerates its
/// traces in the open.</b> Measuring reclamation on append-then-delete-the-end
/// traces reproduces exactly the shape 7.3 found the suite had been testing all
/// along — every tombstone collects, no placeholders survive, and the answer
/// comes back "there is nothing to save" from a corpus in which nothing could
/// have been saved. So the shapes below are written out and named, one of them
/// is deliberately that best case as a control, and the rest are the editing a
/// person actually does.
/// </para><para>
/// This reports; it does not assert a threshold (§8). The two assertions it
/// does make are structural: that the corpus reaches the shape it claims to
/// reach, so a trace set that quietly stopped producing placeholders would go
/// red rather than report a comfortable zero.
/// </para>
/// </remarks>
public sealed class PlaceholderPayloadMeasurement
{
    private static readonly ReplicaId Author = ReplicaIdConversion.FromGuid(
        Guid.Parse("00000000-0000-0000-0000-0000000000a9"));

    /// <summary>One trace's numbers, after collection.</summary>
    private readonly record struct Reading(
        string Shape,
        int Elements,
        int Live,
        int Tombstones,
        int Collected,
        int Placeholders,
        int PayloadBytes,
        int SnapshotBytes,
        int LiveOnlyBytes)
    {
        /// <summary>Placeholder payload as a share of the encoded snapshot.</summary>
        public double PayloadShare =>
            SnapshotBytes == 0 ? 0 : (double)PayloadBytes / SnapshotBytes;

        /// <summary>
        /// What the placeholders cost in total, payload and structure together.
        /// </summary>
        /// <remarks>
        /// The upper bound on any change in this direction, measured against a
        /// snapshot of the live elements alone. That snapshot is <b>not legal</b>
        /// — §5 keeps placeholder positions because right origins can name
        /// tombstones, and a replica that dropped them would place concurrent
        /// inserts differently. It is here as the denominator: it says how much
        /// of the placeholder problem dropping the payload actually addresses.
        /// </remarks>
        public double PlaceholderShare =>
            SnapshotBytes == 0 ? 0 : (double)(SnapshotBytes - LiveOnlyBytes) / SnapshotBytes;
    }

    [Fact]
    public void Placeholder_payload_across_realistic_edit_shapes()
    {
        var readings = new List<Reading>
        {
            Measure("append then trim the tail (7.3's shape, the control)", AppendThenTrim),
            Measure("write and revise: delete a word from the middle", WriteAndRevise),
            Measure("draft and rewrite: replace a sentence in place", DraftAndRewrite),
            Measure("backspace while typing", BackspaceWhileTyping),
            Measure("a long document edited over months", LongDocumentEdited),
        };

        var report = new StringBuilder();
        report.AppendLine("Row 29 — what a retained tombstone's payload costs");
        report.AppendLine();
        report.AppendLine(
            "| shape | elements | live | tombstones | collected | placeholders | snapshot B | payload B | payload share | all-placeholder share |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        foreach (var r in readings)
        {
            report.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {r.Shape} | {r.Elements} | {r.Live} | {r.Tombstones} | {r.Collected} | {r.Placeholders} | {r.SnapshotBytes} | {r.PayloadBytes} | {r.PayloadShare:P1} | {r.PlaceholderShare:P1} |"));
        }

        TestContext.Current.SendDiagnosticMessage(report.ToString());
        Editor.Api.Tests.Load.Results.Write(report.ToString());

        // THE CORPUS REACHES THE SHAPE IT CLAIMS TO. A trace set in which
        // everything collects would report a comfortable zero saving from a
        // measurement that could not have found one — which is the finding 7.3
        // made about the collection suite, arriving inside the measurement
        // written to answer it.
        var withPlaceholders = readings.Count(r => r.Placeholders > 0);
        Assert.True(
            withPlaceholders >= 4,
            $"only {withPlaceholders} of {readings.Count} shapes left a placeholder; "
                + "the corpus is back to testing trailing runs.");

        // And the control really is the best case, so the comparison between
        // "the shape the old suite used" and "the shapes people produce" is a
        // comparison and not a coincidence. All but one: §5's rule 4 keeps the
        // leading tombstone of every collected run, so a trailing run of twelve
        // collects eleven and leaves its leader. Asserting zero was wrong and
        // this measurement said so on its first run.
        Assert.Equal(1, readings[0].Placeholders);
        Assert.True(readings[0].Collected == readings[0].Tombstones - 1);
    }

    private static Reading Measure(string shape, Action<Replica> edit)
    {
        var replica = new Replica(Author);
        edit(replica);

        var elements = replica.Export();
        var tombstones = elements.Count(e => e.IsDeleted);

        // Everything this author wrote is causally stable: one replica, and the
        // watermark is its own next-expected sequence. Collection's other three
        // rules are what decide the outcome, which is the point.
        var collected = replica.Collect(
            new Dictionary<ReplicaId, ulong> { [Author] = NextSeq(elements) });

        var after = replica.Export();
        var placeholders = after.Count(e => e.IsDeleted);

        var payload = 0;
        foreach (var element in after)
        {
            if (element.IsDeleted)
            {
                payload += element.Value.Utf8SequenceLength;
            }
        }

        // The illegal control: the same document with the placeholders simply
        // absent. Not a proposal — §5 keeps their positions because right
        // origins can name them — but the only way to say what share of the
        // cost the payload actually is.
        var live = after.Where(e => !e.IsDeleted).ToList();

        return new Reading(
            shape,
            elements.Count,
            elements.Count - tombstones,
            tombstones,
            collected,
            placeholders,
            payload,
            SnapshotBinary.Encode(after, replica.VersionVector).Length,
            SnapshotBinary.Encode(live, replica.VersionVector).Length);
    }

    /// <summary>One past the highest sequence this author used.</summary>
    private static ulong NextSeq(IReadOnlyList<ElementState> elements)
    {
        ulong next = 0;
        foreach (var element in elements)
        {
            if (element.Id.Replica.Equals(Author) && element.Id.Seq >= next)
            {
                next = element.Id.Seq + 1;
            }
        }

        return next;
    }

    /// <summary>7.3's shape: type, then delete from the end. Everything collects.</summary>
    private static void AppendThenTrim(Replica replica)
    {
        Type(replica, 0, "the quick brown fox jumps over the lazy dog");

        // From the end backwards, which is what trimming a line is.
        for (var i = 0; i < 12; i++)
        {
            replica.Delete(replica.Text.Length - 1);
        }
    }

    /// <summary>Delete a word from the middle of a sentence, repeatedly.</summary>
    private static void WriteAndRevise(Replica replica)
    {
        Type(replica, 0, "the quick brown fox jumps over the lazy dog every single morning");

        // "quick " and "lazy " go: interior runs, each with a visible right
        // child hanging off the last tombstone.
        Remove(replica, replica.Text.IndexOf("quick ", StringComparison.Ordinal), 6);
        Remove(replica, replica.Text.IndexOf("lazy ", StringComparison.Ordinal), 5);
        Remove(replica, replica.Text.IndexOf("every ", StringComparison.Ordinal), 6);
    }

    /// <summary>Replace a sentence with another in the same place.</summary>
    private static void DraftAndRewrite(Replica replica)
    {
        Type(replica, 0, "First sentence. The middle one needs work. Third sentence.");

        var at = replica.Text.IndexOf("The middle one needs work.", StringComparison.Ordinal);
        Remove(replica, at, "The middle one needs work.".Length);
        Type(replica, at, "This one reads better.");
    }

    /// <summary>Type, back up, retype — the commonest edit there is.</summary>
    private static void BackspaceWhileTyping(Replica replica)
    {
        Type(replica, 0, "collaborative editting");

        // Back over "itting", then type it correctly. The tombstones are at the
        // end when they are made and in the middle once the correction lands.
        for (var i = 0; i < 6; i++)
        {
            replica.Delete(replica.Text.Length - 1);
        }

        Type(replica, replica.Text.Length, "iting is hard");
    }

    /// <summary>Many small revisions spread through a long document.</summary>
    private static void LongDocumentEdited(Replica replica)
    {
        const string Paragraph = "the quick brown fox jumps over the lazy dog and keeps going. ";
        for (var i = 0; i < 20; i++)
        {
            Type(replica, replica.Text.Length, Paragraph);
        }

        // A revision every paragraph or so, at a position that is interior by
        // construction — never the tail, which is the case that collects.
        for (var i = 0; i < 20; i++)
        {
            var at = (i * Paragraph.Length) + 4;
            if (at + 6 < replica.Text.Length)
            {
                Remove(replica, at, 6);
            }
        }
    }

    private static void Type(Replica replica, int at, string text)
    {
        var offset = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            replica.Insert(at + offset, rune);
            offset++;
        }
    }

    private static void Remove(Replica replica, int at, int count)
    {
        // The same index each time: each delete tombstones whatever is now at
        // `at`, so walking forward would skip every second character.
        for (var i = 0; i < count; i++)
        {
            replica.Delete(at);
        }
    }
}
