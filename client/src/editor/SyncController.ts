import { Replica, decodeSnapshot, parseReplicaId } from '../crdt';
import { Backoff, DEFAULT_BACKOFF, type BackoffOptions } from './backoff';
import { recoveryFor } from './rejections';
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
 * A connection refused for a reason §9 has a recovery for.
 *
 * @remarks
 * A dropped socket and a refusal are different events with different answers
 * (§13.13), and until this existed every connect failure was the former: the
 * controller went offline and retried, and a client whose session had expired
 * retried forever against something that would never accept it, with nothing on
 * screen to say why. A transport that knows the reason says so.
 */
export class ConnectionRefused extends Error {
  readonly code: string;

  constructor(code: string, cause?: unknown) {
    super(`Connection refused: ${code}`);
    this.name = 'ConnectionRefused';
    this.code = code;
    this.cause = cause;
  }
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

/**
 * A refusal the user has to be told about (§9, §13.13).
 *
 * @param code - The server's code, verbatim, so a report names it.
 * @param lost - Operations discarded as unrecoverable. Non-zero only for a
 * resync, which is §5's one exception to "do not drop".
 */
export interface SyncProblem {
  readonly code: string;
  readonly lost: number;
}

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
  private readonly build: (replicaId: string) => DocumentSession;
  private sessionState: DocumentSession | null;
  private readonly transport: Transport;
  private readonly backoff: Backoff;
  private readonly schedule: (run: () => void, delayMs: number) => void;
  private readonly listeners = new Set<() => void>();

  private outbox: Uint8Array[] = [];
  private replicaId: string | null;
  private current: SyncState = 'offline';
  private stopped = false;
  private draining = false;
  private problemState: SyncProblem | null = null;
  private readOnlyState = false;
  private retried = new Set<string>();

  /**
   * @param build - Makes the session once the server has assigned a replica id.
   * Deferred rather than taken ready-made, because §7 assigns that id at
   * `negotiate` and a session built before it would author operations under an
   * id the server never issued — which tier-1 refuses, one batch at a time,
   * with no obvious cause.
   */
  constructor(
    build: (replicaId: string) => DocumentSession,
    transport: Transport,
    replicaId: string | null = null,
    outbox: readonly Uint8Array[] = [],
    options: SyncOptions = {},
  ) {
    this.build = build;
    this.sessionState = null;
    this.transport = transport;
    this.replicaId = replicaId;
    this.outbox = [...outbox];
    this.backoff = new Backoff(options.backoff ?? DEFAULT_BACKOFF, options.random);
    this.schedule = options.schedule ?? ((run, delay) => setTimeout(run, delay));

    transport.onBroadcast((operations) => {
      // A broadcast can land before this client has a session — the server
      // starts sending the moment the connection joins the group. Dropping it
      // is safe: catch-up runs on the same connection and asks for everything
      // this replica does not have.
      this.sessionState?.receive(operations);
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

  /** The session, once a connection has assigned this client a replica id. */
  get session(): DocumentSession | null {
    return this.sessionState;
  }

  /**
   * The refusal the user needs to see, if any.
   *
   * @remarks
   * §13.13: a rejection the rejected party cannot observe is not a rejection.
   * Every code the server can return sets this, so no refusal reaches the
   * client and stops there.
   */
  get problem(): SyncProblem | null {
    return this.problemState;
  }

  /** Whether this client may still author (§7's mid-session demotion). */
  get readOnly(): boolean {
    return this.readOnlyState;
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
    } catch (error) {
      // A refusal with a code is reported before the retry is scheduled, so
      // the state and the reason change together and there is no window in
      // which the client is offline for no stated cause (§13.13).
      if (error instanceof ConnectionRefused) {
        this.fail(error.code, 0);
      }

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

    // Built on the first connection, and rebuilt if the server assigned a
    // different id than the one resumed — the old session authored under an id
    // this connection may not use.
    this.sessionState ??= this.build(session.replicaId);
    if (refused) {
      this.sessionState = this.build(session.replicaId);
    }

    // Cleared on a connection that succeeded. The server re-reads the role at
    // negotiate, so a demotion that has been reversed should not leave the
    // client read-only until it reloads — and one that has not will refuse the
    // first submission again, which restores this immediately.
    this.readOnlyState = false;

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
    if (!discard && this.sessionState !== null) {
      for (const [replica, next] of this.sessionState.versionVector) {
        known[replica] = Number(next);
      }
    }

    const caught = await this.transport.catchUp(known, discard);
    if (caught.code !== null) {
      return;
    }

    if (this.sessionState === null) {
      return;
    }

    if (caught.snapshot !== null) {
      const decoded = decodeSnapshot(caught.snapshot);
      this.sessionState.adopt(
        Replica.import(parseReplicaId(this.replicaId!), decoded.elements, decoded.versionVector),
      );
    }

    this.sessionState.receive(caught.operations);
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
      while (
        this.outbox.length > 0
        && this.current === 'live'
        && !this.stopped
        && !this.readOnlyState
      ) {
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
          // The recovery says whether this loop keeps going. Calling drain
          // again from inside it would hit the re-entrancy guard and silently
          // do nothing, which is how "retries once" becomes "never retries".
          if (await this.recover(outcome.code, batch) === 'halt') {
            return;
          }

          continue;
        }

        this.outbox.shift();

        // Cleared only on an acceptance. A batch that succeeded after a
        // catch-up is the case the retry budget exists for; leaving it marked
        // would make the next unrelated unknown_origin unrecoverable.
        this.retried.delete(key(batch));
        this.changed();
      }
    } finally {
      this.draining = false;
    }
  }

  /**
   * Acts on one refusal, per §9's table.
   *
   * @remarks
   * The batch is never discarded except by a resync, which §5 names as its one
   * exception to "do not drop" — and which reports what was lost, because
   * losing a user's unsent work silently is the failure this whole path exists
   * to avoid.
   */
  private async recover(code: string, batch: Uint8Array): Promise<'continue' | 'halt'> {
    switch (recoveryFor(code)) {
      case 'catch-up-and-retry': {
        // The server does not have something this batch references. Once, and
        // once only: a second occurrence after a successful catch-up is a bug
        // in this client, and retrying a bug forever is a loop that looks like
        // a network problem.
        const seen = key(batch);
        if (this.retried.has(seen)) {
          this.fail(code, 0);
          return 'halt';
        }

        this.retried.add(seen);
        await this.reconcile(false);
        return 'continue';
      }

      case 'resync': {
        // §5's GC watermark passed the elements this batch refers to. They are
        // gone from the server and cannot be reconstructed, so the work is
        // lost — and the number is reported rather than the queue quietly
        // emptying.
        const lost = this.outbox.length;
        this.outbox = [];
        await this.reconcile(true);
        this.fail(code, lost);
        return 'halt';
      }

      case 'read-only':
        // Demoted mid-session (§7). Still connected, still receiving, no longer
        // authoring — and the outbox is kept, because the work may become
        // submittable again if the role is restored.
        this.readOnlyState = true;
        this.fail(code, 0);
        return 'halt';

      case 'reconnect':
        // Expected to clear on its own: a replica slot freeing up, a ticket
        // being reissued. The outbox survives the reconnection.
        this.fail(code, 0);
        this.setState('offline');
        this.retry();
        return 'halt';

      case 'stop':
      default:
        // Retrying cannot help. The outbox is kept for diagnosis rather than
        // discarded, because it is the evidence.
        this.fail(code, 0);
        this.stopped = true;
        this.setState('stopped');
        return 'halt';
    }
  }

  private fail(code: string, lost: number): void {
    this.problemState = { code, lost };
    this.changed();
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

/**
 * Identifies a batch for the retry budget.
 *
 * @remarks
 * The bytes, not the object. A batch rebuilt from the store after a reload is a
 * different object and the same work, and a budget keyed on identity would give
 * it a fresh retry on every page load — which is the loop the budget exists to
 * stop.
 */
function key(batch: Uint8Array): string {
  return Array.from(batch).join(',');
}
