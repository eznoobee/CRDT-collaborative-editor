import { Replica, decodeSnapshot, parseReplicaId } from '../crdt';
import { Backoff, DEFAULT_BACKOFF, type BackoffOptions } from './backoff';
import type { DocumentSession } from './DocumentSession';

/** What the server answered a submission with (§7). */
export interface SubmitOutcome {
  readonly code: string | null;
}

/** What the server answered a catch-up with (§8). */
export interface CatchUpOutcome {
  readonly code: string | null;
  readonly snapshot: Uint8Array | null;
  readonly operations: Uint8Array;
}

/** What negotiate assigned (§7). */
export interface Session {
  readonly replicaId: string;
  readonly resumed: boolean;
}

/**
 * Everything the controller needs from a connection, and nothing else.
 *
 * @remarks
 * An interface rather than SignalR directly, so the reconnect logic can be
 * driven through failures a real socket only produces by accident. The real
 * adapter is thin and is exercised end to end in the browser (§9's interop
 * requirement); what is worth unit-testing is what happens *around* a
 * connection, which is where the interesting failures are.
 */
export interface Transport {
  /** Opens a connection, asking to resume `replicaId` if given (§7). */
  connect(replicaId: string | null): Promise<Session>;

  submit(operations: Uint8Array): Promise<SubmitOutcome>;

  catchUp(known: Record<string, number>, forceSnapshot: boolean): Promise<CatchUpOutcome>;

  /** Registers the handler for broadcasts (§8). */
  onBroadcast(handler: (operations: Uint8Array) => void): void;

  /** Registers the handler for the connection dropping. */
  onClosed(handler: () => void): void;

  close(): Promise<void>;
}

/** How the controller reports what it is doing (§9, §13.13). */
export type SyncState = 'offline' | 'connecting' | 'live' | 'stopped';

export interface SyncOptions {
  readonly backoff?: BackoffOptions;
  readonly random?: () => number;

  /** Schedules a retry. Injected so tests do not wait out real delays. */
  readonly schedule?: (run: () => void, delayMs: number) => void;
}

/**
 * Keeps a session in sync with the server across disconnections (§8, §9).
 *
 * @remarks
 * <p>
 * The outbox is the point. Operations are applied locally the instant they are
 * typed and queued here until the server accepts them; nothing in the typing
 * path waits for this class.
 * </p><p>
 * On every connection — the first as much as a reconnect — the controller
 * catches up by **version vector**, never by a server_seq watermark. §8 makes
 * broadcast unordered, so a client can hold 105 without holding 100, and a
 * watermark would silently skip the gap. Catch-up happens before the outbox
 * drains, so operations are submitted against a server the client has already
 * reconciled with.
 * </p>
 */
export class SyncController {
  private readonly session: DocumentSession;
  private readonly transport: Transport;
  private readonly backoff: Backoff;
  private readonly schedule: (run: () => void, delayMs: number) => void;
  private readonly listeners = new Set<() => void>();

  private outbox: Uint8Array[] = [];
  private replicaId: string | null;
  private current: SyncState = 'offline';
  private stopped = false;
  private draining = false;

  constructor(
    session: DocumentSession,
    transport: Transport,
    replicaId: string | null = null,
    outbox: readonly Uint8Array[] = [],
    options: SyncOptions = {},
  ) {
    this.session = session;
    this.transport = transport;
    this.replicaId = replicaId;
    this.outbox = [...outbox];
    this.backoff = new Backoff(options.backoff ?? DEFAULT_BACKOFF, options.random);
    this.schedule = options.schedule ?? ((run, delay) => setTimeout(run, delay));

    transport.onBroadcast((operations) => {
      this.session.receive(operations);
      this.changed();
    });

    transport.onClosed(() => {
      if (this.stopped) {
        return;
      }

      // §13.13: a client that cannot tell a refusal from a dropped connection
      // retries forever against something that will never accept it. The state
      // is what the UI shows, and it changes before the retry is scheduled so
      // there is no window in which the client looks connected and is not.
      this.setState('offline');
      this.retry();
    });
  }

  get state(): SyncState {
    return this.current;
  }

  /** Batches authored and not yet accepted, oldest first. */
  get pending(): readonly Uint8Array[] {
    return this.outbox;
  }

  /** How many times a connection has failed since the last success. */
  get attempts(): number {
    return this.backoff.attempts;
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** Queues a batch the session authored. */
  enqueue(operations: Uint8Array): void {
    this.outbox.push(operations);
    this.changed();

    if (this.current === 'live') {
      void this.drain();
    }
  }

  /** Connects, catches up, and drains whatever is queued. */
  async start(): Promise<void> {
    if (this.stopped) {
      return;
    }

    this.setState('connecting');

    // Captured before the connection replaces it. Whether local state has to be
    // discarded depends on what was *asked for*, and reading the field
    // afterwards makes a first connection — which asks for nothing and is
    // legitimately not a resumption — look like a refused one.
    const requested = this.replicaId;

    let session: Session;
    try {
      session = await this.transport.connect(requested);
    } catch {
      this.setState('offline');
      this.retry();
      return;
    }

    // §7: the server may have refused the resumption and assigned a fresh
    // replica. That is the signal to discard local state — the outbox was
    // authored under an id this connection may not use, and tier-1 will refuse
    // every batch in it.
    const refused = requested !== null && !session.resumed;

    if (refused) {
      this.outbox = [];
    }

    this.replicaId = session.replicaId;
    this.backoff.reset();

    await this.reconcile(refused);

    this.setState('live');
    await this.drain();
  }

  /** Stops reconnecting and closes. */
  async stop(): Promise<void> {
    this.stopped = true;
    this.setState('stopped');
    await this.transport.close();
  }

  /**
   * Asks the server what this client has missed (§8).
   *
   * @param discard - Take a snapshot and replace local state, rather than a
   * delta. Used when the server refused a resumption, because the local replica
   * may hold operations no longer valid under a new id.
   */
  private async reconcile(discard: boolean): Promise<void> {
    const known: Record<string, number> = {};
    if (!discard) {
      for (const [replica, next] of this.session.versionVector) {
        known[replica] = Number(next);
      }
    }

    const caught = await this.transport.catchUp(known, discard);
    if (caught.code !== null) {
      return;
    }

    if (caught.snapshot !== null) {
      const decoded = decodeSnapshot(caught.snapshot);
      this.session.adopt(
        Replica.import(parseReplicaId(this.replicaId!), decoded.elements, decoded.versionVector),
      );
    }

    this.session.receive(caught.operations);
    this.changed();
  }

  /**
   * Submits the outbox, oldest first, stopping on the first refusal.
   *
   * @remarks
   * In order, and one at a time. §5's density rule requires a replica's
   * operations to reach the server without gaps, so submitting the third batch
   * while the second is in flight risks the server seeing them out of order and
   * rejecting the pair. Throughput is not the constraint here — a single
   * client's typing is.
   */
  private async drain(): Promise<void> {
    if (this.draining) {
      return;
    }

    this.draining = true;
    try {
      while (this.outbox.length > 0 && this.current === 'live' && !this.stopped) {
        const batch = this.outbox[0]!;

        let outcome: SubmitOutcome;
        try {
          outcome = await this.transport.submit(batch);
        } catch {
          // The connection went away mid-submission. The batch stays at the
          // head of the queue: dropping it here would lose work the server
          // never saw, and re-sending one it did see is harmless (§5).
          return;
        }

        if (outcome.code !== null) {
          // 4.6 turns each code into its own recovery. Until then the batch
          // stays queued rather than being silently discarded, because
          // discarding is the one outcome that loses a user's work.
          return;
        }

        this.outbox.shift();
        this.changed();
      }
    } finally {
      this.draining = false;
    }
  }

  private retry(): void {
    if (this.stopped) {
      return;
    }

    this.schedule(() => void this.start(), this.backoff.next());
  }

  private setState(state: SyncState): void {
    this.current = state;
    this.changed();
  }

  private changed(): void {
    for (const listener of this.listeners) {
      listener();
    }
  }
}
