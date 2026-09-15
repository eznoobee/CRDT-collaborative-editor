import { Replica } from './replica';
import { parseReplicaId, formatReplicaId } from './replicaId';

export interface TraceResult {
  readonly name: string;
  readonly text: string;
  readonly replicaTexts: ReadonlyMap<string, string>;
  readonly versionVector: ReadonlyMap<string, string>;
}

interface TraceOp {
  op: 'insert' | 'delete' | 'deliver' | 'sync';
  replica?: number;
  index?: number;
  value?: string;
  from?: number;
  to?: number;
}

export interface Trace {
  name: string;
  replicas: { index: number; id: string }[];
  ops: TraceOp[];
  expected: {
    text?: string;
    oneOf?: string[];
    forbidden?: string[];
    rationale: string;
  };
}

function deliver(from: Replica, to: Replica): void {
  for (const op of from.operationsSince(to.versionVector)) {
    to.apply(op);
  }
}

function syncAll(replicas: Replica[]): void {
  for (let pass = 0; pass < 2; pass++) {
    for (const from of replicas) {
      for (const to of replicas) {
        if (from !== to) {
          deliver(from, to);
        }
      }
    }
  }
}

/** Replays one trace. Mirrors the C# TraceReplay, driving the same public API. */
export function replay(trace: Trace): TraceResult {
  const ids = trace.replicas.map((r) => parseReplicaId(r.id));
  const replicas = ids.map((id) => new Replica(id));

  for (const op of trace.ops) {
    switch (op.op) {
      case 'insert': {
        const codePoints = [...op.value!];
        if (codePoints.length !== 1) {
          throw new Error(`A trace value must be exactly one code point, got '${op.value!}'.`);
        }
        replicas[op.replica!]!.insert(op.index!, codePoints[0]!);
        break;
      }
      case 'delete':
        replicas[op.replica!]!.delete(op.index!);
        break;
      case 'deliver':
        deliver(replicas[op.from!]!, replicas[op.to!]!);
        break;
      case 'sync':
        syncAll(replicas);
        break;
    }
  }

  const replicaTexts = new Map<string, string>();
  ids.forEach((id, i) => replicaTexts.set(formatReplicaId(id), replicas[i]!.text));

  const versionVector = new Map<string, string>();
  for (const [replica, count] of replicas[0]!.versionVector) {
    // §6: 64-bit values are decimal strings, never JSON numbers.
    versionVector.set(replica, count.toString());
  }

  return { name: trace.name, text: replicas[0]!.text, replicaTexts, versionVector };
}

/** Compares by Unicode code point, not UTF-16 code unit (§9). */
function compareByCodePoint(a: string, b: string): number {
  const x = [...a];
  const y = [...b];
  for (let i = 0; i < Math.min(x.length, y.length); i++) {
    const cx = x[i]!.codePointAt(0)!;
    const cy = y[i]!.codePointAt(0)!;
    if (cx !== cy) {
      return cx - cy;
    }
  }
  return x.length - y.length;
}

/** Escapes only what JSON requires; non-ASCII stays literal (§9). */
function quote(value: string): string {
  let out = '"';
  for (const ch of value) {
    switch (ch) {
      case '"':
        out += '\\"';
        break;
      case '\\':
        out += '\\\\';
        break;
      case '\n':
        out += '\\n';
        break;
      case '\r':
        out += '\\r';
        break;
      case '\t':
        out += '\\t';
        break;
      case '\b':
        out += '\\b';
        break;
      case '\f':
        out += '\\f';
        break;
      default: {
        const code = ch.codePointAt(0)!;
        out += code < 0x20 ? `\\u${code.toString(16).padStart(4, '0')}` : ch;
      }
    }
  }
  return `${out}"`;
}

function renderMap(key: string, map: ReadonlyMap<string, string>): string {
  if (map.size === 0) {
    return `      ${quote(key)}: {}`;
  }

  const keys = [...map.keys()].sort(compareByCodePoint);
  const body = keys
    .map((k) => `        ${quote(k)}: ${quote(map.get(k)!)}`)
    .join(',\n');

  return `      ${quote(key)}: {\n${body}\n      }`;
}

/**
 * Renders the normalised result file defined in PROJECT_SPEC.md §9.
 *
 * Hand-rolled rather than JSON.stringify: "byte-identical across two languages"
 * is a property of the serialiser, not of the data, and the defaults differ in
 * exactly the places that matter — key order and non-ASCII escaping.
 */
export function renderNormalised(implementation: string, results: TraceResult[]): string {
  const ordered = [...results].sort((a, b) => compareByCodePoint(a.name, b.name));

  const blocks = ordered.map((r) =>
    [
      '    {',
      `      ${quote('name')}: ${quote(r.name)},`,
      `${renderMap('replicaTexts', r.replicaTexts)},`,
      `      ${quote('text')}: ${quote(r.text)},`,
      renderMap('versionVector', r.versionVector),
      '    }',
    ].join('\n'),
  );

  return [
    '{',
    `  ${quote('implementation')}: ${quote(implementation)},`,
    '  "results": [',
    blocks.join(',\n'),
    '  ],',
    '  "v": 1',
    '}',
    '',
  ].join('\n');
}
