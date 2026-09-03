import 'fake-indexeddb/auto';

import { IndexedDbDocumentStore } from './documentStore';
import {
  STORE_VERSION,
  UnsupportedStoreVersion,
  fromStored,
  toStored,
  type PersistedDocument,
} from './persistedDocument';

/**
 * Local persistence (§9).
 *
 * The vacuity risks, named before these were written:
 *
 * 1. **A round trip through the same object proves nothing.** A store that
 *    returned the argument it was handed passes any save-then-load assertion.
 *    So every read here goes through a *fresh* store instance — a new page
 *    load, as far as the code is concerned — and the decisive test reads with
 *    an object that never saw the write.
 * 2. **"It rejects a bad version" is satisfied by rejecting everything.** Each
 *    refusal is paired with the record that must still load.
 * 3. **A version check that passes on shape alone is not a version check**, and
 *    a shape check that passes on version alone is not one either. Both
 *    directions are asserted, because a partially written record with the right
 *    `v` is exactly what a crash mid-save leaves behind.
 */

function record(overrides: Partial<PersistedDocument> = {}): PersistedDocument {
  return {
    replicaId: '00000000-0000-0000-0000-00000000000a',
    snapshot: new Uint8Array([67, 82, 68, 84, 1, 1]),
    outbox: [new Uint8Array([67, 82, 68, 84, 1, 2])],
    lastSyncedAt: 1_700_000_000_000,
    ...overrides,
  };
}

let databases = 0;

/** A store over a database no other test shares. */
function store(name?: string): IndexedDbDocumentStore {
  return new IndexedDbDocumentStore(name ?? `test-${++databases}`);
}

describe('the stored record', () => {
  it('round-trips through its own encoding', () => {
    const original = record();

    expect(fromStored(toStored(original))).toEqual(original);
  });

  it('refuses a version this build does not understand', () => {
    const future = { ...toStored(record()), v: STORE_VERSION + 1 };

    expect(() => fromStored(future)).toThrow(UnsupportedStoreVersion);

    // The pair: the current version still loads, so "refuses everything"
    // cannot pass.
    expect(() => fromStored(toStored(record()))).not.toThrow();
  });

  it('refuses a record whose version is right and whose payload is missing', () => {
    // What a crash mid-save leaves behind. "v is 1, so the rest must be fine"
    // is how a half-written record becomes a document — and the operations
    // derived from it get submitted.
    const truncated = { ...toStored(record()), snapshot: undefined };

    expect(() => fromStored(truncated)).toThrow(UnsupportedStoreVersion);
  });

  it('refuses an outbox holding something that is not a batch', () => {
    const wrong = { ...toStored(record()), outbox: [new Uint8Array([1]), 'nonsense'] };

    expect(() => fromStored(wrong)).toThrow(UnsupportedStoreVersion);
  });

  it('refuses a value that is not a record at all', () => {
    expect(() => fromStored(null)).toThrow(UnsupportedStoreVersion);
    expect(() => fromStored('hello')).toThrow(UnsupportedStoreVersion);
  });

  it('accepts a document that has never synced', () => {
    // The first session, before the server has accepted anything. §9's offline
    // window has to cope with having no start point, and a null that failed
    // validation would make a brand-new document unloadable.
    const fresh = record({ lastSyncedAt: null });

    expect(fromStored(toStored(fresh))).toEqual(fresh);
  });
});

describe('IndexedDB', () => {
  it('survives a reload, read by an instance that never saw the write', async () => {
    // The assertion an in-memory fake cannot pass.
    const name = `reload-${++databases}`;
    const original = record();

    await store(name).save('doc-1', original);

    const reloaded = await store(name).load('doc-1');

    expect(reloaded).not.toBeNull();
    expect(reloaded?.replicaId).toBe(original.replicaId);
    expect([...(reloaded?.snapshot ?? [])]).toEqual([...original.snapshot]);
    expect(reloaded?.outbox).toHaveLength(1);
    expect([...(reloaded?.outbox[0] ?? [])]).toEqual([...original.outbox[0]!]);
    expect(reloaded?.lastSyncedAt).toBe(original.lastSyncedAt);
  });

  it('keeps the outbox as bytes rather than as something JSON-shaped', async () => {
    // §6 stays the encoding. A store that stringified would round-trip the
    // numbers and lose the type, and the first decode would fail somewhere far
    // from here.
    const name = `bytes-${++databases}`;
    await store(name).save('doc-1', record());

    const reloaded = await store(name).load('doc-1');

    expect(reloaded?.snapshot).toBeInstanceOf(Uint8Array);
    expect(reloaded?.outbox[0]).toBeInstanceOf(Uint8Array);
  });

  it('returns null for a document it has never stored', async () => {
    expect(await store().load('absent')).toBeNull();
  });

  it('replaces rather than accumulating', async () => {
    const name = `replace-${++databases}`;
    const one = store(name);

    await one.save('doc-1', record({ lastSyncedAt: 1 }));
    await one.save('doc-1', record({ lastSyncedAt: 2, outbox: [] }));

    const reloaded = await store(name).load('doc-1');

    expect(reloaded?.lastSyncedAt).toBe(2);
    expect(reloaded?.outbox).toHaveLength(0);
  });

  it('clears one document without touching another', async () => {
    // A resync discards the document it was told to (§9). Clearing the store
    // would take away work on documents that were fine.
    const name = `clear-${++databases}`;
    const one = store(name);

    await one.save('keep', record());
    await one.save('drop', record());
    await one.clear('drop');

    const fresh = store(name);
    expect(await fresh.load('drop')).toBeNull();
    expect(await fresh.load('keep')).not.toBeNull();
  });

  it('reports a write the browser threw away', async () => {
    // §13.15, and the reason `save` waits for the transaction rather than for
    // the request. A `put` reporting success only means the request was
    // accepted; the transaction can still abort — quota, a concurrent version
    // change — and the write never lands. Both versions look identical on the
    // happy path, which is every other test in this file, so the abort is
    // injected here deliberately.
    //
    // Without the wait, `save` resolves and the caller believes the document is
    // safe. That is the loss nobody notices until a reload.
    const name = `abort-${++databases}`;
    const one = store(name);
    await one.save('warm-up', record());

    // Captured unbound on purpose: it is put back on the prototype afterwards,
    // and binding it would install a method permanently attached to whichever
    // store happened to be first.
    // eslint-disable-next-line @typescript-eslint/unbound-method
    const put = IDBObjectStore.prototype.put;
    IDBObjectStore.prototype.put = function aborting(
      this: IDBObjectStore,
      ...args: Parameters<typeof put>
    ): IDBRequest<IDBValidKey> {
      const operation = put.apply(this, args);
      this.transaction.abort();
      return operation;
    };

    try {
      await expect(one.save('doc-1', record())).rejects.toThrow();
    } finally {
      IDBObjectStore.prototype.put = put;
    }

    expect(await store(name).load('doc-1')).toBeNull();
  });

  it('refuses to load a record written by a newer build', async () => {
    // The rule that matters most in a browser: the build that wrote this is
    // not the build reading it, because the user holds whatever they last
    // loaded and IndexedDB survives the upgrade.
    const name = `future-${++databases}`;
    const one = store(name);
    await one.save('doc-1', record());

    // Written behind the store's back, the way a newer deployment would.
    const database = await new Promise<IDBDatabase>((resolve, reject) => {
      const opening = indexedDB.open(name, 1);
      opening.onsuccess = () => resolve(opening.result);
      opening.onerror = () => reject(opening.error ?? new Error('open failed'));
    });

    await new Promise<void>((resolve, reject) => {
      const transaction = database.transaction('documents', 'readwrite');
      transaction.objectStore('documents').put(
        { ...toStored(record()), v: STORE_VERSION + 1 },
        'doc-1',
      );
      transaction.oncomplete = () => resolve();
      transaction.onerror = () => reject(transaction.error ?? new Error('write failed'));
    });

    await expect(store(name).load('doc-1')).rejects.toThrow(UnsupportedStoreVersion);
  });
});
