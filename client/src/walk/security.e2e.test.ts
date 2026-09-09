import { afterAll, beforeAll, describe, expect, it } from 'vitest';

import { startWalk, type Walk } from './harness';

/**
 * §7, asserted against the application as Compose starts it (register row 13).
 *
 * @remarks
 * <p>
 * Every §7 test written before this phase runs against a test host, where the
 * test supplies the configuration it is about to verify. That is §13.28's
 * shape: the check passes and the artefact is broken, which is exactly the
 * state Phase 5b found the deployment in under a suite green for eleven
 * phases. §7 now says what "verified against the deployment" means, and this
 * file is where that verification happens — every request below entered
 * through the published port of a stack brought up from `docker-compose.yml`
 * and a `.env`, with nothing reconfigured by the test.
 * </p><p>
 * <strong>Its own stack, not the walk's.</strong> Sharing one bring-up would
 * be faster and would make a walk failure cascade into spurious failures here,
 * which is §13.23's cost — a harness that cannot explain its own failure. Two
 * independent bring-ups also mean the artefact is shown to start correctly
 * twice.
 * </p><p>
 * <strong>The vacuity risk this file exists to avoid, and can fall into.</strong>
 * A test here that asserts something the test host already proved is a slower
 * copy of a test that exists. What makes a check worth its minute is a
 * dependency on the deployed <em>configuration</em> — so the token tests below
 * mint tokens that are wrong in one claim each, because absence of a setting
 * is already loud (`docker-compose.yml` uses `${VAR:?}` for every OIDC value,
 * so a missing one fails interpolation before anything starts) and wrongness
 * is silent. A deployment whose audience is misconfigured accepts tokens meant
 * for another service and looks entirely healthy.
 * </p>
 */
describe('§7 against the deployed stack', () => {
  let walk: Walk;

  beforeAll(async () => {
    walk = await startWalk();
  }, 900_000);

  afterAll(async () => {
    await walk?.close();
  });

  /** A request through the proxy, with a bearer token. */
  function call(path: string, token: string, init: RequestInit = {}): Promise<Response> {
    return fetch(`${walk.baseUrl}${path}`, {
      ...init,
      headers: {
        authorization: `Bearer ${token}`,
        ...(init.body === undefined ? {} : { 'content-type': 'application/json' }),
        ...(init.headers ?? {}),
      },
    });
  }

  it('accepts a correctly-issued token, so the refusals below mean something', async () => {
    // The control. Without it every assertion in this file is satisfied by a
    // deployment that refuses everything — including one whose database is
    // unreachable, which is how §13.28's smoke test stayed green.
    const response = await call('/me', walk.oidc.mint('deployment-baseline'));

    expect(response.status).toBe(200);

    const me = (await response.json()) as { userId: string };
    expect(me.userId).toMatch(/^[0-9a-fA-F-]{36}$/);
  }, 60_000);

  it('refuses a token from another issuer (Oidc__Issuer is enforced, not merely set)', async () => {
    const forged = walk.oidc.mintWith({
      subject: 'deployment-wrong-issuer',
      issuer: 'https://someone-elses-issuer.invalid/',
    });

    const response = await call('/me', forged);

    expect(response.status).toBe(401);
  }, 60_000);

  it('refuses a token for another audience (Oidc__Audience is enforced)', async () => {
    // The check §7 names and the one a deployment most plausibly gets wrong:
    // a token minted by the right issuer, signed by the right key, for a
    // different service. Everything about it is valid except who it is for.
    const elsewhere = walk.oidc.mintWith({
      subject: 'deployment-wrong-audience',
      audience: 'some-other-service',
    });

    const response = await call('/me', elsewhere);

    expect(response.status).toBe(401);
  }, 60_000);

  it('refuses an expired token, with no clock skew allowance', async () => {
    const stale = walk.oidc.mintWith({
      subject: 'deployment-expired',
      expiresInSeconds: -30,
    });

    const response = await call('/me', stale);

    expect(response.status).toBe(401);
  }, 60_000);

  it('refuses an unauthenticated call rather than answering it', async () => {
    const response = await fetch(`${walk.baseUrl}/me`);

    expect(response.status).toBe(401);
  }, 60_000);

  it('answers 404 for a document the caller cannot see, the same as for one that does not exist', async () => {
    // §7's rule, through the proxy. A different status for "exists but not
    // yours" than for "does not exist" is an enumeration oracle, and both ids
    // below are real in exactly one of the two senses.
    const owner = walk.oidc.mint('deployment-owner');
    const stranger = walk.oidc.mint('deployment-stranger');

    const created = await call('/documents', owner, {
      method: 'POST',
      body: JSON.stringify({ title: 'Not yours' }),
    });

    expect(created.status).toBe(201);
    const document = (await created.json()) as { id: string };

    const theirs = await call(`/documents/${document.id}`, stranger);
    const absent = await call('/documents/00000000-0000-4000-8000-000000000000', stranger);

    expect(theirs.status).toBe(404);
    expect(absent.status).toBe(theirs.status);
  }, 60_000);

  it('issues a connect ticket that is opaque and not the bearer token', async () => {
    // §7 puts a ticket in the query string precisely so a JWT is not there.
    // Asserted on what the deployed negotiate actually hands back.
    const token = walk.oidc.mint('deployment-ticket');

    const created = await call('/documents', token, {
      method: 'POST',
      body: JSON.stringify({ title: 'Ticketed' }),
    });

    const document = (await created.json()) as { id: string };

    const negotiated = await call(`/documents/${document.id}/negotiate`, token, {
      method: 'POST',
      body: JSON.stringify({ replicaId: null }),
    });

    expect(negotiated.status).toBe(200);

    const answer = (await negotiated.json()) as { ticket: string; replicaId: string };

    expect(answer.ticket).not.toBe(token);
    expect(answer.ticket.startsWith('eyJ')).toBe(false);
    expect(answer.ticket).not.toContain('.');
    expect(answer.replicaId).toMatch(/^[0-9a-fA-F-]{36}$/);
  }, 60_000);

  it('refuses to negotiate on a document the caller is not a member of', async () => {
    const owner = walk.oidc.mint('deployment-negotiate-owner');
    const stranger = walk.oidc.mint('deployment-negotiate-stranger');

    const created = await call('/documents', owner, {
      method: 'POST',
      body: JSON.stringify({ title: 'Members only' }),
    });

    const document = (await created.json()) as { id: string };

    const refused = await call(`/documents/${document.id}/negotiate`, stranger, {
      method: 'POST',
      body: JSON.stringify({ replicaId: null }),
    });

    expect(refused.status).toBe(404);
  }, 60_000);
});
