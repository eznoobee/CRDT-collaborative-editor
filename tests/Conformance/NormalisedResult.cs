using System.Text;
using Editor.Infrastructure.Serialization;

namespace Conformance;

/// <summary>
/// Writes the normalised result file defined in PROJECT_SPEC.md §9.
/// </summary>
/// <remarks>
/// The escaping and ordering rules live in
/// <see cref="NormalisedJson"/> so there is one C# implementation of them rather
/// than one here and another for snapshots. The TypeScript serialiser is the
/// second implementation, and the two disagreeing is what this file exists to
/// detect — a third C# copy would only add a way for the check to pass while the
/// production encoder was wrong.
/// </remarks>
public static class NormalisedResult
{
    /// <summary>Renders results in the §9 format.</summary>
    public static string Render(string implementation, IEnumerable<TraceResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"implementation\": ").Append(NormalisedJson.Quote(implementation)).Append(",\n");
        sb.Append("  \"results\": [\n");

        var ordered = results
            .OrderBy(r => r.Name, Comparer<string>.Create(NormalisedJson.CompareByCodePoint))
            .ToArray();

        for (var i = 0; i < ordered.Length; i++)
        {
            var result = ordered[i];
            sb.Append("    {\n");
            sb.Append("      \"name\": ").Append(NormalisedJson.Quote(result.Name)).Append(",\n");
            NormalisedJson.AppendMap(sb, 3, "replicaTexts", result.ReplicaTexts);
            sb.Append(",\n");
            sb.Append("      \"snapshot\": ").Append(NormalisedJson.Quote(result.Snapshot))
              .Append(",\n");
            sb.Append("      \"text\": ").Append(NormalisedJson.Quote(result.Text)).Append(",\n");
            NormalisedJson.AppendMap(sb, 3, "versionVector", result.VersionVector);
            sb.Append('\n');
            sb.Append("    }").Append(i == ordered.Length - 1 ? "\n" : ",\n");
        }

        sb.Append("  ],\n");
        sb.Append("  \"v\": 2\n");
        sb.Append("}\n");
        return sb.ToString();
    }
}
