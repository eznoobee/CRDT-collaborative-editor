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

  /**
   * Waits for something to become true of the page, and says what was on it if
   * it does not.
   *
   * @remarks
   * §13.23. `page.waitForFunction` times out with the source of the predicate
   * and nothing about the page, which on a remote runner is a failure nobody
   * can act on — the first CI run of this suite timed out waiting for a URL and
   * the log said only that sixty seconds had passed. This reports what the page
   * actually showed and what the stack logged, so an iteration buys a diagnosis
   * rather than a guess.
   */
  /**
   * Every request the browser failed to make, newest last.
   *
   * @remarks
   * "Failed to fetch" is what a page says; it is not what went wrong. Two of
   * three CI runs died on that string with the API demonstrably up — its own
   * retirement sweep was in the stack's logs — so the question is which request
   * failed and why, and only the browser knows. Playwright reports it; nothing
   * was listening.
   */
  const failures: string[] = [];

  function watch(page: Awaited<ReturnType<Walk['browsing']['open']>>['page']): void {
    page.on('requestfailed', (request) => {
      failures.push(`${request.method()} ${request.url()} — ${request.failure()?.errorText ?? 'no reason given'}`);
    });
    page.on('console', (message) => {
      if (message.type() === 'error') {
        failures.push(`console: ${message.text()}`);
      }
    });
  }

  async function until(
    page: Awaited<ReturnType<Walk['browsing']['open']>>['page'],
    predicate: () => boolean,
    describeWait: string,
    timeout = 60_000,
  ): Promise<void> {
    try {
      await page.waitForFunction(predicate, undefined, { timeout });
    } catch (error) {
      const text = await page
        .evaluate(() => window.document.body.innerText)
        .catch(() => '(the page could not be read)');

      throw new Error(
        `waiting for ${describeWait} timed out after ${timeout} ms.\n`
        + `--- url ---\n${page.url()}\n`
        + `--- requests the browser could not make ---\n${
          failures.length === 0 ? '(none)' : failures.slice(-15).join('\n')}\n`
        + `--- what the page showed ---\n${text.slice(0, 2_000)}\n`
        + `--- what the stack logged ---\n${walk.logs().slice(-4_000)}\n`
        + `--- the original error ---\n${String(error)}`,
        { cause: error },
      );
    }
  }

  /**
   * Chromium's error when the host's network configuration changes underneath
   * an in-flight request.
   *
   * @remarks
   * Not a name this suite invented and not a class: it is the one `errorText`
   * Chromium emits for that condition, and it is what row 40's instrumentation
   * finally named — `GET /me — net::ERR_NETWORK_CHANGED` on the first load,
   * with the API serving throughout (its retirement sweep is in the same run's
   * logs). A GitHub runner brings Docker's bridge network up while this suite's
   * stack starts, and a request in flight across that moment dies here.
   */
  const NETWORK_CHANGED = 'net::ERR_NETWORK_CHANGED';

  /**
   * Signs in, retrying only a load that died because the runner's network
   * changed.
   *
   * @remarks
   * <p>
   * <b>The narrowest thing that makes row 40's failure survivable</b>, and
   * deliberately not a retry of the test. §13.29: name the specific thing you
   * are trusting, never widen the class. Three conditions, all required — the
   * failure is the sign-in prologue, before a single claim about §9 has been
   * made; the browser recorded exactly `NETWORK_CHANGED`; and there have been
   * fewer than three attempts. Any other error, or the same error anywhere
   * after this point, fails the suite as before.
   * </p><p>
   * <b>Why not further.</b> Everything after this line is the property under
   * test — the offline transition, the wait past `T_retire`, the reconnection —
   * and a retry there could hide a real discard failure behind a second
   * attempt. This prologue asserts nothing; it gets a signed-in page or it does
   * not.
   * </p><p>
   * <b>It tolerates; it does not fix.</b> The cause is the runner's network,
   * which this repository does not control. Recorded that way in row 40 rather
   * than as a repair.
   * </p>
   */
  async function signIn(
    page: Awaited<ReturnType<Walk['browsing']['open']>>['page'],
    attempts = 3,
  ): Promise<void> {
    for (let attempt = 1; ; attempt++) {
      // Cleared per attempt, so a stale entry from a previous one cannot
      // authorise a retry for a cause that is no longer happening.
      failures.length = 0;

      try {
        await page.goto(walk.baseUrl);
        await pick(page, 'offline-walker');

        await until(
          page,
          () => document.querySelector('[data-testid="create"]') !== null,
          'the signed-in home page to offer a Create button');

        return;
      } catch (error) {
        const blamed = failures.some((failure) => failure.includes(NETWORK_CHANGED))
          || String(error).includes(NETWORK_CHANGED);

        if (!blamed || attempt >= attempts) {
          throw error;
        }

        console.warn(
          `sign-in attempt ${attempt} died on ${NETWORK_CHANGED}; retrying (row 40).`);
      }
    }
  }

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
    watch(page);

    await signIn(page);
    await page.fill('[data-testid="new-title"]', 'Written before the link went');
    await page.click('[data-testid="create"]');

    await until(
      page,
      () => /\/d\/[0-9a-fA-F-]{36}$/.test(window.location.pathname),
      'the application to navigate to the document it created');

    // The first vacuity guard: a session that was never established has nothing
    // to lose, and its screen is indistinguishable from one that lost work
    // silently.
    await until(
      page,
      () => document.querySelector('[data-testid="state"]')?.textContent === 'live',
      "the session to reach 'live'");

    await page.click('textarea');
    await page.keyboard.type('sent while connected');

    await until(
      page,
      () => document.querySelector('[data-testid="backlog"]') === null,
      'the outbox to drain while connected');

    // The link goes. Not the server, and not the document: this is a person on
    // a train, and everything about the deployment stays up.
    await context.setOffline(true);

    await until(
      page,
      () => document.querySelector('[data-testid="state"]')?.textContent === 'offline',
      "the session to notice the link is gone and report 'offline'",
      120_000);

    // Enough to fill the outbox past the threshold the unsent-work line uses,
    // so the second guard can assert there was something to lose.
    await page.click('textarea');
    for (let batch = 0; batch < 12; batch++) {
      await page.keyboard.type(` offline-${batch}`);
    }

    await until(
      page,
      () => document.querySelector('textarea') !== null,
      'the editor to still be on screen after typing offline',
      30_000);

    // Longer than T_retire plus a sweep, so the server has actually retired
    // this replica rather than merely being entitled to.
    await new Promise((done) => setTimeout(done, 45_000));

    await context.setOffline(false);

    // §9's sentence, on the screen. The count matters: a client reporting zero
    // would have discarded silently in every way that counts.
    await until(
      page,
      () => /This client was offline too long\. [1-9]\d* unsent change/
        .test(window.document.body.innerText),
      "§9's discard to be reported on screen with a non-zero count",
      180_000);

    const reported = await page.evaluate(
      () => window.document.querySelector('[data-testid="problem"]')?.textContent ?? '',
    );

    expect(reported).toMatch(/This client was offline too long\./);
    expect(reported).toMatch(/[1-9]\d* unsent changes? could not be recovered\./);

    // THE SECOND GUARD, AND WHERE IT HAD TO MOVE TO. An empty outbox is
    // discarded silently and correctly, so a run that queued nothing would
    // prove nothing — something has to establish that there was work to lose.
    //
    // The first version read the unsent-work line while offline. That can never
    // pass: `backlogMessage` shows it only while `live`, deliberately, because
    // offline already says so on its own line — a guard asserting on a thing the
    // product hides in exactly the state the guard runs in. The run it failed
    // had already passed the assertion above, so §9's discard was on screen and
    // the only broken thing was the check.
    //
    // The count in §9's own sentence is the evidence, and a better one: it is
    // what the user is told. Twelve words were typed offline and each change
    // event is a batch, so a client reporting one or two would be under-counting
    // what it threw away.
    const lost = Number(/(\d+) unsent change/.exec(reported)?.[1] ?? '0');

    expect(lost, `§9 reported "${reported}", which does not account for a page of offline typing`)
      .toBeGreaterThanOrEqual(10);

    // And the session recovers rather than stopping: §9's discard costs the
    // unsent work, not the document.
    await until(
      page,
      () => document.querySelector('[data-testid="state"]')?.textContent === 'live',
      'the session to recover after the discard',
      120_000);
  }, 900_000);
});
