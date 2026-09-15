import {
  BinaryFormatError,
  FORMAT_VERSION,
  decodeOperationBinary,
  decodeOperations,
  decodeSnapshot,
  encodeOperationBinary,
  encodeOperations,
  encodeSnapshot,
} from './binary';
import type { ElementState, VersionVectorEntry } from './elementState';
import type { ElementId } from './elementId';
import type { Operation } from './operation';
import { Replica } from './replica';
import { parseReplicaId, type ReplicaId } from './replicaId';

/**
 * The TypeScript codec against PROJECT_SPEC.md §6.
 *
 * Round-tripping proves only that the codec agrees with itself; the corpus is
 * what pins it to the C# one and to §9's normative form. These cover the shapes
 * and the refusals, which whole-document traces reach only by accident.
 */
function R(n: number): ReplicaId {
  return parseReplicaId(`00000000-0000-0000-0000-${n.toString(16).padStart(12, '0')}`);
}

function id(replica: number, seq: number): ElementId {
  return { replica: R(replica), seq: BigInt(seq) };
}

const noVector: VersionVectorEntry[] = [];

function chain(length: number, deleted: (i: number) => boolean = () => false): ElementState[] {
  return Array.from({ length }, (_, i) => ({
    id: id(1, i),
    value: String.fromCodePoint('a'.codePointAt(0)! + (i % 26)),
    parent: i === 0 ? null : id(1, i - 1),
    side: 'R' as const,
    rightOrigin: null,
    isDeleted: deleted(i),
  }));
}

/** The version byte sits after the four magic bytes. */
const VERSION_OFFSET = 4;
const KIND_OFFSET = 5;
/** 6 header + a one-entry table (1 + 16) + an empty vector (1) + element count (1). */
const FIRST_RECORD = 6 + 1 + 16 + 1 + 1;

function rejects(encoded: Uint8Array): BinaryFormatError {
  let thrown: unknown;
  try {
    decodeSnapshot(encoded);
  } catch (error) {
    thrown = error;
  }
  expect(thrown).toBeInstanceOf(BinaryFormatError);
  return thrown as BinaryFormatError;
}

describe('binary snapshots', () => {
  it('round-trips an empty document', () => {
    const encoded = encodeSnapshot([], noVector);
    const decoded = decodeSnapshot(encoded);
    expect(decoded.elements).toEqual([]);
    expect(decoded.versionVector).toEqual([]);
  });

  it('round-trips a typed document through a replica', () => {
    const replica = new Replica(R(1));
    for (const c of 'hello') {
      replica.insert(replica.values.length, c);
    }
    replica.delete(1);

    const encoded = encodeSnapshot(replica.export(), replica.versionVectorEntries);
    const decoded = decodeSnapshot(encoded);
    const restored = Replica.import(R(1), decoded.elements, decoded.versionVector);

    expect(restored.text).toBe('hllo');
    expect(restored.allIds).toEqual(replica.allIds);
  });

  it('round-trips the four shapes the encoding must carry', () => {
    // Trace 0050's four: a left child, a right child with an explicit right
    // origin, a right child at end of document, and a tombstone. The last two
    // both have no right-origin id, and conflating them is the encoding bug
    // that would reach the client as reordered text.
    const elements: ElementState[] = [
      { id: id(1, 0), value: 'a', parent: null, side: 'R', rightOrigin: null, isDeleted: false },
      { id: id(2, 0), value: 'b', parent: id(1, 0), side: 'L', rightOrigin: null, isDeleted: false },
      {
        id: id(2, 1),
        value: 'c',
        parent: id(1, 0),
        side: 'R',
        rightOrigin: id(1, 0),
        isDeleted: false,
      },
      { id: id(3, 7), value: 'd', parent: id(1, 0), side: 'R', rightOrigin: null, isDeleted: true },
    ];

    expect(decodeSnapshot(encodeSnapshot(elements, noVector)).elements).toEqual(elements);
  });

  it('does not confuse a left child with a right child at end of document', () => {
    const left: ElementState[] = [
      { id: id(1, 0), value: 'a', parent: null, side: 'R', rightOrigin: null, isDeleted: false },
      { id: id(2, 0), value: 'b', parent: id(1, 0), side: 'L', rightOrigin: null, isDeleted: false },
    ];
    const right: ElementState[] = [
      left[0]!,
      { ...left[1]!, side: 'R' as const },
    ];

    expect(encodeSnapshot(left, noVector)).not.toEqual(encodeSnapshot(right, noVector));
  });

  it('collapses a forward chain into one run', () => {
    const elements = chain(64, (i) => i % 5 === 0);
    const encoded = encodeSnapshot(elements, noVector);

    expect(decodeSnapshot(encoded).elements).toEqual(elements);
    expect(encoded.length).toBeLessThan(elements.length * 3);
  });

  it('charges one bit per tombstone inside a run', () => {
    // §8's stress case is 500k tombstones, and §5 says they cannot be collected
    // on causal stability alone, so what they cost is load-bearing.
    const live = encodeSnapshot(chain(256), noVector);
    const dead = encodeSnapshot(
      chain(256, () => true),
      noVector,
    );
    expect(dead.length).toBe(live.length);
  });

  it('does not fold a run onto an element that carries a right origin', () => {
    // Found by trying to break the cross-implementation check (§13.11). An
    // element with an explicit right origin can neither start a run nor sit
    // inside one, so what follows it begins a new record however well it would
    // otherwise continue. A canonical rule that ignores the earlier element's
    // right origin makes the decoder reject its own encoder's output.
    //
    // Ordinary typing cannot reach this shape: the right origin records what
    // followed at insert time, and tombstones keep it there. Garbage collection
    // (§5) and a directly built snapshot can.
    const elements: ElementState[] = [
      { id: id(1, 0), value: 'a', parent: null, side: 'R', rightOrigin: null, isDeleted: false },
      {
        id: id(2, 0),
        value: 'x',
        parent: id(1, 0),
        side: 'R',
        rightOrigin: id(1, 0),
        isDeleted: false,
      },
      {
        id: id(2, 1),
        value: 'c',
        parent: id(2, 0),
        side: 'R',
        rightOrigin: null,
        isDeleted: false,
      },
    ];

    expect(decodeSnapshot(encodeSnapshot(elements, noVector)).elements).toEqual(elements);
  });

  it('is stable across calls', () => {
    const elements = chain(9);
    expect(encodeSnapshot(elements, noVector)).toEqual(encodeSnapshot(elements, noVector));
  });
});

describe('binary snapshot refusals', () => {
  it('refuses an unknown version by name', () => {
    const encoded = encodeSnapshot(chain(2), noVector);
    encoded[VERSION_OFFSET] = 99;

    const error = rejects(encoded);
    expect(error.message).toContain('99');
    expect(error.message).toContain(String(FORMAT_VERSION));
  });

  it('refuses a future version rather than parsing it leniently', () => {
    // Version 2 would very likely be readable by a version 1 parser for a
    // while, which is exactly the trap.
    const encoded = encodeSnapshot(chain(2), noVector);
    encoded[VERSION_OFFSET] = FORMAT_VERSION + 1;
    rejects(encoded);
  });

  it('refuses the wrong body kind', () => {
    const encoded = encodeSnapshot(chain(2), noVector);
    encoded[KIND_OFFSET] = 0x02;
    rejects(encoded);
  });

  it('refuses a body that is not ours', () => {
    const encoded = encodeSnapshot(chain(2), noVector);
    encoded[0] = 0x58;
    expect(rejects(encoded).message).toContain('magic');
  });

  it('refuses truncated input at every length', () => {
    const encoded = encodeSnapshot(chain(2), noVector);
    for (let length = 0; length < encoded.length; length++) {
      expect(() => decodeSnapshot(encoded.subarray(0, length))).toThrow(BinaryFormatError);
    }
  });

  it('refuses trailing bytes', () => {
    const encoded = encodeSnapshot(chain(2), noVector);
    expect(rejects(Uint8Array.from([...encoded, 0])).message).toContain('remain');
  });

  it('refuses reserved flag bits', () => {
    const encoded = encodeSnapshot(chain(2), noVector);
    encoded[FIRST_RECORD + 2]! |= 0b1110_0000;
    expect(rejects(encoded).message).toContain('Reserved');
  });

  it('refuses a deleted flag on a run', () => {
    const encoded = encodeSnapshot(chain(2), noVector);
    encoded[FIRST_RECORD + 2]! |= 0b0000_0010;
    rejects(encoded);
  });

  it('refuses an explicit right origin on a run', () => {
    const encoded = encodeSnapshot(chain(2), noVector);
    encoded[FIRST_RECORD + 2]! |= 0b0001_0000;
    rejects(encoded);
  });

  it('refuses parent kind three', () => {
    const encoded = encodeSnapshot(chain(2), noVector);
    encoded[FIRST_RECORD + 2]! |= 0b0000_1100;
    rejects(encoded);
  });

  it('refuses a replica index past the table', () => {
    const encoded = encodeSnapshot(chain(2), noVector);
    encoded[FIRST_RECORD + 3] = 1;
    expect(rejects(encoded).message).toContain('past the end');
  });

  it('refuses a run shorter than two', () => {
    const encoded = encodeSnapshot(chain(2), noVector);
    encoded[FIRST_RECORD + 1] = 1;
    rejects(encoded);
  });

  it('refuses a non-minimal varint', () => {
    // 0x80 0x00 is zero in two bytes: a second spelling of the same number, and
    // therefore of the same document.
    const encoded = encodeSnapshot(chain(2), noVector);
    const at = FIRST_RECORD + 1;
    const padded = Uint8Array.from([
      ...encoded.subarray(0, at),
      encoded[at]! | 0x80,
      0x00,
      ...encoded.subarray(at + 1),
    ]);
    expect(rejects(padded).message).toContain('minimally');
  });

  it('refuses a descending replica table', () => {
    const elements: ElementState[] = [
      { id: id(1, 0), value: 'a', parent: null, side: 'R', rightOrigin: null, isDeleted: false },
      { id: id(2, 0), value: 'b', parent: id(1, 0), side: 'L', rightOrigin: null, isDeleted: false },
    ];
    const encoded = encodeSnapshot(elements, noVector);

    const first = encoded.slice(7, 7 + 16);
    const second = encoded.slice(7 + 16, 7 + 32);
    encoded.set(second, 7);
    encoded.set(first, 7 + 16);

    expect(rejects(encoded).message).toContain('ascend');
  });

  it('refuses two records that should have been one run', () => {
    // Maximality, reduced by §6 to one local rule. Built by hand because the
    // encoder will not produce it.
    const bytes = [
      0x43, 0x52, 0x44, 0x54, FORMAT_VERSION, 0x01,
      0x01, ...new Uint8Array(15), 0x01,
      0x00,
      0x02,
      0x00, 0b0000_0001, 0x00, 0x00, 0x01, 0x61,
      0x00, 0b0000_0101, 0x00, 0x01, 0x01, 0x62,
    ];
    expect(rejects(Uint8Array.from(bytes)).message).toContain('single run');
  });

  it('refuses parent flag one on the first record', () => {
    const bytes = [
      0x43, 0x52, 0x44, 0x54, FORMAT_VERSION, 0x01,
      0x01, ...new Uint8Array(15), 0x01,
      0x00,
      0x01,
      0x00, 0b0000_0101, 0x00, 0x00, 0x01, 0x61,
    ];
    expect(rejects(Uint8Array.from(bytes)).message).toContain('first record');
  });

  it('refuses a lone surrogate', () => {
    // §7: validate UTF-8, reject lone surrogates, normalize nothing.
    const bytes = [
      0x43, 0x52, 0x44, 0x54, FORMAT_VERSION, 0x01,
      0x01, ...new Uint8Array(15), 0x01,
      0x00,
      0x01,
      0x00, 0b0000_0001, 0x00, 0x00, 0x03, 0xed, 0xa0, 0x80,
    ];
    rejects(Uint8Array.from(bytes));
  });

  it('refuses a value that is not one code point', () => {
    const bytes = [
      0x43, 0x52, 0x44, 0x54, FORMAT_VERSION, 0x01,
      0x01, ...new Uint8Array(15), 0x01,
      0x00,
      0x01,
      0x00, 0b0000_0001, 0x00, 0x00, 0x02, 0x61, 0x62,
    ];
    rejects(Uint8Array.from(bytes));
  });

  it('refuses an unknown record tag', () => {
    const encoded = encodeSnapshot(chain(2), noVector);
    encoded[FIRST_RECORD] = 0x7f;
    expect(rejects(encoded).message).toContain('tag');
  });
});

describe('binary operation batches', () => {
  it('round-trips typing and a delete', () => {
    const replica = new Replica(R(1));
    const operations: Operation[] = [];
    for (const c of 'hello') {
      operations.push(replica.insert(replica.values.length, c));
    }
    operations.push(replica.delete(0));

    expect(decodeOperations(encodeOperations(operations))).toEqual(operations);
  });

  it('does not confuse a right child at end of document with a left child', () => {
    const atEnd: Operation = {
      kind: 'insert',
      id: id(1, 1),
      value: 'x',
      parent: id(1, 0),
      side: 'R',
      rightOrigin: null,
    };
    const leftChild: Operation = { ...atEnd, side: 'L' };

    expect(encodeOperationBinary(atEnd)).not.toEqual(encodeOperationBinary(leftChild));
    expect(decodeOperationBinary(encodeOperationBinary(atEnd))).toEqual(atEnd);
    expect(decodeOperationBinary(encodeOperationBinary(leftChild))).toEqual(leftChild);
  });

  it('names a chained parent in no bytes', () => {
    const replica = new Replica(R(1));
    const operations: Operation[] = [];
    for (let i = 0; i < 32; i++) {
      operations.push(replica.insert(i, 'a'));
    }

    const batch = encodeOperations(operations);
    const separately = operations.reduce((n, op) => n + encodeOperationBinary(op).length, 0);

    expect(batch.length).toBeLessThan(separately / 2);
    expect(decodeOperations(batch)).toEqual(operations);
  });

  it('refuses a snapshot body as an operation batch', () => {
    expect(() => decodeOperations(encodeSnapshot(chain(2), noVector))).toThrow(BinaryFormatError);
  });

  it('refuses parent flag one after a delete', () => {
    const bytes = [
      0x43, 0x52, 0x44, 0x54, FORMAT_VERSION, 0x02,
      0x01, ...new Uint8Array(15), 0x01,
      0x02,
      0x01, 0x00, 0x00, 0x00, 0x01,
      0x00, 0b0000_0101, 0x00, 0x02, 0x01, 0x61,
    ];

    let thrown: unknown;
    try {
      decodeOperations(Uint8Array.from(bytes));
    } catch (error) {
      thrown = error;
    }
    expect(thrown).toBeInstanceOf(BinaryFormatError);
    expect((thrown as Error).message).toContain('previous operation');
  });
});
