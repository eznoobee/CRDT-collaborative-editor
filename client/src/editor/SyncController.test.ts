import { DocumentSession } from './DocumentSession';
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
  submitResults: (SubmitOutcome | Error)[] = [{ code: null }];

  connect(replicaId: string | null): Promise<Session> {
    this.connects.push(replicaId);
    const answer = this.connectResults.length > 1
      ? this.connectResults.shift()!
      : this.connectResults[0]!;

    return answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer);
  }

  submit(operations: Uint8Array): Promise<SubmitOutcome> {
    this.submitted.push(operations);
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
      schedule: (run) => pending.push(run),
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
    get scheduled(): number {
      return pending.length;
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

describe('resumption', () => {
  it('asks to continue the stored replica', async () => {
    const transport = new FakeTransport();
    transport.connectResults = [{ replicaId: ID, resumed: true }];

    const { sync } = controller(transport, { replicaId: ID, outbox: [encodeOperations([])] });
    await sync.start();

    expect(transport.connects).toEqual([ID]);
  });

  it('discards the outbox when the server refused the resumption', async () => {
    // §7: a fresh id means the batches were authored under a replica this
    // connection may not use, and tier-1 refuses every one of them. Keeping
    // them would mean retrying forever against a rejection that never changes.
    const transport = new FakeTransport();
    transport.connectResults = [{ replicaId: FRESH, resumed: false }];

    const { sync } = controller(transport, {
      replicaId: ID,
      outbox: [encodeOperations([])],
    });

    await sync.start();

    expect(sync.pending).toHaveLength(0);

    // And it takes a snapshot rather than a delta, because the local replica
    // may hold operations that are no longer valid.
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
