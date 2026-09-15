import { Replica } from './replica';
import { parseReplicaId } from './replicaId';
import type { InsertOperation } from './operation';

/**
 * PROJECT_SPEC.md §13.9. Typing left to right makes every character a right
 * child of the previous one, so tree depth equals document length. A recursive
 * in-order traversal overflows the JavaScript stack long before §8's document
 * sizes, and in a browser that takes the tab with it — there is no catching it.
 *
 * The C# side carries the same regression test (ReplicaTests). Both were found
 * by the 100k snapshot size metric, not by any correctness test: every other
 * test in the suite builds documents small enough for recursion to survive.
 *
 * Operations are constructed rather than produced by `insert`, which is linear
 * in document length and would make building the document quadratic. The shape
 * is the one `insert` produces when typing at the end: a right child of the
 * previous element with no right origin, because nothing followed it.
 */
describe('deeply nested documents', () => {
  const DEPTH = 150_000;
  const author = parseReplicaId('00000000-0000-0000-0000-000000000001');

  function forwardChain(count: number): InsertOperation[] {
    const operations: InsertOperation[] = [];
    for (let i = 0; i < count; i++) {
      operations.push({
        kind: 'insert',
        id: { replica: author, seq: BigInt(i) },
        value: 'a',
        parent: i === 0 ? null : { replica: author, seq: BigInt(i - 1) },
        side: 'R',
        rightOrigin: null,
      });
    }
    return operations;
  }

  it('matches what typing produces, at a size recursion survives', () => {
    // Guards the construction above: if it stopped matching real typing, the
    // deep case below would be exercising a shape the editor never creates.
    const typed = new Replica(author);
    const typedOps = [];
    for (let i = 0; i < 20; i++) {
      typedOps.push(typed.insert(i, 'a'));
    }

    expect(typedOps).toEqual(forwardChain(20));
  });

  it('can be traversed without overflowing the stack', () => {
    const replica = new Replica(parseReplicaId('00000000-0000-0000-0000-000000000009'));
    for (const operation of forwardChain(DEPTH)) {
      replica.apply(operation);
    }

    expect(replica.allIds).toHaveLength(DEPTH);
    expect(replica.values).toHaveLength(DEPTH);
  }, 120_000);
});
