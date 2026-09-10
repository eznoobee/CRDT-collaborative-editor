import { SignalRTransport } from './signalRTransport';
import { ConnectionRefused } from './SyncController';
import { REJECTION } from './rejections';

/**
 * §7 makes the membership decision at `negotiate`, so its statuses are §9's
 * codes rather than transport noise.
 *
 * @remarks
 * This exists because a 404 from `negotiate` used to become a generic
 * `Error('negotiate failed: 404')`, which `SyncController` does not recognise
 * as a refusal: the client went quietly offline and retried forever against a
 * document its user will never be allowed to open, with nothing on screen
 * saying why. §13.13 — a rejection the rejected party cannot observe is not a
 * rejection. It was found by 6.5's account-switch test, the first thing in this
 * project to open a document as the wrong person.
 */
describe('negotiate refusals', () => {
  function transport(status: number): SignalRTransport {
    return new SignalRTransport({
      baseUrl: 'https://api.test.invalid',
      documentId: '00000000-0000-0000-0000-000000000001',
      tokens: { token: () => Promise.resolve('a-token') },
      fetch: () => Promise.resolve(new Response(null, { status })),
      build: () => { throw new Error('a refused negotiate must never reach the socket'); },
    });
  }

  it.each([
    [404, REJECTION.notFound],
    [403, REJECTION.forbidden],
    [401, REJECTION.signInRequired],
    [429, REJECTION.tooManyConnections],
  ])('turns %i into %s', async (status, code) => {
    await expect(transport(status).connect(null)).rejects.toMatchObject({
      name: 'ConnectionRefused',
      code,
    });
  });

  it('leaves a status §7 does not define as a transport failure', async () => {
    // Deliberately not mapped to a code. §9's table says an unrecognised
    // refusal stops the client, and a 500 is not a refusal — it is the server
    // being broken, which is a reconnect.
    await expect(transport(500).connect(null)).rejects.not.toBeInstanceOf(ConnectionRefused);
  });
});
