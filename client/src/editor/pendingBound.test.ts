import {
  Replica,
  decodeSnapshot,
  encodeOperations,
  parseReplicaId,
  type Operation,
  type ReplicaId,
} from '../crdt';
import { DocumentSession, MAX_PENDING_OPERATIONS } from './DocumentSession';
import { BURST } from '../limits/use';

/**
 * §5's pending-set bound, set by the layer that owns it (register row 36).
 *
 * @remarks
 * <p>
 * <b>The vacuity risk that let this survive two phases.</b> Both cores already
 * had a complete test of the bound — `causalReadiness.test.ts` here and
 * `CausalReadinessTests` in C# — and both <i>set `maxPending` themselves</i>
 * before overflowing it. Those tests prove the mechanism works, which was never
 * in doubt. What nothing asked was whether any shipped code ever sets it, and
 * the answer for nine phases was no: `DocumentSession` constructed
 * `new Replica(id)` and left the bound at `Number.MAX_SAFE_INTEGER`.
 * </p><p>
 * So nothing in this file sets `maxPending`. Every test here drives a real
 * `DocumentSession` and would go red if the product stopped configuring it —
 * which is the only property worth asserting, given that the mechanism is
 * covered elsewhere.
 * </p><p>
 * <b>Second risk: a bound nothing reaches.</b> A test that overflows at 4 would
 * pass against any bound, including the unbounded one, if it set the bound
 * itself — and against a bound of 4 in the product, which would be wrong in the
 * other direction. These tests sit at two specific numbers taken from outside
 * the implementation: §13.37's largest legitimate use (`BURST`, a peer's
 * offline afternoon) must be <i>accepted</i>, and one past the configured bound
 * must be refused. A limit set below the first or an unbounded one fails.
 * </p><p>
 * <b>Third: the bound is installed in three places.</b> The constructor is the
 * obvious one; `restore` and `adopt` are the paths that follow a reload and a
 * snapshot catch-up, which is exactly when a peer's backlog is largest. Each is
 * tested separately, because a session that lost its bound on reload looks
 * identical to one that never had it.
 * </p>
 */
const PEER = parseReplicaId('00000000-0000-0000-0000-0000000000aa');
const SELF = parseReplicaId('00000000-0000-0000-0000-0000000000bb');

/**
 * `count` operations from `PEER` that can never become ready.
 *
 * Every one is parented on the peer's sequence 0, which is never delivered, so
 * the density rule holds all of them. Built directly rather than by typing into
 * a second replica: 10,001 real inserts is a quadratic traversal and this
 * asserts nothing about placement.
 */
function stranded(count: number): Uint8Array {
  const parent = { replica: PEER, seq: 0n };
  const operations: Operation[] = [];

  for (let i = 1; i <= count; i++) {
    operations.push({
      kind: 'insert',
      id: { replica: PEER, seq: BigInt(i) },
      value: 'a',
      parent,
      side: 'R',
      rightOrigin: null,
    });
  }

  return encodeOperations(operations);
}

function session(id: ReplicaId = SELF): DocumentSession {
  return new DocumentSession(id, () => {});
}

describe("§5's pending-set bound, as the product sets it", () => {
  it('holds a peer’s whole offline afternoon without refusing it', () => {
    // §13.37: the largest legitimate use, in the unit the user acts in. If this
    // goes red the bound has been set below what a real partition delivers, and
    // the failure is a person's afternoon of work refused rather than buffered.
    const document = session();

    document.receive(stranded(BURST));

    expect(document.pendingCount).toBe(BURST);
    expect(document.text).toBe('');
  });

  it('refuses one operation past the bound, and does not drop it', () => {
    const document = session();

    expect(() => { document.receive(stranded(MAX_PENDING_OPERATIONS + 1)); })
      .toThrow(/pending/i);

    // §5 allows exactly one exception to "do not drop" and this is not it. The
    // operations that fitted are still here; the core threw rather than making
    // room, which is what lets the connection layer recover by fetching what is
    // missing instead of diverging quietly.
    expect(document.pendingCount).toBe(MAX_PENDING_OPERATIONS);
  });

  it('keeps the bound across a reload', () => {
    // `restore` builds its replica with `Replica.import`, not the constructor.
    // A bound applied only in the constructor is silently gone here — and a
    // reload is followed by a catch-up, which is when the backlog is largest.
    const before = session();
    const reloaded = DocumentSession.restore(SELF, before.snapshot, () => {});

    expect(() => { reloaded.receive(stranded(MAX_PENDING_OPERATIONS + 1)); })
      .toThrow(/pending/i);
  });

  it('keeps the bound across a snapshot catch-up', () => {
    // `adopt` replaces the replica wholesale — that is what a snapshot is — so
    // it is the third place the bound has to be reapplied.
    const document = session();

    // Exactly what `SyncController.reconcile` does with a snapshot answer.
    const decoded = decodeSnapshot(session().snapshot);
    document.adopt(Replica.import(SELF, decoded.elements, decoded.versionVector));

    expect(() => { document.receive(stranded(MAX_PENDING_OPERATIONS + 1)); })
      .toThrow(/pending/i);
  });

  it('is set above the largest legitimate use, with room for several peers', () => {
    // The relationship, asserted rather than left to the two numbers happening
    // to be right. A bound at or below one peer's backlog would refuse the case
    // §5's pending set exists for.
    expect(MAX_PENDING_OPERATIONS).toBeGreaterThan(BURST);
  });
});
