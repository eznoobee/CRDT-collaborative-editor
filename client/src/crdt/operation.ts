import type { ElementId } from './elementId';

/** Which child list of its parent a node belongs to. */
export type Side = 'L' | 'R';

/**
 * Creates one element. `rightOrigin` is carried only on right children; a null
 * value there means end-of-document, the paper's `end`. A null `parent` means
 * the root sentinel. The two nulls are unrelated, which is why `side` is needed
 * to tell them apart.
 */
export interface InsertOperation {
  readonly kind: 'insert';
  readonly id: ElementId;
  readonly value: string;
  readonly parent: ElementId | null;
  readonly side: Side;
  readonly rightOrigin: ElementId | null;
}

/** Tombstones the element named by `target`. The node is never removed. */
export interface DeleteOperation {
  readonly kind: 'delete';
  readonly id: ElementId;
  readonly target: ElementId;
}

export type Operation = InsertOperation | DeleteOperation;
