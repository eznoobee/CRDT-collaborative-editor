import {
  Replica,
  decodeOperations,
  decodeSnapshot,
  encodeOperations,
  encodeSnapshot,
  type Operation,
} from '../crdt';
import { formatReplicaId, type ReplicaId } from '../crdt';
import { serializeSnapshot } from '../crdt/snapshotJson';
import { replacementBetween } from './diff';

/**
 * Operations one submission may carry (§7).
 *
 * @remarks
 * <p>
 * §7's ingest cap, restated on the client because the client is the party that
 * has to respect it: the server counts operations *after* runs are expanded, so
 * a paste that looks like one record on the wire is 900 operations to the
 * validator, and 900 is refused.
 * </p><p>
 * <b>It was not restated for two phases, and a pasted paragraph was refused the
 * whole time.</b> Typing produces one operation per change event and never came
 * near the cap; pasting produces as many as the clipboard held, in one event,
 * and `batch_too_large` recovers by `stop` — so the first paragraph a user
 * pasted halted their sync for the rest of the session (§13.37: the number that
 * matters belongs to the action the limit is not named after). Found by row
 * 27's largest-legitimate-use test, which is what it is for.
 * </p><p>
 * A constant rather than a value fetched from the server: the split has to be
 * decided before the first submission, and a client that learned its batch size
 * from a response would learn it after the paste it needed it for.
 * </p>
 */
export const MAX_OPERATIONS_PER_BATCH = 256;

/**
 * Operations this session will hold waiting on a causal dependency (§5).
 *
 * @remarks
 * <p>
 * §5 bounds the pending set <b>per connection</b>, and says in as many words
 * that whoever attaches a replica to a network connection sets it. Both cores
 * therefore default it to unbounded, deliberately: a replica replaying a stored
 * trace legitimately buffers as much as the trace demands, and an unbounded
 * buffer fed by a local file is not a denial-of-service vector.
 * </p><p>
 * <b>Nobody was that layer.</b> The server has no pending set by design —
 * `IngestValidator` refuses a non-ready operation rather than buffering it,
 * which removes the vector instead of bounding it — so the browser client is the
 * only place the bound can be set, and for nine phases it was not. The only
 * assignments anywhere were in the two causal-readiness test files, which is why
 * a fully covered core hid it: the core's tests set the bound themselves and
 * proved the mechanism works, which was never the question (register row 36).
 * </p><p>
 * <b>The number.</b> §13.37's rule is to size a limit by the largest legitimate
 * use, not by what feels safe. That use is a peer's offline afternoon arriving
 * across a partition — 2,000 operations, the figure row 27 put in
 * `limits/use.ts` as `BURST` — and the realistic worst case is several
 * peers reconnecting at once, so this is five of them. It is not a memory
 * budget: 10,000 buffered operations is a few megabytes in a tab, and what makes
 * an unbounded pending set dangerous is that it has no number at all, which is
 * why §5 asks for one rather than for a small one.
 * </p><p>
 * Exceeding it is not a drop. §5 allows exactly one exception to "do not drop"
 * and this is not it, so the core throws and the connection layer recovers by
 * fetching what it is missing — see `SyncController`'s `pending_overflow`.
 * </p>
 */
export const MAX_PENDING_OPERATIONS = 10_000;

/**
 * How long one operation may wait on a causal dependency (§5).
 *
 * @remarks
 * <p>
 * §5 bounds the pending set <b>in operations and in seconds</b>, and the second
 * half is not decoration. The size bound catches a flood; it does nothing at all
 * about four operations stuck forever, which is the quieter failure and the
 * worse one — the client renders a document missing those operations, converges
 * with nobody, and shows `live` the whole time (§13.13).
 * </p><p>
 * <b>The number.</b> Longer than any legitimate wait. A dependency held back by
 * §8's unordered fan-out arrives within a round trip; the longest legitimate
 * case is a catch-up answer crossing a bad link, which row 27 put at 45 seconds
 * (`SLOW_RELOAD_SECONDS`, the train tunnel). Sixty seconds is past that, and is
 * also two of this client's own 30-second acknowledgement intervals — a
 * dependency still missing after the client has twice told the server what it
 * holds is not in flight.
 * </p><p>
 * <b>Age is measured per operation, from when it entered the set</b>, which §5
 * is explicit about: a cascade that releases some of the backlog must not
 * restart the clock on what is left. `SyncController` therefore keys on element
 * identity rather than watching the count, because a set that stays at four
 * while four different operations pass through it is healthy and one that stays
 * at four because the same four are stuck is not, and the count cannot tell
 * them apart.
 * </p>
 */
export const MAX_PENDING_AGE_MS = 60_000;

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
    this.replica = this.install(new Replica(id));
    this.sink = sink;
  }

  /**
   * Installs a replica under this session's bounds (§5).
   *
   * @remarks
   * Every path that puts a replica in this session goes through here — the
   * constructor, `restore`, and `adopt` — because a bound set in the
   * constructor alone is lost on exactly the paths that follow a long absence,
   * which is when a peer's backlog is largest. A reload and a snapshot catch-up
   * would each have silently returned the session to unbounded, and nothing
   * about a session reading `maxPending === MAX_SAFE_INTEGER` looks different
   * from one that never set it.
   */
  private install(replica: Replica): Replica {
    replica.maxPending = MAX_PENDING_OPERATIONS;
    return replica;
  }

  /** The replica id this session authors under (§7's assignment). */
  get replicaId(): string {
    return formatReplicaId(this.replica.id);
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

  /** Which operations are waiting, for §5's age bound. */
  get pendingKeys(): readonly string[] {
    return this.replica.pendingKeys;
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
      // Encoded after applying, and split into batches §7 admits. The sink gets
      // §6 bytes rather than objects because §6 is the authoritative encoding
      // and the transport frames it without looking inside (§13.13a) — which is
      // also why the split has to happen here: the transport cannot look inside
      // to do it, so a layer that encodes is the only layer that can.
      for (let at = 0; at < operations.length; at += MAX_OPERATIONS_PER_BATCH) {
        this.sink(encodeOperations(operations.slice(at, at + MAX_OPERATIONS_PER_BATCH)));
      }

      this.changed();
    }

    return operations;
  }

  /**
   * This document in §6's snapshot encoding, for the local store (§9).
   *
   * @remarks
   * The same bytes the server would send, deliberately. A second local format
   * would be a second codec to keep in step with §6, and §13.11 is what happens
   * when two encoders drift — except that this one has no conformance runner
   * comparing it to anything.
   */
  get snapshot(): Uint8Array {
    return encodeSnapshot(this.replica.export(), this.replica.versionVectorEntries);
  }

  /** Rebuilds a session from a stored snapshot (§9's reload path). */
  static restore(
    id: ReplicaId,
    snapshot: Uint8Array,
    sink: (operations: Uint8Array) => void,
  ): DocumentSession {
    const decoded = decodeSnapshot(snapshot);
    const session = new DocumentSession(id, sink);
    session.replica = session.install(
      Replica.import(id, decoded.elements, decoded.versionVector));
    return session;
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
    this.replica = this.install(replica);
    this.changed();
  }

  private changed(): void {
    this.version++;
    for (const listener of this.listeners) {
      listener();
    }
  }
}
