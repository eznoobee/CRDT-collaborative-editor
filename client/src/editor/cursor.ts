import { elementKey, type ElementId, type Replica } from '../crdt';

/**
 * Which side of an element a caret sits on.
 *
 * @remarks
 * A caret lives in the *gap* between two elements, and a gap has no identity of
 * its own — so it is named by one of its neighbours plus the side it is on.
 * Which neighbour matters: it decides where concurrently inserted text lands
 * relative to the caret. `after` keeps the caret to the left of anything a peer
 * inserts into the same gap; `before` keeps it to the right.
 *
 * For a selection the two ends take opposite biases — start `after` its
 * predecessor, end `before` its successor — so text inserted at either boundary
 * falls outside the selection rather than being silently swept into whatever the
 * user does next.
 */
export type Bias = 'before' | 'after';

/**
 * A caret position that survives concurrent edits (§9).
 *
 * @param element - The element the caret is anchored to, or null at the edge of
 * the document in the direction of the bias.
 */
export interface Anchor {
  readonly element: ElementId | null;
  readonly bias: Bias;
}

/**
 * The anchor for a caret currently at a given visible index.
 *
 * @remarks
 * §9 forbids integer indices for exactly the reason this function exists: an
 * index is invalidated by any concurrent edit earlier in the document, so a
 * remote collaborator typing above you drags your caret backwards through your
 * own sentence. The index is only ever a momentary translation of the DOM's
 * idea of where the caret is.
 */
export function anchorAt(replica: Replica, index: number, bias: Bias = 'after'): Anchor {
  const visible = replica.visibleIds;

  if (bias === 'after') {
    // The gap after the element at index - 1. At the very start there is no
    // such element, and null means "the beginning" rather than "unknown".
    //
    // The index is clamped rather than trusted: it comes from the DOM, and a
    // selection can outlive the text it pointed at by a frame. `?? null` is the
    // same decision made once more — an out-of-range index means an edge, and
    // an edge is what null names.
    return { element: index <= 0 ? null : visible[Math.min(index, visible.length) - 1] ?? null, bias };
  }

  return { element: index >= visible.length ? null : visible[Math.max(index, 0)] ?? null, bias };
}

/**
 * Where an anchor now sits, as a visible index.
 *
 * @remarks
 * The anchored element may have been deleted by someone else while the caret
 * was on it. The caret then moves to the nearest surviving neighbour **in the
 * direction the bias already committed to** — which keeps a caret that was after
 * some text after whatever remains of that text, rather than jumping to the top
 * of the document or to wherever an index happens to land.
 *
 * Falling back through document order rather than to index 0 is the whole
 * difference between "the sentence you were editing was deleted, so you are now
 * where it used to be" and "you are now somewhere else entirely".
 */
export function resolve(replica: Replica, anchor: Anchor): number {
  const visible = replica.visibleIds;

  if (anchor.element === null) {
    return anchor.bias === 'after' ? 0 : visible.length;
  }

  const target = elementKey(anchor.element);
  const position = visible.findIndex((id) => elementKey(id) === target);

  if (position >= 0) {
    return anchor.bias === 'after' ? position + 1 : position;
  }

  // Deleted. Walk document order — tombstones included, because they are what
  // preserves the relationship between the anchor and its surviving
  // neighbours — and take the first element that is still visible.
  const all = replica.allIds;
  const index = all.findIndex((id) => elementKey(id) === target);

  if (index < 0) {
    // An element this replica has never seen: the caret was restored from a
    // store written before a catch-up discarded state (§9). The document start
    // is the only position that is certainly valid.
    return anchor.bias === 'after' ? 0 : visible.length;
  }

  const alive = new Set(visible.map(elementKey));

  if (anchor.bias === 'after') {
    for (let at = index - 1; at >= 0; at--) {
      const previous = all[at];
      if (previous === undefined) {
        continue;
      }

      const key = elementKey(previous);
      if (alive.has(key)) {
        return visible.findIndex((id) => elementKey(id) === key) + 1;
      }
    }

    return 0;
  }

  for (let at = index + 1; at < all.length; at++) {
    const next = all[at];
    if (next === undefined) {
      continue;
    }

    const key = elementKey(next);
    if (alive.has(key)) {
      return visible.findIndex((id) => elementKey(id) === key);
    }
  }

  return visible.length;
}
