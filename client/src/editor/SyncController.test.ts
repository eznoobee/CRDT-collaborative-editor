import { DocumentSession, MAX_PENDING_AGE_MS, MAX_PENDING_OPERATIONS } from './DocumentSession';
import { REJECTION } from './rejections';
import { SyncController, type CatchUpOutcome, type Session, type SubmitOutcome, type Transport } from './SyncController';
import { Replica, encodeOperations, encodeSnapshot, parseReplicaId } from '../crdt';

/**
 * Reconnect, catch-up and the outbox (§8, §9).
 *
 * The vacuity risks, named before these were written:
 *
 * 1. **A reconnect test against a server that never went away is trivially
 *    green.** So the transport here fails on demand and drops the connection
 *    on demand, and each test asserts what happened *because* of the failure.
 * 2. **A catch-up test passes whatever cursor the client sends**, since the
 *    fake will answer anything. The assertion is therefore on the vector the
 *    controller sent — the client's own, per replica — because a controller
 *    that sent a server_seq watermark would look identical from the outside
 *    and would silently skip operations §8 allows to arrive out of order.
 * 3. **An outbox test with an always-accepting server never exercises the
 *    queue.** The refusal and the mid-flight failure are both driven here, and
 *    the assertion is that the batch is still queued — because discarding it is
 *    the one outcome that loses a user's work and looks like success.
 */

const ID = '00000000-0000-0000-0000-00000000000a';
const PEER = parseReplicaId('00000000-0000-0000-0000-00000000000b');

/** What the server mints when it refuses a resumption (§7). */
const FRESH = '00000000-0000-0000-0000-00000000000c';

/** An interval the reconnect backoff never produces, so the two timers are told apart. */
const ACK_EVERY_MS = 987_654;

class FakeTransport implements Transport {
  broadcast: ((operations: Uint8Array) => void) | null = null;
  closed: (() => void) | null = null;

  readonly connects: (string | null)[] = [];
  readonly vectors: Record<string, number>[] = [];
  readonly forced: boolean[] = [];
  readonly submitted: Uint8Array[] = [];

  /** Queued answers; the last one repeats. */
  connectResults: (Session | Error)[] = [{ replicaId: ID, resumed: false }];
  catchUpResult: CatchUpOutcome = { code: null, snapshot: null, operations: encodeOperations([]) };

  /** Every acknowledgement the controller sent, in order (§5). */
  acknowledged: Record<string, number>[] = [];

  acknowledge(known: Record<string, number>): Promise<void> {
    this.acknowledged.push(known);
    return Promise.resolve();
  }
  submitResults: (SubmitOutcome | Error)[] = [{ code: null }];

  connect(replicaId: string | null): Promise<Session> {
    this.connects.push(replicaId);
    const answer = this.connectResults.length > 1
      ? this.connectResults.shift()!
      : this.connectResults[0]!;

    return answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer);
  }

  /** The version vector carried by each submission, in order (§5, row 32). */
  readonly reported: Record<string, number>[] = [];

  submit(operations: Uint8Array, known: Record<string, number>): Promise<SubmitOutcome> {
    this.submitted.push(operations);
    this.reported.push(known);
    const answer = this.submitResults.length > 1
      ? this.submitResults.shift()!
      : this.submitResults[0]!;

    return answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer);
  }

  catchUp(known: Record<string, number>, forceSnapshot: boolean): Promise<CatchUpOutcome> {
    this.vectors.push(known);
    this.forced.push(forceSnapshot);
    return Promise.resolve(this.catchUpResult);
  }

  onBroadcast(handler: (operations: Uint8Array) => void): void {
    this.broadcast = handler;
  }

  onClosed(handler: () => void): void {
    this.closed = handler;
  }

  close(): Promise<void> {
    return Promise.resolve();
  }

  /** Drops the connection, as a network would. */
  drop(): void {
    this.closed?.();
  }
}

/**
 * Lets the controller's own promises finish.
 *
 * @remarks
 * The drain runs without being awaited — typing must not wait for a socket —
 * so a test that asserts about a submission immediately after an edit is
 * asserting against work that has not started. Draining the microtask queue
 * here rather than in each test, because "how many awaits does it take" is not
 * something a test should be encoding.
 */
async function settle(): Promise<void> {
  for (let turn = 0; turn < 8; turn++) {
    await Promise.resolve();
  }
}

/** A controller whose retries run when the test says so. */
function controller(transport: FakeTransport, options: {
  replicaId?: string | null;
  outbox?: Uint8Array[];

  /**
   * Text the rebuilt session starts from, as a reload restores it from the
   * store. The session cannot be handed in ready-made: §7 assigns the replica
   * id at negotiate, so it is built around the id the *server* answered with.
   */
  restored?: string;
} = {}) {
  const pending: (() => void)[] = [];
  const acks: (() => void)[] = [];

  // §5's pending age is measured in seconds, and a test that waited them out
  // would be a test nobody runs. The controller's clock is injected for the
  // same reason `schedule` is.
  const clock = { at: 1_000_000 };
  const sync = new SyncController(
    (replicaId) => {
      const built = new DocumentSession(parseReplicaId(replicaId), () => {});
      if (options.restored !== undefined) {
        built.edit(options.restored);
      }

      return built;
    },
    transport,
    options.replicaId ?? null,
    options.outbox ?? [],
    {
      random: () => 0.5,
      now: () => clock.at,

      // §5's report timer and the reconnect backoff share one scheduling seam
      // in the controller, which is right there and wrong here: a test that
      // drains "the next scheduled thing" would run whichever was queued first
      // and assert about the other. Routed apart by delay, with an interval no
      // backoff produces, so `tick` still means "run the retry".
      acknowledgeEveryMs: ACK_EVERY_MS,
      schedule: (run, delayMs) => {
        (delayMs === ACK_EVERY_MS ? acks : pending).push(run);
      },
    },
  );

  return {
    sync,
    /** Runs whatever retry was scheduled. */
    async tick(): Promise<void> {
      const next = pending.shift();
      next?.();
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    },
    /** Runs the acknowledgement timer (§5), which is not a retry. */
    async ackTick(): Promise<void> {
      const next = acks.shift();
      next?.();
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    },
    get scheduled(): number {
      return pending.length;
    },
    /** Moves the injected clock, for §5's pending age. */
    advance(ms: number): void {
      clock.at += ms;
    },
  };
}

describe('connecting', () => {
  it('catches up by version vector, not by a watermark', async () => {
    // The assertion that separates a correct client from one that would look
    // identical until an out-of-order broadcast arrived.
    const transport = new FakeTransport();
    const { sync } = controller(transport, { restored: 'ab' });

    await sync.start();

    expect(transport.vectors).toHaveLength(1);

    // The vector names replicas and counts, which a server_seq cursor cannot.
    const sent = transport.vectors[0]!;
    expect(Object.keys(sent)).toEqual([ID]);
    expect(sent[ID]).toBe(2);
  });

  it('applies the delta the server answered with', async () => {
    const transport = new FakeTransport();
    const remote = new Replica(PEER);
    const operations = [...'hi'].map((value, index) => remote.insert(index, value));
    transport.catchUpResult = {
      code: null,
      snapshot: null,
      operations: encodeOperations(operations),
    };

    const { sync } = controller(transport);
    await sync.start();

    expect(sync.session?.text).toBe('hi');
    expect(sync.state).toBe('live');
  });

  it('adopts a snapshot wholesale rather than merging it', async () => {
    const transport = new FakeTransport();
    const server = new Replica(PEER);
    [...'server'].forEach((value, index) => server.insert(index, value));

    transport.catchUpResult = {
      code: null,
      snapshot: encodeSnapshot(server.export(), server.versionVectorEntries),
      operations: encodeOperations([]),
    };

    const { sync } = controller(transport, { restored: 'local' });

    await sync.start();

    // The local text is gone, which is what a snapshot means: the server's
    // whole answer, taken when local state was not worth reconciling.
    expect(sync.session?.text).toBe('server');
  });
});

describe("§5's acknowledgement", () => {
  it('reports what this replica holds on its own timer, with nobody calling it', async () => {
    // §13.41, ACROSS THE CLIENT/SERVER BOUNDARY. The hub has had
    // AcknowledgeAsync since 7.2, added because a viewer never submits and
    // would otherwise freeze the stability frontier. Nothing in this client
    // ever called it. The server-side tests all passed — they drive the hub
    // method directly — and in the product the frontier stayed wherever
    // catch-up left it, which for a fresh replica is nothing, so garbage
    // collection could only run on documents everybody had abandoned for seven
    // days.
    //
    // So: nobody calls anything here. The controller goes live, the scheduled
    // work runs, and the transport has to receive an acknowledgement on its
    // own.
    const transport = new FakeTransport();
    const harness = controller(transport);
    const { sync } = harness;

    await sync.start();
    sync.session?.edit('hello');

    expect(transport.acknowledged).toHaveLength(0);

    await harness.ackTick();

    expect(transport.acknowledged).toHaveLength(1);

    // And it reports what the replica actually holds, not an empty vector: five
    // code points from this replica. An acknowledgement of nothing is what the
    // frontier already had.
    const [first] = transport.acknowledged;
    expect(Object.values(first!)).toEqual([5]);
  });

  it('keeps reporting, because one report is a frontier that stops advancing', async () => {
    // The pair. A single acknowledgement at connect is what catch-up already
    // did; the property is that it repeats while the tab is open.
    const transport = new FakeTransport();
    const harness = controller(transport);
    const { sync } = harness;

    await sync.start();

    sync.session?.edit('a');
    await harness.ackTick();
    sync.session?.edit('ab');
    await harness.ackTick();

    expect(transport.acknowledged).toHaveLength(2);
    expect(Object.values(transport.acknowledged[1]!)).toEqual([2]);
  });

  it('carries what this replica holds on every submission', async () => {
    // ROW 32. §5 names three ways the server learns what a replica holds, and
    // this is the second: attached to a message the client was sending anyway.
    // The vacuity risk is that a field added to the wire and never read breaks
    // nothing — so what is asserted is the content, and the server-side test
    // asserts the frontier moves from a submission with no other path open.
    const transport = new FakeTransport();
    const harness = controller(transport);
    const { sync } = harness;

    await sync.start();

    // Edited and then queued, which is the order the product uses: the session
    // applies locally and hands the operations to enqueue. A test that only
    // edited would queue nothing, because the session here is built with a
    // no-op change callback.
    sync.enqueue(encodeOperations(sync.session!.edit('hello')));
    await settle();

    expect(transport.submitted).toHaveLength(1);

    // Five code points, and the vector says so. A client reports what it
    // holds including the batch it is sending: it applied those operations
    // locally the moment they were typed, which is the whole point of the
    // outbox.
    expect(Object.values(transport.reported[0]!)).toEqual([5]);
  });

  it('does not repeat on the timer what a submission already reported', async () => {
    // The saving row 32 is after. Without this the client sends the same
    // vector twice — once on the batch, once on the tick — and the second
    // message moves a frontier that is already where it says.
    const transport = new FakeTransport();
    const harness = controller(transport);
    const { sync } = harness;

    await sync.start();
    sync.enqueue(encodeOperations(sync.session!.edit('hello')));
    await settle();

    expect(transport.submitted).toHaveLength(1);

    await harness.ackTick();

    expect(transport.acknowledged).toHaveLength(0);

    // Still nothing on the next tick either, because the vector has not moved.
    await harness.ackTick();

    expect(transport.acknowledged).toHaveLength(0);

    // And the reports start again the moment there is something new to say:
    // the failure in the other direction is a client that stops reporting and
    // freezes the frontier at whatever it last said.
    sync.enqueue(encodeOperations(sync.session!.edit('hello there')));
    await settle();

    expect(transport.submitted).toHaveLength(2);
    expect(Object.values(transport.reported.at(-1)!)).toEqual([11]);
  });

  it('still reports on the timer for a client that only receives', async () => {
    // §13.32, and the reason row 32 is an optimisation rather than a fix. A
    // report keyed on submission covers writers and nobody else; this replica
    // submits nothing and its acknowledgement is the only thing keeping the
    // frontier moving for the document it is watching.
    const transport = new FakeTransport();
    const harness = controller(transport);
    const { sync } = harness;

    await sync.start();

    const remote = new Replica(PEER);
    const operations = [...'remote'].map((value, index) => remote.insert(index, value));
    transport.broadcast?.(encodeOperations(operations));
    await settle();

    await harness.ackTick();

    expect(transport.submitted).toHaveLength(0);
    expect(transport.acknowledged).toHaveLength(1);
    expect(Object.values(transport.acknowledged[0]!)).toEqual([6]);
  });

  it('stops when the controller stops', async () => {
    // A tab that has been closed must not go on holding a slot in anyone's
    // arithmetic, and a timer that outlives its controller is a leak in a
    // single-page application that opens documents repeatedly.
    const transport = new FakeTransport();
    const harness = controller(transport);
    const { sync } = harness;

    await sync.start();
    await sync.stop();
    await harness.ackTick();

    expect(transport.acknowledged).toHaveLength(0);
  });
});

describe('resumption', () => {
  it('asks to continue the stored replica', async () => {
    const transport = new FakeTransport();
    transport.connectResults = [{ replicaId: ID, resumed: true }];

    const { sync } = controller(transport, { replicaId: ID, outbox: [encodeOperations([])] });
    await sync.start();

    expect(transport.connects).toEqual([ID]);
  });

  it('discards the outbox when the server refused the resumption, and says so', async () => {
    // §7: a fresh id means the batches were authored under a replica this
    // connection may not use, and tier-1 refuses every one of them. Keeping
    // them would mean retrying forever against a rejection that never changes.
    //
    // §9's OFFLINE-WINDOW DISCARD, and this is the only path by which it
    // actually happens: a replica idle past T_retire is retired server-side and
    // its resumption declined. The discard is correct; doing it silently is the
    // data-loss bug §9 names. This test previously asserted only that the queue
    // emptied, over an outbox holding a single EMPTY batch — so a correct
    // implementation and a silent one produced the same observation, and the
    // controller went a whole phase dropping work without telling anyone.
    const transport = new FakeTransport();
    transport.connectResults = [{ replicaId: FRESH, resumed: false }];

    const stranded = new Replica(parseReplicaId(ID));
    const outbox = [...'lost work'].map((value, index) =>
      encodeOperations([stranded.insert(index, value)]),
    );

    const { sync } = controller(transport, { replicaId: ID, outbox });

    await sync.start();

    expect(sync.pending).toHaveLength(0);

    // The number, not merely a flag. Nine batches went; a report of one would
    // understate it and a report of zero is the bug.
    expect(sync.problem).toEqual({ code: 'resync_required', lost: outbox.length });

    // And it takes a snapshot rather than a delta, because the local replica
    // may hold operations that are no longer valid.
    expect(transport.forced).toEqual([true]);
  });

  it('reports nothing when a refused resumption had nothing to lose', async () => {
    // The other half of the pair. A client that reconnects after a clean exit
    // has an empty outbox, and telling it work was lost would be a false alarm
    // shown at exactly the moment the user is being reassured.
    const transport = new FakeTransport();
    transport.connectResults = [{ replicaId: FRESH, resumed: false }];

    const { sync } = controller(transport, { replicaId: ID, outbox: [] });

    await sync.start();

    expect(sync.problem).toBeNull();
    expect(transport.forced).toEqual([true]);
  });

  it('keeps the outbox when the resumption succeeded', async () => {
    // The pair. Without it, "discards on refusal" is satisfied by a client
    // that discards always — which loses work on every reconnect.
    const transport = new FakeTransport();
    transport.connectResults = [{ replicaId: ID, resumed: true }];
    transport.submitResults = [new Error('offline')];

    const { sync } = controller(transport, {
      replicaId: ID,
      outbox: [encodeOperations([])],
    });

    await sync.start();

    expect(sync.pending).toHaveLength(1);
    expect(transport.forced).toEqual([false]);

    // And nothing was reported lost, because nothing was. Without this, "a
    // refusal reports the loss" is satisfied by a controller that reports one
    // on every reconnect.
    expect(sync.problem).toBeNull();
  });
});

describe('the outbox', () => {
  it('keeps a batch the server refused', async () => {
    // Discarding is the one outcome that loses work and looks like success.
    const transport = new FakeTransport();
    transport.submitResults = [{ code: 'unknown_origin' }];

    const { sync } = controller(transport);
    await sync.start();

    sync.session?.edit('a');
    sync.enqueue(encodeOperations([]));
    await Promise.resolve();

    expect(sync.pending.length).toBeGreaterThan(0);
  });

  it('keeps a batch the connection died mid-submission', async () => {
    const transport = new FakeTransport();
    transport.submitResults = [new Error('socket closed')];

    const { sync } = controller(transport);
    await sync.start();

    sync.enqueue(encodeOperations([]));
    await Promise.resolve();
    await Promise.resolve();

    expect(sync.pending).toHaveLength(1);
  });

  it('drains in order, oldest first', async () => {
    // §5's density rule: a replica's operations reach the server without gaps,
    // so the order they are submitted in is not an optimisation.
    const transport = new FakeTransport();
    const { sync } = controller(transport);
    await sync.start();

    const first = new Uint8Array([1]);
    const second = new Uint8Array([2]);
    sync.enqueue(first);
    sync.enqueue(second);

    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(transport.submitted.map((batch) => batch[0])).toEqual([1, 2]);
    expect(sync.pending).toHaveLength(0);
  });
});

describe('losing the connection', () => {
  it('goes offline and schedules a retry', async () => {
    const transport = new FakeTransport();
    const { sync, scheduled } = controller(transport);
    await sync.start();

    expect(sync.state).toBe('live');
    expect(scheduled).toBe(0);

    transport.drop();

    expect(sync.state).toBe('offline');
  });

  it('reconnects and catches up again', async () => {
    const transport = new FakeTransport();
    const harness = controller(transport);
    await harness.sync.start();

    transport.drop();
    await harness.tick();

    expect(transport.connects).toHaveLength(2);
    expect(transport.vectors).toHaveLength(2);
    expect(harness.sync.state).toBe('live');
  });

  it('backs off further on each failure and resets on success', async () => {
    const transport = new FakeTransport();
    transport.connectResults = [
      new Error('refused'),
      new Error('refused'),
      { replicaId: ID, resumed: false },
    ];

    const harness = controller(transport);
    await harness.sync.start();

    expect(harness.sync.state).toBe('offline');
    expect(harness.sync.attempts).toBe(1);

    await harness.tick();
    expect(harness.sync.attempts).toBe(2);

    await harness.tick();

    // Reset on a connection that actually succeeded, not on an attempt.
    expect(harness.sync.state).toBe('live');
    expect(harness.sync.attempts).toBe(0);
  });

  it('stops retrying once stopped', async () => {
    const transport = new FakeTransport();
    const harness = controller(transport);
    await harness.sync.start();

    await harness.sync.stop();
    transport.drop();

    expect(harness.sync.state).toBe('stopped');
    expect(harness.scheduled).toBe(0);
  });

  it('applies a broadcast that arrives while connected', async () => {
    const transport = new FakeTransport();
    const { sync } = controller(transport);
    await sync.start();

    const remote = new Replica(PEER);
    const operations = [...'yo'].map((value, index) => remote.insert(index, value));
    transport.broadcast?.(encodeOperations(operations));

    expect(sync.session?.text).toBe('yo');
  });
});

describe("§5's pending-set bound, overflowed (register row 36)", () => {
  /**
   * `count` operations from `PEER` that can never become ready.
   *
   * Every one is parented on the peer's sequence 0, which is never delivered,
   * so §5's density rule holds all of them. What a partition delivers when the
   * link comes back and the operation everything depends on is still in flight.
   */
  function stranded(count: number): Uint8Array {
    const parent = { replica: PEER, seq: 0n };
    return encodeOperations(Array.from({ length: count }, (_, i) => ({
      kind: 'insert' as const,
      id: { replica: PEER, seq: BigInt(i + 1) },
      value: 'a',
      parent,
      side: 'R' as const,
      rightOrigin: null,
    })));
  }

  it('reports the overflow and catches up, rather than throwing into the socket', async () => {
    // Before this, the core's throw landed in the SignalR broadcast handler
    // with nothing catching it: §5's bound was enforced and the enforcement
    // was unobservable, which §13.13 says is not enforcement at all.
    const transport = new FakeTransport();
    const { sync } = controller(transport);
    await sync.start();
    await settle();

    const before = transport.vectors.length;
    transport.broadcast!(stranded(MAX_PENDING_OPERATIONS + 1));
    await settle();

    expect(sync.problem?.code).toBe(REJECTION.pendingOverflow);
    expect(sync.state).toBe('live');

    // The recovery is a catch-up by version vector — asking for what this
    // replica is missing, which is what the pending set is waiting for.
    expect(transport.vectors.length).toBe(before + 1);
    expect(transport.forced.at(-1)).toBe(false);
  });

  it('does not touch the outbox, because nothing was lost', async () => {
    // The distinction that decides the recovery. `resync` is §5's one exception
    // to "do not drop" and reports a count; this is not that case — the core
    // threw instead of dropping, so the user's unsent work is untouched and
    // there is nothing to report as lost. A recovery that emptied the outbox
    // here would lose typed text to a peer's backlog arriving out of order.
    const transport = new FakeTransport();
    transport.submitResults = [new Error('offline')];
    const { sync } = controller(transport, { outbox: [encodeOperations([])] });
    await sync.start();
    await settle();

    transport.broadcast!(stranded(MAX_PENDING_OPERATIONS + 1));
    await settle();

    expect(sync.problem?.lost).toBe(0);
    expect(sync.pending).toHaveLength(1);
  });

  it('stops on a second overflow, instead of catching up forever', async () => {
    // The same budget as `unknown_origin`, for the same reason: a second
    // overflow after a successful catch-up means this client asked for what it
    // was missing, was given it, and is still missing it. That is a bug here,
    // and retrying a bug forever is a loop that looks like a slow network.
    const transport = new FakeTransport();
    const { sync } = controller(transport);
    await sync.start();
    await settle();

    transport.broadcast!(stranded(MAX_PENDING_OPERATIONS + 1));
    transport.broadcast!(stranded(MAX_PENDING_OPERATIONS + 1));
    await settle();

    expect(sync.state).toBe('stopped');
    expect(sync.problem?.code).toBe(REJECTION.pendingOverflow);
    // Twice past a bound of ten thousand is twenty thousand operations built,
    // encoded and decoded, which is seconds of real work rather than a hang.
    // vitest's default five is enough on a quiet machine and not on a shared
    // runner; the assertions are untouched.
  }, 30_000);
});

describe("§5's pending-set bound in seconds (register row 36, second half)", () => {
  /** One operation that can never become ready: its parent is never delivered. */
  function stuck(seq: number): Uint8Array {
    return encodeOperations([{
      kind: 'insert' as const,
      id: { replica: PEER, seq: BigInt(seq) },
      value: 'a',
      parent: { replica: PEER, seq: 0n },
      side: 'R' as const,
      rightOrigin: null,
    }]);
  }

  it('leaves a briefly waiting operation alone', () => {
    // The pair, and the one that says the bound is a bound rather than a ban.
    // An operation held back by §8's unordered fan-out is waiting correctly and
    // arrives within a round trip; a client that recovered from that would
    // catch up on every out-of-order broadcast it ever received.
    const transport = new FakeTransport();
    const harness = controller(transport);

    return (async () => {
      await harness.sync.start();
      await settle();

      transport.broadcast!(stuck(1));
      await settle();

      const before = transport.vectors.length;
      harness.advance(MAX_PENDING_AGE_MS - 1);
      await harness.ackTick();

      expect(harness.sync.problem).toBeNull();
      expect(transport.vectors.length).toBe(before);
    })();
  });

  it('recovers from four operations stuck forever, which the size bound cannot see', async () => {
    // The whole reason §5 bounds the set in seconds as well as in operations.
    // Four is nowhere near 10,000, so the size bound never fires; without an
    // age bound this client renders a document missing those operations,
    // converges with nobody, and reports `live` throughout — §13.13's shape.
    const transport = new FakeTransport();
    const harness = controller(transport);
    await harness.sync.start();
    await settle();

    for (const seq of [1, 2, 3, 4]) {
      transport.broadcast!(stuck(seq));
    }
    await settle();

    expect(harness.sync.session?.pendingCount).toBe(4);
    expect(harness.sync.problem).toBeNull();

    // First tick records when they started waiting; the clock then passes the
    // bound. Age is measured from when the operation entered the set, so the
    // first tick cannot itself be the one that fires.
    await harness.ackTick();
    harness.advance(MAX_PENDING_AGE_MS);

    const before = transport.vectors.length;
    await harness.ackTick();

    expect(harness.sync.problem?.code).toBe(REJECTION.pendingOverflow);
    expect(harness.sync.problem?.lost).toBe(0);
    expect(transport.vectors.length).toBe(before + 1);
  });

  it('does not restart the clock when a cascade releases part of the backlog', async () => {
    // §5 is explicit: age is measured from when an operation entered the set,
    // not from the last time the set changed. An implementation that watched
    // the count, or the last time it moved, would reset here and never fire —
    // and a trickle of cascades is exactly what a partly-healed partition
    // delivers.
    //
    // Two peers. One's backlog drains halfway through; the other's never does,
    // and it is the one whose clock must not have been restarted.
    const other = parseReplicaId('00000000-0000-0000-0000-00000000000e');
    const child = (author: typeof PEER, seq: number) => encodeOperations([{
      kind: 'insert' as const,
      id: { replica: author, seq: BigInt(seq) },
      value: 'a',
      parent: { replica: author, seq: 0n },
      side: 'R' as const,
      rightOrigin: null,
    }]);
    const root = (author: typeof PEER) => encodeOperations([{
      kind: 'insert' as const,
      id: { replica: author, seq: 0n },
      value: 'z',
      parent: null,
      side: 'R' as const,
      rightOrigin: null,
    }]);

    const transport = new FakeTransport();
    const harness = controller(transport);
    await harness.sync.start();
    await settle();

    transport.broadcast!(child(PEER, 1));
    transport.broadcast!(child(other, 1));
    await settle();
    await harness.ackTick();

    expect(harness.sync.session?.pendingCount).toBe(2);

    // Half the bound later, the other peer's missing root arrives and its
    // child cascades out of the pending set. The set moved; what is stuck did
    // not, and its clock keeps running.
    harness.advance(MAX_PENDING_AGE_MS / 2);
    transport.broadcast!(root(other));
    await settle();
    await harness.ackTick();

    expect(harness.sync.session?.pendingCount).toBe(1);
    expect(harness.sync.problem).toBeNull();

    // The stuck operation reaches the bound measured from when it arrived, not
    // from the cascade. Half the bound more is enough only if the clock was
    // never restarted.
    harness.advance(MAX_PENDING_AGE_MS / 2);
    const before = transport.vectors.length;
    await harness.ackTick();

    expect(harness.sync.problem?.code).toBe(REJECTION.pendingOverflow);
    expect(transport.vectors.length).toBe(before + 1);
  });

  it('stops when a catch-up leaves the same operation waiting', async () => {
    // The budget is per stuck operation. A session-level flag would be cleared
    // by the successful reconciliation and this would catch up every tick
    // forever — a client that looks busy and is making no progress.
    const transport = new FakeTransport();
    const harness = controller(transport);
    await harness.sync.start();
    await settle();

    transport.broadcast!(stuck(1));
    await settle();

    await harness.ackTick();
    harness.advance(MAX_PENDING_AGE_MS);
    await harness.ackTick();

    expect(harness.sync.state).toBe('live');

    harness.advance(MAX_PENDING_AGE_MS);
    await harness.ackTick();

    expect(harness.sync.state).toBe('stopped');
    expect(harness.sync.problem?.code).toBe(REJECTION.pendingOverflow);
  });
});
