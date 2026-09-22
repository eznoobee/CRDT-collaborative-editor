import { readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

import { replay, type Trace } from './conformance';

/**
 * §9's committed traces, replayed by the default suite (register row 39).
 *
 * @remarks
 * <p>
 * **These were already in the repository and already asserted — in a file
 * `npm test` does not run.** `conformance.test.ts` is excluded from the default
 * suite because it also replays the *generated* corpus, which the C# runner has
 * to materialise first, and its loader throws when that is absent. So the
 * committed traces, which need nothing, were excluded along with the ones that
 * do. 7b.9 measured the consequence: inverting the sibling tie-break left the
 * whole default client suite green, 213 of 213.
 * </p><p>
 * Splitting them out costs nothing and is the whole of row 39's fix. The
 * generated corpus stays where it was, behind the runner that produces it.
 * </p><p>
 * **Why these are an oracle and not the implementation agreeing with itself.**
 * A trace is a scripted execution in *user* terms — "insert 'a' at index 0" —
 * and never names a parent, a side or an origin, so each core derives its own
 * structure from it. The `expected` block is not a recording of what either
 * implementation produced: it carries a `rationale` citing §5 or the paper, and
 * says what the text must be, what it may be, and what it must never be. That
 * makes it a statement from the specification, committed, which is precisely
 * what §13.47 says a convergence assertion can never supply.
 * </p><p>
 * **Vacuity risks, named before this was written.**
 * </p><p>
 * **First: a corpus that silently loads nothing passes every assertion it
 * makes.** §13.28's shape. The count is asserted against a floor, and the floor
 * is the number of files actually committed rather than one — a directory that
 * lost half its traces would otherwise still clear a floor of one.
 * </p><p>
 * **Second: a trace with no `expected` block is a convergence check wearing an
 * oracle's clothes**, and would leave this file green under exactly the
 * comparator inversion it exists to catch. Every committed trace is required to
 * state at least one of `text`, `oneOf` or `forbidden`, so a new trace added
 * without an expectation fails rather than quietly weakening the set.
 * </p>
 */
const repoRoot = resolve(__dirname, '..', '..', '..');
const traceDir = join(repoRoot, 'tests', 'Conformance', 'traces');

function loadCommitted(): Trace[] {
  return readdirSync(traceDir)
    .filter((f) => f.endsWith('.json'))
    .sort()
    .map((f) => JSON.parse(readFileSync(join(traceDir, f), 'utf8')) as Trace);
}

describe('the committed conformance traces, replayed without the C# runner', () => {
  const traces = loadCommitted();

  it('loaded the whole committed corpus', () => {
    // Not `> 0`. A floor of one is cleared by a directory that lost everything
    // but the first file, and the point of this file is to be the client's only
    // placement oracle — one that can shrink silently is not one.
    expect(traces.length).toBeGreaterThanOrEqual(9);
  });

  it('every committed trace states an expectation', () => {
    // A trace without one is a convergence check, and convergence is invariant
    // under any consistent ordering rule (§13.47) — so it would sit here
    // looking like coverage while catching nothing.
    for (const trace of traces) {
      const { expected } = trace;
      expect(
        expected.text !== undefined
          || expected.oneOf !== undefined
          || expected.forbidden !== undefined,
        `${trace.name} carries no expected text, oneOf or forbidden, so replaying `
          + 'it asserts only that this implementation agrees with itself.',
      ).toBe(true);
    }
  });

  it.each(traces.map((t) => [t.name, t] as const))(
    'satisfies the expectations of %s',
    (_name, trace) => {
      const result = replay(trace);
      const { expected } = trace;

      for (const [replica, text] of result.replicaTexts) {
        expect(text, `replica ${replica} diverged. ${expected.rationale}`).toBe(result.text);
      }

      expect(
        result.wireRoundTripText,
        `wire round trip diverged from direct replay. ${expected.rationale}`,
      ).toBe(result.text);

      if (expected.text !== undefined) {
        expect(result.text, expected.rationale).toBe(expected.text);
      }

      if (expected.oneOf !== undefined) {
        expect(expected.oneOf, expected.rationale).toContain(result.text);
      }

      if (expected.forbidden !== undefined) {
        expect(expected.forbidden, expected.rationale).not.toContain(result.text);
      }
    },
  );
});
