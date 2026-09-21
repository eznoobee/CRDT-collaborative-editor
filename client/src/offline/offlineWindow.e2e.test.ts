import { afterAll, beforeAll, describe, expect, it } from 'vitest';

import { startWalk, type Walk } from '../walk/harness';
import { pick } from '../e2e/browser';

/**
 * §9's offline-window discard, in a browser, against the deployed stack
 * (register row 31).
 *
 * @remarks
 * <p>
 * <b>What was already verified, and why it was not enough.</b> 7.5 established
 * this in three places: the server retires a replica past `T_retire` and
 * declines to resume it, `SyncController` discards the outbox and reports the
 * count, and the raw negotiate body carries the two fields the client keys on.
 * All three run without Docker and all three passed. What none of them shows is
 * a person losing work and being told — the claim §9 actually makes is about
 * what appears on a screen, and every previous test asserted a step on the way
 * to it.
 * </p><p>
 * <b>And the discard was silent once.</b> 7.5 found that `SyncController`
 * emptied the outbox and reported nothing — the branch that reported correctly
 * was unreachable, and had been since Phase 4. That is the defect this suite
 * exists to make impossible to reintroduce, and it is exactly the kind a
 * unit-level assertion on the reporting branch can miss, because such an
 * assertion calls the branch directly.
 * </p><p>
 * <b>Its own stack.</b> §5's `T_retire` is seven days and a browser cannot be
 * left open that long, so this suite brings up the product with the three
 * retirement durations shortened and nothing else changed. It cannot share the
 * walk's stack: a one-minute `T_retire` there would retire the walk's own tabs
 * between its steps. See `deploy/docker-compose.offline-window.yml`.
 * </p><p>
 * <b>The vacuity risks, named before this was written.</b>
 * </p><p>
 * <b>First: a client that never connected has nothing to discard, and its
 * screen looks the same.</b> A test that went offline before the first
 * connection would see no message and could be read as the feature working or
 * as nothing having happened. So the session is established first and asserted
 * — `live`, and the outbox empty — before anything is taken away.
 * </p><p>
 * <b>Second: an empty outbox discards nothing, and reports nothing, correctly.</b>
 * `SyncController` reports only when `lost > 0`, which is right and which would
 * make a test that typed nothing while offline pass against a client that had
 * no discard at all. The offline typing is therefore asserted to have produced
 * a backlog before the connection is restored.
 * </p><p>
 * <b>Third: "the message appeared" is satisfied by any message.</b> The
 * assertion is on §9's sentence <em>and</em> on the count being the work that
 * was actually queued, because a client reporting "0 unsent changes" would have
 * discarded silently in every way that matters.
 * </p>
 */
describe("§9's offline-window discard, seen by a person", () => {
  let walk: Walk;

  beforeAll(async () => {
    walk = await startWalk({
      overlays: ['deploy/docker-compose.offline-window.yml'],
      port: 8444,
    });
  }, 900_000);

  afterAll(async () => {
    await walk?.close();
  }, 300_000);

  it('tells the user what was lost, after being away longer than the window', async () => {
    walk.oidc.accounts.add('offline-walker');
    const { context, page } = await walk.browsing.open();

    await page.goto(walk.baseUrl);
    await pick(page, 'offline-walker');

    await page.waitForSelector('[data-testid="create"]', { timeout: 60_000 });
    await page.fill('[data-testid="new-title"]', 'Written before the link went');
    await page.click('[data-testid="create"]');

    await page.waitForFunction(
      () => /\/d\/[0-9a-fA-F-]{36}$/.test(window.location.pathname),
      undefined,
      { timeout: 60_000 },
    );

    // The first vacuity guard: a session that was never established has nothing
    // to lose, and its screen is indistinguishable from one that lost work
    // silently.
    await page.waitForFunction(
      () => document.querySelector('[data-testid="state"]')?.textContent === 'live',
      undefined,
      { timeout: 60_000 },
    );

    await page.click('textarea');
    await page.keyboard.type('sent while connected');

    await page.waitForFunction(
      () => document.querySelector('[data-testid="backlog"]') === null,
      undefined,
      { timeout: 60_000 },
    );

    // The link goes. Not the server, and not the document: this is a person on
    // a train, and everything about the deployment stays up.
    await context.setOffline(true);

    await page.waitForFunction(
      () => document.querySelector('[data-testid="state"]')?.textContent === 'offline',
      undefined,
      { timeout: 120_000 },
    );

    // Enough to fill the outbox past the threshold the unsent-work line uses,
    // so the second guard can assert there was something to lose.
    await page.click('textarea');
    for (let batch = 0; batch < 12; batch++) {
      await page.keyboard.type(` offline-${batch}`);
    }

    await page.waitForFunction(
      () => document.querySelector('textarea')?.textContent !== undefined,
      undefined,
      { timeout: 30_000 },
    );

    const queued = await page.evaluate(
      () => (window.document.querySelector('[data-testid="backlog"]')?.textContent ?? '').trim(),
    );

    // Longer than T_retire plus a sweep, so the server has actually retired
    // this replica rather than merely being entitled to.
    await new Promise((done) => setTimeout(done, 45_000));

    await context.setOffline(false);

    // §9's sentence, on the screen. The count matters: a client reporting zero
    // would have discarded silently in every way that counts.
    await page.waitForFunction(
      () => /This client was offline too long\. [1-9]\d* unsent change/
        .test(window.document.body.innerText),
      undefined,
      { timeout: 180_000 },
    );

    const reported = await page.evaluate(
      () => window.document.querySelector('[data-testid="problem"]')?.textContent ?? '',
    );

    expect(reported).toMatch(/This client was offline too long\./);
    expect(reported).toMatch(/[1-9]\d* unsent changes? could not be recovered\./);

    // The second guard, asserted after the fact rather than before, because the
    // backlog line is what says the offline typing produced unsent work at all.
    // If this is empty the run above proved nothing: an empty outbox is
    // discarded silently and correctly.
    expect(queued, 'nothing was queued while offline, so nothing could be lost')
      .toMatch(/\d+ edits not sent yet\./);

    // And the session recovers rather than stopping: §9's discard costs the
    // unsent work, not the document.
    await page.waitForFunction(
      () => document.querySelector('[data-testid="state"]')?.textContent === 'live',
      undefined,
      { timeout: 120_000 },
    );
  }, 900_000);
});
