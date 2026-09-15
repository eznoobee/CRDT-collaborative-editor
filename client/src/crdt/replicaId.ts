/**
 * A 128-bit replica identifier, compared as unsigned big-endian bytes.
 *
 * PROJECT_SPEC.md §5 makes this ordering normative. It is the first component of
 * the ElementId comparison that breaks sibling ties, so any disagreement with
 * the C# implementation reorders user text rather than producing some harmless
 * internal difference.
 *
 * Stored as two big-endian halves so that comparing `hi` then `lo` as unsigned
 * integers is, by construction, lexicographic comparison of the sixteen bytes —
 * the same representation the C# side uses, for the same reason.
 */
export interface ReplicaId {
  readonly hi: bigint;
  readonly lo: bigint;
}

const HEX = /^[0-9a-f]{32}$/;

/** Parses the canonical lowercase hyphenated form. */
export function parseReplicaId(canonical: string): ReplicaId {
  const hex = canonical.replaceAll('-', '').toLowerCase();
  if (!HEX.test(hex)) {
    throw new Error(`'${canonical}' is not a canonical UUID.`);
  }

  return {
    hi: BigInt(`0x${hex.slice(0, 16)}`),
    lo: BigInt(`0x${hex.slice(16, 32)}`),
  };
}

/** Renders the canonical lowercase hyphenated form. */
export function formatReplicaId(id: ReplicaId): string {
  const hex = id.hi.toString(16).padStart(16, '0') + id.lo.toString(16).padStart(16, '0');
  return [
    hex.slice(0, 8),
    hex.slice(8, 12),
    hex.slice(12, 16),
    hex.slice(16, 20),
    hex.slice(20, 32),
  ].join('-');
}

/** Unsigned big-endian byte order. Never compare the string forms. */
export function compareReplicaId(a: ReplicaId, b: ReplicaId): number {
  if (a.hi !== b.hi) {
    return a.hi < b.hi ? -1 : 1;
  }

  if (a.lo !== b.lo) {
    return a.lo < b.lo ? -1 : 1;
  }

  return 0;
}
