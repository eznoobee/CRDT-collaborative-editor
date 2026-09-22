using System.Globalization;
using System.Text;

namespace Editor.Infrastructure.Serialization;

/// <summary>
/// The normalised JSON conventions of PROJECT_SPEC.md §9.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than delegating to a JSON library. "Byte-identical across
/// two languages" is a property of the serialiser, not of the data, and the
/// defaults differ in exactly the places that matter: C# escapes non-ASCII and
/// JavaScript does not, neither guarantees key order, and sorting strings
/// compares UTF-16 code units in both while the specification calls for code
/// points.
/// </para>
/// <para>
/// One implementation, used by both snapshots and the conformance runner, so a
/// snapshot and a conformance artefact are directly comparable (§6).
/// </para>
/// </remarks>
public static class NormalisedJson
{
    /// <summary>Two-space indentation, one level per depth.</summary>
    public static string Indent(int depth) => new(' ', depth * 2);

    /// <summary>
    /// Escapes only what JSON requires — the quote, the backslash and C0
    /// controls. Non-ASCII is emitted literally; <c>/</c> is left alone, since
    /// escaping it is legal but never required and the two languages disagree by
    /// default.
    /// </summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

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

    /// <summary>
    /// Orders by Unicode code point, not UTF-16 code unit.
    /// </summary>
    /// <remarks>
    /// The two differ above the BMP, and .NET's ordinal comparison is by code
    /// unit. Keys here are ASCII today, so the orders agree; spelling it out
    /// keeps that from becoming an assumption nobody wrote down.
    /// </remarks>
    public static int CompareByCodePoint(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var x = left.EnumerateRunes().ToArray();
        var y = right.EnumerateRunes().ToArray();

        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            if (x[i].Value != y[i].Value)
            {
                return x[i].Value.CompareTo(y[i].Value);
            }
        }

        return x.Length.CompareTo(y.Length);
    }

    /// <summary>Renders a string map with keys in code-point order.</summary>
    public static void AppendMap(
        StringBuilder builder, int depth, string key, IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(map);

        builder.Append(Indent(depth)).Append(Quote(key)).Append(": {");
        if (map.Count == 0)
        {
            builder.Append('}');
            return;
        }

        builder.Append('\n');
        var keys = map.Keys.ToList();
        keys.Sort(CompareByCodePoint);

        for (var i = 0; i < keys.Count; i++)
        {
            builder.Append(Indent(depth + 1))
                   .Append(Quote(keys[i])).Append(": ").Append(Quote(map[keys[i]]))
                   .Append(i == keys.Count - 1 ? "\n" : ",\n");
        }

        builder.Append(Indent(depth)).Append('}');
    }

    /// <summary>A 64-bit value as a decimal string (§6).</summary>
    public static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A 64-bit value as a decimal string (§6).</summary>
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
