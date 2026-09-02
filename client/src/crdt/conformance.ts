import { Replica } from './replica';
import { parseReplicaId, formatReplicaId } from './replicaId';
import { encodeOperation, decodeOperation } from './wire';
import type { Operation } from './operation';
import { compareByCodePoint, quote, renderMap } from './normalisedJson';
import { decodeSnapshot, encodeSnapshot } from './binary';
import { deserializeSnapshot, serializeSnapshot } from './snapshotJson';

export interface TraceResult {
  readonly name: string;
  readonly text: string;
  readonly replicaTexts: ReadonlyMap<string, string>;
  readonly versionVector: ReadonlyMap<string, string>;
  /** The same operations replayed after a trip through the wire encoding. */
  readonly wireRoundTripText: string;
  /** The binary snapshot (§6) as lowercase hex, so the artefacts compare it. */
  readonly snapshot: string;
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
  const produced: Operation[] = [];

  for (const op of trace.ops) {
    switch (op.op) {
      case 'insert': {
        const codePoints = [...op.value!];
        if (codePoints.length !== 1) {
          throw new Error(`A trace value must be exactly one code point, got '${op.value!}'.`);
        }
        produced.push(replicas[op.replica!]!.insert(op.index!, codePoints[0]!));
        break;
      }
      case 'delete':
        produced.push(replicas[op.replica!]!.delete(op.index!));
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

  // §6: the encoding is a second implementation alongside the C# one, so it is
  // exercised on every trace. Anything it loses — a right origin that meant
  // end-of-document, a side, a sequence past 2^53 — changes this text.
  const mirror = new Replica(ids[0]!);
  for (const operation of produced) {
    mirror.apply(decodeOperation(encodeOperation(operation)));
  }

  return {
    name: trace.name,
    text: replicas[0]!.text,
    replicaTexts,
    versionVector,
    wireRoundTripText: mirror.text,
    snapshot: snapshotHex(replicas[0]!),
  };
}

function hex(bytes: Uint8Array): string {
  let out = '';
  for (const b of bytes) {
    out += b.toString(16).padStart(2, '0');
  }
  return out;
}

/**
 * Encodes the replica as binary, having first checked that binary and the
 * normative JSON agree about it in both directions (PROJECT_SPEC.md §6, §9).
 *
 * Binary is the storage form; JSON is what a correct serialisation *is*. The two
 * round trips are what tie them together: without them binary would be a second
 * definition of correctness that nothing checks against the first, and the two
 * would drift the way any unchecked pair of implementations drifts.
 */
function snapshotHex(replica: Replica): string {
  const elements = replica.export();
  const vector = replica.versionVectorEntries;

  const binary = encodeSnapshot(elements, vector);
  const json = serializeSnapshot(elements, vector, replica.text);

  // binary -> JSON -> binary
  const fromBinary = decodeSnapshot(binary);
  const viaJson = deserializeSnapshot(
    serializeSnapshot(
      fromBinary.elements,
      fromBinary.versionVector,
      Replica.import(replica.id, fromBinary.elements, fromBinary.versionVector).text,
    ),
  );
  const reBinary = encodeSnapshot(viaJson.elements, viaJson.versionVector);
  if (hex(reBinary) !== hex(binary)) {
    throw new Error(
      'binary -> JSON -> binary is not byte-identical, so the binary form and the normative ' +
        'form disagree about this document (§6).',
    );
  }

  // JSON -> binary -> JSON
  const fromJson = deserializeSnapshot(json);
  const roundTripped = decodeSnapshot(encodeSnapshot(fromJson.elements, fromJson.versionVector));
  const reJson = serializeSnapshot(
    roundTripped.elements,
    roundTripped.versionVector,
    fromJson.text,
  );
  if (reJson !== json) {
    throw new Error('JSON -> binary -> JSON is not byte-identical (§6).');
  }

  return hex(binary);
}

/**
 * Renders the normalised result file defined in PROJECT_SPEC.md §9.
 *
 * The escaping and ordering rules come from `normalisedJson`, so there is one
 * TypeScript implementation of them rather than one here and another for
 * snapshots.
 */
export function renderNormalised(implementation: string, results: TraceResult[]): string {
  const ordered = [...results].sort((a, b) => compareByCodePoint(a.name, b.name));

  const blocks = ordered.map((r) =>
    [
      '    {',
      `      ${quote('name')}: ${quote(r.name)},`,
      `${renderMap(3, 'replicaTexts', r.replicaTexts)},`,
      `      ${quote('snapshot')}: ${quote(r.snapshot)},`,
      `      ${quote('text')}: ${quote(r.text)},`,
      renderMap(3, 'versionVector', r.versionVector),
      '    }',
    ].join('\n'),
  );

  return [
    '{',
    `  ${quote('implementation')}: ${quote(implementation)},`,
    '  "results": [',
    blocks.join(',\n'),
    '  ],',
    '  "v": 2',
    '}',
    '',
  ].join('\n');
}
