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
   * <p>
   * Not a name this suite invented and not a class: it is the one `errorText`
   * Chromium emits for that condition, and it is what row 40's instrumentation
   * named. Twice, on two different requests:
   * </p>
   * <pre>
   * bdbf784  GET  /me         — net::ERR_NETWORK_CHANGED
   * 7be8c7a  POST /documents  — net::ERR_NETWORK_CHANGED   (nginx logged 499)
   * </pre>
   * <p>
   * <b>The second one corrected the first reading.</b> `bdbf784` was explained
   * as a runner bringing up Docker's bridge network while this suite's stack
   * starts, and a repair scoped to the sign-in prologue followed from that.
   * `7be8c7a` got past the prologue — `GET /me` and `GET /documents` both
   * answered 200 — and died on the next request, with nginx recording a 499
   * because the client had gone. The stack was not still starting; it had been
   * serving for a second.
   * </p><p>
   * <b>What both have in common is the browser, not the stack.</b> Each failure
   * landed within the first two seconds of the browser's first navigation, and
   * the API answered every request either side of it. That is the observable
   * pattern, and it is as far as the evidence goes: why a GitHub runner's
   * network changes at that moment is not something this repository can see.
   * </p>
   */
  const NETWORK_CHANGED = 'net::ERR_NETWORK_CHANGED';

  /**
   * How many times the arrangement may be rebuilt after a network change.
   *
   * @remarks
   * The largest legitimate use is one — a single network change during a single
   * arrangement. Three leaves two spare; a fourth would be a suite waiting on
   * something that is not going to resolve.
   */
  const ATTEMPTS = 3;

  function blames(error: unknown): boolean {
    return failures.some((failure) => failure.includes(NETWORK_CHANGED))
      || String(error).includes(NETWORK_CHANGED);
  }

  /**
   * Everything up to the moment the link is taken away, retried only when the
   * runner's network changed under it.
   *
   * @remarks
   * <p>
   * <b>The boundary is `setOffline`, and it is a principled one rather than the
   * line the last failure happened to be on.</b> Everything here is arrangement:
   * sign in, make a document, reach `live`, type something and watch it drain.
   * A failure in any of it means the property was never exercised. Everything
   * after it — the offline transition, the wait past `T_retire`, the
   * reconnection, §9's sentence — is the property, and a retry there could hide
   * a real discard regression behind a second attempt. Nothing after this
   * function retries, for any reason.
   * </p><p>
   * <b>The first attempt at this was scoped to the sign-in prologue</b>, because
   * the one failure then on record was in the prologue. It was scoped to the
   * evidence rather than to a boundary that means something, and the next CI run
   * failed four requests later. Widening it now is not widening the class — the
   * error is still exactly `NETWORK_CHANGED`, still required to have been
   * reported by the browser, still bounded (§13.29). What changed is the span
   * that counts as "the test has not started yet", which is a fact about this
   * test and not about the failure.
   * </p><p>
   * <b>A fresh context per attempt</b>, so a retry is an arrangement rather than
   * a continuation: a half-signed-in browser carrying a document that may or may
   * not have been created is not a state any assertion below should run against.
   * </p>
   */
  async function arrange(): Promise<{
    context: Awaited<ReturnType<Walk['browsing']['open']>>['context'];
    page: Awaited<ReturnType<Walk['browsing']['open']>>['page'];
  }> {
    for (let attempt = 1; ; attempt++) {
      const { context, page } = await walk.browsing.open();

      // Cleared per attempt, so a stale entry cannot authorise a retry for a
      // cause that has stopped happening.
      failures.length = 0;
      watch(page);

      try {
        await page.goto(walk.baseUrl);
        await pick(page, 'offline-walker');

        await until(
          page,
          () => document.querySelector('[data-testid="create"]') !== null,
          'the signed-in home page to offer a Create button');

        await page.fill('[data-testid="new-title"]', 'Written before the link went');
        await page.click('[data-testid="create"]');

        await until(
          page,
          () => /\/d\/[0-9a-fA-F-]{36}$/.test(window.location.pathname),
          'the application to navigate to the document it created');

        // The first vacuity guard: a session that was never established has
        // nothing to lose, and its screen is indistinguishable from one that
        // lost work silently.
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

        return { context, page };
      } catch (error) {
        if (!blames(error) || attempt >= ATTEMPTS) {
          throw error;
        }

        console.warn(
          `arrangement attempt ${attempt} died on ${NETWORK_CHANGED}; rebuilding it (row 40).`);
        await context.close();
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

    // Everything the property needs to be in place, and the last point at which
    // a retry is legitimate. See `arrange`.
    const { context, page } = await arrange();

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
