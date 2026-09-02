import {
  BinaryFormatError,
  RunLengthExceededError,
  decodeOperations,
  decodeSnapshot,
  encodeOperations,
  encodeSnapshot,
} from './binary';
import { parseReplicaId, replicaIdToBytes, type ReplicaId } from './replicaId';

/**
 * Hand-written bodies that §6 says are VALID, built from the specification
 * rather than by the encoder (PROJECT_SPEC.md §12).
 *
 * Round-trip testing defines codec correctness as encoder-decoder agreement,
 * which is circular: an encoder that never emits a legal shape and a decoder
 * that rejects it agree perfectly and are both wrong. The property that matters
 * is that a decoder accepts every document the format admits, and only input
 * written by hand from the specification can test it.
 *
 * The C# suite carries the same fixtures. That is not duplication for its own
 * sake — the whole point is that each decoder is checked against the format
 * rather than against the other one.
 */
const MAGIC = [0x43, 0x52, 0x44, 0x54];
const VERSION = 1;
const KIND_SNAPSHOT = 0x01;
const TAG_ELEMENT = 0x00;
const TAG_RUN = 0x01;
const SIDE_RIGHT = 0b0000_0001;
const PARENT_ROOT = 0b0000_0000;
const PARENT_PREVIOUS = 0b0000_0100;

function R(n: number): ReplicaId {
  return parseReplicaId(`00000000-0000-0000-0000-${n.toString(16).padStart(12, '0')}`);
}

class Body {
  readonly bytes: number[] = [];

  constructor(table: ReplicaId[]) {
    this.bytes.push(...MAGIC, VERSION, KIND_SNAPSHOT);
    this.varint(table.length);
    for (const id of table) {
      this.bytes.push(...replicaIdToBytes(id));
    }
  }

  byte(value: number): this {
    this.bytes.push(value & 0xff);
    return this;
  }

  varint(value: bigint | number): this {
    let v = typeof value === 'bigint' ? value : BigInt(value);
    while (v >= 0x80n) {
      this.bytes.push(Number((v & 0x7fn) | 0x80n));
      v >>= 7n;
    }
    this.bytes.push(Number(v));
    return this;
  }

  utf8(value: string): this {
    const encoded = new TextEncoder().encode(value);
    this.varint(encoded.length);
    this.bytes.push(...encoded);
    return this;
  }

  finish(): Uint8Array {
    return Uint8Array.from(this.bytes);
  }
}

/** Decodes, then re-encodes and requires the same bytes: §6 permits one spelling. */
function acceptsAndRoundTrips(body: Body) {
  const original = body.finish();
  const decoded = decodeSnapshot(original);
  expect(encodeSnapshot(decoded.elements, decoded.versionVector)).toEqual(original);
  return decoded;
}

describe('canonical-form fixtures the encoder never produces', () => {
  it('accepts a run beginning at a left child', () => {
    // §6: "a run may begin at a left child, and every element after it is a
    // right child regardless." No document the corpus builds reaches it.
    const body = new Body([R(1), R(2)]);
    body.varint(0).varint(3);

    body.byte(TAG_ELEMENT).byte(SIDE_RIGHT | PARENT_ROOT).varint(0).varint(0).utf8('z');

    body
      .byte(TAG_RUN)
      .varint(2)
      .byte(PARENT_PREVIOUS) // side bit clear: a left child
      .varint(1)
      .varint(0)
      .byte(0b0000_0000)
      .utf8('ab');

    const { elements } = acceptsAndRoundTrips(body);

    expect(elements[1]!.side).toBe('L');
    expect(elements[2]!.side).toBe('R');
    expect(elements[2]!.parent).toEqual(elements[1]!.id);
  });

  it('accepts a sequence number at the top of the range', () => {
    // 2^64-1 is a ten-byte varint, the longest this format carries, and the
    // only thing that reaches the final shift in the varint reader.
    const max = 2n ** 64n - 1n;
    const body = new Body([R(1)]);
    body.varint(1).varint(0).varint(max).varint(1);
    body.byte(TAG_ELEMENT).byte(SIDE_RIGHT | PARENT_ROOT).varint(0).varint(max - 1n).utf8('x');

    const { elements } = acceptsAndRoundTrips(body);
    expect(elements[0]!.id.seq).toBe(max - 1n);
  });

  it('accepts a replica index that needs two varint bytes', () => {
    // Every one of the 130 must be referenced: §6's canonical form says the
    // table holds exactly the replicas the body names and no more.
    const count = 130;
    const table = Array.from({ length: count }, (_, i) => R(i + 1));
    const body = new Body(table);
    body.varint(0).varint(count);

    for (let i = 0; i < count; i++) {
      body
        .byte(TAG_ELEMENT)
        .byte(SIDE_RIGHT | (i === 0 ? PARENT_ROOT : PARENT_PREVIOUS))
        .varint(i)
        .varint(0)
        .utf8('q');
    }

    const { elements } = acceptsAndRoundTrips(body);
    expect(elements).toHaveLength(count);
    expect(elements[129]!.id.replica).toEqual(table[129]);
  });

  it('accepts a four-byte code point', () => {
    // §7 works in code points. An astral character is four UTF-8 bytes and two
    // UTF-16 units, and the corpus is ASCII.
    const body = new Body([R(1)]);
    body.varint(0).varint(1);
    body.byte(TAG_ELEMENT).byte(SIDE_RIGHT | PARENT_ROOT).varint(0).varint(0).utf8('\u{1F600}');

    const { elements } = acceptsAndRoundTrips(body);
    expect(elements[0]!.value).toBe('\u{1F600}');
    expect([...elements[0]!.value]).toHaveLength(1);
  });

  it('accepts a run whose bitmap ends mid-byte', () => {
    const body = new Body([R(1)]);
    body.varint(0).varint(5);
    body
      .byte(TAG_RUN)
      .varint(5)
      .byte(SIDE_RIGHT | PARENT_ROOT)
      .varint(0)
      .varint(0)
      .byte(0b0001_0101)
      .utf8('abcde');

    const { elements } = acceptsAndRoundTrips(body);
    expect(elements.map((e) => e.isDeleted)).toEqual([true, false, true, false, true]);
  });

  it('refuses a non-zero bit past the last element of a bitmap', () => {
    // The mirror of the fixture above, and why §6 now pins the spare bits.
    // Before that rule the two bodies decoded to the same document — one
    // document with several spellings, which defeats byte-identity. Found by
    // writing these fixtures (§12).
    const body = new Body([R(1)]);
    body.varint(0).varint(5);
    body
      .byte(TAG_RUN)
      .varint(5)
      .byte(SIDE_RIGHT | PARENT_ROOT)
      .varint(0)
      .varint(0)
      .byte(0b1110_0000)
      .utf8('abcde');

    let thrown: unknown;
    try {
      decodeSnapshot(body.finish());
    } catch (error) {
      thrown = error;
    }
    expect(thrown).toBeInstanceOf(BinaryFormatError);
    expect((thrown as Error).message).toContain('past its last element');
  });

  it('accepts an empty document that still carries a version vector', () => {
    // Everything collected: no elements, but the replica has seen operations
    // and must not forget. Reachable only after GC takes the last tombstone.
    const body = new Body([R(1), R(2)]);
    body.varint(2).varint(0).varint(41).varint(1).varint(7).varint(0);

    const { elements, versionVector } = acceptsAndRoundTrips(body);

    expect(elements).toEqual([]);
    expect(versionVector.map((v) => v.count)).toEqual([41n, 7n]);
  });
});

/**
 * §6's run insert, from the same hand-written bodies the C# suite uses.
 *
 * The bytes below are written from the specification, not produced by either
 * encoder. That is the point: two implementations agreeing on what they both
 * generate is evidence they read the specification the same way, not that
 * either read it correctly.
 */
const KIND_OPERATIONS = 0x02;
const OP_RUN = 0x02;
const RIGHT_ORIGIN_EXPLICIT = 0b0001_0000;

function runFixture(
  text: string,
  options: { rightOrigin?: boolean; declaredCount?: number } = {},
): Uint8Array {
  const bytes: number[] = [...MAGIC, VERSION, KIND_OPERATIONS];
  const table = options.rightOrigin ? [R(1), R(2)] : [R(1)];

  varint(bytes, table.length);
  for (const id of table) {
    bytes.push(...replicaIdToBytes(id));
  }

  varint(bytes, 1);

  bytes.push(OP_RUN);
  bytes.push(PARENT_ROOT | SIDE_RIGHT | (options.rightOrigin ? RIGHT_ORIGIN_EXPLICIT : 0));
  varint(bytes, 0);
  varint(bytes, 0);

  if (options.rightOrigin) {
    varint(bytes, 1);
    varint(bytes, 7);
  }

  // The first element's value is part of the insert body; the count and the
  // remaining values follow it (§6).
  const points = [...text];
  value(bytes, points[0]!);
  varint(bytes, options.declaredCount ?? points.length);
  for (const point of points.slice(1)) {
    value(bytes, point);
  }

  return new Uint8Array(bytes);
}

function varint(bytes: number[], value: number): void {
  let v = BigInt(value);
  while (v >= 0x80n) {
    bytes.push(Number((v & 0x7fn) | 0x80n));
    v >>= 7n;
  }
  bytes.push(Number(v));
}

function value(bytes: number[], point: string): void {
  const utf8 = new TextEncoder().encode(point);
  varint(bytes, utf8.length);
  bytes.push(...utf8);
}

describe('run insert fixtures', () => {
  it('expands into a chain rather than into siblings', () => {
    const operations = decodeOperations(runFixture('hello'));

    expect(operations).toHaveLength(5);
    expect(operations.map((op) => (op.kind === 'insert' ? op.value : ''))).toEqual([
      'h',
      'e',
      'l',
      'l',
      'o',
    ]);

    // §5 and §6: same parent and side for every element would make them
    // siblings, and invariant 8 forbids exactly that.
    for (let i = 1; i < operations.length; i++) {
      const element = operations[i]!;
      const previous = operations[i - 1]!;
      if (element.kind !== 'insert' || previous.kind !== 'insert') {
        throw new Error('expected inserts');
      }
      expect(element.parent).toEqual(previous.id);
      expect(element.side).toBe('R');
      expect(element.rightOrigin).toBeNull();
      expect(element.id.seq).toBe(BigInt(i));
    }
  });

  it("lets the run's first element carry a right origin", () => {
    const operations = decodeOperations(runFixture('ab', { rightOrigin: true }));
    const first = operations[0]!;
    const second = operations[1]!;

    if (first.kind !== 'insert' || second.kind !== 'insert') {
      throw new Error('expected inserts');
    }

    expect(first.rightOrigin).not.toBeNull();
    expect(second.rightOrigin).toBeNull();
  });

  it('re-encodes a hand-written run to the same bytes', () => {
    for (const fixture of [runFixture('hello'), runFixture('ab', { rightOrigin: true })]) {
      expect(encodeOperations(decodeOperations(fixture))).toEqual(fixture);
    }
  });

  it('encodes left-to-right typing as one run', () => {
    const operations = decodeOperations(runFixture('hello world'));
    expect(encodeOperations(operations)).toEqual(runFixture('hello world'));
  });

  it('refuses a run of one', () => {
    expect(() => decodeOperations(runFixture('a', { declaredCount: 1 }))).toThrow(BinaryFormatError);
  });

  it('refuses a run past the cap before expanding it', () => {
    expect(() => decodeOperations(runFixture('ab', { declaredCount: 1000 }))).toThrow(
      RunLengthExceededError,
    );
  });

  it('honours a configured cap below the ceiling', () => {
    expect(() => decodeOperations(runFixture('hello'), 4)).toThrow(RunLengthExceededError);
    expect(decodeOperations(runFixture('hello'), 5)).toHaveLength(5);
  });
});
