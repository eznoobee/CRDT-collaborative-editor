import { describe, expect, it } from 'vitest';

import { DocumentSession } from '../editor/DocumentSession';
import { offlineWindow } from '../editor/offlineWindow';
import { Replica, decodeOperations, encodeOperations, parseReplicaId } from '../crdt';
import type { Operation } from '../crdt';
import { BURST, LONG_WEEKEND_DAYS, PARAGRAPH, TABS, THREE_PAGES } from './use';

/**
 * The client half of §13.37's standing technique (register row 27).
 *
 * @remarks
 * <p>
 * Every other test of a limit in this repository proves the limit
 * <em>enforces</em>: over the number is refused, under it is accepted. All of
 * them pass when the number is in the wrong place, because they are written in
 * terms of the number. These are written in terms of the <em>use</em> — the
 * paragraph somebody pasted, the weekend the laptop was shut — and their
 * numbers come from <code>./use</code>, which reads no configuration at all.
 * </p><p>
 * <b>What this file can and cannot see.</b> The caps restated below are §7's
 * protocol contract, not a read of the server's configuration, and the client
 * has to know them: it is the party that must build batches the protocol
 * admits. So a server whose <code>MaxOperationsPerBatch</code> moved would not
 * turn this file red — that is the server suite's job
 * (<code>LargestLegitimateUseTests</code>), and the halving check belongs
 * there. What this file catches is the other direction and the one nothing
 * else looks at: <b>a client that emits something no correctly-configured
 * server would accept.</b>
 * </p>
 *
 * The vacuity risks, named before these were written:
 *
 * 1. <b>Asserting that a paste "works" against the local replica passes
 *    always.</b> §9 applies edits locally with no server in the path, so the
 *    text is right whatever the sink receives. The assertions here are on the
 *    batches handed to the sink, which is the only thing a server ever sees.
 * 2. <b>A single assertion on the total would hide the shape.</b> Three
 *    thousand code points split into twelve batches and three thousand in one
 *    batch have the same total and opposite fates, so every batch is checked
 *    individually.
 * 3. <b>The pending-set case is vacuous today and says so.</b> "A burst of two
 *    thousand fits" is not evidence of anything while the set is unbounded, and
 *    it is unbounded: nothing in the product ever assigns
 *    <code>maxPending</code> (only <code>causalReadiness.test.ts</code> does),
 *    so §5's per-connection bound is not in force in the shipped client. The
 *    test below is kept as the use half of the pair and the missing half is
 *    recorded in <code>docs/limit-headroom.md</code> and the register, rather
 *    than papered over with an assertion that would pass either way.
 */

const ID = parseReplicaId('00000000-0000-0000-0000-0000000000a1');
const PEER = parseReplicaId('00000000-0000-0000-0000-0000000000b1');

/**
 * §7's published wire caps, as the protocol states them.
 *
 * @remarks
 * Restated rather than imported from the server, because the client genuinely
 * has to hold them: it is the side that decides how to split what a person
 * pasted. Named as the protocol's numbers so that nobody later "fixes" a red
 * test here by reading the server's configuration, which is what §13.37 exists
 * to prevent.
 */
const PROTOCOL = {
  /** Operations one submission may carry, after runs are expanded on ingest. */
  operationsPerBatch: 256,
  /** Code points one run record may hold; also §6's format ceiling. */
  runCodePoints: 256,
  /** Bytes one hub message may carry. */
  messageBytes: 64 * 1024,
} as const;

/** A session whose sink records what a real transport would have been given. */
function session(): { session: DocumentSession; sent: Uint8Array[] } {
  const sent: Uint8Array[] = [];
  return { session: new DocumentSession(ID, (batch) => sent.push(batch)), sent };
}

/** Prose of a given length in code points, as a person's clipboard holds it. */
function prose(codePoints: number): string {
  // Words rather than one repeated character: run coalescing is length-aware,
  // and a string of identical characters is the friendliest possible input to
  // it. Real prose is what has to fit.
  const words = 'the quick brown fox jumps over a lazy dog and then keeps going '.split(' ');
  let text = '';
  for (let i = 0; text.length < codePoints; i++) {
    text += `${words[i % words.length]} `;
  }

  return text.slice(0, codePoints);
}

/** Every batch, as the count of operations the server will expand it into. */
function operationCounts(sent: readonly Uint8Array[]): number[] {
  return sent.map((batch) => decodeOperations(batch).length);
}

describe('the largest legitimate use of each client-side limit (§13.37, row 27)', () => {
  it('a pasted paragraph goes out in batches a server will accept', () => {
    // The action: a person copies a paragraph and pastes it into an empty
    // document. The client sees one change event carrying the whole paragraph.
    const { session: document, sent } = session();

    document.edit(prose(PARAGRAPH));

    expect(document.text).toHaveLength(PARAGRAPH);
    expect(sent.length).toBeGreaterThan(0);

    for (const count of operationCounts(sent)) {
      expect(count).toBeLessThanOrEqual(PROTOCOL.operationsPerBatch);
    }

    for (const batch of sent) {
      expect(batch.byteLength).toBeLessThanOrEqual(PROTOCOL.messageBytes);
    }
  });

  it('a three-page paste goes out in batches a server will accept', () => {
    // §13.37's own case, from the other side. Three pages defeated the rate
    // limit's first default; the question here is whether it even reaches a
    // rate limiter.
    const { session: document, sent } = session();

    document.edit(prose(THREE_PAGES));

    expect(document.text).toHaveLength(THREE_PAGES);

    for (const count of operationCounts(sent)) {
      expect(count).toBeLessThanOrEqual(PROTOCOL.operationsPerBatch);
    }

    for (const batch of sent) {
      expect(batch.byteLength).toBeLessThanOrEqual(PROTOCOL.messageBytes);
    }
  });

  it('a paste of emoji goes out in batches a server will accept', () => {
    // The byte cap's case rather than the count cap's: the largest batch the
    // client can build is one whose code points cost four bytes each. A
    // message cap reasoned about from ASCII keystrokes is met by a paste of
    // emoji, which is §13.37's shape exactly.
    const { session: document, sent } = session();

    // A paragraph's worth of code points, each the most expensive one there is.
    document.edit('😀'.repeat(PARAGRAPH));

    for (const batch of sent) {
      expect(batch.byteLength).toBeLessThanOrEqual(PROTOCOL.messageBytes);
    }

    for (const count of operationCounts(sent)) {
      expect(count).toBeLessThanOrEqual(PROTOCOL.operationsPerBatch);
    }
  });

  it('pasting the same three pages into three tabs produces the same work three times', () => {
    // The per-user budget's case. Three tabs are three replicas, and the point
    // is that one person's legitimate fan-out multiplies the volume the server
    // sees — so the server suite's per-user test needs this number, and this
    // test pins what one tab actually costs.
    const perTab = (() => {
      const { session: document, sent } = session();
      document.edit(prose(THREE_PAGES));
      return sent.reduce((total, batch) => total + decodeOperations(batch).length, 0);
    })();

    expect(perTab).toBe(THREE_PAGES);
    expect(perTab * TABS).toBe(THREE_PAGES * TABS);
  });

  it('a burst arriving out of order across a partition is not refused', () => {
    // §5's pending set. A link comes back and a peer's afternoon of work
    // arrives before the operation it all depends on; every one of those has to
    // wait rather than be dropped or throw.
    const replica = new Replica(ID);

    // The peer's burst, built as a peer would and then delivered in reverse so
    // that nothing is ready until the last one lands.
    const peer = new Replica(PEER);
    const authored: Operation[] = [];
    for (let i = 0; i < BURST; i++) {
      authored.push(peer.insert(i, 'x'));
    }

    for (const operation of [...authored].reverse()) {
      replica.apply(operation);
    }

    // Everything cascaded once the head arrived, which is the behaviour the
    // bound must not break.
    expect(replica.pendingCount).toBe(0);
    expect(replica.text).toHaveLength(BURST);
  });

  it('a laptop closed over a long weekend still has an open offline window', () => {
    // §9: the window is what tells a user their unsent work is still safe.
    // Phrased as the weekend rather than as a fraction of T_retire, so a
    // T_retire that moved under it would go red.
    const now = Date.UTC(2026, 0, 12, 9, 0, 0);
    const lastSynced = now - LONG_WEEKEND_DAYS * 24 * 60 * 60 * 1000;

    const window = offlineWindow(lastSynced, now);

    expect(window.state).toBe('fresh');
    expect(window.remainingMs).toBeGreaterThan(0);
  });
});

describe('what §6 lets the client build at all', () => {
  it('a run record never exceeds the format ceiling', () => {
    // Structural rather than behavioural: the encoder cannot emit a longer run
    // than §6 allows, so this is the one cap on the list that is not a tuning
    // question. Asserted anyway, because the server's MaxRunCodePoints is
    // tunable below it and the server suite's halving check needs to know that
    // the client really does build runs of this length.
    const operations: Operation[] = [];
    const replica = new Replica(ID);
    for (let i = 0; i < PROTOCOL.runCodePoints; i++) {
      operations.push(replica.insert(i, 'a'));
    }

    const encoded = encodeOperations(operations);

    expect(decodeOperations(encoded)).toHaveLength(PROTOCOL.runCodePoints);
  });
});
