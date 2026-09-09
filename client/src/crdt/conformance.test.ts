import { existsSync, readFileSync, readdirSync, mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { replay, renderNormalised, type Trace, type TraceResult } from './conformance';

const repoRoot = resolve(__dirname, '..', '..', '..');
const traceDir = join(repoRoot, 'tests', 'Conformance', 'traces');

/**
 * §9's generated corpus, written by the C# runner from the committed seed.
 *
 * Produced by one implementation and replayed by both, which is legitimate
 * precisely because a trace is a scripted execution in user terms: it says
 * "insert 'a' at index 0" and never names a parent, a side or an origin, so
 * each core still derives its own structure. The alternative — a second
 * generator written in TypeScript — is §13.11's shape, two implementations of
 * one idea with nothing comparing them.
 */
const generatedDir = join(repoRoot, 'artifacts', 'conformance', 'generated');

function load(dir: string): Trace[] {
  return readdirSync(dir)
    .filter((f) => f.endsWith('.json'))
    .sort()
    .map((f) => JSON.parse(readFileSync(join(dir, f), 'utf8')) as Trace);
}

function loadTraces(): Trace[] {
  return load(traceDir);
}

function loadGenerated(): Trace[] {
  if (!existsSync(generatedDir)) {
    // Never silently zero. An empty generated corpus would make the comparison
    // pass over nothing, which is the shape §13.28 is about — a check that is
    // green because it examined nothing.
    throw new Error(
      `${generatedDir} does not exist. The C# runner materialises the generated `
      + 'corpus from the committed seed; run scripts/conformance.sh, which runs it first.',
    );
  }

  return load(generatedDir);
}

describe('conformance corpus', () => {
  const traces = loadTraces();

  it('has traces to run', () => {
    expect(traces.length).toBeGreaterThan(0);
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

  it('replays the generated corpus, converging every time', () => {
    const generated = loadGenerated();

    // §11's Phase 5 row. Asserted here rather than trusted, because a corpus
    // the runner did not actually read is a corpus that proves nothing.
    expect(generated.length).toBeGreaterThanOrEqual(1_000);

    for (const trace of generated) {
      const result = replay(trace);

      // No expectation to check — generated traces carry none, deliberately.
      // What is checkable without one is convergence and the wire round trip;
      // agreement with the other implementation is the diff of the two result
      // files.
      for (const [replica, text] of result.replicaTexts) {
        expect(text, `${trace.name}: replica ${replica} diverged`).toBe(result.text);
      }

      expect(result.wireRoundTripText, `${trace.name}: wire round trip diverged`)
        .toBe(result.text);
    }
  });

  it('writes the normalised result file', () => {
    const results: TraceResult[] = [...traces, ...loadGenerated()].map(replay);
    const rendered = renderNormalised('typescript', results);

    const outDir = join(repoRoot, 'artifacts', 'conformance');
    mkdirSync(outDir, { recursive: true });
    writeFileSync(join(outDir, 'typescript.json'), rendered, 'utf8');

    expect(rendered.startsWith('{\n')).toBe(true);
    expect(rendered.endsWith('}\n')).toBe(true);
  });
});
