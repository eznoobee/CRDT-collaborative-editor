import { codePoints } from './textUnits';

/**
 * One contiguous replacement, in code points (§9).
 *
 * @param index - Where the change starts, as a code-point index.
 * @param removed - How many code points it removes.
 * @param inserted - The code points it adds, in order.
 */
export interface Replacement {
  readonly index: number;
  readonly removed: number;
  readonly inserted: readonly string[];
}

/**
 * What changed between two versions of the text, as one replacement.
 *
 * @remarks
 * <p>
 * A `textarea` reports its whole value, not what the user did to it. Turning
 * that back into CRDT operations means finding the change, and finding it in
 * **code points** rather than UTF-16 units: a prefix computed on units can end
 * halfway through a surrogate pair, and the operations derived from it would
 * insert half an emoji as its own element — a document that no later edit can
 * repair, because the element is real and the CRDT will faithfully keep it.
 * </p><p>
 * One replacement rather than a real diff. Every edit a person makes at a
 * keyboard is contiguous — typing, backspace, paste, replacing a selection —
 * and a minimal-diff algorithm would produce smaller operations only for
 * changes no editor generates, at the cost of being the sort of code that is
 * subtly wrong for years. A programmatic bulk change comes out as one large
 * replacement, which is correct, just not minimal.
 * </p><p>
 * The common prefix is taken before the common suffix, so a repeated character
 * resolves consistently: typing a second `l` into `hello` is reported as an
 * insert after the existing pair rather than before it. Either is a correct
 * description of the text change, and picking one keeps two clients that made
 * the same edit from producing different trees.
 * </p>
 */
export function replacementBetween(before: string, after: string): Replacement | null {
  const from = codePoints(before);
  const to = codePoints(after);

  let prefix = 0;
  while (prefix < from.length && prefix < to.length && from[prefix] === to[prefix]) {
    prefix++;
  }

  let suffix = 0;
  while (
    suffix < from.length - prefix
    && suffix < to.length - prefix
    && from[from.length - 1 - suffix] === to[to.length - 1 - suffix]
  ) {
    suffix++;
  }

  const removed = from.length - prefix - suffix;
  const inserted = to.slice(prefix, to.length - suffix);

  if (removed === 0 && inserted.length === 0) {
    return null;
  }

  return { index: prefix, removed, inserted };
}
