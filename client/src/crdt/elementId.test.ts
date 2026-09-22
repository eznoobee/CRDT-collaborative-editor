import { describe, expect, it } from 'vitest';

import { compareElementId, elementIdsEqual, type ElementId } from './elementId';
import { compareReplicaId, parseReplicaId } from './replicaId';

/**
 * Direct tests for the sibling tie-break: lexicographic on (replica, seq).
 *
 * @remarks
 * <p>
 * **This file exists because 7b.9 found it missing.** `Crdt.Core` has
 * `ElementIdTests` and TypeScript had no equivalent — and the comparator is the
 * one piece AGENTS.md singles out as load-bearing *because* TypeScript has no
 * `Guid` to agree with. The audit measured the consequence: inverting
 * `compareElementId` leaves the entire default client suite green, 213 of 213,
 * while reordering every user's text under concurrent editing. The only thing
 * that caught it was the conformance corpus, which `npm test` does not run.
 * </p><p>
 * **The expectations come from §5's rule, not from the implementation.** §5
 * says compare the replica's sixteen bytes in RFC 4122 big-endian order, then
 * `seq` — so the cases below are written from that sentence, and a comparator
 * that agreed with itself while disagreeing with the spec fails them. That
 * matters more here than in C#: the conformance corpus compares the two
 * implementations against each other, and two implementations written by one
 * author from one paper can be wrong together (§9).
 * </p>
 */
describe('the sibling tie-break', () => {
  const id = (replica: string, seq: bigint): ElementId => ({
    replica: parseReplicaId(replica),
    seq,
  });

  const one = '00000000-0000-0000-0000-000000000001';
  const two = '00000000-0000-0000-0000-000000000002';

  it('orders by replica before seq', () => {
    // A higher seq must not outrank a lower replica id: replica is the primary
    // component (§5). This is the case an implementation that compared seq
    // first would pass every convergence test while failing.
    expect(compareElementId(id(one, 999n), id(two, 0n))).toBeLessThan(0);
    expect(compareElementId(id(two, 0n), id(one, 999n))).toBeGreaterThan(0);
  });

  it('orders by seq within one replica', () => {
    expect(compareElementId(id(one, 0n), id(one, 1n))).toBeLessThan(0);
    expect(compareElementId(id(one, 1n), id(one, 0n))).toBeGreaterThan(0);
    expect(compareElementId(id(one, 5n), id(one, 5n))).toBe(0);
  });

  it('orders seq beyond what a double can represent', () => {
    // §6 keeps 64-bit values out of JSON numbers for this reason, and the
    // comparator is where it would show up as reordered text rather than as a
    // parse error. 2^53 and 2^53 + 1 are equal as doubles.
    const below = (1n << 53n) - 1n;
    const at = 1n << 53n;
    const above = (1n << 53n) + 1n;

    expect(compareElementId(id(one, below), id(one, at))).toBeLessThan(0);
    expect(compareElementId(id(one, at), id(one, above))).toBeLessThan(0);
    expect(compareElementId(id(one, at), id(one, at))).toBe(0);
  });

  it('compares the replica big-endian: the first byte dominates the last', () => {
    // §5 and §6: the sixteen bytes in RFC 4122 order, most significant first.
    // The pair that pins it is one where the two directions disagree — a
    // replica larger in its first byte and smaller in its last must still sort
    // after. A little-endian comparison, which is the historical .NET `Guid`
    // field order AGENTS.md warns about, answers the opposite.
    const firstByteBigger = '01000000-0000-0000-0000-000000000000';
    const lastByteBigger = '00000000-0000-0000-0000-0000000000ff';

    expect(
      compareReplicaId(parseReplicaId(lastByteBigger), parseReplicaId(firstByteBigger)),
    ).toBeLessThan(0);
  });

  it('carries the comparison across the hi and lo halves', () => {
    // The replica is held as two 64-bit halves, so the boundary between them is
    // a seam in the comparator rather than in the spec. A pair differing only
    // in the low half must still order, and a difference in the high half must
    // outrank any difference in the low one.
    const lowHalfSmall = '00000000-0000-0000-0000-000000000001';
    const lowHalfLarge = '00000000-0000-0000-ffff-ffffffffffff';
    const highHalfLarge = '00000000-0000-0001-0000-000000000000';

    expect(
      compareReplicaId(parseReplicaId(lowHalfSmall), parseReplicaId(lowHalfLarge)),
    ).toBeLessThan(0);

    expect(
      compareReplicaId(parseReplicaId(lowHalfLarge), parseReplicaId(highHalfLarge)),
    ).toBeLessThan(0);
  });

  it('treats equality by value', () => {
    expect(elementIdsEqual(id(one, 1n), id(one, 1n))).toBe(true);
    expect(elementIdsEqual(id(one, 1n), id(one, 2n))).toBe(false);
    expect(elementIdsEqual(id(one, 1n), id(two, 1n))).toBe(false);
  });
});
