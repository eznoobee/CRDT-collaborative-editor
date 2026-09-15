using System.Globalization;
using System.Text;
using System.Text.Json;
using Crdt.Core;
using Editor.Infrastructure.Persistence;

namespace Editor.Infrastructure.Serialization;

/// <summary>
/// Encodes and decodes a document snapshot (PROJECT_SPEC.md §6).
/// </summary>
/// <remarks>
/// <para>
/// Uses the normalised JSON conventions of §9, so the <c>text</c> and
/// <c>versionVector</c> a snapshot carries are byte-comparable with the same
/// fields in a conformance artefact. A snapshot additionally carries
/// <c>elements</c>, which a conformance result has no need of: text is not
/// resumable, and operations arriving after a snapshot attach to elements by id,
/// tombstones included.
/// </para>
/// <para>
/// Sequence numbers are decimal strings throughout (§6): JSON numbers are
/// doubles and stop round-tripping above 2^53.
/// </para>
/// </remarks>
public static class SnapshotSerializer
{
    public const int Version = 1;

    /// <summary>Encodes a replica's state.</summary>
    public static string Serialize(Replica replica)
    {
        ArgumentNullException.ThrowIfNull(replica);

        var builder = new StringBuilder();
        builder.Append("{\n");
        builder.Append(NormalisedJson.Indent(1)).Append("\"elements\": [");

        var elements = replica.Export();
        if (elements.Count == 0)
        {
            builder.Append(']');
        }
        else
        {
            builder.Append('\n');
            for (var i = 0; i < elements.Count; i++)
            {
                AppendElement(builder, elements[i]);
                builder.Append(i == elements.Count - 1 ? "\n" : ",\n");
            }

            builder.Append(NormalisedJson.Indent(1)).Append(']');
        }

        builder.Append(",\n");
        builder.Append(NormalisedJson.Indent(1)).Append("\"text\": ")
               .Append(NormalisedJson.Quote(replica.Text)).Append(",\n");
        builder.Append(NormalisedJson.Indent(1)).Append("\"v\": ")
               .Append(Version.ToString(CultureInfo.InvariantCulture)).Append(",\n");

        NormalisedJson.AppendMap(
            builder,
            1,
            "versionVector",
            replica.VersionVector.ToDictionary(
                e => ReplicaIdConversion.ToGuid(e.Key).ToString(),
                e => NormalisedJson.Number(e.Value),
                StringComparer.Ordinal));

        builder.Append('\n').Append("}\n");
        return builder.ToString();
    }

    /// <summary>Decodes a snapshot into a replica.</summary>
    public static Replica Deserialize(ReplicaId id, string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var version = root.GetProperty("v").GetInt32();
        if (version != Version)
        {
            throw new NotSupportedException($"Snapshot version {version} is not supported.");
        }

        var elements = root.GetProperty("elements").EnumerateArray()
            .Select(ReadElement)
            .ToArray();

        var versionVector = new Dictionary<ReplicaId, ulong>();
        foreach (var entry in root.GetProperty("versionVector").EnumerateObject())
        {
            versionVector[ReplicaIdConversion.FromGuid(Guid.Parse(entry.Name))] =
                ulong.Parse(entry.Value.GetString()!, CultureInfo.InvariantCulture);
        }

        return Replica.Import(id, elements, versionVector);
    }

    private static void AppendElement(StringBuilder builder, ElementState element)
    {
        // Keys in code-point order: deleted, id, parent, rightOrigin, side, value.
        builder.Append(NormalisedJson.Indent(2)).Append("{\n");
        builder.Append(NormalisedJson.Indent(3)).Append("\"deleted\": ")
               .Append(element.IsDeleted ? "true" : "false").Append(",\n");
        builder.Append(NormalisedJson.Indent(3)).Append("\"id\": ")
               .Append(NormalisedJson.Quote(Format(element.Id))).Append(",\n");
        builder.Append(NormalisedJson.Indent(3)).Append("\"parent\": ")
               .Append(element.Parent is { } parent ? NormalisedJson.Quote(Format(parent)) : "null")
               .Append(",\n");
        builder.Append(NormalisedJson.Indent(3)).Append("\"rightOrigin\": ")
               .Append(element.RightOrigin is { } origin ? NormalisedJson.Quote(Format(origin)) : "null")
               .Append(",\n");
        builder.Append(NormalisedJson.Indent(3)).Append("\"side\": ")
               .Append(NormalisedJson.Quote(element.Side == Side.Left ? "L" : "R")).Append(",\n");
        builder.Append(NormalisedJson.Indent(3)).Append("\"value\": ")
               .Append(NormalisedJson.Quote(element.Value.ToString())).Append('\n');
        builder.Append(NormalisedJson.Indent(2)).Append('}');
    }

    private static ElementState ReadElement(JsonElement element)
    {
        var side = element.GetProperty("side").GetString() switch
        {
            "L" => Side.Left,
            "R" => Side.Right,
            var other => throw new FormatException($"Unknown side '{other}'."),
        };

        var runes = element.GetProperty("value").GetString()!.EnumerateRunes().ToArray();
        if (runes.Length != 1)
        {
            throw new FormatException("An element carries exactly one code point (§7).");
        }

        return new ElementState(
            Parse(element.GetProperty("id").GetString()!),
            runes[0],
            element.GetProperty("parent").ValueKind == JsonValueKind.Null
                ? null
                : Parse(element.GetProperty("parent").GetString()!),
            side,
            element.GetProperty("rightOrigin").ValueKind == JsonValueKind.Null
                ? null
                : Parse(element.GetProperty("rightOrigin").GetString()!),
            element.GetProperty("deleted").GetBoolean());
    }

    /// <summary>An element id as <c>replica:seq</c>, the sequence in decimal.</summary>
    private static string Format(ElementId id) =>
        $"{ReplicaIdConversion.ToGuid(id.Replica)}:{NormalisedJson.Number(id.Seq)}";

    private static ElementId Parse(string text)
    {
        var separator = text.LastIndexOf(':');
        if (separator < 0)
        {
            throw new FormatException($"'{text}' is not an element id.");
        }

        return new ElementId(
            ReplicaIdConversion.FromGuid(Guid.Parse(text[..separator])),
            ulong.Parse(text[(separator + 1)..], CultureInfo.InvariantCulture));
    }
}
