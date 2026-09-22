/**
 * How long to wait before the next reconnect attempt (§9).
 *
 * @remarks
 * Exponential so a server that is down is not hammered; jittered because
 * exponential alone synchronises clients. Every tab that lost the same server
 * at the same moment retries at the same moment, and the herd arrives together
 * on each attempt — which is how a server that would have recovered does not.
 */
export interface BackoffOptions {
  /** The first delay, before any doubling. */
  readonly base: number;

  /** The longest a client will ever wait between attempts. */
  readonly cap: number;
}

export const DEFAULT_BACKOFF: BackoffOptions = { base: 500, cap: 30_000 };

/**
 * Produces successive reconnect delays.
 *
 * @remarks
 * **Full jitter** — a uniform draw from `[0, capped]` rather than a fraction
 * either side of it. Both spread a herd; full jitter spreads it further, and
 * more importantly it lets an early attempt come back quickly, so a client that
 * lost the server to a one-second blip is not sitting out eight seconds of an
 * outage that has ended.
 *
 * The randomness is injected. A policy that reached for `Math.random` directly
 * could only be tested by running it many times and asserting something about
 * the distribution, which is a slow test that fails occasionally for no reason.
 */
export class Backoff {
  private readonly options: BackoffOptions;
  private readonly random: () => number;
  private attempt = 0;

  constructor(options: BackoffOptions = DEFAULT_BACKOFF, random: () => number = Math.random) {
    this.options = options;
    this.random = random;
  }

  /** How many failures have happened since the last success. */
  get attempts(): number {
    return this.attempt;
  }

  /** The ceiling for the next delay, before jitter. Exposed for tests. */
  get ceiling(): number {
    return Math.min(this.options.cap, this.options.base * 2 ** this.attempt);
  }

  /** The next delay, in milliseconds, and counts one more attempt. */
  next(): number {
    const ceiling = this.ceiling;
    this.attempt++;
    return Math.floor(this.random() * ceiling);
  }

  /**
   * Returns to the first delay.
   *
   * @remarks
   * Called on a *successful* connection, not on a successful attempt to open
   * one. A socket that opens and immediately closes has not succeeded at
   * anything, and resetting on it turns backoff into a tight loop against a
   * server that is failing at exactly the point where it needs the room.
   */
  reset(): void {
    this.attempt = 0;
  }
}
