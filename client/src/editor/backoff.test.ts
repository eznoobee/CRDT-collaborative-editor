import { Backoff, DEFAULT_BACKOFF } from './backoff';

/**
 * Reconnect delays (§9).
 *
 * The vacuity risks, named before these were written:
 *
 * 1. **A backoff test measured on the wall clock is either slow or flaky**, and
 *    usually both — so nothing here sleeps. The randomness is injected and the
 *    delays are asserted as numbers.
 * 2. **"It grows" is satisfied by any increasing sequence**, including one that
 *    grows forever, which is a client that gives up on a server that came back.
 *    The cap is asserted, and asserted as a value reached rather than as a
 *    limit that happens not to be hit.
 * 3. **Jitter is invisible to a test with a fixed random source.** With
 *    `random = () => 1` every delay equals its ceiling and a policy with no
 *    jitter at all passes. So the jitter tests vary the source and assert the
 *    delays differ — which a jitterless policy cannot do.
 */

describe('backoff', () => {
  it('doubles the ceiling on each attempt', () => {
    // Asserted on the ceiling rather than the jittered delay, because a
    // uniform draw makes any single delay uninformative — a growing sequence
    // that produced 3ms twice in a row would look like it was not growing.
    const backoff = new Backoff({ base: 100, cap: 100_000 }, () => 1);

    const ceilings = [0, 1, 2, 3, 4].map(() => {
      const ceiling = backoff.ceiling;
      backoff.next();
      return ceiling;
    });

    expect(ceilings).toEqual([100, 200, 400, 800, 1600]);
  });

  it('stops growing at the cap', () => {
    const backoff = new Backoff({ base: 1000, cap: 4000 }, () => 1);

    for (let attempt = 0; attempt < 10; attempt++) {
      backoff.next();
    }

    // Reached, not merely never exceeded: a policy capped at a value it never
    // gets to is untested at its only interesting point.
    expect(backoff.ceiling).toBe(4000);
    expect(backoff.next()).toBeLessThanOrEqual(4000);
  });

  it('never returns a delay above the ceiling', () => {
    const backoff = new Backoff({ base: 100, cap: 1000 }, () => 0.999999);

    for (let attempt = 0; attempt < 20; attempt++) {
      const ceiling = backoff.ceiling;
      expect(backoff.next()).toBeLessThanOrEqual(ceiling);
    }
  });

  it('spreads two clients that failed at the same moment', () => {
    // The property jitter exists for. Without it these two sequences are
    // identical, every tab retries in lockstep, and the herd arrives together
    // on each attempt — which is how a server that would have recovered does
    // not.
    const draws = [0.1, 0.9, 0.3, 0.7];
    let one = 0;
    let two = 0;

    const first = new Backoff({ base: 1000, cap: 60_000 }, () => draws[one++ % draws.length]!);
    const second = new Backoff({ base: 1000, cap: 60_000 }, () => draws[(two++ + 1) % draws.length]!);

    const firstDelays = [0, 1, 2, 3].map(() => first.next());
    const secondDelays = [0, 1, 2, 3].map(() => second.next());

    expect(firstDelays).not.toEqual(secondDelays);
  });

  it('varies within one client, so an early attempt can come back quickly', () => {
    // Full jitter rather than a band around the ceiling: a client that lost the
    // server to a one-second blip should not sit out eight seconds of an
    // outage that has already ended.
    const draws = [0.01, 0.99, 0.5];
    let at = 0;
    const backoff = new Backoff({ base: 10_000, cap: 60_000 }, () => draws[at++ % draws.length]!);

    const delays = [0, 1, 2].map(() => backoff.next());

    expect(new Set(delays).size).toBe(3);
    expect(Math.min(...delays)).toBeLessThan(1000);
  });

  it('returns to the first delay on reset', () => {
    const backoff = new Backoff({ base: 100, cap: 100_000 }, () => 1);

    backoff.next();
    backoff.next();
    backoff.next();
    expect(backoff.ceiling).toBe(800);

    backoff.reset();

    expect(backoff.attempts).toBe(0);
    expect(backoff.ceiling).toBe(100);
  });

  it('has a cap a person would accept waiting', () => {
    // §9 asks for backoff and does not name numbers, so this pins the ones
    // chosen: half a second to first retry, and never more than thirty seconds
    // between attempts. A cap in minutes means a laptop that wakes up sits
    // disconnected while the document is available.
    expect(DEFAULT_BACKOFF.base).toBe(500);
    expect(DEFAULT_BACKOFF.cap).toBe(30_000);
  });
});
