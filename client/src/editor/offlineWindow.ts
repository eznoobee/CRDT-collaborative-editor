/**
 * How long offline work has before the server discards it (§5, §9).
 *
 * @remarks
 * §5 retires a replica after `T_retire` of inactivity and tells it to resync
 * and discard local state on reconnect. §9 turns that into a client obligation:
 * record the last successful sync, show the remaining window, and warn before
 * it runs out — "accepting an hour of offline work and then throwing it away
 * without warning is a data-loss bug, not a limitation".
 */

/** §5's `T_retire`, in milliseconds. Seven days. */
export const RETIRE_AFTER_MS = 7 * 24 * 60 * 60 * 1000;

/**
 * How long is left before the warning turns urgent.
 *
 * @remarks
 * A day, because the action it asks for — get online once — is not something a
 * person can necessarily do in the next ten minutes. A warning that arrives
 * with an hour left is a warning that arrives after the decision that mattered.
 */
export const WARN_WITHIN_MS = 24 * 60 * 60 * 1000;

export type WindowState =
  /** Plenty of time; nothing to say. */
  | 'fresh'
  /** Running out. The user is told, and can still act. */
  | 'warning'
  /** Past `T_retire`. Unsent work will be discarded on reconnect. */
  | 'expired';

export interface OfflineWindow {
  readonly state: WindowState;

  /** Milliseconds until `T_retire`, floored at zero. */
  readonly remainingMs: number;
}

/**
 * The window as it stands.
 *
 * @param lastSyncedAt - Epoch milliseconds of the last accepted submission, or
 * null if the server has never accepted anything from this replica.
 * @param now - Epoch milliseconds. Injected: a clock read inside this function
 * makes every test of it either fragile or untestable.
 *
 * @remarks
 * A replica that has never synced is treated as counting from *now* rather than
 * from the epoch. A brand-new document is not seven days stale, and the
 * alternative — reporting `expired` for a client that has simply not saved
 * yet — would warn every user on their first keystroke and train them to
 * dismiss the warning that matters.
 */
export function offlineWindow(
  lastSyncedAt: number | null,
  now: number,
  retireAfterMs: number = RETIRE_AFTER_MS,
  warnWithinMs: number = WARN_WITHIN_MS,
): OfflineWindow {
  const since = lastSyncedAt ?? now;
  const remainingMs = Math.max(0, since + retireAfterMs - now);

  if (remainingMs === 0) {
    return { state: 'expired', remainingMs };
  }

  return { state: remainingMs <= warnWithinMs ? 'warning' : 'fresh', remainingMs };
}

/** The window as a phrase a person can act on. */
export function describeWindow(window: OfflineWindow): string {
  if (window.state === 'expired') {
    return 'Offline too long — unsent changes will be discarded when you reconnect.';
  }

  const hours = Math.floor(window.remainingMs / (60 * 60 * 1000));

  if (window.state === 'warning') {
    return hours <= 1
      ? 'Less than an hour to reconnect before unsent changes are discarded.'
      : `About ${hours} hours to reconnect before unsent changes are discarded.`;
  }

  const days = Math.floor(hours / 24);
  return `Offline. ${days} day${days === 1 ? '' : 's'} to reconnect.`;
}
