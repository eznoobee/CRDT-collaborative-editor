import { afterAll, beforeAll, describe, expect, it } from 'vitest';

import { startApi, startOidc, seed, type Api, type Oidc } from './harness';
import { InteropClient } from './client';

/**
 * Two TypeScript replicas, one C# server, one real socket (§9, §8).
 *
 * Everything else in this repository checks the two implementations against a
 * shared corpus (§9's conformance runner) or checks the server through an
 * in-memory transport. Neither arrangement can fail the way a deployment fails:
 * the conformance runner never opens a socket, and the in-memory transport is
 * the C# client talking to the C# server, which agrees with itself by
 * construction.
 *
 * The vacuity risks, named before these were written:
 *
 * 1. **A harness that encodes for itself proves nothing.** If the test built §6
 *    bytes with its own encoder, it would be checking the harness against the
 *    server. Every batch here comes out of the shipped core's
 *    `encodeOperations`, and every arriving batch goes into its `decodeOperations`.
 * 2. **Convergence between two TypeScript replicas is not interoperability.**
 *    Two copies of the same core agree with each other whatever the server
 *    does. So the assertions that matter are the ones where the *server*
 *    produced the bytes: the broadcast it relayed and, above all, the snapshot
 *    it encoded from its own C# replica, which the TypeScript core has to
 *    decode into the same normalised document.
 * 3. **A test that only sends would pass against a server that never
 *    broadcasts**, because the sender applies its own operations locally. So
 *    the receiving client here never applies its peer's operations except from
 *    what arrived over the wire.
 */
describe('two TypeScript clients against the running server', () => {
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

  function document(subjects: readonly string[]): string {
    return seed(
      oidc.issuer,
      subjects.map((subject) => ({ subject, role: 'editor' as const })),
    );
  }

  it('relays one client’s operations to the other, decoded by the core that did not write them', async () => {
    const [left, right] = ['interop-left', 'interop-right'];
    const documentId = document([left, right]);

    const a = await InteropClient.join(api.baseUrl, oidc.mint(left), documentId);
    const b = await InteropClient.join(api.baseUrl, oidc.mint(right), documentId);

    try {
      const batch = a.build('hello');
      const result = await a.submit(batch);

      expect(result.Code).toBeNull();
      expect(result.Accepted).toBe(5);

      a.apply(batch);

      // What b applies came off the socket. Nothing here hands it the bytes a
      // encoded, so a server that accepted and never relayed fails outright.
      const arrived = await b.next();
      expect(arrived.DocumentId).toBe(documentId);
      expect(arrived.ServerSeq).toBeGreaterThan(0);
      b.apply(arrived.Operations);

      expect(b.replica.text).toBe('hello');
      expect(b.replica.pendingCount).toBe(0);
      expect(b.normalised).toBe(a.normalised);
    } finally {
      await a.close();
      await b.close();
    }
  }, 60_000);

  it('converges two clients editing concurrently', async () => {
    const [left, right] = ['interop-concurrent-a', 'interop-concurrent-b'];
    const documentId = document([left, right]);

    const a = await InteropClient.join(api.baseUrl, oidc.mint(left), documentId);
    const b = await InteropClient.join(api.baseUrl, oidc.mint(right), documentId);

    try {
      for (let round = 0; round < 5; round++) {
        const fromA = a.build('A');
        expect((await a.submit(fromA)).Code).toBeNull();
        a.apply(fromA);
        b.apply((await b.next()).Operations);

        const fromB = b.build('B');
        expect((await b.submit(fromB)).Code).toBeNull();
        b.apply(fromB);
        a.apply((await a.next()).Operations);
      }

      expect(a.replica.pendingCount).toBe(0);
      expect(b.replica.pendingCount).toBe(0);
      expect(a.replica.text).toHaveLength(10);

      // §9: compared on the normalised form, tombstones and tree shape
      // included. Equal text is a much weaker claim and two replicas can have
      // it while disagreeing about everything underneath.
      expect(b.normalised).toBe(a.normalised);
    } finally {
      await a.close();
      await b.close();
    }
  }, 60_000);

  it('decodes a snapshot the C# server encoded from its own replica', async () => {
    // The sharpest assertion in this file. The bytes are produced by the C#
    // core from state it rebuilt out of Postgres, and read by the TypeScript
    // core into a document that has to normalise identically. Nothing else in
    // the project exercises that direction end to end: §9's conformance runner
    // compares two files, and this is the format crossing a wire.
    const [writer, joiner] = ['interop-snapshot-writer', 'interop-snapshot-joiner'];
    const documentId = document([writer, joiner]);

    const a = await InteropClient.join(api.baseUrl, oidc.mint(writer), documentId);

    try {
      const batch = a.build('snapshot');
      expect((await a.submit(batch)).Code).toBeNull();
      a.apply(batch);

      const b = await InteropClient.join(api.baseUrl, oidc.mint(joiner), documentId);

      try {
        expect(b.replica.allIds).toHaveLength(0);

        // §13.14: the snapshot floor, exercised on its own rather than left
        // behind a delta path that would answer every test in this file.
        const caught = await b.catchUp(true);

        expect(caught.Code).toBeNull();
        expect(caught.Snapshot).not.toBeNull();
        expect(caught.Snapshot!.length).toBeGreaterThan(0);

        b.applyCatchUp(caught);

        expect(b.replica.text).toBe('snapshot');
        expect(b.normalised).toBe(a.normalised);
      } finally {
        await b.close();
      }
    } finally {
      await a.close();
    }
  }, 60_000);

  it('answers a delta from the version vector the TypeScript core keeps', async () => {
    // The other half of the cursor: the vector crossing the wire is the one the
    // TypeScript replica maintains, so the two implementations have to agree on
    // what "already applied" means (§5). A disagreement here is silent — the
    // client is told it is current and simply never receives the rest.
    const [writer, reader] = ['interop-delta-writer', 'interop-delta-reader'];
    const documentId = document([writer, reader]);

    const a = await InteropClient.join(api.baseUrl, oidc.mint(writer), documentId);
    const b = await InteropClient.join(api.baseUrl, oidc.mint(reader), documentId);

    try {
      const seen = a.build('seen');
      expect((await a.submit(seen)).Code).toBeNull();
      a.apply(seen);
      b.apply((await b.next()).Operations);

      // Received and deliberately not applied, so b is genuinely behind.
      const missed = a.build('missed');
      expect((await a.submit(missed)).Code).toBeNull();
      a.apply(missed);
      await b.next();

      const caught = await b.catchUp();

      expect(caught.Code).toBeNull();

      // The delta path, asserted rather than inferred: a snapshot would
      // converge too, and would mean every reconnect pays for the document.
      expect(caught.Snapshot).toBeNull();

      b.applyCatchUp(caught);

      expect(b.replica.text).toBe('seenmissed');
      expect(b.normalised).toBe(a.normalised);
    } finally {
      await a.close();
      await b.close();
    }
  }, 60_000);

  it('refuses a connection whose token the issuer did not sign', async () => {
    // §7's checks are on for real here, and this is the only test in the file
    // that says so. Every other one uses a valid token and would pass just as
    // well against a server with authentication switched off entirely.
    //
    // The status is asserted rather than "it failed somehow": this subject is a
    // member of the document, so a rejection could otherwise be §7's 404 for
    // non-membership and the test would pass while proving nothing about the
    // signature. 401 is the token being refused.
    const documentId = document(['interop-forged']);
    const claims = oidc.mint('interop-forged').split('.').slice(0, 2).join('.');

    await expect(
      InteropClient.join(api.baseUrl, `${claims}.bm90LWEtc2lnbmF0dXJl`, documentId),
    ).rejects.toThrow(/negotiate failed: 401/);

    // And the same token, unforged, is accepted — so the refusal above is the
    // signature and not something standing in the way of every request.
    const accepted = await InteropClient.join(
      api.baseUrl,
      oidc.mint('interop-forged'),
      documentId,
    );
    await accepted.close();
  }, 60_000);
});
