import { afterAll, beforeAll, describe, expect, it } from 'vitest';

import { startWalk, type Walk } from './harness';

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
    walk.oidc.subject = 'walker';
    const { page } = await walk.browsing.open();

    const document = '00000000-0000-0000-0000-000000000001';
    await page.goto(`${walk.baseUrl}/d/${document}`);

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

  it('step 7 — STOPS HERE: nothing in this product creates a document', async () => {
    // The walk's output. Steps 1 to 6 pass; this is where it ends, and it ends
    // here because there is no way to make a document — register rows 15 and
    // 16, owned by Phase 6 (§13.27). The assertion is deliberately of the gap
    // rather than of a workaround: seeding one here through psql would make the
    // walk green and would be the exact self-concealment §13.27 warns about.
    //
    // When Phase 6 lands, this test is replaced by the next step rather than
    // deleted, and the walk gets further. That is the progress no suite reports.
    walk.oidc.subject = 'walker';
    const { page } = await walk.browsing.open();

    await page.goto(walk.baseUrl);
    await page.waitForFunction(
      () => window.document.body.innerText.includes('Open a document'),
      undefined,
      { timeout: 60_000 },
    );

    const shown = await page.evaluate(() => window.document.body.innerText);

    // The application's own answer to "I have signed in, now what?" is an
    // instruction to type an identifier the product cannot give anyone.
    expect(shown).toContain('Open a document at /d/<document id>');
  }, 180_000);
});
