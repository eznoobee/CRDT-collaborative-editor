/**
 * Translating between what the CRDT counts and what the DOM counts (§9).
 *
 * The core addresses text in **code points**: one element per code point, and
 * §1 keeps it ignorant of anything else. The DOM, `Selection`, `input` events
 * and every JavaScript string index address text in **UTF-16 code units**. The
 * two agree exactly until someone types an emoji, and then they disagree by one
 * for every astral character earlier in the document — silently, because both
 * numbers are plausible small integers and neither carries its unit.
 *
 * This module is the only place that converts, and it lives above the core: §1
 * makes the core dependency-free and code-points-only, and the same core runs in
 * the conformance runner where there is no DOM at all.
 *
 * **Graphemes are deliberately not here.** §9 accepts that deleting an emoji ZWJ
 * sequence removes one code point rather than the whole visible glyph. A
 * grapheme layer would be a third unit with its own rules, and the CRDT would
 * still be storing code points underneath it.
 */

/** How many code points a string holds. */
export function codePointLength(text: string): number {
  let count = 0;
  for (let unit = 0; unit < text.length; unit += unitsAt(text, unit)) {
    count++;
  }

  return count;
}

/**
 * The UTF-16 offset at which a given code point starts.
 *
 * @param text - The document text, as JavaScript holds it.
 * @param index - A code-point index, as the CRDT holds positions.
 * @returns The UTF-16 offset, clamped to the end of the string.
 */
export function toUtf16(text: string, index: number): number {
  if (index <= 0) {
    return 0;
  }

  let remaining = index;
  let offset = 0;

  while (remaining > 0 && offset < text.length) {
    offset += unitsAt(text, offset);
    remaining--;
  }

  return offset;
}

/**
 * The code-point index containing a given UTF-16 offset.
 *
 * @remarks
 * An offset landing **inside** a surrogate pair resolves to the code point that
 * pair belongs to, rather than being rejected or rounded up. The browser
 * produces such offsets on its own — a selection dragged through an emoji, a
 * composition event mid-character — and the alternatives are worse than
 * rounding down: rejecting means the editor throws on a legal user action, and
 * rounding up silently moves the caret past a character the user is pointing at.
 */
export function toCodePoint(text: string, offset: number): number {
  if (offset <= 0) {
    return 0;
  }

  let index = 0;
  let cursor = 0;

  while (cursor < text.length && cursor < offset) {
    const width = unitsAt(text, cursor);

    // The offset falls strictly inside this code point, so the code point
    // containing it is this one — not the next.
    if (cursor + width > offset) {
      return index;
    }

    cursor += width;
    index++;
  }

  return index;
}

/**
 * Whether a UTF-16 offset splits a code point.
 *
 * @remarks
 * Exposed because "we rounded down" is a decision the caller sometimes needs to
 * know about — a delete of a range whose ends were adjusted covers different
 * characters from the one the user drew — and because a silent adjustment is
 * the kind of thing that is invisible until someone reports losing an emoji.
 */
export function splitsCodePoint(text: string, offset: number): boolean {
  if (offset <= 0 || offset >= text.length) {
    return false;
  }

  return isLowSurrogate(text.charCodeAt(offset));
}

/** The code points of a string, in order. */
export function codePoints(text: string): string[] {
  return [...text];
}

/** How many UTF-16 units the code point starting at <paramref/> occupies. */
function unitsAt(text: string, offset: number): number {
  const code = text.charCodeAt(offset);

  // A high surrogate followed by a low one is one code point in two units.
  // A lone surrogate — which a string is allowed to contain — is one unit, and
  // is treated as its own code point rather than throwing: this layer reports
  // what the string is, and refusing to measure malformed text would make the
  // editor unusable rather than correct.
  if (isHighSurrogate(code) && offset + 1 < text.length && isLowSurrogate(text.charCodeAt(offset + 1))) {
    return 2;
  }

  return 1;
}

function isHighSurrogate(code: number): boolean {
  return code >= 0xd800 && code <= 0xdbff;
}

function isLowSurrogate(code: number): boolean {
  return code >= 0xdc00 && code <= 0xdfff;
}
