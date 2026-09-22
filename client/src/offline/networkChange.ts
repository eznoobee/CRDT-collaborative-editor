/**
 * The one transport error row 40's instrumentation named, and the bounded
 * rebuild that absorbs it.
 *
 * @remarks
 * <p>
 * <b>This is a module rather than four lines inside the suite because the four
 * lines had never run.</b> Two CI runs went green after the rebuild was added
 * and neither exercised it — `net::ERR_NETWORK_CHANGED` simply did not happen,
 * which is what green means most of the time. A retry that has never fired is
 * the same shape as 7.5's discard reporting: a branch that was unreachable for
 * nine phases while the suite around it passed.
 * </p><p>
 * Extracted, it is reachable from the client's default suite, where the
 * conditions can be made to occur on demand. The e2e suite composes it; nothing
 * here knows about Playwright.
 * </p>
 */

/**
 * Chromium's error when the host's network configuration changes underneath an
 * in-flight request.
 *
 * @remarks
 * Not a class and not a name this project invented: it is the one `errorText`
 * Chromium emits for that condition. Row 40 recorded it twice, on `GET /me` and
 * on `POST /documents`, each within the first two seconds of the browser's
 * first navigation and each with the API answering on either side (§13.65).
 */
export const NETWORK_CHANGED = 'net::ERR_NETWORK_CHANGED';

/**
 * Whether a failure should be blamed on the runner's network changing.
 *
 * @remarks
 * Both sources are consulted because the two failure modes report differently:
 * a request that dies mid-flight is recorded by the browser's `requestfailed`
 * handler, while a navigation that dies rejects with the reason in its message.
 * Neither alone saw both of row 40's sightings.
 */
export function isNetworkChange(error: unknown, recorded: readonly string[]): boolean {
  return recorded.some((failure) => failure.includes(NETWORK_CHANGED))
    || String(error).includes(NETWORK_CHANGED);
}

export interface Rebuildable<R, T> {
  /** Acquires whatever the attempt needs — a fresh browser context, in the suite. */
  open: () => Promise<R>;

  /** Does the work. Its failure is what may be retried. */
  build: (resource: R) => Promise<T>;

  /** Releases a resource whose attempt is being abandoned. */
  abandon: (resource: R) => Promise<void>;

  /** What the browser has recorded as unmakeable since {@link reset}. */
  recorded: () => readonly string[];

  /**
   * Forgets recorded failures before an attempt.
   *
   * @remarks
   * Without this a single early `NETWORK_CHANGED` would authorise a retry for
   * every later failure of any kind, which is the widening §13.29 forbids.
   */
  reset: () => void;

  /**
   * How many attempts in total.
   *
   * @remarks
   * The largest legitimate use is one: a single network change during a single
   * arrangement. Three leaves two spare, and a fourth would be a suite waiting
   * on something that is not going to resolve.
   */
  attempts?: number;

  /** Called when an attempt is abandoned, so a CI log says the rebuild fired. */
  note?: (attempt: number, total: number) => void;
}

/**
 * Runs `build` until it succeeds or fails for a reason that is not the runner's
 * network changing.
 *
 * @remarks
 * <p>
 * <b>Every other failure propagates on the first attempt.</b> That is the whole
 * design: the caller's span may be wide — in the offline suite it is the entire
 * arrangement, everything before the link is taken away — but the *error* stays
 * exactly one string. Widening the span is not widening the class (§13.65).
 * </p><p>
 * A fresh resource per attempt, and the abandoned one released, so a retry is
 * an arrangement rather than a continuation.
 * </p>
 */
export async function rebuildingOnNetworkChange<R, T>(
  steps: Rebuildable<R, T>,
): Promise<T> {
  const total = steps.attempts ?? 3;

  for (let attempt = 1; ; attempt++) {
    const resource = await steps.open();
    steps.reset();

    try {
      return await steps.build(resource);
    } catch (error) {
      if (!isNetworkChange(error, steps.recorded()) || attempt >= total) {
        throw error;
      }

      steps.note?.(attempt, total);
      await steps.abandon(resource);
    }
  }
}
