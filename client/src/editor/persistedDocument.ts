import { BinaryFormatError } from '../crdt';

/**
 * The schema version of the stored record.
 *
 * @remarks
 * Separate from §6's format version, which is inside the payloads. Two things
 * can change independently: how §6 encodes a snapshot, and what this record
 * holds around it. Conflating them would mean bumping the wire format to add a
 * field to local storage.
 */
export const STORE_VERSION = 1;

/**
 * What a client keeps locally between sessions (§9).
 *
 * @param replicaId - The replica this state was authored under (§7). A reload
 * asks to resume it, and the outbox is only submittable under a binding for it.
 * @param snapshot - The local replica, in §6's snapshot encoding.
 * @param outbox - Batches authored and not yet accepted, in §6's batch
 * encoding, oldest first.
 * @param lastSyncedAt - When the server last accepted something, as epoch
 * milliseconds, or null if it never has. §9's offline window counts from here.
 */
export interface PersistedDocument {
  readonly replicaId: string;
  readonly snapshot: Uint8Array;
  readonly outbox: readonly Uint8Array[];
  readonly lastSyncedAt: number | null;
}

/** Refused rather than guessed at (§6, §9). */
export class UnsupportedStoreVersion extends BinaryFormatError {
  readonly found: unknown;
  readonly supported: number;

  constructor(found: unknown, supported: number) {
    super(
      `Stored document is version ${String(found)}; this build understands ${supported}. `
      + 'Local state must be discarded and resynced.',
    );
    this.name = 'UnsupportedStoreVersion';
    this.found = found;
    this.supported = supported;
  }
}

/** The shape as it is written, before IndexedDB structured-clones it. */
interface StoredShape {
  v: number;
  replicaId: string;
  snapshot: Uint8Array;
  outbox: Uint8Array[];
  lastSyncedAt: number | null;
}

/** Prepares a record for storage. */
export function toStored(document: PersistedDocument): StoredShape {
  return {
    v: STORE_VERSION,
    replicaId: document.replicaId,
    snapshot: document.snapshot,
    outbox: [...document.outbox],
    lastSyncedAt: document.lastSyncedAt,
  };
}

/**
 * Reads a stored record, refusing anything this build does not understand.
 *
 * @throws UnsupportedStoreVersion when the record was written by another build.
 * @remarks
 * §6 calls rejecting an unrecognised version the one rule with no exceptions,
 * and it applies here with more force than anywhere else: the browser is the
 * place where a build you did not deploy reads state you did write, because a
 * user holds whatever version they last loaded and IndexedDB survives the
 * upgrade.
 *
 * A best-effort parse produces a replica that is subtly wrong and then
 * *submits operations derived from it* — corruption that leaves the client's
 * own screen looking fine. Rejecting loses unsent work and says so, which is a
 * worse afternoon and a recoverable one.
 *
 * The field checks are part of the same rule. A record whose version matches but
 * whose payload is missing is not a version this build understands either, and
 * "v is 1 so the rest must be fine" is how a partially written record becomes a
 * document.
 */
export function fromStored(value: unknown): PersistedDocument {
  if (typeof value !== 'object' || value === null) {
    throw new UnsupportedStoreVersion(value, STORE_VERSION);
  }

  const record = value as Partial<StoredShape>;

  if (record.v !== STORE_VERSION) {
    throw new UnsupportedStoreVersion(record.v, STORE_VERSION);
  }

  const snapshot = asBytes(record.snapshot);
  const outbox = Array.isArray(record.outbox)
    ? record.outbox.map(asBytes)
    : null;

  if (
    typeof record.replicaId !== 'string'
    || snapshot === null
    || outbox === null
    || outbox.some((batch) => batch === null)
    || !(record.lastSyncedAt === null || typeof record.lastSyncedAt === 'number')
  ) {
    throw new UnsupportedStoreVersion(record.v, STORE_VERSION);
  }

  return {
    replicaId: record.replicaId,
    snapshot,
    outbox: outbox as Uint8Array[],
    lastSyncedAt: record.lastSyncedAt,
  };
}

/**
 * Reads a stored value as bytes in *this* realm.
 *
 * @returns The bytes, or null if the value is not a byte sequence at all.
 * @remarks
 * Not `instanceof Uint8Array`. A value that has been through the structured
 * clone algorithm may come back from another realm, where `Uint8Array` is a
 * different constructor and `instanceof` is false for data that is perfectly
 * good — which is how a strict check ends up telling a user their document is
 * corrupt and discarding their unsent work.
 *
 * Found by the store's own tests: every valid record failed to load, and the
 * error said "version 1; this build understands 1".
 *
 * Anything that views a buffer is accepted and copied into a `Uint8Array` here,
 * so what leaves this function is always usable by §6's decoder. A string, a
 * number or an array of numbers still fails — this is a widening of what counts
 * as bytes, not an abandonment of the check.
 */
function asBytes(value: unknown): Uint8Array | null {
  if (value instanceof Uint8Array) {
    return value;
  }

  if (ArrayBuffer.isView(value)) {
    return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
  }

  if (value instanceof ArrayBuffer) {
    return new Uint8Array(value);
  }

  return null;
}
