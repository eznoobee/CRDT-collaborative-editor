import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { Page } from 'playwright';

import { seed } from '../interop/harness';
import { startSystem, type System } from './harness';

/**
 * §11's Phase 4 deliverable: the client exists as a client (§13.22).
 *
 * A browser loads the built application from the API's own origin, signs in
 * through §7's PKCE flow against a real issuer, opens a document, types, and
 * the text is there. Every part of Phase 4 was tested before this file existed
 * and none of it had a user — which is the defect §13.22 records.
 *
 * The vacuity risks, named before these were written:
 *
 * 1. **Typing into a textarea and reading it back tests the browser.** The
 *    round trip has to be the long way: a second browser context, a second
 *    user, and the assertion that what one typed reached the other over the
 *    wire. That is also what makes the transport load-bearing — an app with no
 *    connection at all passes the naive version of this test.
 * 2. **"Authenticates" is satisfied by a stubbed token source.** Nothing is
 *    stubbed. The page is navigated to by a real browser, redirected to a real
 *    issuer, and returned with a real code that is exchanged for a real token.
 * 3. **A mount assertion is satisfied by a component that renders and does
 *    nothing.** Nothing here asserts that an element exists; every assertion is
 *    about behaviour reaching another party or another page load.
 * 4. **A test against a dev server proves nothing about what ships.** The
 *    harness runs `npm run build` and the API serves that output from the
 *    static path a deployment uses.
 */
describe('the application, in a browser', () => {
  let system: System;

  beforeAll(async () => {
    system = await startSystem();
  }, 240_000);

  afterAll(async () => {
    await system?.close();
  });

  /** Signs in as `subject` and opens `documentId`, returning the ready page. */
  async function open(subject: string, documentId: string): Promise<Page> {
    // Sequential by construction: the issuer has one subject at a time, which
    // is how a test picks a user without the application carrying a query
    // parameter no real user ever sets.
    system.oidc.subject = subject;

    const { page } = await system.browsing.open();

    // §13.23: a harness that cannot explain its own failure costs more than the
    // failure. "Timed out waiting for a textarea" sends the reader to the wrong
    // file; the application's own message is on screen, and printing it is the
    // difference between a diagnosis and a bisect.
    const logged: string[] = [];
    page.on('console', (message) => logged.push(`${message.type()}: ${message.text()}`));
    page.on('pageerror', (error) => logged.push(`pageerror: ${error.message}`));

    await page.goto(`${system.api.baseUrl}/d/${documentId}`);

    try {
      await page.waitForSelector('textarea', { timeout: 60_000 });
      await page.waitForFunction(
        () => document.querySelector('[data-testid="state"]')?.textContent === 'live',
        undefined,
        { timeout: 60_000 },
      );
    } catch (error) {
      const shown = await page
        .evaluate(() => window.document.body.innerText)
        .catch(() => '(the page could not be read)');

      throw new Error(
        `${subject} never reached a live editor at ${page.url()}.\n`
        + `The page said: ${shown}\n`
        + `The console said:\n${logged.join('\n') || '(nothing)'}\n`
        + `The API said:\n${system.log.slice(-20).join('')}`,
        { cause: error },
      );
    }

    return page;
  }

  it('signs in, opens a document, and carries typing to another user', async () => {
    const documentId = seed(system.oidc.issuer, [
      { subject: 'e2e-writer', role: 'editor' },
      { subject: 'e2e-reader', role: 'editor' },
    ]);

    const writer = await open('e2e-writer', documentId);
    const reader = await open('e2e-reader', documentId);

    await writer.click('textarea');
    await writer.keyboard.type('hello from a browser');

    // The long way round: through the CRDT, the outbox, the hub, the backplane
    // and a second browser's replica. A test that read the writer's own
    // textarea back would pass with no server at all.
    await reader.waitForFunction(
      () => document.querySelector('textarea')?.value === 'hello from a browser',
      undefined,
      { timeout: 60_000 },
    );

    expect(await reader.inputValue('textarea')).toBe('hello from a browser');
  }, 180_000);

  it('leaves no token and no code verifier anywhere the page can read', async () => {
    // §7's rule is a sweep, not a lookup: "not in localStorage" is satisfied by
    // a client that uses sessionStorage. This asks every store, and the URL,
    // whether anything looks like the credential — after a *complete* login,
    // which is when oidc-client-ts has consumed and removed the state entry
    // holding the PKCE verifier.
    const documentId = seed(system.oidc.issuer, [{ subject: 'e2e-sweep', role: 'editor' }]);
    const page = await open('e2e-sweep', documentId);

    const issued = system.oidc.tokenRequests.filter((request) => request.outcome === 'issued');
    const last = issued.at(-1);
    expect(last).toBeDefined();

    const verifier = last!.form['code_verifier'];
    expect(verifier).toBeTruthy();

    const swept = await page.evaluate(() => {
      const dump = (store: Storage): string[] =>
        Object.keys(store).map((key) => `${key}=${store.getItem(key) ?? ''}`);

      return {
        local: dump(window.localStorage),
        session: dump(window.sessionStorage),
        cookie: document.cookie,
        url: window.location.href,
        // Anything a redirect left behind in the address bar of this entry.
        search: window.location.search,
      };
    });

    const everywhere = [
      ...swept.local,
      ...swept.session,
      swept.cookie,
      swept.url,
      swept.search,
    ].join('\n');

    // A JWT is three base64url segments; finding one anywhere in a browser
    // store is the failure, whatever key it was filed under.
    expect(everywhere).not.toMatch(/eyJ[A-Za-z0-9_-]{10,}\./);
    expect(everywhere).not.toContain(verifier);
  }, 180_000);

  it('never puts the bearer token in the hub URL', async () => {
    // §7 put a single-use 60-second ticket in the query string precisely so a
    // JWT would not be there. 4.9 changed how tokens are obtained, so the
    // guarantee is re-asserted rather than inherited.
    const documentId = seed(system.oidc.issuer, [{ subject: 'e2e-url', role: 'editor' }]);

    system.oidc.subject = 'e2e-url';
    const { page } = await system.browsing.open();

    const urls: string[] = [];
    page.on('request', (request) => urls.push(request.url()));
    page.on('websocket', (socket) => urls.push(socket.url()));

    await page.goto(`${system.api.baseUrl}/d/${documentId}`);
    await page.waitForFunction(
      () => document.querySelector('[data-testid="state"]')?.textContent === 'live',
      undefined,
      { timeout: 60_000 },
    );

    const hub = urls.filter((url) => url.includes('/hub/editor'));
    expect(hub.length).toBeGreaterThan(0);

    for (const url of hub) {
      const ticket = new URL(url).searchParams.get('access_token');
      expect(ticket).toBeTruthy();

      // A ticket is opaque; a JWT has three dot-separated base64url segments
      // and starts with the base64 of '{"alg'. Asserting the shape rather than
      // a literal is what makes this a check on the property.
      expect(ticket).not.toMatch(/^eyJ/);
      expect(ticket!.split('.')).toHaveLength(1);
    }
  }, 180_000);
});
