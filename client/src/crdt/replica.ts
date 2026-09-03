import { compareElementId, elementKey, type ElementId } from './elementId';
import type { ElementState, VersionVectorEntry } from './elementState';
import type { InsertOperation, DeleteOperation, Operation, Side } from './operation';
import { compareReplicaId, formatReplicaId, type ReplicaId } from './replicaId';
import { PendingSetOverflowError } from './pendingSetOverflow';

interface Node {
  id: ElementId;
  value: string;
  isDeleted: boolean;
  isRoot: boolean;
  parent: Node | null;
  side: Side;
  /** Meaningful only when `side` is 'R'; null there means end-of-document. */
  rightOrigin: Node | null;
  leftChildren: Node[];
  rightChildren: Node[];
}

const ROOT_ID: ElementId = {
  replica: { hi: 0n, lo: 0n },
  seq: -1n,
};

/**
 * One FugueMax replica of a text document.
 *
 * Implements PROJECT_SPEC.md §5 — TPDS Algorithm 1 as amended by Definition 6 —
 * and must agree with the C# implementation byte for byte (§9). Where the two
 * could plausibly diverge, this file says so.
 *
 * The unit of text is a Unicode code point, never a UTF-16 code unit: JavaScript
 * strings are UTF-16, so every index here is a code-point index and conversion
 * to the DOM's units is the caller's job.
 */
export class Replica {
  readonly id: ReplicaId;

  private readonly root: Node;
  private readonly byId = new Map<string, Node>();
  private readonly versionVectorByKey = new Map<string, { replica: ReplicaId; count: bigint }>();
  private readonly log: Operation[] = [];
  private pending: Operation[] = [];

  /**
   * Operations discarded because this replica had already applied or buffered
   * them.
   *
   * Diagnostic, not a health check. §5 guarantees this is non-zero in normal
   * operation; what is worth alerting on is its rate.
   */
  duplicatesDropped = 0;

  /**
   * How many operations may wait in the pending set (§5).
   *
   * Unbounded by default, deliberately: §5 bounds the pending set *per
   * connection*, and a replica is not a connection. A replica replaying a
   * stored trace legitimately buffers as much as the trace demands. Whoever
   * attaches a replica to a network connection sets this, because that is the
   * layer where an unbounded buffer fed by a remote peer is a denial-of-service
   * vector and one fed by a local file is not.
   */
  maxPending = Number.MAX_SAFE_INTEGER;
  private nextSeq = 0n;

  constructor(id: ReplicaId) {
    this.id = id;
    this.root = {
      id: ROOT_ID,
      value: '',
      isDeleted: true,
      isRoot: true,
      parent: null,
      side: 'R',
      rightOrigin: null,
      leftChildren: [],
      rightChildren: [],
    };
  }

  /** The visible text: the traversal with tombstones skipped. */
  get text(): string {
    return this.inOrder()
      .filter((n) => !n.isDeleted)
      .map((n) => n.value)
      .join('');
  }

  /** The visible text as code points. */
  get values(): string[] {
    return this.inOrder()
      .filter((n) => !n.isDeleted)
      .map((n) => n.value);
  }

  /** Element ids of the visible text, positionally aligned with `values`. */
  get visibleIds(): ElementId[] {
    return this.inOrder()
      .filter((n) => !n.isDeleted)
      .map((n) => n.id);
  }

  /** Element ids in traversal order including tombstones (§5). */
  get allIds(): ElementId[] {
    return this.inOrder().map((n) => n.id);
  }

  /** How many operations from each replica have been applied here. */
  get versionVector(): Map<string, bigint> {
    const result = new Map<string, bigint>();
    for (const entry of this.versionVectorByKey.values()) {
      result.set(formatReplicaId(entry.replica), entry.count);
    }
    return result;
  }

  /** Operations buffered because a dependency has not arrived (§5). */
  get pendingCount(): number {
    return this.pending.length;
  }

  /** Inserts a code point at a visible index, returning the operation. */
  insert(index: number, value: string): InsertOperation {
    const all = this.inOrder();
    const visible = all.filter((n) => !n.isDeleted);
    if (index < 0 || index > visible.length) {
      throw new RangeError(`index ${index} out of range 0..${visible.length}`);
    }

    const leftOrigin = index === 0 ? this.root : visible[index - 1]!;

    // Algorithm 1 line 24: next node in the traversal INCLUDING tombstones,
    // computed once, before the placement branch.
    const rightOrigin = nextIncludingTombstones(all, leftOrigin);

    const id: ElementId = { replica: this.id, seq: this.nextSeq };
    let op: InsertOperation;

    if (leftOrigin.rightChildren.length === 0) {
      op = {
        kind: 'insert',
        id,
        value,
        parent: leftOrigin.isRoot ? null : leftOrigin.id,
        side: 'R',
        rightOrigin: rightOrigin === null ? null : rightOrigin.id,
      };
    } else {
      if (rightOrigin === null) {
        throw new Error('A node with right children must have a traversal successor.');
      }

      op = { kind: 'insert', id, value, parent: rightOrigin.id, side: 'L', rightOrigin: null };
    }

    this.apply(op);
    return op;
  }

  /** Tombstones the element at a visible index, returning the operation. */
  delete(index: number): DeleteOperation {
    const visible = this.inOrder().filter((n) => !n.isDeleted);
    if (index < 0 || index >= visible.length) {
      throw new RangeError(`index ${index} out of range 0..${visible.length - 1}`);
    }

    const op: DeleteOperation = {
      kind: 'delete',
      id: { replica: this.id, seq: this.nextSeq },
      target: visible[index]!.id,
    };

    this.apply(op);
    return op;
  }

  /** Applies an operation, buffering it if a dependency is missing. */
  /**
   * Every element in traversal order, tombstones included — the basis of a
   * snapshot (PROJECT_SPEC.md §6).
   *
   * Tombstones are in it because operations arriving after the snapshot still
   * attach to them: a `rightOrigin` can name a tombstone (§5), so dropping them
   * would make a snapshot unable to accept operations a full replay accepts.
   */
  export(): ElementState[] {
    return this.inOrder().map((node) => ({
      id: node.id,
      value: node.value,
      parent: node.parent !== null && !node.parent.isRoot ? node.parent.id : null,
      side: node.side,
      rightOrigin: node.rightOrigin !== null ? node.rightOrigin.id : null,
      isDeleted: node.isDeleted,
    }));
  }

  /** This replica's version vector in the shape `import` takes back. */
  get versionVectorEntries(): VersionVectorEntry[] {
    return [...this.versionVectorByKey.values()].map((entry) => ({
      replica: entry.replica,
      count: entry.count,
    }));
  }

  /**
   * Rebuilds a replica from exported elements and a version vector.
   *
   * Mirrors the C# `Replica.Import`, deliberately including the parts that look
   * like overkill. Elements are placed with the live sibling ordering rather
   * than trusting the order they arrive in, so a snapshot written wrongly builds
   * a different tree here instead of quietly restoring a corrupt one. And
   * placement iterates to a fixpoint because traversal order does not guarantee
   * parents precede children — a left child is traversed before its parent.
   *
   * Each pass rebuilds the unplaced list rather than splicing out of it: the
   * splice version is quadratic in exactly the common case where everything
   * places on the first pass, which is what §13.9 records finding at 100k.
   */
  static import(
    id: ReplicaId,
    elements: readonly ElementState[],
    versionVector: readonly VersionVectorEntry[],
  ): Replica {
    const replica = new Replica(id);
    let remaining = [...elements];

    while (remaining.length > 0) {
      const deferred: ElementState[] = [];

      for (const element of remaining) {
        const parentPresent =
          element.parent === null || replica.byId.has(elementKey(element.parent));
        const originPresent =
          element.rightOrigin === null || replica.byId.has(elementKey(element.rightOrigin));

        if (!parentPresent || !originPresent) {
          deferred.push(element);
          continue;
        }

        const parent =
          element.parent === null ? replica.root : replica.byId.get(elementKey(element.parent))!;
        const node: Node = {
          id: element.id,
          value: element.value,
          isDeleted: element.isDeleted,
          isRoot: false,
          parent,
          side: element.side,
          rightOrigin:
            element.rightOrigin === null
              ? null
              : replica.byId.get(elementKey(element.rightOrigin))!,
          leftChildren: [],
          rightChildren: [],
        };

        replica.byId.set(elementKey(node.id), node);
        insertAmongSiblings(node, parent);
      }

      if (deferred.length === remaining.length) {
        throw new Error(
          `${deferred.length} elements reference a parent or right origin that is not in the ` +
            'snapshot. The snapshot is incomplete or was written out of order.',
        );
      }

      remaining = deferred;
    }

    for (const entry of versionVector) {
      replica.versionVectorByKey.set(formatReplicaId(entry.replica), {
        replica: entry.replica,
        count: entry.count,
      });
      if (compareReplicaId(entry.replica, id) === 0) {
        replica.nextSeq = entry.count;
      }
    }

    return replica;
  }

  apply(operation: Operation): void {
    if (this.hasSeen(operation)) {
      // Counted, not merely skipped (§5). Duplicate delivery is guaranteed —
      // the backplane can repeat a broadcast, catch-up re-sends what a client
      // already has, a client dropped for backpressure recovers by being resent
      // state — so this is never zero and is not itself a problem. A sudden
      // rise in it is how a resend loop announces itself, and that signal does
      // not exist if duplicates are silently absorbed.
      this.duplicatesDropped += 1;
      return;
    }

    if (!this.isReady(operation)) {
      if (this.pending.some((p) => compareElementId(p.id, operation.id) === 0)) {
        // Already buffered. This is the duplicate the watermark cannot see,
        // because the operation has not been applied yet, and buffering it
        // twice would apply it twice when the gap closes.
        this.duplicatesDropped += 1;
      } else {
        if (this.pending.length >= this.maxPending) {
          // §5: a protocol violation, not something to absorb by dropping the
          // oldest. Dropping would leave this replica permanently missing an
          // operation with nothing to indicate it — divergence arrived at
          // quietly, which is the one outcome this project exists to prevent.
          throw new PendingSetOverflowError(this.pending.length, this.maxPending);
        }
        this.pending.push(operation);
      }
      return;
    }

    this.applyReady(operation);
    this.drainPending();
  }

  /** Operations this replica knows that a peer at `remote` does not. */
  operationsSince(remote: Map<string, bigint>): Operation[] {
    return this.log.filter(
      (op) => op.id.seq >= (remote.get(formatReplicaId(op.id.replica)) ?? 0n),
    );
  }

  /** Reclaims tombstones per §5, returning how many were collected. */
  collect(stableFrontier: Map<string, bigint>): number {
    let collected = 0;
    const referenced = new Set<Node>();
    for (const node of this.inOrder()) {
      if (node.rightOrigin !== null) {
        referenced.add(node.rightOrigin);
      }
    }

    const order = this.inOrder();
    for (let i = 0; i < order.length; i++) {
      const node = order[i]!;
      const frontier = stableFrontier.get(formatReplicaId(node.id.replica)) ?? 0n;

      if (
        !node.isDeleted ||
        node.leftChildren.length > 0 ||
        node.rightChildren.length > 0 ||
        referenced.has(node) ||
        node.id.seq >= frontier
      ) {
        continue;
      }

      // Retain the leading tombstone of every run: only it can be named as a
      // future right origin (§5).
      if (i === 0 || !order[i - 1]!.isDeleted) {
        continue;
      }

      const siblings = node.side === 'L' ? node.parent!.leftChildren : node.parent!.rightChildren;
      siblings.splice(siblings.indexOf(node), 1);
      this.byId.delete(elementKey(node.id));
      collected++;
    }

    return collected;
  }

  private hasSeen(operation: Operation): boolean {
    const key = formatReplicaId(operation.id.replica);
    const entry = this.versionVectorByKey.get(key);
    return entry !== undefined && entry.count > operation.id.seq;
  }

  private isReady(operation: Operation): boolean {
    const key = formatReplicaId(operation.id.replica);
    const expected = this.versionVectorByKey.get(key)?.count ?? 0n;
    if (operation.id.seq !== expected) {
      return false;
    }

    if (operation.kind === 'insert') {
      if (operation.parent !== null && !this.byId.has(elementKey(operation.parent))) {
        return false;
      }
      if (
        operation.side === 'R' &&
        operation.rightOrigin !== null &&
        !this.byId.has(elementKey(operation.rightOrigin))
      ) {
        return false;
      }
      return true;
    }

    return this.byId.has(elementKey(operation.target));
  }

  private applyReady(operation: Operation): void {
    if (operation.kind === 'insert') {
      this.applyInsert(operation);
    } else {
      this.byId.get(elementKey(operation.target))!.isDeleted = true;
    }

    this.versionVectorByKey.set(formatReplicaId(operation.id.replica), {
      replica: operation.id.replica,
      count: operation.id.seq + 1n,
    });
    this.log.push(operation);

    if (compareReplicaId(operation.id.replica, this.id) === 0) {
      this.nextSeq = operation.id.seq + 1n;
    }
  }

  private applyInsert(insert: InsertOperation): void {
    const parent = insert.parent === null ? this.root : this.byId.get(elementKey(insert.parent))!;
    const node: Node = {
      id: insert.id,
      value: insert.value,
      isDeleted: false,
      isRoot: false,
      parent,
      side: insert.side,
      rightOrigin:
        insert.side === 'R' && insert.rightOrigin !== null
          ? this.byId.get(elementKey(insert.rightOrigin))!
          : null,
      leftChildren: [],
      rightChildren: [],
    };

    this.byId.set(elementKey(node.id), node);
    insertAmongSiblings(node, parent);
  }

  private drainPending(): void {
    let progressed = true;
    while (progressed) {
      progressed = false;
      const remaining: Operation[] = [];
      for (const candidate of this.pending) {
        if (this.hasSeen(candidate)) {
          progressed = true;
        } else if (this.isReady(candidate)) {
          this.applyReady(candidate);
          progressed = true;
        } else {
          remaining.push(candidate);
        }
      }
      this.pending = remaining;
    }
  }

  /**
   * Depth-first in-order traversal, tombstones included.
   *
   * Iterative, not recursive, for the same reason as the C# side: typing left to
   * right makes each character a right child of the previous one, so a
   * document's tree depth equals its length. A recursive walk exceeds the call
   * stack well below the document sizes §8 targets — and in a browser that takes
   * the tab, not just the call.
   */
  private inOrder(): Node[] {
    const result: Node[] = [];

    // Each frame is [node, phase, next child index]: phase 0 walks the left
    // children, 1 emits the node, 2 walks the right children.
    const stack: [Node, number, number][] = [[this.root, 0, 0]];

    while (stack.length > 0) {
      const [node, phase, index] = stack.pop()!;

      if (phase === 0) {
        if (index < node.leftChildren.length) {
          stack.push([node, 0, index + 1]);
          stack.push([node.leftChildren[index]!, 0, 0]);
        } else {
          stack.push([node, 1, 0]);
        }
      } else if (phase === 1) {
        if (!node.isRoot) {
          result.push(node);
        }
        stack.push([node, 2, 0]);
      } else if (index < node.rightChildren.length) {
        stack.push([node, 2, index + 1]);
        stack.push([node.rightChildren[index]!, 0, 0]);
      }
    }

    return result;
  }
}

function nextIncludingTombstones(all: Node[], leftOrigin: Node): Node | null {
  if (leftOrigin.isRoot) {
    return all.length > 0 ? all[0]! : null;
  }

  const index = all.indexOf(leftOrigin);
  return index >= 0 && index + 1 < all.length ? all[index + 1]! : null;
}

/** Definition 6, and Algorithm 1 lines 32-37. */
function insertAmongSiblings(node: Node, parent: Node): void {
  if (node.side === 'L') {
    const siblings = parent.leftChildren;
    let i = 0;
    while (i < siblings.length && compareElementId(node.id, siblings[i]!.id) >= 0) {
      i++;
    }
    siblings.splice(i, 0, node);
    return;
  }

  const siblings = parent.rightChildren;
  let i = 0;
  while (i < siblings.length) {
    const sibling = siblings[i]!;
    const byOrigin = comparePosition(node.rightOrigin, sibling.rightOrigin);
    const nodeComesFirst =
      byOrigin > 0 || (byOrigin === 0 && compareElementId(node.id, sibling.id) < 0);

    if (nodeComesFirst) {
      break;
    }
    i++;
  }
  siblings.splice(i, 0, node);
}

/** Compares two nodes by traversal position, treating null as end-of-document. */
function comparePosition(a: Node | null, b: Node | null): number {
  if (a === b) {
    return 0;
  }
  if (a === null) {
    return 1;
  }
  if (b === null) {
    return -1;
  }

  let x = a;
  let y = b;
  let depthX = depth(x);
  let depthY = depth(y);

  while (depthX > depthY) {
    const side = x.side;
    x = x.parent!;
    depthX--;
    if (x === b) {
      return side === 'L' ? -1 : 1;
    }
  }

  while (depthY > depthX) {
    const side = y.side;
    y = y.parent!;
    depthY--;
    if (y === a) {
      return side === 'L' ? 1 : -1;
    }
  }

  while (x.parent !== y.parent) {
    x = x.parent!;
    y = y.parent!;
  }

  if (x.side !== y.side) {
    return x.side === 'L' ? -1 : 1;
  }

  const siblings = x.side === 'L' ? x.parent!.leftChildren : x.parent!.rightChildren;
  return siblings.indexOf(x) - siblings.indexOf(y);
}

function depth(node: Node): number {
  let result = 0;
  for (let current = node; current.parent !== null; current = current.parent) {
    result++;
  }
  return result;
}
