import { DocumentSession } from './DocumentSession';
import { replacementBetween } from './diff';
import { Replica, decodeOperations, encodeOperations, parseReplicaId } from '../crdt';

/**
 * The editor's model: local edits apply and render without the server (§9).
 *
 * The vacuity risks, named before these were written:
 *
 * 1. **"The typed text renders" passes against a plain string.** A session that
 *    kept `value` in a field and never touched the replica satisfies every
 *    assertion about text. So the assertions here are on the operations
 *    produced and on the replica state they produce — and, decisively, on a
 *    *second* replica reaching the same text from those operations alone.
 * 2. **A sink that is never called looks identical to one that is, if nothing
 *    checks.** §9 forbids a round trip in the typing path, and the way to test
 *    that is not to observe latency but to give the session a sink that does
 *    nothing at all and require the text to be right anyway.
 * 3. **BMP-only fixtures hide the unit bug again** (§9, 4.1). The diff runs on
 *    code points, and a prefix computed on UTF-16 units splits a surrogate
 *    pair, so at least one case here edits either side of an emoji.
 */

const ID = parseReplicaId('00000000-0000-0000-0000-00000000000a');
const PEER = parseReplicaId('00000000-0000-0000-0000-00000000000b');

function session(): { session: DocumentSession; sent: Uint8Array[] } {
  const sent: Uint8Array[] = [];
  return { session: new DocumentSession(ID, (batch) => sent.push(batch)), sent };
}

/** Replays everything a session sent into an independent replica. */
function peerFrom(sent: readonly Uint8Array[]): Replica {
  const peer = new Replica(PEER);
  for (const batch of sent) {
    for (const operation of decodeOperations(batch)) {
      peer.apply(operation);
    }
  }

  return peer;
}

describe('the change between two versions of the text', () => {
  it('finds an insertion at the end', () => {
    expect(replacementBetween('ab', 'abc')).toEqual({ index: 2, removed: 0, inserted: ['c'] });
  });

  it('finds an insertion in the middle', () => {
    expect(replacementBetween('ac', 'abc')).toEqual({ index: 1, removed: 0, inserted: ['b'] });
  });

  it('finds a deletion', () => {
    expect(replacementBetween('abc', 'ac')).toEqual({ index: 1, removed: 1, inserted: [] });
  });

  it('finds a replaced selection', () => {
    expect(replacementBetween('abcd', 'axd')).toEqual({ index: 1, removed: 2, inserted: ['x'] });
  });

  it('reports no change as null rather than an empty replacement', () => {
    // A React controlled input fires for changes that are not edits — a
    // re-render, a focus, a composition that resolved to the same text — and
    // turning those into zero-length operations would fill the log with them.
    expect(replacementBetween('abc', 'abc')).toBeNull();
  });

  it('counts in code points, so an emoji is one position', () => {
    // On UTF-16 units the prefix here is 2, which is the middle of the
    // surrogate pair: the operations derived from it would insert half an emoji
    // as its own element, and no later edit could repair it.
    const change = replacementBetween('a😀b', 'a😀xb');

    expect(change).toEqual({ index: 2, removed: 0, inserted: ['x'] });
  });

  it('inserts a repeated character after the run rather than inside it', () => {
    // Either answer describes the text correctly, and picking one keeps two
    // clients that made the same edit from building different trees.
    expect(replacementBetween('hello', 'helllo')).toEqual({
      index: 4,
      removed: 0,
      inserted: ['l'],
    });
  });
});

describe('editing without a server', () => {
  it('renders immediately with a sink that does nothing at all', () => {
    // §9's rule, tested as a structural fact rather than as a latency
    // measurement: nothing this session does depends on the sink, so nothing
    // the network does can delay it.
    const offline = new DocumentSession(ID, () => {});

    offline.edit('h');
    offline.edit('he');
    offline.edit('hey');

    expect(offline.text).toBe('hey');
    expect(offline.pendingCount).toBe(0);
  });

  it('produces operations another replica reaches the same text from', () => {
    // The assertion a plain string cannot pass. The peer never sees the text —
    // only the §6 bytes — so this fails against any session that renders
    // without authoring.
    const { session: local, sent } = session();

    local.edit('hello');
    local.edit('hello world');
    local.edit('hello brave world');

    expect(local.text).toBe('hello brave world');
    expect(peerFrom(sent).text).toBe('hello brave world');
  });

  it('deletes a run without skipping every second character', () => {
    // Each delete tombstones what is at `index`, so a run is removed by asking
    // for the same position repeatedly. Walking forward instead deletes
    // alternate characters and leaves the rest — which still looks like a
    // deletion in the text and is a different document underneath.
    const { session: local, sent } = session();

    local.edit('abcdef');
    local.edit('af');

    expect(local.text).toBe('af');
    expect(peerFrom(sent).text).toBe('af');
  });

  it('replaces a selection', () => {
    const { session: local, sent } = session();

    local.edit('the quick fox');
    local.edit('the slow fox');

    expect(peerFrom(sent).text).toBe('the slow fox');
  });

  it('sends nothing when the text did not change', () => {
    const { session: local, sent } = session();

    local.edit('abc');
    const after = sent.length;
    local.edit('abc');

    expect(sent).toHaveLength(after);
  });

  it('handles an emoji edit end to end', () => {
    const { session: local, sent } = session();

    local.edit('a😀b');
    local.edit('a😀!b');
    local.edit('a!b');

    expect(local.text).toBe('a!b');
    expect(peerFrom(sent).text).toBe('a!b');
  });
});

describe('receiving from the server', () => {
  it('merges a remote batch into the local document', () => {
    const { session: local } = session();
    local.edit('local');

    const remote = new Replica(PEER);
    const operations = [...'remote'].map((value, index) => remote.insert(index, value));

    local.receive(encodeOperations(operations));

    // Both replicas' characters are present; the interleaving is FugueMax's
    // business and is asserted in the core's own suite.
    expect([...local.text].filter((c) => 'local'.includes(c)).length).toBeGreaterThan(0);
    expect(local.text).toContain('remote');
    expect(local.pendingCount).toBe(0);
  });
});
