import type { ElementId } from './elementId';
import type { ElementState, VersionVectorEntry } from './elementState';
import { indent, quote, renderMap } from './normalisedJson';
import type { Side } from './operation';
import { formatReplicaId, parseReplicaId } from './replicaId';

/**
 * The normative JSON form of a snapshot (PROJECT_SPEC.md §6 and §9).
 *
 * The second implementation of what `SnapshotSerializer` is on the C# side. It
 * is the *normative* form: §6 stores binary, but what a correct serialisation
 * *is* is defined here, and §9 requires `binary → JSON → binary` and
 * `JSON → binary → JSON` to be byte-identical on both implementations. Binary
 * correctness derives from this, which is why this file exists on the client
 * even though the client stores binary.
 *
 * Sequence numbers are decimal strings: a JSON number is a double and stops
 * round-tripping above 2^53.
 */
export const SNAPSHOT_VERSION = 1;

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

function renderElement(element: ElementState): string {
  // Keys in code-point order: deleted, id, parent, rightOrigin, side, value.
  return [
    `${indent(2)}{`,
    `${indent(3)}${quote('deleted')}: ${element.isDeleted ? 'true' : 'false'},`,
    `${indent(3)}${quote('id')}: ${quote(formatId(element.id))},`,
    `${indent(3)}${quote('parent')}: ${element.parent === null ? 'null' : quote(formatId(element.parent))},`,
    `${indent(3)}${quote('rightOrigin')}: ${
      element.rightOrigin === null ? 'null' : quote(formatId(element.rightOrigin))
    },`,
    `${indent(3)}${quote('side')}: ${quote(element.side)},`,
    `${indent(3)}${quote('value')}: ${quote(element.value)}`,
    `${indent(2)}}`,
  ].join('\n');
}

/** Encodes a snapshot in the normative form. */
export function serializeSnapshot(
  elements: readonly ElementState[],
  versionVector: readonly VersionVectorEntry[],
  text: string,
): string {
  const vector = new Map<string, string>();
  for (const entry of versionVector) {
    vector.set(formatReplicaId(entry.replica), entry.count.toString());
  }

  const body =
    elements.length === 0
      ? `${indent(1)}${quote('elements')}: []`
      : `${indent(1)}${quote('elements')}: [\n${elements.map(renderElement).join(',\n')}\n${indent(1)}]`;

  return [
    '{',
    `${body},`,
    `${indent(1)}${quote('text')}: ${quote(text)},`,
    `${indent(1)}${quote('v')}: ${SNAPSHOT_VERSION},`,
    renderMap(1, 'versionVector', vector),
    '}',
    '',
  ].join('\n');
}

export interface ParsedSnapshot {
  readonly elements: ElementState[];
  readonly versionVector: VersionVectorEntry[];
  readonly text: string;
}

/** Decodes the normative form, refusing a version it does not know (§9). */
export function deserializeSnapshot(json: string): ParsedSnapshot {
  const root = JSON.parse(json) as {
    elements: {
      deleted: boolean;
      id: string;
      parent: string | null;
      rightOrigin: string | null;
      side: string;
      value: string;
    }[];
    text: string;
    v: number;
    versionVector: Record<string, string>;
  };

  if (root.v !== SNAPSHOT_VERSION) {
    throw new Error(
      `Snapshot version ${root.v} is not supported. This build reads version ${SNAPSHOT_VERSION}.`,
    );
  }

  const elements = root.elements.map((e) => {
    if (e.side !== 'L' && e.side !== 'R') {
      throw new Error(`Unknown side '${e.side}'.`);
    }
    if ([...e.value].length !== 1) {
      throw new Error('An element carries exactly one code point (§7).');
    }
    const side: Side = e.side === 'L' ? 'L' : 'R';
    return {
      id: parseId(e.id),
      value: e.value,
      parent: e.parent === null ? null : parseId(e.parent),
      side,
      rightOrigin: e.rightOrigin === null ? null : parseId(e.rightOrigin),
      isDeleted: e.deleted,
    };
  });

  const versionVector = Object.entries(root.versionVector).map(([replica, count]) => ({
    replica: parseReplicaId(replica),
    count: BigInt(count),
  }));

  return { elements, versionVector, text: root.text };
}
