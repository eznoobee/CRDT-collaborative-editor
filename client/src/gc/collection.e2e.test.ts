import { afterAll, beforeAll, describe, expect, it } from 'vitest';

import { startWalk, type Walk } from '../walk/harness';
import { InteropClient } from '../interop/client';
import { provision } from '../interop/harness';

/**
 * §5's collection and §6's truncation, seen through the product (register
 * row 33).
 *
 * @remarks
 * <p>
 * <b>Why this is not a metrics assertion.</b> §10 publishes counters for
 * collection and truncation, and reading them would be the obvious way to
 * observe this. They are served on an admin port the proxy deliberately does
 * not forward, and §7's posture is that a route which should not exist beats
 * one that exists and is guarded — so publishing it to make this test possible
 * would undo a control in the artefact that ships, which is the wrong trade for
 * a test. 7b.10 found the alternative: collection has a consequence a
 * <em>client</em> can see.
 * </p><p>
 * <b>The consequence.</b> A client arriving with an empty version vector asks
 * for everything. While the whole log is present that is a short delta, and the
 * server sends operations. Once tombstones are collected and the log truncated
 * past a snapshot, the operations that delta was made of are gone — so the same
 * request must be answered with a snapshot instead. Nothing about the document
 * changes; what changes is how the server can answer.
 * </p><p>
 * <b>Both halves, because either alone is vacuous.</b> A test that only asserts
 * "a snapshot comes back" passes against a server that always sends snapshots,
 * which is a correct server with the collection turned off and the catch-up
 * optimisation broken. So the same question is asked before, and the answer has
 * to be a delta. The pair is the observation; neither half is.
 * </p><p>
 * <b>And the document must be unchanged.</b> "A snapshot comes back" is also
 * satisfied by a snapshot of a document collection has damaged — which is the
 * failure §5's four conditions exist to prevent and the one that matters. The
 * text is compared across the transition.
 * </p><p>
 * <b>The before-probe is itself a replica.</b> It connects, so it joins the set
 * the stability frontier is a minimum over, and collection cannot proceed past
 * it until it is retired. That is the design working rather than an
 * inconvenience, and it is why this stack retires in seconds: a fast collector
 * against a seven-day `T_retire` would collect nothing and look broken.
 * 7b.10 was caught by the neighbouring version of this — a probe that inserted
 * an element and thereby made another one uncollectable.
 * </p>
 */
describe("§5's collection, seen by a client that was not there", () => {
  let walk: Walk;

  beforeAll(async () => {
    walk = await startWalk({
      overlays: ['deploy/docker-compose.gc-window.yml'],
      port: 8445,
    });
  }, 900_000);

  afterAll(async () => {
    await walk?.close();
  }, 300_000);

  /** A fresh client's first catch-up, and what the document then says. */
  async function firstOpen(documentId: string, subject: string) {
    const client = await InteropClient.join(
      walk.baseUrl, walk.oidc.mint(subject), documentId);

    try {
      const caught = await client.catchUp();
      client.applyCatchUp(caught);

      return {
        snapshot: caught.Snapshot,
        operations: caught.Operations,
        text: client.replica.text,
      };
    } finally {
      await client.close();
    }
  }

  /** Every probe subject, granted up front because a stranger gets a 404. */
  const probes = ['gc-before', ...Array.from({ length: 8 }, (_, n) => `gc-after-${n + 1}`)];

  it('answers a first-open client with a snapshot once the log it would have sent is gone', async () => {
    // Each probe is a different person, so each needs access. The first run of
    // this suite granted none of them and negotiate answered 404 — which is
    // §7's authorization working, on a test that had not asked for any.
    const documentId = await provision(walk.baseUrl, walk.oidc, {
      owner: 'gc-owner',
      members: probes.map((subject) => ({ subject, role: 'viewer' as const })),
    });

    const author = await InteropClient.join(
      walk.baseUrl, walk.oidc.mint('gc-owner'), documentId);

    try {
      // Past the configured snapshot threshold, so a snapshot exists to
      // truncate past, and with tombstones, because collection only ever
      // reaches those — a document with none is one where collection is
      // correctly a no-op.
      const written = await author.submit(author.build('the quick brown fox jumps over it'));
      expect(written.Code ?? null).toBeNull();

      const caught = await author.catchUp();
      author.applyCatchUp(caught);

      const deleted = await author.submit(author.buildDeletes(10));
      expect(deleted.Code ?? null).toBeNull();

      author.applyCatchUp(await author.catchUp());
    } finally {
      // Disconnected, so this replica can retire and stop holding the frontier
      // where it is.
      await author.close();
    }

    // BEFORE. The whole log is present, the delta is short, and the server
    // answers with operations. Without this half, the assertion below passes
    // against a server that has never sent a delta in its life.
    const before = await firstOpen(documentId, 'gc-before');

    expect(before.snapshot ?? null, 'the document was already answered with a snapshot, '
      + 'so this run cannot show collection changing anything').toBeNull();
    expect(before.operations.byteLength).toBeGreaterThan(0);
    expect(before.text.length).toBeGreaterThan(0);

    // Retirement, then collection, then truncation — each on a three-second
    // sweep, and each needing the one before it.
    //
    // THE PROBE IS A REPLICA, AND THAT CHANGES HOW THIS WAITS. Every catch-up
    // above connects, so it joins the set §5's stability frontier is a minimum
    // over, and collection cannot pass it until it retires. A loop that polled
    // every few seconds would add replicas faster than a fifteen-second
    // `T_retire` removes them, and the document would never become collectable
    // — a test that prevents the thing it is waiting for, which is the shape
    // 7b.10 was caught by one door along. So the gap between probes is longer
    // than `T_retire` plus a sweep, and the wait before the first one is too.
    const probeEvery = 25_000;
    const attempts = 8;

    let after = before;
    let attempt = 0;

    while (attempt < attempts && (after.snapshot ?? null) === null) {
      await new Promise((done) => setTimeout(done, probeEvery));
      attempt += 1;
      after = await firstOpen(documentId, probes[attempt]!);
    }

    // AFTER. The operations the delta was made of are gone, so the same
    // question has to be answered differently.
    // §13.23 on a remote runner: "it did not happen" costs an iteration and
    // teaches nothing. The three sweepers log what they did, so the failure
    // carries their lines — which of retirement, snapshotting, collection and
    // truncation stopped is then readable from the run rather than guessed at.
    expect(
      after.snapshot ?? null,
      `after ${attempt} probes ${probeEvery / 1000}s apart, a first-open client was still `
      + 'answered with a delta — collection or truncation did not happen, or did not reach '
      + `this document.\n--- what the stack logged ---\n${walk.logs().slice(-6_000)}`,
    ).not.toBeNull();

    // And the document is the same document. A snapshot of state collection has
    // damaged would satisfy every assertion above.
    expect(after.text).toBe(before.text);
  }, 900_000);
});
