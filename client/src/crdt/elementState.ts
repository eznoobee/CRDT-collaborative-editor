import type { ElementId } from './elementId';
import type { ReplicaId } from './replicaId';
import type { Side } from './operation';

/**
 * One element as a snapshot carries it (PROJECT_SPEC.md §6).
 *
 * The same six fields as the C# `ElementState`, for the same reason: a snapshot
 * must be able to accept operations that arrive after it, and those attach by
 * id — to tombstones included, since a `rightOrigin` can name one (§5).
 *
 * `value` is exactly one code point, which in a UTF-16 string may be one or two
 * units. Never index it with `[0]`.
 */
export interface ElementState {
  readonly id: ElementId;
  readonly value: string;
  readonly parent: ElementId | null;
  readonly side: Side;
  readonly rightOrigin: ElementId | null;
  readonly isDeleted: boolean;
}

/** One replica's entry in a version vector, as export and import carry it. */
export interface VersionVectorEntry {
  readonly replica: ReplicaId;
  readonly count: bigint;
}
