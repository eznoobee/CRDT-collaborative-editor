import { afterAll, beforeAll, describe, expect, it } from 'vitest';

import { DocumentSession } from '../editor/DocumentSession';
import { SyncController } from '../editor/SyncController';
import { SignalRTransport } from '../editor/signalRTransport';
import { parseReplicaId } from '../crdt';
import { PARAGRAPH, THREE_PAGES } from '../limits/use';
import { startApi, startOidc, provision, type Api, type Oidc } from './harness';

/**
 * A person pastes, against the running server (§13.37, register row 27).
 *
 * The unit tests in `src/limits/` check that the batches the client hands its
 * sink are ones §7 admits, and the C# suite checks that the server accepts what
 * a client of that shape sends. **Neither of them is a paste.** Between the two
 * sits everything that makes a split paste work or not work: the outbox
 * draining strictly in order, each batch's first operation referencing an
 * element the previous batch created, the server's density check on sequence
 * numbers spanning the split, and §9's recovery table deciding what happens if
 * any of it is refused. That is the part this file exercises, and it is the
 * part that was broken.
 *
 * **What was broken.** `DocumentSession.edit()` encoded every operation from
 * one change event into a single batch. Typing produces one operation per
 * change event, so nothing ever approached §7's cap of 256; pasting produces as
 * many as the clipboard held, in one event. A pasted paragraph went out as one
 * batch of 900 operations, the server refused it as `batch_too_large`, and
 * §9's recovery for that code is `stop`. The first paragraph anybody pasted
 * ended their session's sync, with the text still on screen and nothing saying
 * so. Two phases, and every test that existed typed.
 *
 * The vacuity risks, named before these were written:
 *
 * 1. **The text is right on the pasting client whatever the server does.** §9
 *    applies edits locally with no round trip, so `session.text` is the
 *    paragraph even when every batch was refused. So the assertions are on the
 *    outbox draining and on a *second* client seeing the paragraph arrive over
 *    the wire.
 * 2. **An empty outbox can mean "sent" or "discarded".** §9's recovery for
 *    `stop` leaves the queue alone, but a future recovery might not, so the
 *    reader is what decides this — the second client holds the paragraph or it
 *    does not.
 * 3. **Asserting a character count would pass against a paste that arrived in
 *    the wrong order.** Runs split across batches interleave visibly when
 *    causality is mishandled, so the reader is compared on the exact text and
 *    on §9's normalised form, tree shape included.
 */
describe('pasting through the shipped client', () => {
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

  /** A client wired exactly as the app wires one. */
  function client(subject: string, documentId: string) {
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

    const sync: SyncController = new SyncController(
      (replicaId) =>
        new DocumentSession(parseReplicaId(replicaId), (batch) => sync.enqueue(batch)),
      transport,
      null,
      [],
      { schedule: () => {} },
    );

    return { transport, sync };
  }

  /** Prose of a given length in code points, as a clipboard holds it. */
  function prose(codePoints: number): string {
    const source = 'the quick brown fox jumps over a lazy dog and then keeps going ';
    let text = '';
    while (text.length < codePoints) {
      text += source;
    }

    return text.slice(0, codePoints);
  }

  async function pasteReaches(subject: string, codePoints: number): Promise<void> {
    const reader = `${subject}-reader`;
    const documentId = await provision(api.baseUrl, oidc, {
      owner: subject,
      members: [{ subject: reader, role: 'editor' }],
    });

    const pasting = client(subject, documentId);
    const watching = client(reader, documentId);

    try {
      await pasting.sync.start();
      await watching.sync.start();

      const session = pasting.sync.session;
      expect(session).not.toBeNull();

      const pasted = prose(codePoints);

      // One change event carrying the whole clipboard, which is what a paste
      // into a textarea produces — not a loop that types.
      session!.edit(pasted);

      // Waited on the condition rather than a duration: a split paste is
      // several round trips and a fixed sleep tuned to a fast machine fails on
      // a slow one for reasons unrelated to the claim.
      //
      // A REFUSAL ENDS THE WAIT. Waiting only for the outbox to drain makes a
      // refused paste take the full timeout and report `expected false to be
      // true`, which says nothing — and a diagnosis-hostile failure in a test
      // written to catch a diagnosis-hostile bug is the wrong trade twice over.
      // §9 requires every code to reach `problem`, so the refusal is available
      // immediately and the assertion below can name it.
      await waitFor(
        () => pasting.sync.pending.length === 0 || pasting.sync.problem !== null,
        30_000,
        'the outbox neither drained nor reported a refusal',
      );

      // Named before the drain is asserted, so the message is the server's
      // reason rather than a timeout. Reverting the client's batch splitting
      // makes this line read `batch_too_large`.
      expect(pasting.sync.problem?.code ?? null).toBeNull();
      expect(pasting.sync.pending).toHaveLength(0);
      expect(pasting.sync.state).not.toBe('stopped');

      // The reader is the only party that can tell "sent" from "applied
      // locally", and it is compared on the tree rather than on the text: a
      // paste split across batches interleaves visibly if causality across the
      // split is mishandled, and equal text would not show it.
      await waitFor(
        () => watching.sync.session?.text === pasted,
        30_000,
        'the reader never received the pasted text',
      );
      expect(watching.sync.session?.normalised).toBe(session!.normalised);
    } finally {
      await pasting.transport.close();
      await watching.transport.close();
    }
  }

  it('a pasted paragraph reaches another client', async () => {
    await pasteReaches('paste-paragraph', PARAGRAPH);
  }, 60_000);

  it('a three-page paste reaches another client', async () => {
    await pasteReaches('paste-three-pages', THREE_PAGES);
  }, 120_000);
});

async function waitFor(
  condition: () => boolean,
  withinMs = 10_000,
  because = 'the condition never held',
): Promise<void> {
  const deadline = Date.now() + withinMs;
  while (!condition() && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 20));
  }

  expect(condition(), because).toBe(true);
}
