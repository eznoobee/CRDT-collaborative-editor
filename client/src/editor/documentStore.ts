import { fromStored, toStored, type PersistedDocument } from './persistedDocument';

/** Where a client keeps its local state between sessions (§9). */
export interface DocumentStore {
  /** Reads one document's state, or null if none is stored. */
  load(documentId: string): Promise<PersistedDocument | null>;

  /** Writes one document's state, replacing whatever was there. */
  save(documentId: string, document: PersistedDocument): Promise<void>;

  /** Removes one document's state, as a resync does (§9). */
  clear(documentId: string): Promise<void>;
}

const DATABASE = 'editor';
const OBJECT_STORE = 'documents';

/**
 * IndexedDB, holding §6 binary (§9).
 *
 * @remarks
 * <p>
 * Both payloads are §6 byte strings: the replica as a snapshot body, the outbox
 * as batch bodies. Not a JSON shape invented for the browser — §6 is the sole
 * authoritative encoding and a second one acquires canonical-form rules of its
 * own, which is where §13.11's bug came from. IndexedDB stores `Uint8Array`
 * natively through the structured clone algorithm, so this costs nothing.
 * </p><p>
 * `localStorage` would have been less code and is the wrong tool twice over: it
 * is synchronous, so writing a large document blocks the frame the user is
 * typing into, and it stores strings, so §6 bytes would have to be base64'd back
 * to a third of extra size.
 * </p>
 */
export class IndexedDbDocumentStore implements DocumentStore {
  private readonly name: string;
  private opened: Promise<IDBDatabase> | null = null;

  /**
   * @param name - Database name. Overridable so tests do not share one.
   */
  constructor(name: string = DATABASE) {
    this.name = name;
  }

  async load(documentId: string): Promise<PersistedDocument | null> {
    const database = await this.open();

    const raw = await request<unknown>(
      database.transaction(OBJECT_STORE, 'readonly').objectStore(OBJECT_STORE).get(documentId),
    );

    if (raw === undefined) {
      return null;
    }

    // Throws on a record this build does not understand, rather than salvaging
    // what it recognises (§6, §9). The caller's recovery is to clear and
    // resync, which loses unsent work and says so.
    return fromStored(raw);
  }

  async save(documentId: string, document: PersistedDocument): Promise<void> {
    const database = await this.open();
    const transaction = database.transaction(OBJECT_STORE, 'readwrite');

    await request(transaction.objectStore(OBJECT_STORE).put(toStored(document), documentId));
    await completed(transaction);
  }

  async clear(documentId: string): Promise<void> {
    const database = await this.open();
    const transaction = database.transaction(OBJECT_STORE, 'readwrite');

    await request(transaction.objectStore(OBJECT_STORE).delete(documentId));
    await completed(transaction);
  }

  private open(): Promise<IDBDatabase> {
    // Cached, because opening on every call serialises behind the upgrade
    // handler and turns each keystroke's save into a database open.
    this.opened ??= new Promise<IDBDatabase>((resolve, reject) => {
      const opening = indexedDB.open(this.name, 1);

      opening.onupgradeneeded = () => {
        if (!opening.result.objectStoreNames.contains(OBJECT_STORE)) {
          opening.result.createObjectStore(OBJECT_STORE);
        }
      };

      opening.onsuccess = () => resolve(opening.result);
      opening.onerror = () => reject(opening.error ?? new Error('IndexedDB refused to open.'));
    });

    return this.opened;
  }
}

function request<T>(operation: IDBRequest<T>): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    operation.onsuccess = () => resolve(operation.result);
    operation.onerror = () => reject(operation.error ?? new Error('IndexedDB request failed.'));
  });
}

/**
 * Waits for a write transaction to commit.
 *
 * @remarks
 * A `put` reporting success only means the request was accepted; the
 * transaction can still abort — quota, a concurrent version change — and the
 * write never lands. Resolving on the request alone gives a save that reports
 * success for data the browser then discards, which is exactly the kind of loss
 * nobody notices until a reload.
 */
function completed(transaction: IDBTransaction): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    transaction.oncomplete = () => resolve();
    transaction.onabort = () => reject(transaction.error ?? new Error('IndexedDB write aborted.'));
    transaction.onerror = () => reject(transaction.error ?? new Error('IndexedDB write failed.'));
  });
}
