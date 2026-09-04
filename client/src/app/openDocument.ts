import { DocumentSession } from '../editor/DocumentSession';
import { IndexedDbDocumentStore, type DocumentStore } from '../editor/documentStore';
import { SignalRTransport } from '../editor/signalRTransport';
import { SyncController } from '../editor/SyncController';
import { UnsupportedStoreVersion, type PersistedDocument } from '../editor/persistedDocument';
import { parseReplicaId } from '../crdt';
import type { TokenSource } from '../auth/tokenSource';

export interface OpenOptions {
  readonly origin: string;
  readonly documentId: string;
  readonly tokens: TokenSource;
  readonly store?: DocumentStore;
}

export interface OpenDocument {
  readonly sync: SyncController;

  /** What the store held when this opened, if anything. */
  readonly restored: PersistedDocument | null;

  close(): Promise<void>;
}

/**
 * Everything §9's client is, assembled and connected.
 *
 * @remarks
 * <p>
 * The order matters and is the reason this is a function rather than four lines
 * in a component. Local state is loaded first, so the document is on screen
 * before the network is touched; the session is built around the replica id the
 * *server* assigns, because §7 issues it at `negotiate` and a session built
 * earlier authors under an id the server never gave out; and the outbox is
 * handed to the controller at construction, because batches that were unsent
 * when the tab closed are the ones with nothing else holding them.
 * </p><p>
 * A store written by a version this build does not understand is discarded and
 * resynced rather than parsed hopefully (§6, §9) — which is safe precisely
 * because the server is authoritative, and is why that failure is caught here
 * instead of propagating as a load error.
 * </p>
 */
export async function openDocument(options: OpenOptions): Promise<OpenDocument> {
  const store = options.store ?? new IndexedDbDocumentStore();

  let restored: PersistedDocument | null = null;
  try {
    restored = await store.load(options.documentId);
  } catch (error) {
    if (!(error instanceof UnsupportedStoreVersion)) {
      throw error;
    }

    await store.clear(options.documentId);
  }

  const transport = new SignalRTransport({
    baseUrl: options.origin,
    documentId: options.documentId,
    tokens: options.tokens,
  });

  const sync: SyncController = new SyncController(
    (replicaId) => {
      const id = parseReplicaId(replicaId);
      const sink = (batch: Uint8Array) => sync.enqueue(batch);

      // Restored only when the server agreed to continue this replica. A
      // snapshot authored under one id, reopened under another, would have this
      // client claiming operations it did not write (§7).
      return restored !== null && restored.replicaId === replicaId
        ? DocumentSession.restore(id, restored.snapshot, sink)
        : new DocumentSession(id, sink);
    },
    transport,
    restored?.replicaId ?? null,
    restored?.outbox ?? [],
  );

  // Persisted on every change, local or remote. §9's offline window counts from
  // the last accepted submission, so the timestamp moves when the outbox
  // drains and not when a key is pressed.
  let lastSyncedAt = restored?.lastSyncedAt ?? null;
  let unsent = sync.pending.length;

  sync.subscribe(() => {
    const session = sync.session;
    if (session === null) {
      return;
    }

    if (sync.pending.length < unsent) {
      lastSyncedAt = Date.now();
    }

    unsent = sync.pending.length;

    void store.save(options.documentId, {
      replicaId: session.replicaId,
      snapshot: session.snapshot,
      outbox: sync.pending,
      lastSyncedAt,
    });
  });

  await sync.start();

  return {
    sync,
    restored,
    close: () => sync.stop(),
  };
}
