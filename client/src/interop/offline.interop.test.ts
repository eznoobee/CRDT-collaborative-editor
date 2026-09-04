import { afterAll, beforeAll, describe, expect, it } from 'vitest';

import { DocumentSession } from '../editor/DocumentSession';
import { SyncController } from '../editor/SyncController';
import { SignalRTransport } from '../editor/signalRTransport';
import { offlineWindow, RETIRE_AFTER_MS } from '../editor/offlineWindow';
import { parseReplicaId } from '../crdt';
import { startApi, startOidc, seed, type Api, type Oidc } from './harness';

/**
 * §11's Phase 4 done-when: offline edit, reconnect, converge (§9).
 *
 * **The disconnection and the reconnection are real.** The socket is genuinely
 * closed, the server genuinely observes it, `negotiate` genuinely runs again,
 * and the client genuinely resumes its replica and drains an outbox it authored
 * while there was nothing to send to. Only the *clock arithmetic* for §9's
 * offline window is simulated, and that is asserted separately from the round
 * trip rather than folded into it — a "five-minute offline test" that never
 * disconnects proves nothing, and one that waits five minutes is a test nobody
 * runs.
 *
 * The vacuity risks, named before these were written:
 *
 * 1. **A reconnect test can pass without the client ever having been offline.**
 *    So the edits below are made after the socket is closed, and the assertion
 *    is that the server did not have them until the reconnection — checked by a
 *    second client that stays connected throughout and sees nothing in between.
 * 2. **Convergence asserted on visible text is weak** (§8). Both sides are
 *    compared on §9's normalised form, tombstones and tree shape included.
 * 3. **An outbox test where the server was reachable all along never exercises
 *    the queue.** The work here is authored with no connection at all, and the
 *    controller's own queue is asserted to be non-empty while offline.
 * 4. **A resumption test passes trivially if the server mints a fresh id and
 *    the client silently accepts it** — so the id after the reconnection is
 *    asserted to be the same one, which is what makes the drained outbox
 *    survive §7's tier-1 check rather than being discarded as unusable.
 */
describe('offline editing, reconnection and convergence', () => {
  const log: string[] = [];
  let oidc: Oidc;
  let api: Api;

  beforeAll(async () => {
    oidc = await startOidc();
    api = await startApi(oidc, log);
  }, 90_000);

  afterAll(async () => {
    await api?.close();
    await oidc?.close();
  });

  /**
   * A client wired exactly as the app wires one.
   *
   * @param replicaId - What the store held, if this is a reload.
   * @param outbox - Batches the store held unsent, oldest first.
   */
  function client(
    subject: string,
    documentId: string,
    replicaId: string | null = null,
    outbox: readonly Uint8Array[] = [],
  ) {
    const token = oidc.mint(subject);
    const transport = new SignalRTransport({
      baseUrl: api.baseUrl,
      documentId,
      fetch: (url, init) =>
        fetch(url, {
          ...init,
          headers: { ...init.headers, authorization: `Bearer ${token}` },
        }),
    });

    // Every replica id the server has assigned this client. A resumption that
    // was granted builds no second session; a refused one builds one per §7.
    const assigned: string[] = [];

    // Retries are driven by the test rather than by a timer, so "it reconnected"
    // is something this test made happen at a known moment.
    const sync: SyncController = new SyncController(
      (replicaId) => {
        assigned.push(replicaId);
        return new DocumentSession(parseReplicaId(replicaId), (batch) => sync.enqueue(batch));
      },
      transport,
      replicaId,
      outbox,
      { schedule: () => {} },
    );

    return { transport, sync, assigned };
  }

  it('keeps work made offline and converges after a real reconnection', async () => {
    const documentId = seed(oidc.issuer, [
      { subject: 'offline-author', role: 'editor' },
      { subject: 'offline-watcher', role: 'editor' },
    ]);

    const author = client('offline-author', documentId);
    const watcher = client('offline-watcher', documentId);

    try {
      await author.sync.start();
      await watcher.sync.start();

      const authoring = author.sync.session;
      const watching = watcher.sync.session;
      expect(authoring).not.toBeNull();
      expect(watching).not.toBeNull();
      expect(author.assigned).toHaveLength(1);

      authoring!.edit('online ');

      // Waited on the condition rather than on a duration. The first submission
      // of a document pays for partition creation and an advisory lock, and a
      // fixed sleep tuned to a fast machine is a test that fails on a slow one
      // for a reason that has nothing to do with what it checks.
      await waitFor(() => author.sync.pending.length === 0);
      await waitFor(() => watcher.sync.session?.text === 'online ');

      // THE DISCONNECTION. A real socket close, not a flag.
      await author.transport.simulateNetworkLoss();
      await waitFor(() => author.sync.state === 'offline');

      // Typed with nothing to send to. Applied locally and rendered — §9 has no
      // round trip in the typing path — and queued.
      authoring!.edit('online and offline');

      expect(authoring!.text).toBe('online and offline');
      expect(author.sync.pending.length).toBeGreaterThan(0);

      // The watcher stayed connected and saw nothing, which is what makes the
      // work genuinely offline rather than merely slow.
      expect(watcher.sync.session?.text).toBe('online ');

      // And the watcher writes while the author is away — at the same position,
      // so this is a genuine concurrent edit rather than two turns. It is
      // accepted and broadcast to a group the author is no longer in, which is
      // what makes the author's catch-up the only path by which it can arrive:
      // without it the author reconnects, converges on nothing, and looks
      // correct until the next edit.
      watching!.edit('online watched');
      await waitFor(() => watcher.sync.pending.length === 0);

      expect(authoring!.text).toBe('online and offline');

      // THE RECONNECTION. negotiate runs again, resumes the replica, catches up
      // by version vector, and drains the outbox.
      await author.sync.start();
      await waitFor(() => author.sync.pending.length === 0);

      expect(author.sync.state).toBe('live');

      // §7: resumption is authorisation to *continue* a replica. The same
      // session survived, so the queued batches were authored under the id this
      // connection is bound to — which is why they were drained rather than
      // discarded.
      expect(author.sync.session).toBe(authoring);
      expect(author.assigned).toHaveLength(1);

      // Each side ends up holding what the other wrote while they could not
      // talk: the author's offline run reaches the watcher by broadcast, the
      // watcher's run reaches the author by §8's catch-up.
      await waitFor(() => watcher.sync.session!.text.includes('and offline'));
      await waitFor(() => authoring!.text.includes('watched'));

      // §8: convergence on §9's normalised state, not on visible text. The
      // merged text is not asserted as a literal: what the two concurrent
      // inserts at the same index resolve to is FugueMax's business, and
      // pinning it here would turn a tie-break into a wire contract.
      await waitFor(
        () => watcher.sync.session!.normalised === author.sync.session!.normalised,
      );
    } finally {
      await author.sync.stop();
      await watcher.sync.stop();
    }
  }, 90_000);

  it('resumes the stored replica and drains a stored outbox after a reload', async () => {
    // §11's other Phase 4 done-when. A reconnect keeps the controller and its
    // queue in memory, so it proves nothing about the case that actually loses
    // work: the tab was closed with unsent batches in IndexedDB, and everything
    // the client knows about itself now comes from the store.
    //
    // The vacuity risk: a reload test whose new client is handed the old
    // session in memory is a reconnect test wearing a hat. Nothing of the first
    // client survives here except the two values §9 says the store holds — the
    // replica id and the unsent bytes — and the assertion is on what the
    // *watcher* receives, so the work is proved to have reached the server
    // rather than merely to have been re-rendered locally.
    const documentId = seed(oidc.issuer, [
      { subject: 'reload-author', role: 'editor' },
      { subject: 'reload-watcher', role: 'editor' },
    ]);

    const watcher = client('reload-watcher', documentId);
    const first = client('reload-author', documentId);

    try {
      await watcher.sync.start();
      await first.sync.start();

      const id = first.assigned[0]!;
      first.sync.session!.edit('saved ');
      await waitFor(() => first.sync.pending.length === 0);

      await first.transport.simulateNetworkLoss();
      await waitFor(() => first.sync.state === 'offline');

      // Authored with no connection, so this is what the store would hold.
      first.sync.session!.edit('saved and unsent');
      const stored = [...first.sync.pending];
      expect(stored.length).toBeGreaterThan(0);

      // THE RELOAD. The first client is gone — its claim released, its session
      // and its queue discarded — and a new one starts from the store alone.
      await first.sync.stop();

      const reloaded = client('reload-author', documentId, id, stored);
      try {
        await reloaded.sync.start();
        await waitFor(() => reloaded.sync.pending.length === 0);

        // §7: continued, not re-authored. A fresh id here would mean the stored
        // batches are refused by tier-1 and discarded, which is the silent
        // data loss this whole path exists to prevent.
        expect(reloaded.assigned).toEqual([id]);
        expect(reloaded.sync.problem).toBeNull();

        // And the work reached the server, which only the watcher can attest.
        await waitFor(() => watcher.sync.session!.text.includes('and unsent'));
      } finally {
        await reloaded.sync.stop();
      }
    } finally {
      await first.sync.stop();
      await watcher.sync.stop();
    }
  }, 90_000);

  it('reports the offline window from the last accepted submission', () => {
    // The clock arithmetic, simulated and kept apart from the round trip above.
    // §9's discard cannot be observed end to end until Phase 7 sets retired_at
    // (§5), so this asserts what the client would say, not what the server
    // would then do — and 4.7 records that the task is not done in isolation.
    const syncedAt = 1_700_000_000_000;
    const fiveMinutes = 5 * 60 * 1000;

    expect(offlineWindow(syncedAt, syncedAt + fiveMinutes).state).toBe('fresh');
    expect(offlineWindow(syncedAt, syncedAt + RETIRE_AFTER_MS).state).toBe('expired');
  });
});

async function waitFor(condition: () => boolean, withinMs = 10_000): Promise<void> {
  const deadline = Date.now() + withinMs;
  while (!condition() && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 20));
  }

  expect(condition()).toBe(true);
}
