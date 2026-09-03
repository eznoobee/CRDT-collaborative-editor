import { anchorAt, resolve } from './cursor';
import { Replica, parseReplicaId, type Operation } from '../crdt';

/**
 * Cursors anchored to element ids (§9).
 *
 * The vacuity risk, named before these were written and the reason every test
 * below has a *concurrent* edit in it: **with no concurrent edit, an integer
 * index is correct.** A suite that anchors a caret and immediately resolves it
 * passes against `anchorAt = i => i`, which is precisely what §9 forbids. So
 * every case here changes the document between anchoring and resolving, and one
 * asserts the index moved while the anchor did not — the difference an index
 * cannot express.
 */

const LOCAL = parseReplicaId('00000000-0000-0000-0000-00000000000a');
const REMOTE = parseReplicaId('00000000-0000-0000-0000-00000000000b');

/** A replica holding `text`, plus a peer that can edit it concurrently. */
function pair(text: string): { local: Replica; remote: Replica } {
  const local = new Replica(LOCAL);
  const operations: Operation[] = [...text].map((value, index) => local.insert(index, value));

  const remote = new Replica(REMOTE);
  for (const operation of operations) {
    remote.apply(operation);
  }

  return { local, remote };
}

/** Applies one replica's operations to the other. */
function deliver(from: readonly Operation[], to: Replica): void {
  for (const operation of from) {
    to.apply(operation);
  }
}

describe('an anchored caret', () => {
  it('stays on the same character when a peer types earlier in the document', () => {
    // The case §9 names. An index of 5 in "hello| world" becomes an index of 5
    // in "XXXhe|llo world" — three characters backwards, inside a word the user
    // was not editing.
    const { local, remote } = pair('hello world');
    const anchor = anchorAt(local, 5);

    expect(resolve(local, anchor)).toBe(5);

    const inserted = [...'XXX'].map((value, index) => remote.insert(index, value));
    deliver(inserted, local);

    // The index moved, which is what makes the anchor necessary...
    expect(resolve(local, anchor)).toBe(8);

    // ...and the caret is still after the same character.
    expect([...local.text][resolve(local, anchor) - 1]).toBe('o');
    expect(local.text).toBe('XXXhello world');
  });

  it('does not move when a peer types after it', () => {
    const { local, remote } = pair('abc');
    const anchor = anchorAt(local, 1);

    const inserted = [...'ZZ'].map((value, index) => remote.insert(3 + index, value));
    deliver(inserted, local);

    expect(resolve(local, anchor)).toBe(1);
  });

  it('falls back to the nearest surviving neighbour when its element is deleted', () => {
    // The sentence the caret was in gets deleted by someone else. The caret
    // lands where that text used to be, not at the top of the document.
    const { local, remote } = pair('one two three');
    const anchor = anchorAt(local, 7); // after "one two"

    const deletes = [3, 3, 3, 3].map(() => remote.delete(3)); // removes " two"
    deliver(deletes, local);

    expect(local.text).toBe('one three');
    expect(resolve(local, anchor)).toBe(3);
    expect([...local.text][resolve(local, anchor) - 1]).toBe('e');
  });

  it('reaches the document start when everything before it is gone', () => {
    const { local, remote } = pair('abc');
    const anchor = anchorAt(local, 2);

    deliver([remote.delete(0), remote.delete(0)], local);

    expect(local.text).toBe('c');
    expect(resolve(local, anchor)).toBe(0);
  });
});

describe('the bias', () => {
  it('decides which side of a concurrent insertion the caret ends up on', () => {
    // The property that makes a bias necessary rather than decorative: both
    // carets are in the same gap, and text inserted into that gap has to go on
    // one side of each. Without a bias they would both land on the same side
    // and a selection boundary would swallow text nobody selected.
    const { local, remote } = pair('ab');

    const after = anchorAt(local, 1, 'after');
    const before = anchorAt(local, 1, 'before');

    expect(resolve(local, after)).toBe(1);
    expect(resolve(local, before)).toBe(1);

    deliver([remote.insert(1, 'X')], local);

    expect(local.text).toBe('aXb');
    expect(resolve(local, after)).toBe(1);
    expect(resolve(local, before)).toBe(2);
  });

  it('anchors the ends of a document to the edges rather than to an element', () => {
    const { local, remote } = pair('mid');

    const start = anchorAt(local, 0, 'after');
    const end = anchorAt(local, 3, 'before');

    expect(start.element).toBeNull();
    expect(end.element).toBeNull();

    deliver([remote.insert(0, '<'), remote.insert(4, '>')], local);

    // The edges stay the edges. A caret parked at the end of the document
    // follows text appended after it, which is what a person expects from
    // "the end".
    expect(resolve(local, start)).toBe(0);
    expect(resolve(local, end)).toBe(local.visibleIds.length);
  });

  it('resolves an anchor on an element this replica has never seen', () => {
    // §9: a caret restored from IndexedDB after a catch-up discarded local
    // state names an element that no longer exists here. Guessing a position
    // would put the caret somewhere arbitrary in the middle of the document;
    // the start is the one position that is certainly valid.
    const { local } = pair('abc');
    const stranger = new Replica(REMOTE);
    stranger.insert(0, 'z');

    const anchor = { element: stranger.visibleIds[0], bias: 'after' as const };

    expect(resolve(local, anchor)).toBe(0);
  });
});
