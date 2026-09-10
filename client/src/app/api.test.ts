import { afterEach, describe, expect, it, vi } from 'vitest';

import { ApiRefusal, DocumentApi } from './api';

/**
 * §7's document-API refusals, as the reader of the screen experiences them.
 *
 * The vacuity risk: "the refusal was surfaced" is satisfied by any string at
 * all, including "The server answered 429", which tells the person nothing they
 * can act on. §13.13's requirement is that they can tell what happened and what
 * to do about it, so these assert the sentence rather than the throw.
 */
describe('a refusal from the document API', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function answering(response: Response): DocumentApi {
    vi.stubGlobal('fetch', () => Promise.resolve(response));
    return new DocumentApi('https://example.test', { token: () => Promise.resolve('token') });
  }

  it('turns §7’s throttle into a wait, with the server’s own number', async () => {
    const throttled = new Response(JSON.stringify({ code: 'rate_limited' }), {
      status: 429,
      headers: { 'retry-after': '17' },
    });

    await expect(answering(throttled).list()).rejects.toMatchObject({
      name: 'ApiRefusal',
      status: 429,
      message: expect.stringContaining('17 seconds') as unknown as string,
    });
  });

  it('still says to wait when the server named no delay', async () => {
    const throttled = new Response(JSON.stringify({ code: 'rate_limited' }), { status: 429 });

    const failure = (await answering(throttled)
      .list()
      .catch((error: unknown) => error)) as ApiRefusal;

    expect(failure.status).toBe(429);
    expect(failure.message).toContain('Wait a moment');
    expect(failure.message).not.toContain('429');
  });

  it('keeps the server’s own words for a refusal that has some', async () => {
    // The pair. A 429 branch that swallowed every other refusal's detail would
    // pass both tests above and lose the message that matters most — the one
    // saying a role can be granted only to someone who has signed in.
    const invalid = new Response(
      JSON.stringify({ errors: { memberId: ['that user has never signed in'] } }),
      { status: 400 },
    );

    await expect(answering(invalid).list()).rejects.toMatchObject({
      status: 400,
      message: 'that user has never signed in',
    });
  });
});
