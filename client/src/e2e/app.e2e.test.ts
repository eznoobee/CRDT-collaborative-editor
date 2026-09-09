import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { Page } from 'playwright';

import { provision } from '../interop/harness';
import { pick } from './browser';
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
    // The identity is chosen by clicking it at the issuer, not by setting a
    // variable. Phase 6 replaced the harness issuer's single mutable subject
    // with real sessions for exactly this reason: an identity established out
    // of band cannot be signed out of, so a test of sign-out would have had
    // nothing to end.
    system.oidc.accounts.add(subject);

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
      // The chooser is the issuer's login form. A browser with a session
      // already skips it, which is why this waits for either.
      await pick(page, subject);
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
    const documentId = await provision(system.api.baseUrl, system.oidc, {
      owner: 'e2e-writer',
      members: [{ subject: 'e2e-reader', role: 'editor' }],
    });

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
    const documentId = await provision(system.api.baseUrl, system.oidc, {
      owner: 'e2e-sweep',
    });
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

  it('signs out of the provider too, so the next load asks who you are', async () => {
    // §7's sign-out is three things, and this is the one that is invisible from
    // inside the client: the token is gone either way, and only the *next*
    // sign-in shows whether the provider's session went with it. Register row
    // 21 is exactly this — closing the tab drops the token and the next load
    // silently re-authenticates as the same person.
    const documentId = await provision(system.api.baseUrl, system.oidc, {
      owner: 'e2e-signout',
    });
    const page = await open('e2e-signout', documentId);

    // Relative, because this issuer is shared with every other test in the
    // file and each of them signed somebody in. An absolute count here would
    // be a test of the execution order.
    const before = system.oidc.sessionCount;
    expect(before).toBeGreaterThan(0);

    await signOutFrom(page);

    // The provider's own session, ended. Asserted at the issuer rather than in
    // the browser, because a client that never called the end-session endpoint
    // looks identical from the page.
    expect(system.oidc.sessionCount).toBe(before - 1);

    // And this browser's local copy of the document went with it. IndexedDB
    // outlives the tab and every token in memory, so leaving it would hand the
    // next person at this machine the previous one's text. The record is what
    // is asserted, not the database: the store deletes the document's entry and
    // leaves the (now empty) database, which is the right behaviour and would
    // make "no database named editor" a test of the wrong thing.
    const stored = await page.evaluate(async (id: string) => {
      const opening = indexedDB.open('editor');
      const database = await new Promise<IDBDatabase>((resolve, reject) => {
        opening.onsuccess = () => resolve(opening.result);
        opening.onerror = () => reject(opening.error ?? new Error('indexedDB.open failed'));
      });

      if (!database.objectStoreNames.contains('documents')) {
        return null;
      }

      const read = database.transaction('documents', 'readonly').objectStore('documents').get(id);
      return new Promise<unknown>((resolve, reject) => {
        read.onsuccess = () => resolve(read.result ?? null);
        read.onerror = () => reject(read.error ?? new Error('the read failed'));
      });
    }, documentId);

    expect(stored).toBeNull();

    // The load after a sign-out, in the same browser: the chooser, not the
    // editor. This is the assertion register row 21 names.
    await page.goto(`${system.api.baseUrl}/d/${documentId}`);
    const asked = await pick(page, 'e2e-signout');

    expect(asked).toBe(true);
  }, 180_000);

  it('switches accounts without carrying the first account\'s document across', async () => {
    // The assertion that a store keyed by document id alone would fail: the
    // second user reaches the same URL, and must see a refusal rather than the
    // replica the first user left behind.
    const documentId = await provision(system.api.baseUrl, system.oidc, {
      owner: 'e2e-first',
    });
    system.oidc.accounts.add('e2e-second');

    const page = await open('e2e-first', documentId);
    await page.click('textarea');
    await page.keyboard.type('written by the first account');

    await signOutFrom(page);

    await page.goto(`${system.api.baseUrl}/d/${documentId}`);
    expect(await pick(page, 'e2e-second')).toBe(true);

    // §7: a document this caller has no role on is a 404, which §9 turns into
    // this message. The first account's text must not be on screen.
    await page.waitForFunction(
      () => window.document.body.innerText.includes('This document is gone'),
      undefined,
      { timeout: 60_000 },
    );

    expect(await page.evaluate(() => window.document.body.innerText))
      .not.toContain('written by the first account');
  }, 180_000);

  /**
   * Clicks sign out, confirming the discard when there is unsent work.
   *
   * @remarks
   * §7 makes the confirmation appear only when the outbox is non-empty, and
   * whether it is depends on whether the last keystroke had been acknowledged
   * — a race this test has no reason to win either way. Handling both is not
   * papering over it: the confirmation itself is asserted directly in
   * SignOut.test.tsx, where the count is not a race.
   */
  async function signOutFrom(page: Page): Promise<void> {
    await page.click('[data-testid="sign-out"]');

    const confirm = await page
      .waitForSelector('[data-testid="sign-out-confirm"]', { timeout: 2_000 })
      .catch(() => null);

    if (confirm !== null) {
      await confirm.click();
    }

    await page.waitForSelector('[data-testid="signed-out"]', { timeout: 60_000 });
  }

  it('never puts the bearer token in the hub URL', async () => {
    // §7 put a single-use 60-second ticket in the query string precisely so a
    // JWT would not be there. 4.9 changed how tokens are obtained, so the
    // guarantee is re-asserted rather than inherited.
    const documentId = await provision(system.api.baseUrl, system.oidc, {
      owner: 'e2e-url',
    });

    system.oidc.accounts.add('e2e-url');
    const { page } = await system.browsing.open();

    const urls: string[] = [];
    page.on('request', (request) => urls.push(request.url()));
    page.on('websocket', (socket) => urls.push(socket.url()));

    await page.goto(`${system.api.baseUrl}/d/${documentId}`);
    await pick(page, 'e2e-url');
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
