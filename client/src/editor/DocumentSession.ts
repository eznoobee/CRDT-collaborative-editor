import { Replica, decodeOperations, encodeOperations, type Operation } from '../crdt';
import type { ReplicaId } from '../crdt';
import { serializeSnapshot } from '../crdt/snapshotJson';
import { replacementBetween } from './diff';

/**
 * One editing session over the local replica (§9).
 *
 * @remarks
 * <p>
 * Local edits apply here and render from here. §9 forbids a server round trip in
 * the typing path, which is not a performance preference: a round trip means the
 * cursor position a keystroke lands at depends on network latency, and every
 * character typed during a hiccup arrives somewhere the user did not put it.
 * </p><p>
 * Nothing in this class knows about a connection. Operations produced by an edit
 * are handed to a sink the caller supplies, and whether that sink submits them
 * now, queues them, or drops them on the floor changes nothing here. That is
 * what makes "the editor works offline" a structural property rather than a
 * behaviour to be tested for.
 * </p>
 */
export class DocumentSession {
  private replica: Replica;
  private readonly sink: (operations: Uint8Array) => void;
  private readonly listeners = new Set<() => void>();
  private version = 0;

  /**
   * @param id - This client's replica id, assigned by the server (§7).
   * @param sink - Receives every batch this session authors, in §6 binary.
   */
  constructor(id: ReplicaId, sink: (operations: Uint8Array) => void) {
    this.replica = new Replica(id);
    this.sink = sink;
  }

  /** What this client believes the document says. */
  get text(): string {
    return this.replica.text;
  }

  /**
   * §9's normalised form of this document.
   *
   * @remarks
   * What convergence is asserted on. Equal text is a much weaker claim: two
   * replicas can render the same characters while disagreeing about the tree
   * underneath, and that disagreement is what diverges on the next concurrent
   * edit.
   */
  get normalised(): string {
    return serializeSnapshot(
      this.replica.export(),
      this.replica.versionVectorEntries,
      this.replica.text,
    );
  }

  /**
   * Notifies <paramref name="listener"/> whenever this session changes.
   *
   * @returns A function that stops the notifications.
   * @remarks
   * A remote edit has to reach the screen without a local one. Rendering only
   * on keystroke gives an editor that is correct for the person typing and
   * stale for everyone else — and, worse, one whose staleness disappears the
   * moment anybody touches the keyboard, so it looks fine in every test that
   * types.
   */
  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /**
   * A value that changes whenever the document does.
   *
   * @remarks
   * For `useSyncExternalStore`, which compares snapshots by identity. The text
   * itself will not do: two different documents can render the same string —
   * an insert and a delete that cancel out — and React would skip the render
   * that a cursor or a collaborator list depends on.
   */
  get revision(): number {
    return this.version;
  }

  /** Operations waiting on a causal dependency (§5). */
  get pendingCount(): number {
    return this.replica.pendingCount;
  }

  /** Duplicate deliveries dropped, which §5 guarantees is never zero for long. */
  get duplicatesDropped(): number {
    return this.replica.duplicatesDropped;
  }

  /** The version vector this session would catch up from (§8). */
  get versionVector(): Map<string, bigint> {
    return this.replica.versionVector;
  }

  /**
   * Applies what the editor now shows, as operations against what it showed.
   *
   * @returns The operations produced, which have already been applied locally.
   */
  edit(next: string): readonly Operation[] {
    const change = replacementBetween(this.replica.text, next);
    if (change === null) {
      return [];
    }

    const operations: Operation[] = [];

    // Deletes first, and from the same index each time. Each delete tombstones
    // the element that is now at `index`, so removing a run means asking for
    // that position repeatedly rather than walking forward — walking would skip
    // every second character.
    for (let removed = 0; removed < change.removed; removed++) {
      operations.push(this.replica.delete(change.index));
    }

    change.inserted.forEach((value, offset) => {
      operations.push(this.replica.insert(change.index + offset, value));
    });

    if (operations.length > 0) {
      // Encoded once, after applying. The sink gets §6 bytes rather than
      // objects because §6 is the authoritative encoding and the transport
      // frames it without looking inside (§13.13a).
      this.sink(encodeOperations(operations));
      this.changed();
    }

    return operations;
  }

  /** Applies a batch that arrived from the server, in §6 binary. */
  receive(operations: Uint8Array): void {
    for (const operation of decodeOperations(operations)) {
      this.replica.apply(operation);
    }

    this.changed();
  }

  /**
   * Replaces local state wholesale, as a snapshot catch-up does (§8).
   *
   * @remarks
   * A replacement rather than a merge, because that is what a snapshot is: the
   * server's whole answer, taken when the client's own state is not worth
   * reconciling. Merging one in would leave elements the snapshot deliberately
   * omits — collected tombstones, above all — resurrected on this client alone.
   */
  adopt(replica: Replica): void {
    this.replica = replica;
    this.changed();
  }

  private changed(): void {
    this.version++;
    for (const listener of this.listeners) {
      listener();
    }
  }
}
