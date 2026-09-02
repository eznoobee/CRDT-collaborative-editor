import { compareElementId, elementIdsEqual, type ElementId } from './elementId';
import type { ElementState, VersionVectorEntry } from './elementState';
import type { InsertOperation, Operation, Side } from './operation';
import { compareReplicaId, replicaIdFromBytes, replicaIdToBytes, type ReplicaId } from './replicaId';

/**
 * The binary encoding of PROJECT_SPEC.md §6 — the storage and wire form.
 *
 * The normative form is still the normalised JSON of §9. §9 requires
 * `binary → JSON → binary` and `JSON → binary → JSON` to be byte-identical on
 * both implementations, so binary correctness derives from JSON correctness
 * rather than standing on its own.
 *
 * Written from §6's layout, not from `Editor.Infrastructure`. Two codecs derived
 * from one description disagree loudly when either is wrong; a second derived
 * from the first inherits its mistakes without disagreeing at all.
 */

const MAGIC = Uint8Array.of(0x43, 0x52, 0x44, 0x54); // "CRDT"
export const FORMAT_VERSION = 1;

const KIND_SNAPSHOT = 0x01;
const KIND_OPERATIONS = 0x02;

const TAG_ELEMENT = 0x00;
const TAG_RUN = 0x01;

const OP_INSERT = 0x00;
const OP_DELETE = 0x01;

const FLAG_SIDE_RIGHT = 0b0000_0001;
const FLAG_DELETED = 0b0000_0010;
const PARENT_MASK = 0b0000_1100;
const PARENT_ROOT = 0b0000_0000;
const PARENT_PREVIOUS = 0b0000_0100;
const PARENT_EXPLICIT = 0b0000_1000;
const PARENT_INVALID = 0b0000_1100;
const FLAG_RIGHT_ORIGIN_EXPLICIT = 0b0001_0000;
const RESERVED_MASK = 0b1110_0000;

/**
 * Thrown when a body does not decode. Never partially applied.
 *
 * §9: a codec that guesses at input it does not understand produces a document
 * that is wrong but well-formed, and every replica then agrees on it. Agreeing
 * is the whole point of the system, which is why agreeing on corruption is the
 * failure it exists to prevent.
 */
export class BinaryFormatError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'BinaryFormatError';
  }
}

class Writer {
  private readonly bytes: number[] = [];

  byte(value: number): void {
    this.bytes.push(value & 0xff);
  }

  varint(value: bigint | number): void {
    let v = typeof value === 'bigint' ? value : BigInt(value);
    if (v < 0n) {
      throw new BinaryFormatError('A varint is unsigned.');
    }
    while (v >= 0x80n) {
      this.bytes.push(Number((v & 0x7fn) | 0x80n));
      v >>= 7n;
    }
    this.bytes.push(Number(v));
  }

  raw(values: Uint8Array): void {
    for (const value of values) {
      this.bytes.push(value);
    }
  }

  finish(): Uint8Array {
    return Uint8Array.from(this.bytes);
  }
}

/**
 * A bounds-checked forward reader. Every read either succeeds or throws:
 * there is no try-variant, because a partial decode is the outcome §6 forbids.
 */
class Reader {
  private position = 0;
  private readonly source: Uint8Array;

  constructor(source: Uint8Array) {
    this.source = source;
  }

  get remaining(): number {
    return this.source.length - this.position;
  }

  byte(): number {
    if (this.position >= this.source.length) {
      throw new BinaryFormatError('Input ended in the middle of a record.');
    }
    return this.source[this.position++]!;
  }

  bytes(count: number): Uint8Array {
    if (count < 0 || this.remaining < count) {
      throw new BinaryFormatError(
        `Input ended after ${this.remaining} bytes with ${count} expected.`,
      );
    }
    const slice = this.source.subarray(this.position, this.position + count);
    this.position += count;
    return slice;
  }

  /** Unsigned LEB128, rejecting a non-minimal encoding (§6 canonical form). */
  varint(): bigint {
    let value = 0n;
    let shift = 0n;
    for (;;) {
      if (shift > 63n) {
        throw new BinaryFormatError('Varint is longer than 64 bits.');
      }
      const b = this.byte();
      value |= BigInt(b & 0x7f) << shift;
      if ((b & 0x80) === 0) {
        if (b === 0 && shift > 0n) {
          throw new BinaryFormatError('Varint is not minimally encoded (trailing zero group).');
        }
        return value;
      }
      shift += 7n;
    }
  }

  count(what: string): number {
    const value = this.varint();
    if (value > BigInt(Number.MAX_SAFE_INTEGER)) {
      throw new BinaryFormatError(`${what} of ${value} is larger than this build accepts.`);
    }
    return Number(value);
  }
}

const utf8Encoder = new TextEncoder();
const utf8Decoder = new TextDecoder('utf-8', { fatal: true });

function decodeCodePoints(bytes: Uint8Array, expected: number, what: string): string[] {
  let text: string;
  try {
    text = utf8Decoder.decode(bytes);
  } catch {
    throw new BinaryFormatError(`${what} is not well-formed UTF-8 (§7).`);
  }

  const points = [...text];
  if (points.length !== expected) {
    throw new BinaryFormatError(`${what} carries ${points.length} code points, not ${expected}.`);
  }
  return points;
}

function readValue(reader: Reader): string {
  const length = reader.count('A value byte length');
  if (length < 1 || length > 4) {
    throw new BinaryFormatError(
      `A value is one code point, so 1 to 4 UTF-8 bytes, not ${length} (§7).`,
    );
  }
  return decodeCodePoints(reader.bytes(length), 1, 'A value')[0]!;
}

function writeValue(writer: Writer, value: string): void {
  const encoded = utf8Encoder.encode(value);
  writer.varint(encoded.length);
  writer.raw(encoded);
}

function readHeader(reader: Reader, expectedKind: number): void {
  const magic = reader.bytes(MAGIC.length);
  for (let i = 0; i < MAGIC.length; i++) {
    if (magic[i] !== MAGIC[i]) {
      throw new BinaryFormatError('Not a CRDT binary body: the magic bytes do not match.');
    }
  }

  const version = reader.byte();
  if (version !== FORMAT_VERSION) {
    // §9: never a best-effort parse. Naming the supported version is what makes
    // the refusal actionable rather than merely safe.
    throw new BinaryFormatError(
      `Binary format version ${version} is not supported. This build reads version ` +
        `${FORMAT_VERSION}.`,
    );
  }

  const kind = reader.byte();
  if (kind !== expectedKind) {
    throw new BinaryFormatError(`Expected body kind ${expectedKind} but found ${kind}.`);
  }
}

function readTable(reader: Reader): ReplicaId[] {
  const count = reader.count('A replica table size');
  const table: ReplicaId[] = [];
  for (let i = 0; i < count; i++) {
    const id = replicaIdFromBytes(reader.bytes(16).slice());
    if (i > 0 && compareReplicaId(id, table[i - 1]!) <= 0) {
      throw new BinaryFormatError('The replica table must ascend in §5 order and not repeat (§6).');
    }
    table.push(id);
  }
  return table;
}

function readIndex(reader: Reader, tableLength: number): number {
  const index = reader.count('A replica index');
  if (index >= tableLength) {
    throw new BinaryFormatError(
      `Replica index ${index} is past the end of a ${tableLength}-entry table (§6).`,
    );
  }
  return index;
}

function readElementId(reader: Reader, table: ReplicaId[]): ElementId {
  const replica = table[readIndex(reader, table.length)]!;
  return { replica, seq: reader.varint() };
}

function validateFlags(flags: number, isRun: boolean): void {
  if ((flags & RESERVED_MASK) !== 0) {
    throw new BinaryFormatError(
      'Reserved flag bits are set. A version that assigns them is a version bump, and this ' +
        'build must refuse rather than ignore what it cannot see (§6).',
    );
  }
  if ((flags & PARENT_MASK) === PARENT_INVALID) {
    throw new BinaryFormatError('Parent kind 3 is not a value (§6).');
  }
  if (isRun && (flags & FLAG_RIGHT_ORIGIN_EXPLICIT) !== 0) {
    throw new BinaryFormatError('A run record may not carry an explicit right origin (§6).');
  }
  if (isRun && (flags & FLAG_DELETED) !== 0) {
    throw new BinaryFormatError(
      'A run record carries deleted state in its bitmap, so flags bit 1 must be zero (§6).',
    );
  }
}

function readParent(
  reader: Reader,
  flags: number,
  table: ReplicaId[],
  previous: ElementId | null,
): ElementId | null {
  switch (flags & PARENT_MASK) {
    case PARENT_ROOT:
      return null;
    case PARENT_PREVIOUS:
      if (previous === null) {
        throw new BinaryFormatError(
          'The first record cannot name the previous element as its parent (§6).',
        );
      }
      return previous;
    default: {
      const parent = readElementId(reader, table);
      if (previous !== null && elementIdsEqual(parent, previous)) {
        throw new BinaryFormatError(
          'Non-canonical: the parent is the previous element, which flag 1 already says in no ' +
            'bytes (§6).',
        );
      }
      return parent;
    }
  }
}

/**
 * True when `current` continues a run begun by `previous`.
 *
 * A condition on both elements, which §6 is explicit about because an earlier
 * draft was not: `previous` must be able to be in a run at all, meaning it
 * carries no right origin, and `current` must be a right child of it with the
 * next sequence number on the same replica and no right origin of its own.
 *
 * Dropping the first half makes a decoder reject documents its own encoder
 * produces — an element with an explicit right origin followed by a consecutive
 * right child, where the encoder cannot start a run and so writes two records
 * (§13.11).
 */
function canFollow(previous: ElementState, current: ElementState): boolean {
  return (
    previous.rightOrigin === null &&
    compareReplicaId(current.id.replica, previous.id.replica) === 0 &&
    current.id.seq === previous.id.seq + 1n &&
    current.side === 'R' &&
    current.rightOrigin === null &&
    current.parent !== null &&
    elementIdsEqual(current.parent, previous.id)
  );
}

function parentFlag(previous: ElementState | null, parent: ElementId | null): number {
  if (parent === null) {
    return PARENT_ROOT;
  }
  if (previous !== null && elementIdsEqual(parent, previous.id)) {
    return PARENT_PREVIOUS;
  }
  return PARENT_EXPLICIT;
}

function replicaKey(id: ReplicaId): string {
  return `${id.hi.toString(16)}:${id.lo.toString(16)}`;
}

function buildTable(replicas: Iterable<ReplicaId>): ReplicaId[] {
  const seen = new Map<string, ReplicaId>();
  for (const id of replicas) {
    seen.set(replicaKey(id), id);
  }
  return [...seen.values()].sort(compareReplicaId);
}

function indexer(table: ReplicaId[]): (id: ReplicaId) => number {
  const positions = new Map<string, number>();
  table.forEach((id, i) => positions.set(replicaKey(id), i));
  return (id) => {
    const at = positions.get(replicaKey(id));
    if (at === undefined) {
      throw new BinaryFormatError('A replica is referenced but missing from the table.');
    }
    return at;
  };
}

function writeElementId(writer: Writer, id: ElementId, index: (r: ReplicaId) => number): void {
  writer.varint(index(id.replica));
  writer.varint(id.seq);
}

/** Length of the maximal run starting at `start`, or 1 when there is none. */
function runLengthAt(elements: readonly ElementState[], start: number): number {
  // A run's first element may sit on either side, but it must have no right
  // origin: the run form has no room for one.
  if (elements[start]!.rightOrigin !== null) {
    return 1;
  }
  let length = 1;
  while (
    start + length < elements.length &&
    canFollow(elements[start + length - 1]!, elements[start + length]!)
  ) {
    length++;
  }
  return length;
}

function writeElement(
  writer: Writer,
  element: ElementState,
  previous: ElementState | null,
  index: (r: ReplicaId) => number,
): void {
  const parent = parentFlag(previous, element.parent);
  const flags =
    parent |
    (element.side === 'R' ? FLAG_SIDE_RIGHT : 0) |
    (element.isDeleted ? FLAG_DELETED : 0) |
    (element.side === 'R' && element.rightOrigin !== null ? FLAG_RIGHT_ORIGIN_EXPLICIT : 0);

  writer.byte(TAG_ELEMENT);
  writer.byte(flags);
  writeElementId(writer, element.id, index);

  if (parent === PARENT_EXPLICIT) {
    writeElementId(writer, element.parent!, index);
  }
  if ((flags & FLAG_RIGHT_ORIGIN_EXPLICIT) !== 0) {
    writeElementId(writer, element.rightOrigin!, index);
  }

  writeValue(writer, element.value);
}

function writeRun(
  writer: Writer,
  elements: readonly ElementState[],
  position: number,
  length: number,
  previous: ElementState | null,
  index: (r: ReplicaId) => number,
): void {
  const first = elements[position]!;
  const parent = parentFlag(previous, first.parent);
  const flags = parent | (first.side === 'R' ? FLAG_SIDE_RIGHT : 0);

  writer.byte(TAG_RUN);
  writer.varint(length);
  writer.byte(flags);
  writeElementId(writer, first.id, index);

  if (parent === PARENT_EXPLICIT) {
    writeElementId(writer, first.parent!, index);
  }

  const bitmap = new Uint8Array(Math.ceil(length / 8));
  let values = '';
  for (let i = 0; i < length; i++) {
    const element = elements[position + i]!;
    if (element.isDeleted) {
      bitmap[i >> 3]! |= 1 << i % 8;
    }
    values += element.value;
  }

  writer.raw(bitmap);
  const encoded = utf8Encoder.encode(values);
  writer.varint(encoded.length);
  writer.raw(encoded);
}

/** Encodes a snapshot body (kind 0x01) in canonical form. */
export function encodeSnapshot(
  elements: readonly ElementState[],
  versionVector: readonly VersionVectorEntry[],
): Uint8Array {
  const table = buildTable([
    ...elements.flatMap((e) => [
      e.id.replica,
      ...(e.parent !== null ? [e.parent.replica] : []),
      ...(e.rightOrigin !== null ? [e.rightOrigin.replica] : []),
    ]),
    ...versionVector.map((v) => v.replica),
  ]);
  const index = indexer(table);

  const writer = new Writer();
  writer.raw(MAGIC);
  writer.byte(FORMAT_VERSION);
  writer.byte(KIND_SNAPSHOT);

  writer.varint(table.length);
  for (const id of table) {
    writer.raw(replicaIdToBytes(id));
  }

  const vector = [...versionVector].sort((a, b) => index(a.replica) - index(b.replica));
  writer.varint(vector.length);
  for (const entry of vector) {
    writer.varint(index(entry.replica));
    writer.varint(entry.count);
  }

  writer.varint(elements.length);

  let position = 0;
  while (position < elements.length) {
    const previous = position > 0 ? elements[position - 1]! : null;
    const length = runLengthAt(elements, position);

    if (length >= 2) {
      writeRun(writer, elements, position, length, previous, index);
      position += length;
    } else {
      writeElement(writer, elements[position]!, previous, index);
      position++;
    }
  }

  return writer.finish();
}

export interface DecodedSnapshot {
  readonly elements: ElementState[];
  readonly versionVector: VersionVectorEntry[];
}

function readElement(
  reader: Reader,
  table: ReplicaId[],
  elements: ElementState[],
  previous: ElementState | null,
): void {
  const flags = reader.byte();
  validateFlags(flags, false);

  const id = readElementId(reader, table);
  const side: Side = (flags & FLAG_SIDE_RIGHT) !== 0 ? 'R' : 'L';
  const parent = readParent(reader, flags, table, previous === null ? null : previous.id);

  let rightOrigin: ElementId | null = null;
  if ((flags & FLAG_RIGHT_ORIGIN_EXPLICIT) !== 0) {
    if (side === 'L') {
      throw new BinaryFormatError('A left child has no right origin, so bit 4 must be clear (§6).');
    }
    rightOrigin = readElementId(reader, table);
  }

  elements.push({
    id,
    value: readValue(reader),
    parent,
    side,
    rightOrigin,
    isDeleted: (flags & FLAG_DELETED) !== 0,
  });
}

function readRun(
  reader: Reader,
  table: ReplicaId[],
  elements: ElementState[],
  elementCount: number,
  previous: ElementState | null,
): void {
  const count = reader.count('A run length');
  if (count < 2) {
    throw new BinaryFormatError(
      `A run is two or more elements, not ${count}; one element is an element record (§6).`,
    );
  }
  if (count > elementCount - elements.length) {
    throw new BinaryFormatError(`A run of ${count} overruns the declared element count (§6).`);
  }

  const flags = reader.byte();
  validateFlags(flags, true);

  const first = readElementId(reader, table);
  const side: Side = (flags & FLAG_SIDE_RIGHT) !== 0 ? 'R' : 'L';
  const parent = readParent(reader, flags, table, previous === null ? null : previous.id);

  const bitmap = reader.bytes(Math.ceil(count / 8)).slice();
  const values = decodeCodePoints(
    reader.bytes(reader.count('A run value length')).slice(),
    count,
    "A run's values",
  );

  for (let i = 0; i < count; i++) {
    elements.push({
      id: { replica: first.replica, seq: first.seq + BigInt(i) },
      value: values[i]!,
      parent: i === 0 ? parent : { replica: first.replica, seq: first.seq + BigInt(i - 1) },
      side: i === 0 ? side : 'R',
      rightOrigin: null,
      isDeleted: (bitmap[i >> 3]! & (1 << i % 8)) !== 0,
    });
  }
}

/** Decodes a snapshot body, or throws having produced nothing. */
export function decodeSnapshot(encoded: Uint8Array): DecodedSnapshot {
  const reader = new Reader(encoded);
  readHeader(reader, KIND_SNAPSHOT);

  const table = readTable(reader);

  const vectorCount = reader.count('A version vector entry count');
  const versionVector: VersionVectorEntry[] = [];
  let previousIndex = -1;
  for (let i = 0; i < vectorCount; i++) {
    const at = readIndex(reader, table.length);
    if (at <= previousIndex) {
      throw new BinaryFormatError(
        'Version vector entries must ascend by replica index and not repeat (§6).',
      );
    }
    previousIndex = at;
    versionVector.push({ replica: table[at]!, count: reader.varint() });
  }

  const elementCount = reader.count('An element count');
  const elements: ElementState[] = [];

  while (elements.length < elementCount) {
    const tag = reader.byte();
    const firstOfRecord = elements.length;
    const previous = firstOfRecord > 0 ? elements[firstOfRecord - 1]! : null;

    if (tag === TAG_ELEMENT) {
      readElement(reader, table, elements, previous);
    } else if (tag === TAG_RUN) {
      readRun(reader, table, elements, elementCount, previous);
    } else {
      throw new BinaryFormatError(`Unknown record tag ${tag} (§6).`);
    }

    // Canonical form, the one local rule that gives maximality: the first
    // element of a record must not be able to continue the element before it.
    if (previous !== null && canFollow(previous, elements[firstOfRecord]!)) {
      throw new BinaryFormatError(
        'Non-canonical: a record starts with an element that continues the previous one, so ' +
          'they should have been a single run (§6).',
      );
    }
  }

  if (reader.remaining !== 0) {
    throw new BinaryFormatError(
      `${reader.remaining} bytes remain after the declared ${elementCount} elements (§6).`,
    );
  }

  return { elements, versionVector };
}

function writeInsert(
  writer: Writer,
  insert: InsertOperation,
  index: (r: ReplicaId) => number,
  previousInsert: ElementId | null,
): void {
  const parent =
    insert.parent === null
      ? PARENT_ROOT
      : previousInsert !== null && elementIdsEqual(insert.parent, previousInsert)
        ? PARENT_PREVIOUS
        : PARENT_EXPLICIT;

  const flags =
    parent |
    (insert.side === 'R' ? FLAG_SIDE_RIGHT : 0) |
    (insert.side === 'R' && insert.rightOrigin !== null ? FLAG_RIGHT_ORIGIN_EXPLICIT : 0);

  writer.byte(OP_INSERT);
  writer.byte(flags);
  writeElementId(writer, insert.id, index);

  if (parent === PARENT_EXPLICIT) {
    writeElementId(writer, insert.parent!, index);
  }
  if ((flags & FLAG_RIGHT_ORIGIN_EXPLICIT) !== 0) {
    writeElementId(writer, insert.rightOrigin!, index);
  }

  writeValue(writer, insert.value);
}

/** Encodes an operation batch body (kind 0x02) in canonical form. */
export function encodeOperations(operations: readonly Operation[]): Uint8Array {
  const table = buildTable(
    operations.flatMap((op) =>
      op.kind === 'insert'
        ? [
            op.id.replica,
            ...(op.parent !== null ? [op.parent.replica] : []),
            ...(op.rightOrigin !== null ? [op.rightOrigin.replica] : []),
          ]
        : [op.id.replica, op.target.replica],
    ),
  );
  const index = indexer(table);

  const writer = new Writer();
  writer.raw(MAGIC);
  writer.byte(FORMAT_VERSION);
  writer.byte(KIND_OPERATIONS);

  writer.varint(table.length);
  for (const id of table) {
    writer.raw(replicaIdToBytes(id));
  }

  writer.varint(operations.length);

  let previousInsert: ElementId | null = null;
  for (const operation of operations) {
    if (operation.kind === 'insert') {
      writeInsert(writer, operation, index, previousInsert);
      previousInsert = operation.id;
    } else {
      writer.byte(OP_DELETE);
      writeElementId(writer, operation.id, index);
      writeElementId(writer, operation.target, index);
      previousInsert = null;
    }
  }

  return writer.finish();
}

/** Decodes an operation batch, or throws having applied none of it. */
export function decodeOperations(encoded: Uint8Array): Operation[] {
  const reader = new Reader(encoded);
  readHeader(reader, KIND_OPERATIONS);

  const table = readTable(reader);
  const count = reader.count('An operation count');
  const operations: Operation[] = [];

  let previousInsert: ElementId | null = null;
  for (let i = 0; i < count; i++) {
    const tag = reader.byte();

    if (tag === OP_INSERT) {
      const flags = reader.byte();
      validateFlags(flags, false);

      const id = readElementId(reader, table);
      const side: Side = (flags & FLAG_SIDE_RIGHT) !== 0 ? 'R' : 'L';

      let parent: ElementId | null;
      switch (flags & PARENT_MASK) {
        case PARENT_ROOT:
          parent = null;
          break;
        case PARENT_PREVIOUS:
          if (previousInsert === null) {
            throw new BinaryFormatError(
              'Parent flag 1 names the element inserted by the previous operation, and there ' +
                'is none (§6).',
            );
          }
          parent = previousInsert;
          break;
        default:
          parent = readElementId(reader, table);
          if (previousInsert !== null && elementIdsEqual(parent, previousInsert)) {
            throw new BinaryFormatError(
              "Non-canonical: the parent is the previous operation's element, which flag 1 " +
                'already says in no bytes (§6).',
            );
          }
          break;
      }

      let rightOrigin: ElementId | null = null;
      if ((flags & FLAG_RIGHT_ORIGIN_EXPLICIT) !== 0) {
        if (side === 'L') {
          throw new BinaryFormatError(
            'A left child has no right origin, so bit 4 must be clear (§6).',
          );
        }
        rightOrigin = readElementId(reader, table);
      }

      operations.push({
        kind: 'insert',
        id,
        value: readValue(reader),
        parent,
        side,
        rightOrigin,
      });
      previousInsert = id;
    } else if (tag === OP_DELETE) {
      const id = readElementId(reader, table);
      const target = readElementId(reader, table);
      operations.push({ kind: 'delete', id, target });
      previousInsert = null;
    } else {
      throw new BinaryFormatError(`Unknown operation tag ${tag} (§6).`);
    }
  }

  if (reader.remaining !== 0) {
    throw new BinaryFormatError(
      `${reader.remaining} bytes remain after the declared ${count} operations (§6).`,
    );
  }

  return operations;
}

/** Encodes exactly one operation, for a single message. */
export function encodeOperationBinary(operation: Operation): Uint8Array {
  return encodeOperations([operation]);
}

/** Decodes a batch that must hold exactly one operation. */
export function decodeOperationBinary(encoded: Uint8Array): Operation {
  const operations = decodeOperations(encoded);
  if (operations.length !== 1) {
    throw new BinaryFormatError(
      `Expected exactly one operation but the batch holds ${operations.length}.`,
    );
  }
  return operations[0]!;
}

export { compareElementId };
