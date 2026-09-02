/**
 * The normalised JSON primitives of PROJECT_SPEC.md §9.
 *
 * The TypeScript half of what `NormalisedJson` is on the C# side, and separate
 * from its callers for the same reason: "byte-identical across two languages" is
 * a property of the serialiser, not of the data, so there is one implementation
 * of the rules per language and not one per caller. `JSON.stringify` is not
 * usable here — its key order and its non-ASCII escaping are exactly the two
 * places the two languages would differ.
 */

/** Compares by Unicode code point, not UTF-16 code unit. */
export function compareByCodePoint(a: string, b: string): number {
  const x = [...a];
  const y = [...b];
  for (let i = 0; i < Math.min(x.length, y.length); i++) {
    const cx = x[i]!.codePointAt(0)!;
    const cy = y[i]!.codePointAt(0)!;
    if (cx !== cy) {
      return cx - cy;
    }
  }
  return x.length - y.length;
}

/** Escapes only what JSON requires; non-ASCII stays literal. */
export function quote(value: string): string {
  let out = '"';
  for (const ch of value) {
    switch (ch) {
      case '"':
        out += '\\"';
        break;
      case '\\':
        out += '\\\\';
        break;
      case '\n':
        out += '\\n';
        break;
      case '\r':
        out += '\\r';
        break;
      case '\t':
        out += '\\t';
        break;
      case '\b':
        out += '\\b';
        break;
      case '\f':
        out += '\\f';
        break;
      default: {
        const code = ch.codePointAt(0)!;
        out += code < 0x20 ? `\\u${code.toString(16).padStart(4, '0')}` : ch;
      }
    }
  }
  return `${out}"`;
}

/** Two spaces per level, as §9 fixes it. */
export function indent(depth: number): string {
  return '  '.repeat(depth);
}

/** Renders a string map with keys in code-point order. */
export function renderMap(depth: number, key: string, map: ReadonlyMap<string, string>): string {
  if (map.size === 0) {
    return `${indent(depth)}${quote(key)}: {}`;
  }

  const keys = [...map.keys()].sort(compareByCodePoint);
  const body = keys.map((k) => `${indent(depth + 1)}${quote(k)}: ${quote(map.get(k)!)}`).join(',\n');

  return `${indent(depth)}${quote(key)}: {\n${body}\n${indent(depth)}}`;
}
