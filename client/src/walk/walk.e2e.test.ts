import { afterAll, beforeAll, describe, expect, it } from 'vitest';

import { startWalk, type Walk } from './harness';
import { pick } from '../e2e/browser';

/**
 * §13.27's walk, as a test. Phase 5b's done-when.
 *
 * Bring the artefact up the way a deployment does — `docker compose up` and a
 * `.env` — and follow what a user does from a cold start, with nothing seeded
 * and nothing run by hand. No `psql`, no `dotnet ef`, no `npm run build`, no
 * fixtures. Each step is asserted, and **the step it stops at is the output**:
 * a green walk with no recorded stopping point is either a finished product or
 * a walk trimmed to what passes, and nothing distinguishes them (§13.27).
 *
 * The vacuity risks, named before these were written:
 *
 * 1. **Every check here is a check about a deployment, and the cheapest way to
 *    pass each one is to assert it from the test host** — where a harness
 *    applied the schema and a script built the client. That is how eleven
 *    phases of green accumulated over an artefact that could not start
 *    (§13.28), so §12's phase rule applies: nothing is verified by anything
 *    running outside the stack, and where this test reaches in, it reaches in
 *    over the network at the published port.
 * 2. **A smoke test that asserts liveness is the defect this phase is about.**
 *    `/health/live` returns 200 from a process with no database behind it. So
 *    the assertion here is a request that reads a row and would fail against an
 *    empty schema.
 * 3. **A walk that seeds anything is not a walk.** If a step needs something
 *    the product cannot provide, that is a finding, not a setup line.
 * 4. **Volumes persist**, so a migration check passes forever against a
 *    database an earlier run migrated and breaks only on somebody's first
 *    clone. The harness asserts the volumes were actually removed rather than
 *    trusting `down -v` to have done it.
 */
describe('the walk: cold start to the first step that cannot be taken', () => {
  let walk: Walk;

  beforeAll(async () => {
    walk = await startWalk();
  }, 900_000);

  afterAll(async () => {
    await walk?.close();
  });

  it('step 1 — the stack answers over TLS, and only over TLS', async () => {
    const live = await fetch(`${walk.baseUrl}/health/live`);
    expect(live.ok).toBe(true);

    // §4: the API's own port is not published. This is the assertion that the
    // TLS termination is a route rather than an option — a published API port
    // would be a plaintext way past everything above.
    const host = new URL(walk.baseUrl).hostname;
    await expect(fetch(`http://${host}:8080/health/live`, {
      signal: AbortSignal.timeout(4_000),
    })).rejects.toThrow();
  }, 60_000);

  it('step 2 — the schema is there, applied by the deployment', async () => {
    // Not /health/live, which is the defect this phase exists to correct
    // (§13.28): it answers 200 from a process with an empty database behind it.
    // Negotiate reads document_members. Unauthenticated it must be refused by
    // authentication — but a 500 here means the query itself failed, which is
    // what an unmigrated database produces.
    const response = await fetch(
      `${walk.baseUrl}/documents/00000000-0000-0000-0000-000000000001/negotiate`,
      { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' },
    );

    expect(response.status).toBe(401);
    expect(response.status, `the stack logged:\n${walk.logs()}`).not.toBe(500);
  }, 60_000);

  it('step 3 — a request that reads a row succeeds against a real token', async () => {
    // The narrowest thing that would break if the deployment were wrong (§10).
    // A token this issuer signed, a document nobody has created: the answer
    // must be "not found", which is only reachable by querying.
    const token = walk.oidc.mint('walker');
    const response = await fetch(
      `${walk.baseUrl}/documents/00000000-0000-0000-0000-000000000001/negotiate`,
      {
        method: 'POST',
        headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` },
        body: '{}',
      },
    );

    expect([403, 404], `the stack logged:\n${walk.logs()}`).toContain(response.status);
  }, 60_000);

  it('step 4 — the image contains the application', async () => {
    const page = await fetch(walk.baseUrl);
    expect(page.ok).toBe(true);

    const html = await page.text();
    expect(html).toContain('<div id="root">');

    // The asset, not just the reference to it. An index.html that names a
    // bundle nobody shipped is exactly what register row 18 was.
    const asset = /src="([^"]+\.js)"/.exec(html)?.[1];
    expect(asset, 'index.html references no script').toBeDefined();

    const bundle = await fetch(new URL(asset!, walk.baseUrl));
    expect(bundle.ok).toBe(true);

    const source = await bundle.text();
    expect(source.length).toBeGreaterThan(10_000);

    // §13.26: the shipped bundle must not be React's development build. The
    // Dockerfile asserts this at build time; this asserts it about the thing
    // actually being served, which is the claim that matters.
    expect(source).not.toContain('Download the React DevTools');
  }, 60_000);

  it('step 5 — the deployment tells the browser how to sign in', async () => {
    const response = await fetch(`${walk.baseUrl}/config`);
    expect(response.ok).toBe(true);

    const config = (await response.json()) as { issuer: string; clientId: string };
    expect(config.issuer).toBe(walk.oidc.issuer);
    expect(config.clientId).not.toBe('');
  }, 60_000);

  it('step 6 — a person signs in, in a browser, and gets back', async () => {
    walk.oidc.accounts.add('walker');
    const { page } = await walk.browsing.open();

    const document = '00000000-0000-0000-0000-000000000001';
    await page.goto(`${walk.baseUrl}/d/${document}`);
    await pick(page, 'walker');

    // The redirect to the issuer, the code, the exchange, and the return — all
    // of it through the proxy, against a stack nothing seeded.
    await page.waitForFunction(
      () => !window.location.pathname.startsWith('/callback')
        && window.document.body.innerText.trim().length > 0,
      undefined,
      { timeout: 60_000 },
    );

    expect(page.url().startsWith(walk.baseUrl)).toBe(true);
    expect(page.url()).not.toContain('code=');
  }, 180_000);

  it('step 7 — a person makes a document, in the product, and opens it', async () => {
    // Where the walk stopped for two phases. Steps 1 to 6 got a user signed in
    // and then handed them an instruction to type an identifier nothing could
    // produce — register rows 15 and 16, and §13.27's whole point: the gap was
    // between criteria, invisible to a suite that had been green for eleven
    // phases. Nothing here is seeded; the document is made by clicking Create.
    walk.oidc.accounts.add('walker');
    const { page } = await walk.browsing.open();

    await page.goto(walk.baseUrl);
    await pick(page, 'walker');

    await page.waitForSelector('[data-testid="create"]', { timeout: 60_000 });
    await page.fill('[data-testid="new-title"]', 'A document made by a person');
    await page.click('[data-testid="create"]');

    // The application navigates to what it created, which is the step that did
    // not exist: an id produced by the product rather than by a fixture.
    await page.waitForFunction(
      () => /\/d\/[0-9a-fA-F-]{36}$/.test(window.location.pathname),
      undefined,
      { timeout: 60_000 },
    );

    await page.waitForFunction(
      () => document.querySelector('[data-testid="state"]')?.textContent === 'live',
      undefined,
      { timeout: 60_000 },
    );

    await page.click('textarea');
    await page.keyboard.type('typed by the walker');

    expect(await page.inputValue('textarea')).toBe('typed by the walker');
  }, 300_000);

  it('step 8 — the owner grants a second person, and both edit the same document', async () => {
    // The Phase 6 done-when, end to end: a new user signs in, makes a document,
    // grants a role to a second user who has signed in, and both edit it. Every
    // identity here is established by clicking an account at the issuer, and
    // every row is written by the product.
    walk.oidc.accounts.add('walker');
    walk.oidc.accounts.add('second-walker');

    const owner = (await walk.browsing.open()).page;
    await owner.goto(walk.baseUrl);
    await pick(owner, 'walker');
    await owner.waitForSelector('[data-testid="create"]', { timeout: 60_000 });
    await owner.fill('[data-testid="new-title"]', 'Shared by two people');
    await owner.click('[data-testid="create"]');
    await owner.waitForSelector('textarea', { timeout: 60_000 });

    const documentUrl = owner.url();

    // The second person signs in and reads their own user id, which is how §9's
    // grant works: there is no directory, so the invitee supplies it.
    const guest = (await walk.browsing.open()).page;
    await guest.goto(walk.baseUrl);
    await pick(guest, 'second-walker');
    await guest.waitForSelector('[data-testid="my-user-id"]', { timeout: 60_000 });
    const guestId = (await guest.textContent('[data-testid="my-user-id"]'))?.trim() ?? '';

    expect(guestId).toMatch(/^[0-9a-fA-F-]{36}$/);

    // Before the grant, the document is not theirs to open. Asserted first, so
    // the assertion after the grant is about the grant.
    await guest.goto(documentUrl);
    await guest.waitForFunction(
      () => window.document.body.innerText.includes('This document is gone'),
      undefined,
      { timeout: 60_000 },
    );

    await owner.waitForSelector('[data-testid="grant-user"]', { timeout: 60_000 });
    await owner.fill('[data-testid="grant-user"]', guestId);
    await owner.click('[data-testid="grant"]');
    await owner.waitForFunction(
      (id: string) => document.querySelector(`[data-member="${id}"]`) !== null,
      guestId,
      { timeout: 60_000 },
    );

    // And now they can, and what one types reaches the other through the CRDT,
    // the hub and the deployed stack.
    await guest.goto(documentUrl);
    await guest.waitForFunction(
      () => document.querySelector('[data-testid="state"]')?.textContent === 'live',
      undefined,
      { timeout: 60_000 },
    );

    await owner.click('textarea');
    await owner.keyboard.type('written by the owner');

    await guest.waitForFunction(
      () => document.querySelector('textarea')?.value === 'written by the owner',
      undefined,
      { timeout: 60_000 },
    );

    expect(await guest.inputValue('textarea')).toBe('written by the owner');
  }, 600_000);

  it('step 9 — an owner takes access back, and the reader is told why', async () => {
    // Revocation as a person performs it. The server closes the revoked
    // reader's socket inside §7's bound (6.4), which the client sees as an
    // ordinary drop; what makes it a revocation rather than a mystery is the
    // reconnect — negotiate answers 404 and §9's table turns that into words.
    //
    // Worth recording that this step's first draft asserted the opposite. I
    // expected the revoked reader to be told nothing, because the close carries
    // no reason, and wrote the step as a finding. Running it against the real
    // stack showed the message does arrive, by way of the retry. The guess was
    // wrong in the direction that would have written a defect into the walk
    // that was not there.
    walk.oidc.accounts.add('walker');
    walk.oidc.accounts.add('third-walker');

    const owner = (await walk.browsing.open()).page;
    await owner.goto(walk.baseUrl);
    await pick(owner, 'walker');
    await owner.waitForSelector('[data-testid="create"]', { timeout: 60_000 });
    await owner.fill('[data-testid="new-title"]', 'Shared then taken back');
    await owner.click('[data-testid="create"]');
    await owner.waitForSelector('textarea', { timeout: 60_000 });

    const documentUrl = owner.url();

    const guest = (await walk.browsing.open()).page;
    await guest.goto(walk.baseUrl);
    await pick(guest, 'third-walker');
    await guest.waitForSelector('[data-testid="my-user-id"]', { timeout: 60_000 });
    const guestId = (await guest.textContent('[data-testid="my-user-id"]'))?.trim() ?? '';

    await owner.waitForSelector('[data-testid="grant-user"]', { timeout: 60_000 });
    await owner.fill('[data-testid="grant-user"]', guestId);
    await owner.click('[data-testid="grant"]');
    await owner.waitForFunction(
      (id: string) => document.querySelector(`[data-member="${id}"]`) !== null,
      guestId,
      { timeout: 60_000 },
    );

    await guest.goto(documentUrl);
    await guest.waitForFunction(
      () => document.querySelector('[data-testid="state"]')?.textContent === 'live',
      undefined,
      { timeout: 60_000 },
    );

    await owner.click(`[data-revoke="${guestId}"]`);
    await owner.waitForFunction(
      (id: string) => document.querySelector(`[data-member="${id}"]`) === null,
      guestId,
      { timeout: 60_000 },
    );

    await guest.waitForFunction(
      () => window.document.body.innerText.includes('This document is gone'),
      undefined,
      { timeout: 60_000 },
    );

    expect(await guest.evaluate(() => window.document.body.innerText))
      .toContain('This document is gone');
  }, 600_000);

  it('step 10 — a person gets rid of a document they made', async () => {
    // Where the walk stopped for Phase 6, and register row 23. A person could
    // make documents and could not get rid of any of them: the schema has
    // `deleted_at` and every read honoured it, so the storage existed since
    // Phase 2 and no path reached it — rows 15 and 16's shape one level up,
    // invisible to every test because every test creates what it needs and
    // never tidies up.
    //
    // The step it replaced asserted the absence of a `[data-delete-document]`
    // element. That selector never matched anything, then or now, so leaving it
    // in place would have let the walk go on reporting the gap as closed by an
    // assertion that could not have noticed either way. Replaced rather than
    // amended, deliberately (§13.19).
    walk.oidc.accounts.add('walker');
    const { page } = await walk.browsing.open();

    await page.goto(walk.baseUrl);
    await pick(page, 'walker');

    // Its own document, made here, so the step neither depends on nor destroys
    // what the earlier steps built.
    await page.waitForSelector('[data-testid="create"]', { timeout: 60_000 });
    await page.fill('[data-testid="new-title"]', 'A document to be got rid of');
    await page.click('[data-testid="create"]');

    await page.waitForFunction(
      () => /\/d\/[0-9a-fA-F-]{36}$/.test(window.location.pathname),
      undefined,
      { timeout: 60_000 },
    );

    const path = new URL(page.url()).pathname;
    const documentId = path.slice(path.lastIndexOf('/') + 1);

    await page.click('[data-testid="home-link"]');
    await page.waitForSelector(`[data-remove="${documentId}"]`, { timeout: 60_000 });

    // The product asks before destroying something, so the walk answers.
    page.once('dialog', (dialog) => void dialog.accept());
    await page.click(`[data-remove="${documentId}"]`);

    // Gone from the listing, on the server's say-so rather than by the page
    // splicing a row out of its own state.
    await page.waitForFunction(
      (id: string) => window.document.querySelector(`[data-document="${id}"]`) === null,
      documentId,
      { timeout: 60_000 },
    );

    // And gone as a destination: the URL a person may still have open, or
    // bookmarked, no longer opens a document.
    await page.goto(`${walk.baseUrl}/d/${documentId}`);

    await page.waitForFunction(
      () => window.document.querySelector('[role="alert"]') !== null
        || window.document.querySelector('[data-testid="documents"]') !== null,
      undefined,
      { timeout: 60_000 },
    );

    expect(await page.evaluate(
      () => window.document.querySelector('textarea') !== null,
    )).toBe(false);
  }, 300_000);
});
