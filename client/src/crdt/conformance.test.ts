import { readFileSync, readdirSync, mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { replay, renderNormalised, type Trace, type TraceResult } from './conformance';

const repoRoot = resolve(__dirname, '..', '..', '..');
const traceDir = join(repoRoot, 'tests', 'Conformance', 'traces');

function loadTraces(): Trace[] {
  return readdirSync(traceDir)
    .filter((f) => f.endsWith('.json'))
    .sort()
    .map((f) => JSON.parse(readFileSync(join(traceDir, f), 'utf8')) as Trace);
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

  it('writes the normalised result file', () => {
    const results: TraceResult[] = traces.map(replay);
    const rendered = renderNormalised('typescript', results);

    const outDir = join(repoRoot, 'artifacts', 'conformance');
    mkdirSync(outDir, { recursive: true });
    writeFileSync(join(outDir, 'typescript.json'), rendered, 'utf8');

    expect(rendered.startsWith('{\n')).toBe(true);
    expect(rendered.endsWith('}\n')).toBe(true);
  });
});
