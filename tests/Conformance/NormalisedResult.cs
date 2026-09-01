using System.Globalization;
using System.Text;

namespace Conformance;

/// <summary>
/// Writes the normalised result file defined in PROJECT_SPEC.md §9.
/// </summary>
/// <remarks>
/// Hand-rolled rather than delegating to a JSON library. "Byte-identical across
/// two languages" is a property of the serialiser, not of the data, and the
/// defaults differ in exactly the places that matter: C# escapes non-ASCII and
/// JavaScript does not, and neither guarantees key order. Writing both by hand
/// to one specification is the only way the comparison means anything.
/// </remarks>
public static class NormalisedResult
{
    /// <summary>Renders results in the §9 format.</summary>
    public static string Render(string implementation, IEnumerable<TraceResult> results)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"implementation\": ").Append(Quote(implementation)).Append(",\n");
        sb.Append("  \"results\": [\n");

        var ordered = results.OrderBy(r => r.Name, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var result = ordered[i];
            sb.Append("    {\n");
            sb.Append("      \"name\": ").Append(Quote(result.Name)).Append(",\n");
            AppendMap(sb, "replicaTexts", result.ReplicaTexts);
            sb.Append(",\n");
            sb.Append("      \"text\": ").Append(Quote(result.Text)).Append(",\n");
            AppendMap(sb, "versionVector", result.VersionVector);
            sb.Append('\n');
            sb.Append("    }").Append(i == ordered.Length - 1 ? "\n" : ",\n");
        }

        sb.Append("  ],\n");
        sb.Append("  \"v\": 1\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    private static void AppendMap(
        StringBuilder sb, string key, IReadOnlyDictionary<string, string> map)
    {
        sb.Append("      ").Append(Quote(key)).Append(": {");
        if (map.Count == 0)
        {
            sb.Append('}');
            return;
        }

        sb.Append('\n');

        // Keys sorted by Unicode code point. Ordinal comparison on .NET strings
        // is by UTF-16 code unit, which differs above the BMP; replica ids are
        // ASCII so the two agree here, and the sort is spelled out rather than
        // assumed in case that stops being true.
        var keys = map.Keys.OrderBy(CodePoints, CodePointComparer.Instance).ToArray();
        for (var i = 0; i < keys.Length; i++)
        {
            sb.Append("        ").Append(Quote(keys[i])).Append(": ")
              .Append(Quote(map[keys[i]]))
              .Append(i == keys.Length - 1 ? "\n" : ",\n");
        }

        sb.Append("      }");
    }

    private static int[] CodePoints(string value) =>
        [.. value.EnumerateRunes().Select(r => r.Value)];

    private sealed class CodePointComparer : IComparer<int[]>
    {
        public static readonly CodePointComparer Instance = new();

        public int Compare(int[]? x, int[]? y)
        {
            if (x is null || y is null)
            {
                return (x is null ? 0 : 1) - (y is null ? 0 : 1);
            }

            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                if (x[i] != y[i])
                {
                    return x[i].CompareTo(y[i]);
                }
            }

            return x.Length.CompareTo(y.Length);
        }
    }

    /// <summary>
    /// Escapes only what JSON requires: the quote, the backslash and C0
    /// controls. Non-ASCII is emitted literally (§9).
    /// </summary>
    private static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
