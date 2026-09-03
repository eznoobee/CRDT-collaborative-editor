import {
  codePointLength,
  codePoints,
  splitsCodePoint,
  toCodePoint,
  toUtf16,
} from './textUnits';

/**
 * §9's code-point boundaries.
 *
 * The vacuity risk, named before these were written and the reason every
 * fixture below is what it is: **on BMP-only text the identity function is
 * correct.** A suite of ASCII cases passes against a module that does no
 * conversion at all, which is precisely the bug — the two units agree until
 * someone types an emoji, and then they disagree by one per astral character
 * and nothing says so. So no test here uses text that would pass under
 * `x => x`, and the first test asserts that directly rather than leaving it to
 * the reader to notice.
 */

/** Two BMP letters around one astral character: 3 code points, 4 UTF-16 units. */
const ASTRAL = 'a😀b';

/** Woman + ZWJ + woman + ZWJ + girl: 5 code points, one visible glyph. */
const FAMILY = '👩‍👩‍👧';

describe('the two units disagree, which is the point', () => {
  it('counts differently from String.length wherever an astral character appears', () => {
    // If this ever holds, every other test in the file is testing the identity
    // function and would pass without the module existing.
    expect(codePointLength(ASTRAL)).toBe(3);
    expect(ASTRAL.length).toBe(4);
    expect(codePointLength(ASTRAL)).not.toBe(ASTRAL.length);

    expect(codePointLength(FAMILY)).toBe(5);
    expect(FAMILY.length).toBe(8);
  });

  it('agrees on text that has no astral character', () => {
    // The other half: a converter that always disagreed would be equally wrong,
    // and ASCII is most of what anyone types.
    expect(codePointLength('hello')).toBe(5);
    expect(toUtf16('hello', 3)).toBe(3);
    expect(toCodePoint('hello', 3)).toBe(3);
  });
});

describe('code point index to UTF-16 offset', () => {
  it('skips a surrogate pair as one position', () => {
    expect(toUtf16(ASTRAL, 0)).toBe(0);
    expect(toUtf16(ASTRAL, 1)).toBe(1);

    // The character after the emoji is at UTF-16 3, not 2. A converter that
    // returned the index unchanged gives 2, which addresses the low surrogate.
    expect(toUtf16(ASTRAL, 2)).toBe(3);
    expect(toUtf16(ASTRAL, 3)).toBe(4);
  });

  it('clamps past the end rather than running off it', () => {
    // A CRDT index can outrun the rendered text for a moment: a remote delete
    // has been applied and the DOM has not caught up. Throwing here would turn
    // a frame of staleness into a broken editor.
    expect(toUtf16(ASTRAL, 99)).toBe(4);
    expect(toUtf16(ASTRAL, -1)).toBe(0);
  });

  it('walks a ZWJ sequence one code point at a time', () => {
    // §9 accepts that the CRDT sees five elements here, not one glyph. The
    // offsets are 0, 2, 3, 5, 6 — the emoji are surrogate pairs and the two
    // joiners are single units.
    expect([0, 1, 2, 3, 4].map((index) => toUtf16(FAMILY, index))).toEqual([0, 2, 3, 5, 6]);
    expect(toUtf16(FAMILY, 5)).toBe(8);
  });
});

describe('UTF-16 offset to code point index', () => {
  it('is the inverse wherever the offset starts a code point', () => {
    for (const index of [0, 1, 2, 3]) {
      expect(toCodePoint(ASTRAL, toUtf16(ASTRAL, index))).toBe(index);
    }

    for (const index of [0, 1, 2, 3, 4, 5]) {
      expect(toCodePoint(FAMILY, toUtf16(FAMILY, index))).toBe(index);
    }
  });

  it('resolves an offset inside a surrogate pair to the code point holding it', () => {
    // The browser produces this on its own — a selection dragged through an
    // emoji, a composition event mid-character. Rounding down keeps the caret
    // on the character the user is pointing at; rounding up moves it past.
    expect(toCodePoint(ASTRAL, 2)).toBe(1);
    expect(splitsCodePoint(ASTRAL, 2)).toBe(true);

    // And an offset that starts a code point does not report as split, so
    // "always true" cannot pass.
    expect(splitsCodePoint(ASTRAL, 1)).toBe(false);
    expect(splitsCodePoint(ASTRAL, 3)).toBe(false);
  });

  it('clamps past the end', () => {
    expect(toCodePoint(ASTRAL, 99)).toBe(3);
    expect(toCodePoint(ASTRAL, -1)).toBe(0);
  });
});

describe('splitting into code points', () => {
  it('matches what the CRDT will store', () => {
    // The core inserts one element per code point, so this list is the
    // operations a paste produces. A split on `text.split('')` gives four
    // entries for ASTRAL, two of them lone surrogates, and the document would
    // hold half-characters that no later join can repair.
    expect(codePoints(ASTRAL)).toEqual(['a', '😀', 'b']);
    expect(codePoints(FAMILY)).toHaveLength(5);
    expect(codePoints(FAMILY).join('')).toBe(FAMILY);
  });

  it('keeps a lone surrogate rather than throwing', () => {
    // A string may legally contain one, and an editor that refused to measure
    // malformed text would be unusable rather than correct. It counts as its
    // own code point, which is the only answer that lets the offsets stay
    // consistent either side of it.
    const lone = `a${String.fromCharCode(0xd800)}b`;

    expect(codePointLength(lone)).toBe(3);
    expect(toUtf16(lone, 2)).toBe(2);
    expect(toCodePoint(lone, 2)).toBe(2);
  });
});
