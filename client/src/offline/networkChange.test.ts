import { describe, expect, it } from 'vitest';

import {
  NETWORK_CHANGED,
  isNetworkChange,
  rebuildingOnNetworkChange,
} from './networkChange';

/**
 * The rebuild row 40 added, made to fire.
 *
 * @remarks
 * <p>
 * <b>Why this exists.</b> Two CI runs went green after the rebuild was written
 * and neither executed it — `NETWORK_CHANGED` did not occur, which is what
 * green means most of the time. **A green suite proved the fault absent, not
 * the repair working**, and those are the two things a flaky-test fix most
 * needs to tell apart.
 * </p><p>
 * This project has been here before. 7.5 found that `SyncController` emptied
 * the outbox and reported nothing, because the branch that reported correctly
 * had been unreachable since Phase 4 while every suite around it passed. An
 * unexercised retry is that shape exactly.
 * </p><p>
 * <b>The vacuity risks, named first.</b> A test that only checks the happy
 * retry would pass against a function that retries everything — which is the
 * one behaviour §13.29 forbids, because it would also absorb a real discard
 * regression. So the case that matters most here is the one asserting a
 * *different* error is not retried, and the one asserting the attempt cap is
 * real. "It recovered" alone is satisfied by `while (true)`.
 * </p>
 */
describe("row 40's rebuild, made to fire", () => {
  /** The shape of a Playwright request failure, as the suite records it. */
  const changed = `GET https://host:8444/me — ${NETWORK_CHANGED}`;

  interface Run {
    opened: number;
    abandoned: number;
    notes: number[];
  }

  /**
   * Drives the rebuild with a scripted sequence of attempt outcomes.
   *
   * @remarks
   * Each entry is what that attempt does: `'change'` records a network change
   * and throws, an `Error` throws without recording one, and `'ok'` succeeds.
   * The recorded-failure list is real rather than stubbed — `reset` empties it,
   * exactly as the suite's does — because the clearing is itself a guard worth
   * exercising.
   */
  async function drive(
    script: readonly ('change' | 'ok' | Error)[],
    attempts?: number,
  ): Promise<{ result: string | Error; run: Run }> {
    const run: Run = { opened: 0, abandoned: 0, notes: [] };
    let recorded: string[] = [];
    let step = 0;

    try {
      const value = await rebuildingOnNetworkChange<number, string>({
        open: () => Promise.resolve(++run.opened),
        build: (resource) => {
          const outcome = script[step++];

          if (outcome === 'ok') {
            return Promise.resolve(`built on resource ${resource}`);
          }

          if (outcome === 'change') {
            recorded.push(changed);
            return Promise.reject(new Error('page.waitForFunction: Timeout 60000ms exceeded.'));
          }

          return Promise.reject(outcome ?? new Error('script ran out'));
        },
        abandon: () => {
          run.abandoned++;
          return Promise.resolve();
        },
        recorded: () => recorded,
        reset: () => {
          recorded = [];
        },
        note: (attempt) => run.notes.push(attempt),
        ...(attempts === undefined ? {} : { attempts }),
      });

      return { result: value, run };
    } catch (error) {
      return { result: error as Error, run };
    }
  }

  it('rebuilds after a network change and returns the later attempt', async () => {
    const { result, run } = await drive(['change', 'ok']);

    expect(result).toBe('built on resource 2');
    expect(run.opened, 'a retry must open a fresh resource').toBe(2);
    expect(run.abandoned, 'the abandoned resource must be released').toBe(1);
    expect(run.notes, 'the CI log has to say the rebuild fired').toEqual([1]);
  });

  it('does not rebuild for any other failure', async () => {
    // THE CASE THAT MATTERS. Without it, every assertion here is satisfied by a
    // function that retries unconditionally — which would also absorb a real
    // regression in §9's discard, the one thing the suite exists to catch.
    const real = new Error('§9 reported "0 unsent changes", which is the silent discard');
    const { result, run } = await drive([real, 'ok']);

    expect(result).toBe(real);
    expect(run.opened, 'it must not have tried again').toBe(1);
    expect(run.abandoned).toBe(0);
    expect(run.notes).toEqual([]);
  });

  it('gives up after the third attempt rather than waiting forever', async () => {
    const { result, run } = await drive(['change', 'change', 'change']);

    expect(result).toBeInstanceOf(Error);
    expect(run.opened).toBe(3);

    // Two abandoned, not three: the last attempt's failure is the one that
    // propagates, and its resource belongs to the caller's cleanup.
    expect(run.abandoned).toBe(2);
    expect(run.notes).toEqual([1, 2]);
  });

  it('forgets recorded failures between attempts', async () => {
    // Otherwise one early network change authorises a retry for every later
    // failure of any kind — §13.29's widening, arrived at by accident.
    const real = new Error('the editor never reached live');
    const { result, run } = await drive(['change', real, 'ok']);

    expect(result).toBe(real);
    expect(run.opened, 'the second failure was not a network change').toBe(2);
  });

  it('succeeds without opening twice when nothing goes wrong', async () => {
    const { result, run } = await drive(['ok']);

    expect(result).toBe('built on resource 1');
    expect(run.opened).toBe(1);
    expect(run.notes).toEqual([]);
  });

  describe('what counts as a network change', () => {
    it('reads it from what the browser recorded', () => {
      expect(isNetworkChange(new Error('Timeout 60000ms exceeded'), [changed])).toBe(true);
    });

    it('reads it from a navigation that rejected with the reason', () => {
      // The other half. A request that dies mid-flight is recorded by
      // `requestfailed`; a navigation that dies carries it in its message, and
      // neither source alone saw both of row 40's sightings.
      expect(isNetworkChange(new Error(`page.goto: ${NETWORK_CHANGED} at https://host`), []))
        .toBe(true);
    });

    it('is false for a timeout with nothing recorded', () => {
      expect(isNetworkChange(new Error('Timeout 60000ms exceeded'), [])).toBe(false);
    });

    it('is false for a different transport error', () => {
      // Named, not a class: a connection reset is also the network misbehaving
      // and is deliberately not absorbed, because it is not what row 40
      // observed and a suite that tolerates every transport error tolerates a
      // broken stack.
      expect(isNetworkChange(new Error('x'), ['GET /me — net::ERR_CONNECTION_RESET']))
        .toBe(false);
    });
  });
});
