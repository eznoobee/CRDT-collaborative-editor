import {
  RETIRE_AFTER_MS,
  WARN_WITHIN_MS,
  describeWindow,
  offlineWindow,
} from './offlineWindow';

/**
 * §9's offline window.
 *
 * **This task is blocked and is not done in isolation** (§12). Nothing sets
 * `retired_at` — §5 gives that to Phase 7 — so the discard these warnings are
 * about never actually happens yet. Everything below tests the arithmetic and
 * the message against an injected clock, which is real work and is not the
 * property §9 states. A client that warns correctly about a discard that never
 * occurs passes every test anyone would write for it, and that is exactly the
 * §13.15 shape. The end-to-end assertion arrives with retirement.
 *
 * The vacuity risk within what *can* be tested: **"it warns when the window is
 * nearly up" is satisfied by a component that always warns.** So every warning
 * assertion is paired with a case that must stay quiet, on the same clock.
 */

const DAY = 24 * 60 * 60 * 1000;
const NOW = 1_700_000_000_000;

describe('the offline window', () => {
  it('is quiet with days to go', () => {
    const window = offlineWindow(NOW - DAY, NOW);

    expect(window.state).toBe('fresh');
    expect(window.remainingMs).toBe(6 * DAY);
  });

  it('warns inside the last day', () => {
    // The pair for the test above: with the same clock and a different
    // last-sync, the answer changes. A function that always warned would fail
    // the first test, and one that never warned fails this.
    const window = offlineWindow(NOW - (RETIRE_AFTER_MS - 3 * 60 * 60 * 1000), NOW);

    expect(window.state).toBe('warning');
    expect(window.remainingMs).toBe(3 * 60 * 60 * 1000);
  });

  it('is expired once the window has run out', () => {
    const window = offlineWindow(NOW - RETIRE_AFTER_MS - 1, NOW);

    expect(window.state).toBe('expired');
    expect(window.remainingMs).toBe(0);
  });

  it('does not go negative', () => {
    // A negative remaining would render as "-3 hours to reconnect", which is
    // the kind of detail that makes a user distrust the rest of the interface.
    expect(offlineWindow(NOW - 100 * DAY, NOW).remainingMs).toBe(0);
  });

  it('treats a document that has never synced as starting now', () => {
    // A brand-new document is not seven days stale. Counting from the epoch
    // would warn every user on their first keystroke and teach them to dismiss
    // the warning that matters.
    const window = offlineWindow(null, NOW);

    expect(window.state).toBe('fresh');
    expect(window.remainingMs).toBe(RETIRE_AFTER_MS);
  });

  it('changes state exactly at the boundaries', () => {
    // The two edges, asserted on both sides, because an off-by-one here is a
    // warning that arrives a day late or a day early and neither is visible in
    // a test that only samples the middle.
    const justInside = offlineWindow(NOW - (RETIRE_AFTER_MS - WARN_WITHIN_MS) + 1, NOW);
    const justOutside = offlineWindow(NOW - (RETIRE_AFTER_MS - WARN_WITHIN_MS) - 1, NOW);

    expect(justInside.state).toBe('fresh');
    expect(justOutside.state).toBe('warning');

    expect(offlineWindow(NOW - RETIRE_AFTER_MS + 1, NOW).state).toBe('warning');
    expect(offlineWindow(NOW - RETIRE_AFTER_MS, NOW).state).toBe('expired');
  });

  it('matches §5’s T_retire rather than a number of its own', () => {
    expect(RETIRE_AFTER_MS).toBe(7 * DAY);
  });
});

describe('what the user is told', () => {
  it('says how long is left, in units a person acts on', () => {
    expect(describeWindow(offlineWindow(NOW - DAY, NOW))).toContain('6 days');
    expect(describeWindow(offlineWindow(NOW - (RETIRE_AFTER_MS - 3 * 60 * 60 * 1000), NOW)))
      .toContain('3 hours');
  });

  it('says what will happen, not just that something is wrong', () => {
    // §9: the client must not silently accept edits that will be discarded.
    // "Offline" alone is not that warning — it does not tell the user their
    // work is at stake, which is the only part that would change what they do.
    const warning = describeWindow(offlineWindow(NOW - (RETIRE_AFTER_MS - 60 * 60 * 1000), NOW));
    const expired = describeWindow(offlineWindow(NOW - RETIRE_AFTER_MS, NOW));

    expect(warning).toContain('discarded');
    expect(expired).toContain('discarded');
  });

  it('does not threaten a user who has days left', () => {
    // The pair. A message that always mentions discarding is a message people
    // stop reading.
    expect(describeWindow(offlineWindow(NOW - DAY, NOW))).not.toContain('discarded');
  });

  it('reads correctly at one day and under an hour', () => {
    expect(describeWindow(offlineWindow(NOW - (RETIRE_AFTER_MS - DAY - 1), NOW)))
      .toContain('1 day');
    expect(describeWindow(offlineWindow(NOW - (RETIRE_AFTER_MS - 30 * 60 * 1000), NOW)))
      .toContain('Less than an hour');
  });
});
