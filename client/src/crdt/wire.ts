import { type ElementId } from './elementId';
import type { Operation, Side } from './operation';
import { formatReplicaId, parseReplicaId } from './replicaId';

/**
 * The wire encoding of a single operation (PROJECT_SPEC.md §6).
 *
 * A second implementation of the same encoding as Editor.Infrastructure, which
 * is why §9's corpus round-trips every trace through this form. An encoding
 * divergence has to fail the build the way an algorithm divergence does.
 *
 * Sequence numbers are decimal strings: a JSON number is a double and stops
 * round-tripping above 2^53. "End of document" is a right origin rather than the
 * absence of one, so it is carried explicitly — a left child and a right child
 * at the end of the document both have no right-origin id, and they do not order
 * the same way.
 */
function formatId(id: ElementId): string {
  return `${formatReplicaId(id.replica)}:${id.seq.toString()}`;
}

function parseId(text: string): ElementId {
  const separator = text.lastIndexOf(':');
  if (separator < 0) {
    throw new Error(`'${text}' is not an element id.`);
  }
  return {
    replica: parseReplicaId(text.slice(0, separator)),
    seq: BigInt(text.slice(separator + 1)),
  };
}

function quote(value: string): string {
  return JSON.stringify(value);
}

/** Encodes an operation. Keys are emitted in code-point order. */
export function encodeOperation(operation: Operation): string {
  const insert = operation.kind === 'insert' ? operation : null;
  const remove = operation.kind === 'delete' ? operation : null;

  const lines = [
    '{',
    `  "id": ${quote(formatId(operation.id))},`,
    `  "parent": ${insert?.parent ? quote(formatId(insert.parent)) : 'null'},`,
    `  "rightOrigin": ${insert?.rightOrigin ? quote(formatId(insert.rightOrigin)) : 'null'},`,
    `  "rightOriginIsEnd": ${insert !== null && insert.side === 'R' && insert.rightOrigin === null ? 'true' : 'false'},`,
    `  "side": ${insert ? quote(insert.side) : 'null'},`,
    `  "target": ${remove ? quote(formatId(remove.target)) : 'null'},`,
    `  "type": ${quote(insert ? 'insert' : 'delete')},`,
    `  "value": ${insert ? quote(insert.value) : 'null'}`,
    '}',
    '',
  ];

  return lines.join('\n');
}

interface WireOperation {
  id: string;
  parent: string | null;
  rightOrigin: string | null;
  rightOriginIsEnd: boolean;
  side: Side | null;
  target: string | null;
  type: 'insert' | 'delete';
  value: string | null;
}

/** Decodes an operation. */
export function decodeOperation(json: string): Operation {
  const raw = JSON.parse(json) as WireOperation;
  const id = parseId(raw.id);

  if (raw.type === 'delete') {
    if (raw.target === null) {
      throw new Error('A delete needs a target.');
    }
    return { kind: 'delete', id, target: parseId(raw.target) };
  }

  if (raw.side !== 'L' && raw.side !== 'R') {
    throw new Error(`Unknown side '${String(raw.side)}'.`);
  }
  if (raw.value === null || [...raw.value].length !== 1) {
    throw new Error('An insert carries exactly one code point.');
  }

  return {
    kind: 'insert',
    id,
    value: raw.value,
    parent: raw.parent === null ? null : parseId(raw.parent),
    side: raw.side,
    rightOrigin: raw.rightOrigin === null ? null : parseId(raw.rightOrigin),
  };
}
