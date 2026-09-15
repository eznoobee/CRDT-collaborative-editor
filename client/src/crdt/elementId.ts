import { compareReplicaId, formatReplicaId, type ReplicaId } from './replicaId';

/**
 * Identifies one operation, and for an insert the element it creates.
 *
 * Ordered lexicographically as the pair (replica, seq), per PROJECT_SPEC.md §5.
 * It is an identity comparison, not a causal clock: nothing about it needs to
 * respect happens-before, which is why a dense per-replica counter suffices
 * where RGA would have needed a Lamport timestamp.
 *
 * `seq` is a bigint because §6 requires 64-bit values to survive the wire, and a
 * JavaScript number silently loses precision above 2^53.
 */
export interface ElementId {
  readonly replica: ReplicaId;
  readonly seq: bigint;
}

export function compareElementId(a: ElementId, b: ElementId): number {
  const replica = compareReplicaId(a.replica, b.replica);
  if (replica !== 0) {
    return replica;
  }

  return a.seq === b.seq ? 0 : a.seq < b.seq ? -1 : 1;
}

export function elementIdsEqual(a: ElementId, b: ElementId): boolean {
  return compareElementId(a, b) === 0;
}

/** Stable map key. Maps cannot key on structural equality. */
export function elementKey(id: ElementId): string {
  return `${formatReplicaId(id.replica)}:${id.seq.toString()}`;
}
