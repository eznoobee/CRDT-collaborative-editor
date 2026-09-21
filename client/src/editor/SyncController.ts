import { PendingSetOverflowError, Replica, decodeSnapshot, parseReplicaId } from '../crdt';
import { Backoff, DEFAULT_BACKOFF, type BackoffOptions } from './backoff';
import { REJECTION, recoveryFor } from './rejections';
import { MAX_PENDING_AGE_MS, type DocumentSession } from './DocumentSession';

/** What the server answered a submission with (§7). */
export interface SubmitOutcome {
  readonly code: string | null;

  /**
   * For a throttle, how long the server says to wait (§7).
   *
   * Optional because every other refusal leaves it meaningless, and absent is
   * treated the same as zero: the server did not name a delay, so the client
   * uses its own floor rather than resubmitting immediately into the limit it
   * just hit.
   */
  readonly retryAfterMs?: number;
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

  /**
   * Submits a batch, carrying what this replica holds (§5's second report
   * path).
   *
   * @param known - Per replica id, the next sequence this client expects.
   * Attached to a message the client was sending anyway, which is the whole
   * saving: the standalone acknowledgement below costs a round trip that a
   * client mid-edit has already paid for.
   */
  submit(operations: Uint8Array, known: Record<string, number>): Promise<SubmitOutcome>;

  catchUp(known: Record<string, number>, forceSnapshot: boolean): Promise<CatchUpOutcome>;

  /**
   * Tells the server what this replica holds (§5's third report path).
   *
   * @remarks
   * Separate from catch-up because a client that is up to date has nothing to
   * catch up on and still has to say so: the stability frontier is the minimum
   * over what live replicas have acknowledged, and a replica that stops
   * reporting freezes it at whatever it last said.
   */
  acknowledge(known: Record<string, number>): Promise<void>;

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

  /**
   * How often to report what this replica holds, in milliseconds (§5).
   *
   * @remarks
   * A throughput knob rather than a correctness one, in one direction only:
   * reporting less often delays GC and never breaks it, because the frontier is
   * a minimum over what replicas have *said*, and saying less is always safe.
   * Not reporting at all is the failure — see the timer below.
   */
  readonly acknowledgeEveryMs?: number;

  /**
   * The clock §5's pending age is measured against.
   *
   * @remarks
   * Injected for the same reason `schedule` is: a test that waited out sixty
   * seconds of real time to prove the age bound would be a test nobody runs.
   * The core deliberately has no clock at all (see `Replica.pendingKeys`), so
   * this is the only one in the path.
   */
  readonly now?: () => number;
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
  private readonly acknowledgeEveryMs: number;
  private acknowledging = false;

  /**
   * The last vector this connection told the server about, encoded.
   *
   * @remarks
   * <p>
   * What makes row 32's saving real: the timer's job is to keep the frontier
   * moving, and a report identical to the last one moves nothing. A client
   * mid-edit reports on every submission, so its tick has nothing left to say.
   * </p><p>
   * A flag saying "a submission happened since the last tick" would have done
   * the same job and been a race: whether the drain's microtask ran before the
   * tick decided the outcome, so the timer's behaviour depended on scheduling
   * order rather than on anything true. Comparing the content has no such
   * ordering: whichever path reports first, the other finds nothing new.
   * </p><p>
   * Safe to suppress because the frontier is a minimum over what replicas have
   * <i>said</i> and nothing expires it — not saying the same thing twice costs
   * nothing. What must not be suppressed is a report after a reconnect, which
   * is why this resets on every connection.
   * </p>
   */
  private lastReported: string | null = null;
  private readonly listeners = new Set<() => void>();

  private outbox: Uint8Array[] = [];
  private replicaId: string | null;
  private current: SyncState = 'offline';
  private stopped = false;
  private draining = false;
  private problemState: SyncProblem | null = null;

  /** Whether a pending-set overflow is already being recovered from (§5, §9). */
  private overflowed = false;

  /**
   * When each waiting operation was first seen waiting (§5's age bound).
   *
   * @remarks
   * Keyed by element identity rather than tracking the count, because §5
   * measures age from when an operation *entered* the set and requires that a
   * cascade releasing part of the backlog does not restart the clock on the
   * rest. An entry is removed when its operation becomes ready and is applied,
   * which is the only way an operation leaves the pending set — so an id that
   * is still here on a later tick has been waiting the whole time.
   */
  private readonly pendingSince = new Map<string, number>();

  /** Stuck operations a catch-up has already been spent on (§5's age bound). */
  private readonly caughtUpFor = new Set<string>();

  /** The clock §5's pending age is measured against. */
  private readonly now: () => number;
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

    // Thirty seconds. §5 bounds nothing here — a slower report only delays
    // collection — so this is chosen against the other end: T_retire is seven
    // days, and a report frequent enough that an ordinary session contributes
    // many of them costs one small message a minute per open tab.
    this.acknowledgeEveryMs = options.acknowledgeEveryMs ?? 30_000;
    this.now = options.now ?? (() => Date.now());

    transport.onBroadcast((operations) => {
      // A broadcast can land before this client has a session — the server
      // starts sending the moment the connection joins the group. Dropping it
      // is safe: catch-up runs on the same connection and asks for everything
      // this replica does not have.
      this.deliver(() => this.sessionState?.receive(operations));
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
      // §9's offline-window discard, and the ONE path by which it actually
      // happens. A replica idle past T_retire is retired server-side, so the
      // resumption is declined and a fresh id assigned; everything in the
      // outbox was authored under the old id and tier-1 would refuse all of
      // it. Dropping it is correct and it is §5's single exception to "do not
      // drop", which is exactly why the count is reported rather than the
      // queue quietly emptying. §9: accepting an hour of offline work and
      // throwing it away without warning is a data-loss bug, not a limitation.
      //
      // The code is resync_required rather than one of its own. The condition
      // is the same condition — discard local state, take a snapshot, report
      // what was lost — arriving through negotiate instead of through a
      // submission, and a second code would be a second sentence for one
      // event.
      const lost = this.outbox.length;
      this.outbox = [];

      if (lost > 0) {
        this.fail(REJECTION.resyncRequired, lost);
      }
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

    // Cleared per connection. The suppression below is about not repeating
    // what this connection already said; a new connection has said nothing,
    // and a reconnect after a resync may be a different replica entirely.
    this.lastReported = null;

    await this.reconcile(refused);

    this.setState('live');
    this.beginAcknowledging();
    await this.drain();
  }

  /**
   * Reports what this replica holds, on a timer, for as long as it is live (§5).
   *
   * @remarks
   * <p>
   * <b>Nothing else reports it, and without this the stability frontier never
   * advances for a document anybody has open.</b> §5 names three paths by which
   * the server learns what a replica holds: catch-up, a piggyback on
   * submission, and this timer. Catch-up runs once per connection and reports
   * what the client held <i>before</i> it received anything, which for a new
   * replica is nothing; the piggyback is not built (register row 32). So this
   * is the only one, and with it missing the frontier stayed where catch-up
   * left it and garbage collection could only ever run on a document whose
   * replicas had all been retired — seven days of nobody opening it.
   * </p><p>
   * That is §13.32's shape one layer out: 7.2 added the acknowledgement to the
   * hub because a viewer never submits, and then the only thing that called it
   * was a test client. §13.41's question — does anything invoke this, or only
   * the test? — has to be asked across the client/server boundary too, because
   * "the product" is both.
   * </p><p>
   * Failures are swallowed. An acknowledgement is advisory: losing one delays
   * collection and breaks nothing, and a rejected promise here would surface as
   * an unhandled rejection in a browser tab that is otherwise working.
   * </p>
   */
  /** What this replica holds, next-expected, in the shape the wire uses. */
  private known(): Record<string, number> {
    const known: Record<string, number> = {};
    for (const [replica, next] of this.sessionState?.versionVector ?? []) {
      known[replica] = Number(next);
    }

    return known;
  }

  /**
   * Records a report, answering whether it said anything the last one did not.
   */
  private reported(known: Record<string, number>): boolean {
    // Sorted, so two reports of the same vector encode the same way whatever
    // order the map happened to enumerate in.
    const encoded = JSON.stringify(
      Object.keys(known).sort().map((replica) => [replica, known[replica]]),
    );

    if (encoded === this.lastReported) {
      return false;
    }

    this.lastReported = encoded;
    return true;
  }

  private beginAcknowledging(): void {
    if (this.acknowledging) {
      return;
    }

    this.acknowledging = true;
    this.schedule(() => void this.acknowledgeTick(), this.acknowledgeEveryMs);
  }

  private async acknowledgeTick(): Promise<void> {
    if (this.stopped) {
      this.acknowledging = false;
      return;
    }

    if (this.current === 'live' && this.sessionState !== null) {
      // Before the report, not after. The report is skipped when the vector has
      // not moved, and a pending set that is stuck is exactly the case where it
      // has not moved — so putting this behind that check would silence the age
      // bound in the only situation it fires.
      this.checkPendingAge(this.sessionState);

      const known = this.known();

      // Skipped when the last report said the same thing — which, for a client
      // that is submitting, is what its own submissions already said (row 32).
      if (this.reported(known)) {
        try {
          await this.transport.acknowledge(known);
        } catch {
          // Advisory, and unreported: the next tick sees this vector as new
          // again and retries it.
          this.lastReported = null;
        }
      }
    }

    this.acknowledging = false;
    if (!this.stopped) {
      this.beginAcknowledging();
    }
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

    const session = this.sessionState;
    this.deliver(() => session.receive(caught.operations));

    // Cleared only after the catch-up applied without overflowing again. Set
    // before this line and the budget would be spent by the reconciliation
    // that was supposed to restore it.
    this.overflowed = false;
    this.changed();
  }

  /**
   * Applies what arrived, recovering from §5's pending-set bound (§9).
   *
   * @remarks
   * <p>
   * Both paths that hand operations to the session go through here, because
   * both can overflow: a broadcast can arrive with its dependency still in
   * flight, and a catch-up answer can too — the server sends what this replica
   * does not have, in `server_seq` order, which §8 explicitly does not promise
   * to be causal order.
   * </p><p>
   * <b>The overflow is not a loss.</b> The core throws rather than dropping, so
   * nothing has been discarded when this runs; some of the batch applied and
   * the rest did not, and catch-up by version vector asks for exactly what is
   * missing. Duplicates among the re-sent operations are one of §5's three
   * guaranteed sources and cost a counter.
   * </p><p>
   * <b>Once, then stop.</b> The same bound as `unknown_origin`, for the same
   * reason: a second overflow after a successful catch-up means the client
   * asked for what it was missing, was given it, and is still missing it —
   * which is a bug here, and retrying a bug forever is a loop that looks like a
   * slow network. The flag clears on the next successful reconciliation.
   * </p>
   */
  private deliver(apply: () => void): void {
    try {
      apply();
    } catch (error) {
      if (!(error instanceof PendingSetOverflowError)) {
        throw error;
      }

      const tried = this.overflowed;
      this.overflowed = true;
      this.overflow(tried);
    }
  }

  /**
   * §5's pending-set bound was exceeded, by size or by age (§9).
   *
   * @remarks
   * One recovery for both halves of the bound, because both mean the same
   * thing: this replica is missing a dependency that is not going to arrive by
   * itself. Catching up by version vector asks for exactly that.
   */
  private overflow(alreadyTried: boolean): void {
    this.fail(REJECTION.pendingOverflow, 0);

    if (alreadyTried) {
      this.stopped = true;
      this.setState('stopped');
      return;
    }

    void this.reconcile(false);
  }

  /**
   * §5's age bound: nothing may wait on a dependency indefinitely.
   *
   * @remarks
   * <p>
   * The size bound catches a flood. This catches the quieter failure it cannot
   * see — a handful of operations stuck forever behind a dependency that was
   * lost rather than delayed. Without it the client renders a document missing
   * those operations, converges with nobody, and reports `live` throughout,
   * which is precisely the shape §13.13 exists to forbid.
   * </p><p>
   * Run from the acknowledgement timer rather than a timer of its own: that one
   * already ticks while the connection is live, and a second timer would be a
   * second thing to keep in step for no gain.
   * </p>
   */
  private checkPendingAge(session: DocumentSession): void {
    const now = this.now();
    const waiting = session.pendingKeys;
    const live = new Set(waiting);

    // Anything that drained is forgotten, so the map cannot grow across a
    // session and a key that reappears cannot inherit an old timestamp.
    for (const key of [...this.pendingSince.keys()]) {
      if (!live.has(key)) {
        this.pendingSince.delete(key);
        this.caughtUpFor.delete(key);
      }
    }

    const stale: string[] = [];
    for (const key of waiting) {
      const since = this.pendingSince.get(key);
      if (since === undefined) {
        this.pendingSince.set(key, now);
      } else if (now - since >= MAX_PENDING_AGE_MS) {
        stale.push(key);
      }
    }

    if (stale.length === 0) {
      return;
    }

    // The budget is per stuck operation, not per session. A catch-up that
    // succeeded and still left this operation waiting has answered the only
    // question worth asking: the dependency is not coming. Counting per
    // session instead would catch up every tick forever, because a successful
    // reconciliation clears the session-level flag — which is what the size
    // bound's flag is for, and exactly wrong here.
    const tried = stale.some((key) => this.caughtUpFor.has(key));
    for (const key of stale) {
      this.caughtUpFor.add(key);
    }

    this.overflow(tried);
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
          const reporting = this.known();
          outcome = await this.transport.submit(batch, reporting);

          // Recorded only once the call returned. A submission that threw did
          // not reach the server, and marking it reported would leave the
          // timer with nothing to say about a report nobody received.
          this.reported(reporting);
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
          if (await this.recover(outcome.code, batch, outcome.retryAfterMs ?? 0) === 'halt') {
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
  private async recover(
    code: string,
    batch: Uint8Array,
    retryAfterMs: number,
  ): Promise<'continue' | 'halt'> {
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

      case 'wait-and-retry': {
        // §7's throttle. The batch stays at the head of the outbox and goes
        // back up unchanged once the window rolls over — no catch-up, because
        // nothing about the document changed; the server refused to *read*
        // this batch, not to accept what it references.
        //
        // The delay is the server's, clamped at both ends. A zero — an old
        // server, a lost field, a bug — would turn this into a spin that hits
        // the limiter as fast as the socket allows, and a wild number would
        // park unsent work indefinitely with the connection still live. Both
        // bounds are this client's, not the protocol's.
        const wait = Math.min(Math.max(retryAfterMs, THROTTLE_FLOOR_MS), THROTTLE_CEILING_MS);

        // Reported while waiting, because a paused outbox with a live
        // connection is exactly the state §13.13 says must not look like
        // everything is fine.
        this.fail(code, 0);
        await new Promise<void>((resolve) => { this.schedule(resolve, wait); });
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

      case 'catch-up':
        // Unreachable, and an assertion rather than a fall-through. This
        // recovery belongs to the receive path — a pending-set overflow, which
        // this client raises about its own buffer — and reaching it here would
        // mean a server answered a submission with `pending_overflow`, which
        // is not a code it may return. Letting it fall into `stop` would hide
        // that behind a plausible-looking halt.
        throw new Error(`${code} is not a refusal the server may return`);

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
 * The floor under a throttle delay: never resubmit sooner than this, whatever
 * the server said or failed to say.
 */
const THROTTLE_FLOOR_MS = 250;

/** The ceiling over a throttle delay; past this the number is not credible. */
const THROTTLE_CEILING_MS = 60_000;

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
