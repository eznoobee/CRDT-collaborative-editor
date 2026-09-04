import { DocumentSession } from './DocumentSession';
import { REJECTION, recoveryFor } from './rejections';
import { SyncController, type CatchUpOutcome, type Session, type SubmitOutcome, type Transport } from './SyncController';
import { encodeOperations, encodeSnapshot, parseReplicaId, Replica } from '../crdt';

/**
 * One recovery per refusal (§9, §13.13).
 *
 * The vacuity risks, named before these were written:
 *
 * 1. **"It handled the rejection" is satisfied by doing nothing.** Every code
 *    leaves the client in *some* state, so a controller with an empty `switch`
 *    passes any test that only checks it did not crash. Each case here asserts
 *    a specific observable change — the state, the read-only flag, the outbox,
 *    or the reported problem — and no two codes produce the same one.
 * 2. **A table test that maps codes to strings tests the table, not the
 *    client.** So the recoveries are driven through the controller against a
 *    server that actually refuses.
 * 3. **A retry budget is invisible if nothing retries twice.** The
 *    unknown_origin case refuses twice deliberately, because a budget that is
 *    never exhausted is a budget that might not exist.
 */

const ID = '00000000-0000-0000-0000-00000000000a';
const PEER = parseReplicaId('00000000-0000-0000-0000-00000000000b');

class RefusingTransport implements Transport {
  broadcast: ((operations: Uint8Array) => void) | null = null;
  closed: (() => void) | null = null;

  readonly forced: boolean[] = [];
  submits = 0;
  catchUps = 0;

  /** Codes to answer submissions with; the last one repeats. */
  codes: (string | null)[] = [null];
  catchUpResult: CatchUpOutcome = { code: null, snapshot: null, operations: encodeOperations([]) };

  connect(): Promise<Session> {
    return Promise.resolve({ replicaId: ID, resumed: false });
  }

  submit(): Promise<SubmitOutcome> {
    this.submits++;
    const code = this.codes.length > 1 ? this.codes.shift()! : this.codes[0]!;
    return Promise.resolve({ code });
  }

  catchUp(_known: Record<string, number>, forceSnapshot: boolean): Promise<CatchUpOutcome> {
    this.catchUps++;
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
}

function harness(transport: RefusingTransport) {
  const pending: (() => void)[] = [];
  const sync = new SyncController(
    (replicaId) => new DocumentSession(parseReplicaId(replicaId), () => {}),
    transport,
    null,
    [],
    {
      random: () => 0.5,
      schedule: (run) => pending.push(run),
    },
  );

  return { sync, scheduled: () => pending.length };
}

/** Lets every queued microtask and the drain loop finish. */
async function settle(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe('the recovery table', () => {
  it('gives every code a recovery, including ones no server sends yet', () => {
    for (const code of Object.values(REJECTION)) {
      expect(recoveryFor(code)).toBeDefined();
    }

    // resync_required is specified before anything emits it, so Phase 7's
    // server side is written against a stated contract.
    expect(recoveryFor(REJECTION.resyncRequired)).toBe('resync');
  });

  it('stops on a code it has never heard of', () => {
    // The safe assumption about a refusal you do not understand is that
    // repeating it will not help.
    expect(recoveryFor('something_a_future_server_invented')).toBe('stop');
  });

  it('does not resync on a disagreement about this client’s own identity', () => {
    // A sequence gap or a replica mismatch means this client's idea of itself
    // disagrees with the server's, which is a bug here rather than a state to
    // recover from. Resyncing would paper over it, lose the evidence, and
    // rebuild the same batch to be refused again.
    expect(recoveryFor(REJECTION.sequenceGap)).toBe('stop');
    expect(recoveryFor(REJECTION.replicaMismatch)).toBe('stop');
  });
});

describe('acting on a refusal', () => {
  it('catches up and retries once on unknown_origin, then gives up', async () => {
    const transport = new RefusingTransport();
    transport.codes = [REJECTION.unknownOrigin, REJECTION.unknownOrigin];

    const { sync } = harness(transport);
    await sync.start();

    const catchUpsBefore = transport.catchUps;
    sync.enqueue(new Uint8Array([1, 2, 3]));
    await settle();

    // Reconciled and tried again — which is the recovery.
    expect(transport.catchUps).toBe(catchUpsBefore + 1);
    expect(transport.submits).toBe(2);

    // And stopped rather than looping: a second refusal after a successful
    // catch-up is a bug here, and retrying a bug forever looks like a network
    // problem.
    expect(sync.problem?.code).toBe(REJECTION.unknownOrigin);
    expect(transport.submits).toBe(2);
  });

  it('succeeds on the retry when the catch-up was what was missing', async () => {
    // The pair. Without it, "retries once" is satisfied by a client that always
    // fails on the second attempt.
    const transport = new RefusingTransport();
    transport.codes = [REJECTION.unknownOrigin, null];

    const { sync } = harness(transport);
    await sync.start();

    sync.enqueue(new Uint8Array([1]));
    await settle();

    expect(sync.pending).toHaveLength(0);
    expect(sync.problem).toBeNull();
  });

  it('drops to read-only on forbidden, keeps receiving, keeps the work', async () => {
    const transport = new RefusingTransport();
    transport.codes = [REJECTION.forbidden];

    const { sync } = harness(transport);
    await sync.start();

    sync.enqueue(new Uint8Array([1]));
    await settle();

    expect(sync.readOnly).toBe(true);
    expect(sync.problem?.code).toBe(REJECTION.forbidden);

    // Still connected and still receiving: a demoted collaborator watches.
    expect(sync.state).toBe('live');

    const remote = new Replica(PEER);
    transport.broadcast?.(encodeOperations([remote.insert(0, 'x')]));
    expect(sync.session?.text).toBe('x');

    // The work is kept, because the role may be restored.
    expect(sync.pending).toHaveLength(1);
  });

  it('resyncs on resync_required and reports what was lost', async () => {
    // §5's one exception to "do not drop". The elements this batch refers to
    // are past the GC watermark and cannot be reconstructed, so the work is
    // gone — and the number is reported rather than the queue quietly emptying.
    const transport = new RefusingTransport();
    transport.codes = [REJECTION.resyncRequired];

    const server = new Replica(PEER);
    [...'fresh'].forEach((value, index) => server.insert(index, value));
    transport.catchUpResult = {
      code: null,
      snapshot: encodeSnapshot(server.export(), server.versionVectorEntries),
      operations: encodeOperations([]),
    };

    const { sync } = harness(transport);
    await sync.start();

    sync.enqueue(new Uint8Array([1]));
    sync.enqueue(new Uint8Array([2]));
    await settle();

    expect(sync.pending).toHaveLength(0);
    expect(sync.problem?.code).toBe(REJECTION.resyncRequired);
    expect(sync.problem?.lost).toBe(2);

    // And it took a snapshot rather than a delta, because local state is what
    // was invalid.
    expect(transport.forced.at(-1)).toBe(true);
    expect(sync.session?.text).toBe('fresh');
  });

  it('reconnects on too_many_replicas without losing the outbox', async () => {
    // A replica slot frees up on its own; this is not a reason to throw work
    // away or to stop.
    const transport = new RefusingTransport();
    transport.codes = [REJECTION.tooManyReplicas];

    const { sync, scheduled } = harness(transport);
    await sync.start();

    sync.enqueue(new Uint8Array([1]));
    await settle();

    expect(sync.state).toBe('offline');
    expect(scheduled()).toBe(1);
    expect(sync.pending).toHaveLength(1);
    expect(sync.problem?.code).toBe(REJECTION.tooManyReplicas);
  });

  it('stops on not_found and keeps the outbox as evidence', async () => {
    // The document is gone or access was revoked. Retrying cannot help, and
    // discarding the queue would destroy the only record of what was unsent.
    const transport = new RefusingTransport();
    transport.codes = [REJECTION.notFound];

    const { sync, scheduled } = harness(transport);
    await sync.start();

    sync.enqueue(new Uint8Array([1]));
    await settle();

    expect(sync.state).toBe('stopped');
    expect(scheduled()).toBe(0);
    expect(sync.pending).toHaveLength(1);
    expect(sync.problem?.code).toBe(REJECTION.notFound);
  });

  it('stops on malformed, which is a bug in this client', async () => {
    const transport = new RefusingTransport();
    transport.codes = [REJECTION.malformed];

    const { sync } = harness(transport);
    await sync.start();

    sync.enqueue(new Uint8Array([1]));
    await settle();

    expect(sync.state).toBe('stopped');
    expect(sync.problem?.code).toBe(REJECTION.malformed);
    expect(sync.pending).toHaveLength(1);
  });

  it('leaves no problem set when nothing was refused', async () => {
    // The pair for all of the above: a controller that always reported a
    // problem would satisfy every assertion in this file.
    const transport = new RefusingTransport();
    const { sync } = harness(transport);
    await sync.start();

    sync.enqueue(new Uint8Array([1]));
    await settle();

    expect(sync.problem).toBeNull();
    expect(sync.readOnly).toBe(false);
    expect(sync.state).toBe('live');
    expect(sync.pending).toHaveLength(0);
  });
});
