import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { PkceTokenSource } from './pkce';
import { SignInRequired, SignOutUnavailable } from './tokenSource';

/**
 * §7's PKCE clauses (register row 26).
 *
 * @remarks
 * <p>
 * <b>The reason this file exists.</b> §7's rows 4, 5, 7 and 9 rested entirely on
 * `app.e2e.test.ts`, which signs in for real — and <b>would still sign in if the
 * code challenge stopped being sent</b>. A flow that succeeds is not evidence
 * about how it succeeded. Everything here fails for a specific reason.
 * </p><p>
 * <b>The comparison is not circular, and that took care.</b> A test that derives
 * the expected challenge with the same function the code uses proves the
 * function is deterministic and nothing else. The challenge here is computed by
 * an implementation written in this file, and <i>that</i> implementation is
 * checked against RFC 7636's own published test vector — so the chain is
 * library → this file → the RFC, with no step comparing something to itself.
 * </p><p>
 * <b>The navigation promise never resolves</b>, by design: `signinRedirect`
 * awaits a page that is supposed to be going away. So `signIn` is never awaited
 * here; the assertion waits for the captured URL instead. A test that awaited it
 * would time out and the obvious fix — awaiting a different method than the
 * product calls — would prove the library can build a URL rather than that this
 * configuration produces one.
 * </p>
 */
describe("§7's PKCE flow", () => {
  const AUTHORITY = 'https://issuer.test/';
  const CLIENT_ID = 'editor-spa';
  const REDIRECT = 'http://app.test/callback';
  const POST_LOGOUT = 'http://app.test/signed-out';

  const metadata = {
    issuer: AUTHORITY,
    authorization_endpoint: 'https://issuer.test/authorize',
    token_endpoint: 'https://issuer.test/token',
    end_session_endpoint: 'https://issuer.test/logout',
    jwks_uri: 'https://issuer.test/jwks',
  };

  /**
   * BASE64URL(SHA256(verifier)), written here and not imported.
   *
   * @remarks
   * The whole point of the file. Importing the library's own helper would make
   * the comparison below an identity.
   */
  async function challengeOf(verifier: string): Promise<string> {
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
    return btoa(String.fromCharCode(...new Uint8Array(digest)))
      .replace(/\+/g, '-')
      .replace(/\//g, '_')
      .replace(/=+$/, '');
  }

  let navigated: string[] = [];

  /**
   * An id token the library will decode.
   *
   * @remarks
   * Unsigned, and that is correct rather than a shortcut: a public client does
   * not verify the id token's signature — it received it from the token
   * endpoint over TLS, which is what the code exchange is for. Building a
   * signed one would be testing a check nothing performs.
   */
  function idToken(): string {
    const part = (value: unknown) =>
      btoa(JSON.stringify(value)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

    const now = Math.floor(Date.now() / 1000);
    return [
      part({ alg: 'none', typ: 'JWT' }),
      part({
        iss: AUTHORITY,
        aud: CLIENT_ID,
        sub: 'somebody',
        iat: now,
        exp: now + 3600,
      }),
      '',
    ].join('.');
  }

  function serve(responses: Record<string, unknown> = {}): void {
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
      const url = String(input instanceof Request ? input.url : input);
      for (const [fragment, body] of Object.entries(responses)) {
        if (url.includes(fragment)) {
          return Promise.resolve(new Response(JSON.stringify(body), {
            status: 200, headers: { 'content-type': 'application/json' },
          }));
        }
      }

      return Promise.resolve(new Response(JSON.stringify(metadata), {
        status: 200, headers: { 'content-type': 'application/json' },
      }));
    }));
  }

  beforeEach(() => {
    navigated = [];
    sessionStorage.clear();
    localStorage.clear();
    Object.defineProperty(window, 'location', {
      value: {
        assign: (url: string) => navigated.push(url),
        replace: (url: string) => navigated.push(url),
        href: 'http://app.test/',
        search: '',
      },
      writable: true,
      configurable: true,
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function source(): PkceTokenSource {
    return new PkceTokenSource({
      authority: AUTHORITY,
      clientId: CLIENT_ID,
      redirectUri: REDIRECT,
      postLogoutUri: POST_LOGOUT,
    });
  }

  /** Starts a sign-in and waits for the URL the browser was sent to. */
  async function authorizeUrl(): Promise<URL> {
    serve();
    void source().signIn('/d/abc').catch(() => { /* the navigation never settles */ });

    const deadline = Date.now() + 10_000;
    while (navigated.length === 0 && Date.now() < deadline) {
      await new Promise((done) => setTimeout(done, 10));
    }

    expect(navigated[0], 'the browser was never sent to the issuer').toBeDefined();
    return new URL(navigated[0]!);
  }

  it("derives challenges the way RFC 7636's own test vector says", async () => {
    // THE GUARD ON THE GUARD. Every assertion below compares the library's
    // challenge against challengeOf(), so challengeOf() being wrong in the same
    // way the library is wrong would pass. This is the only fixed point
    // available: the RFC's published pair, Appendix B.
    expect(await challengeOf('dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk'))
      .toBe('E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM');
  });

  it('sends an S256 challenge derived from the verifier it stored', async () => {
    // §7 row 4. The e2e test would sign in happily with no challenge at all;
    // this fails if the challenge stops being sent, stops being S256, or stops
    // matching the verifier that was kept.
    const url = await authorizeUrl();

    expect(url.searchParams.get('code_challenge_method')).toBe('S256');

    const challenge = url.searchParams.get('code_challenge');
    expect(challenge, 'no code challenge was sent').toBeTruthy();

    const stored = Object.keys(sessionStorage)
      .map((key) => JSON.parse(sessionStorage.getItem(key) ?? '{}') as { code_verifier?: string })
      .find((entry) => entry.code_verifier !== undefined);

    expect(stored?.code_verifier, 'no code verifier was stored').toBeTruthy();
    expect(challenge).toBe(await challengeOf(stored!.code_verifier!));
  });

  it('sends no client secret', async () => {
    // The other half of §7 row 4. A browser cannot keep a secret, so one being
    // sent at all is the defect — there is no "but it is short" here.
    const url = await authorizeUrl();

    for (const forbidden of ['client_secret', 'client_assertion', 'client_assertion_type']) {
      expect(url.searchParams.has(forbidden), `${forbidden} was sent`).toBe(false);
    }
  });

  it('sends the configured redirect URI exactly', async () => {
    // §7 row 9. Exact-match is the issuer's job to enforce and this client's to
    // make enforceable: a redirect_uri that differs from the registered one by
    // a trailing slash is either rejected or, worse, matched by prefix.
    const url = await authorizeUrl();

    expect(url.searchParams.get('redirect_uri')).toBe(REDIRECT);
    expect(url.origin + url.pathname).toBe(metadata.authorization_endpoint);
  });

  it('keeps the code verifier out of localStorage', async () => {
    // REGISTER ROW 26's FIND. oidc-client-ts defaults its state store to
    // localStorage, which survives the browser closing and is shared by every
    // tab on the origin — so an abandoned sign-in leaves a live code verifier
    // there until some later sign-in sweeps it. This class documented
    // sessionStorage and the reason for it while shipping localStorage,
    // because nothing asserted which was in use.
    await authorizeUrl();

    expect(
      Object.keys(sessionStorage).length,
      'the state did not go to sessionStorage',
    ).toBeGreaterThan(0);

    expect(
      JSON.stringify(Object.entries(localStorage)),
      'something was left in localStorage, which outlives the tab and the browser',
    ).not.toContain('code_verifier');
  });

  it('never writes the access token to either web storage', async () => {
    // §7 row 5, and the headline of the whole section. Asserted after a token
    // actually exists: asserting it before one has been issued passes against a
    // client that would write it the moment it got one.
    const token = 'header.payload.signature-DO-NOT-STORE';
    serve({
      '/token': {
        id_token: idToken(),
        access_token: token,
        token_type: 'Bearer',
        expires_in: 3600,
        refresh_token: 'refresh-DO-NOT-STORE',
        scope: 'openid profile offline_access',
      },
    });

    const pkce = source();
    void pkce.signIn('/d/abc').catch(() => { /* never settles */ });

    const deadline = Date.now() + 10_000;
    while (navigated.length === 0 && Date.now() < deadline) {
      await new Promise((done) => setTimeout(done, 10));
    }

    const started = new URL(navigated[0]!);
    const state = started.searchParams.get('state')!;

    (window.location as unknown as { search: string }).search =
      `?code=the-code&state=${encodeURIComponent(state)}`;
    (window.location as unknown as { href: string }).href =
      `${REDIRECT}?code=the-code&state=${encodeURIComponent(state)}`;
    window.history.replaceState({}, '', '/');

    await pkce.completeSignIn();

    // The token is usable — otherwise this asserts that a failed sign-in stores
    // nothing, which is true of every failed sign-in.
    expect(await pkce.token()).toBe(token);

    const everything = JSON.stringify([
      Object.entries(sessionStorage),
      Object.entries(localStorage),
    ]);

    expect(everything, 'the access token reached web storage').not.toContain(token);
    expect(everything, 'the refresh token reached web storage').not.toContain('refresh-DO-NOT-STORE');
  });

  it('delegates refresh rather than implementing it', () => {
    // §7 row 7, as a source assertion, because the property is an absence.
    // §7 requires refresh to be the provider library's job: clock skew,
    // concurrent refreshes racing, a rotated token discarded on a retry. A
    // behavioural test cannot show that four lines of it were never written.
    // Resolved from the project root rather than from import.meta.url, which
    // under this runner is not a file URL.
    const source_ = readFileSync('src/auth/pkce.ts', 'utf8');

    expect(source_, 'automaticSilentRenew is what delegates it').toContain('automaticSilentRenew: true');

    for (const handRolled of ['grant_type', 'refresh_token:', 'token_endpoint']) {
      expect(
        source_,
        `${handRolled} appears here, which is refresh being implemented rather than delegated`,
      ).not.toContain(handRolled);
    }
  });

  it('reports a missing end-session endpoint rather than a sign-out it did not do', async () => {
    // §7's sign-out clause. A local-only sign-out is indistinguishable from a
    // real one from inside this application, and the difference shows up on the
    // next load as the same person still signed in.
    const withoutEndSession: Record<string, string> = { ...metadata };
    delete withoutEndSession['end_session_endpoint'];

    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(
      new Response(JSON.stringify(withoutEndSession), {
        status: 200, headers: { 'content-type': 'application/json' },
      }))));

    await expect(source().endSession('/')).rejects.toBeInstanceOf(SignOutUnavailable);
  });

  it('asks for a sign-in rather than returning nothing when there is no token', async () => {
    // §7 row 8's client half: a missing token is a state with a recovery, not a
    // null somebody forgets to check.
    serve();
    await expect(source().token()).rejects.toBeInstanceOf(SignInRequired);
  });
});
