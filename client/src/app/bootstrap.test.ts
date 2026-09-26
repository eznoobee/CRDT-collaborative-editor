import { describe, expect, it } from 'vitest';

import { reaching } from './bootstrap';
import { SignInRequired } from '../auth/tokenSource';

/**
 * Naming the origin a failed request was reaching for.
 *
 * @remarks
 * <p>
 * <b>From a real hunt.</b> The stack came up, the proxy logged `/`, the bundle
 * and `/config` all 200, and the page said "Failed to fetch". The cause was the
 * browser not trusting the development issuer on its own origin — a request to
 * an untrusted origin rejects with a bare TypeError and no prompt, so nothing
 * on screen or in the console named the host. Opening the issuer directly and
 * accepting its certificate fixed it.
 * </p><p>
 * <b>The vacuity risk.</b> A test that only checks the happy message would pass
 * against a wrapper that rewrote every failure, which would bury
 * `SignInRequired` — the one error `bootstrap` branches on — and every HTTP
 * error the API took trouble to explain. So the cases that matter here are the
 * ones asserting a failure is left alone.
 * </p>
 */
describe('naming the origin behind "Failed to fetch"', () => {
  const boom = (): Promise<never> => Promise.reject(new TypeError('Failed to fetch'));

  it('returns the value when nothing fails', async () => {
    expect(await reaching('https://app', 'the application', () => Promise.resolve(7))).toBe(7);
  });

  it('names the origin and the role it was reaching', async () => {
    await expect(reaching('https://app:8443', 'the application', boom))
      .rejects.toThrow('Could not reach the application at https://app:8443.');
  });

  it('tells you to accept the issuer certificate, because nothing else will', async () => {
    const failure = await reaching('https://editor-oidc:9443', 'the identity provider', boom)
      .catch((error: unknown) => error as Error);

    expect(failure.message).toContain('https://editor-oidc:9443');
    expect(failure.message).toContain('accept its certificate');
    expect(failure.message).toContain('the browser does not prompt');
  });

  it('keeps the original error as the cause', async () => {
    const failure = await reaching('https://app', 'the application', boom)
      .catch((error: unknown) => error as Error);

    expect(failure.cause).toBeInstanceOf(TypeError);
  });

  it('does not rewrite SignInRequired, which bootstrap branches on', async () => {
    // THE CASE THAT MATTERS. Rewriting this would turn "you need to sign in"
    // into "the provider is unreachable", and bootstrap would stop redirecting
    // to the issuer at all.
    const signIn = new SignInRequired();
    await expect(
      reaching('https://issuer', 'the identity provider', () => Promise.reject(signIn)),
    ).rejects.toBe(signIn);
  });

  it('does not rewrite an error the API already explained', async () => {
    const explained = new Error('Document not found.');
    await expect(
      reaching('https://app', 'the application', () => Promise.reject(explained)),
    ).rejects.toBe(explained);
  });

  it('does not rewrite a TypeError that is a programming mistake', async () => {
    // A TypeError alone is not a transport failure; the message is what
    // separates them. Widening to `instanceof TypeError` would relabel a bug in
    // this file as the network being down.
    const bug = new TypeError('x is not a function');
    await expect(
      reaching('https://app', 'the application', () => Promise.reject(bug)),
    ).rejects.toBe(bug);
  });
});
