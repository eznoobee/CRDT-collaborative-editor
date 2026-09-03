import { Replica } from './replica';
import { parseReplicaId, type ReplicaId } from './replicaId';
import type { Operation } from './operation';

/**
 * The coupling between readiness and duplicate detection (§5).
 *
 * The C# suite carries the same properties. They are stated on both sides
 * because §5 is the shared definition and a divergence here would be a
 * divergence in what "already applied" means — which is the one thing two
 * replicas cannot disagree about and still converge.
 */
function R(n: number): ReplicaId {
  return parseReplicaId(`00000000-0000-0000-0000-${n.toString(16).padStart(12, '0')}`);
}

function insert(
  author: ReplicaId,
  seq: bigint,
  value: string,
  parent: { replica: ReplicaId; seq: bigint } | null,
): Operation {
  return { kind: 'insert', id: { replica: author, seq }, value, parent, side: 'R', rightOrigin: null };
}

describe('causal readiness', () => {
  it('buffers a sequence gap even when the structural dependencies are present', () => {
    // Readiness requires the exact next sequence number, not merely that the
    // dependencies exist. That is what makes the watermark a complete test for
    // "already applied": a replica's operations are applied strictly in order,
    // so the applied set never has a per-replica gap.
    //
    // The third operation depends structurally on the root alone, so nothing
    // but the density rule can be holding it back.
    const author = R(1);
    const first = { replica: author, seq: 0n };

    const replica = new Replica(R(2));
    replica.apply(insert(author, 0n, 'a', null));
    replica.apply(insert(author, 2n, 'c', null));

    expect(replica.pendingCount).toBe(1);
    expect(replica.text).not.toContain('c');

    replica.apply(insert(author, 1n, 'b', first));

    expect(replica.pendingCount).toBe(0);
    expect(replica.text).toContain('c');
  });

  it('applies every operation below the watermark, even after a cascade', () => {
    const author = R(1);
    const replica = new Replica(R(2));

    const operations: Operation[] = [];
    let previous: { replica: ReplicaId; seq: bigint } | null = null;
    for (let i = 0; i < 8; i++) {
      const id = { replica: author, seq: BigInt(i) };
      operations.push(insert(author, BigInt(i), 'a', previous));
      previous = id;
    }

    // Reversed, so all but one spend time in the pending set: the property has
    // to hold after a cascade, not only after an in-order delivery.
    for (const operation of [...operations].reverse()) {
      replica.apply(operation);
    }

    expect(replica.pendingCount).toBe(0);
    expect(replica.versionVector.get(`00000000-0000-0000-0000-000000000001`)).toBe(8n);
  });

  it('counts a duplicate rather than silently absorbing it', () => {
    // §5: duplicate delivery is guaranteed, so the count is never zero and is
    // not itself a problem. Its rate is the signal, and the signal does not
    // exist unless the drop is counted.
    const author = R(1);
    const replica = new Replica(R(2));
    const operation = insert(author, 0n, 'a', null);

    replica.apply(operation);
    expect(replica.duplicatesDropped).toBe(0);

    replica.apply(operation);
    replica.apply(operation);

    expect(replica.duplicatesDropped).toBe(2);
    expect(replica.text).toBe('a');
  });

  it('counts a duplicate that arrives while the original is still buffered', () => {
    // The duplicate the watermark cannot see: the operation has not been
    // applied, so the watermark says nothing about it, and the pending set is
    // what has to recognise it. Buffering it twice applies it twice when the
    // gap closes.
    const author = R(1);
    const first = { replica: author, seq: 0n };

    const replica = new Replica(R(2));
    replica.apply(insert(author, 0n, 'a', null));
    replica.apply(insert(author, 2n, 'c', null));
    replica.apply(insert(author, 2n, 'c', null));

    expect(replica.pendingCount).toBe(1);
    expect(replica.duplicatesDropped).toBe(1);

    replica.apply(insert(author, 1n, 'b', first));

    expect(replica.pendingCount).toBe(0);
    expect([...replica.text].filter((c) => c === 'c')).toHaveLength(1);
  });
});
