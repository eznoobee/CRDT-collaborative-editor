import { BinaryFormatError, decodeSnapshot, encodeSnapshot } from './binary';
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
