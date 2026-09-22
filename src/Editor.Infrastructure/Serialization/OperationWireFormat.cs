using System.Globalization;
using System.Text;
using System.Text.Json;
using Crdt.Core;
using Editor.Infrastructure.Persistence;

namespace Editor.Infrastructure.Serialization;

/// <summary>
/// The wire encoding of a single operation (PROJECT_SPEC.md §6).
/// </summary>
/// <remarks>
/// <para>
/// A second implementation of the same encoding as the TypeScript serialiser,
/// which is why §9's corpus round-trips every trace through this form: an
/// encoding divergence has to fail the build the way an algorithm divergence
/// does, and a shared format that nothing checks is a shared format that drifts.
/// </para>
/// <para>
/// Sequence numbers are decimal strings (§6). "End of document" is a right
/// origin rather than the absence of one, so it is carried explicitly — a left
/// child and a right child at the end of the document both have no right-origin
/// id, and they do not order the same way.
/// </para>
/// </remarks>
public static class OperationWireFormat
{
    /// <summary>Encodes an operation as normalised JSON.</summary>
    public static string Encode(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var builder = new StringBuilder();
        builder.Append("{\n");

        // Keys in code-point order: id, parent, rightOrigin, rightOriginIsEnd,
        // side, target, type, value.
        builder.Append(NormalisedJson.Indent(1)).Append("\"id\": ")
               .Append(NormalisedJson.Quote(Format(operation.Id))).Append(",\n");

        var insert = operation as InsertOperation;
        var delete = operation as DeleteOperation;

        Append(builder, "parent", insert?.Parent);
        Append(builder, "rightOrigin", insert?.RightOrigin);

        builder.Append(NormalisedJson.Indent(1)).Append("\"rightOriginIsEnd\": ")
               .Append(insert is { Side: Side.Right, RightOrigin: null } ? "true" : "false")
               .Append(",\n");

        builder.Append(NormalisedJson.Indent(1)).Append("\"side\": ")
               .Append(insert is null
                   ? "null"
                   : NormalisedJson.Quote(insert.Side == Side.Left ? "L" : "R"))
               .Append(",\n");

        Append(builder, "target", delete?.Target);

        builder.Append(NormalisedJson.Indent(1)).Append("\"type\": ")
               .Append(NormalisedJson.Quote(insert is null ? "delete" : "insert")).Append(",\n");

        builder.Append(NormalisedJson.Indent(1)).Append("\"value\": ")
               .Append(insert is null ? "null" : NormalisedJson.Quote(insert.Value.ToString()))
               .Append('\n');

        builder.Append("}\n");
        return builder.ToString();
    }

    /// <summary>Decodes an operation from normalised JSON.</summary>
    public static Operation Decode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var id = Parse(root.GetProperty("id").GetString()!);

        if (root.GetProperty("type").GetString() == "delete")
        {
            return new DeleteOperation(id, Parse(root.GetProperty("target").GetString()!));
        }

        var side = root.GetProperty("side").GetString() switch
        {
            "L" => Side.Left,
            "R" => Side.Right,
            var other => throw new FormatException($"Unknown side '{other}'."),
        };

        var runes = root.GetProperty("value").GetString()!.EnumerateRunes().ToArray();
        if (runes.Length != 1)
        {
            throw new FormatException("An insert carries exactly one code point (§7).");
        }

        var parent = root.GetProperty("parent");
        var rightOrigin = root.GetProperty("rightOrigin");

        return new InsertOperation(
            id,
            runes[0],
            parent.ValueKind == JsonValueKind.Null ? null : Parse(parent.GetString()!),
            side,
            rightOrigin.ValueKind == JsonValueKind.Null ? null : Parse(rightOrigin.GetString()!));
    }

    private static void Append(StringBuilder builder, string key, ElementId? id)
    {
        builder.Append(NormalisedJson.Indent(1)).Append(NormalisedJson.Quote(key)).Append(": ")
               .Append(id is { } value ? NormalisedJson.Quote(Format(value)) : "null")
               .Append(",\n");
    }

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
