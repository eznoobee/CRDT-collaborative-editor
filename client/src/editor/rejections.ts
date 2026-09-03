/**
 * The codes the server can refuse with, and what a client does about each (§9).
 *
 * @remarks
 * Kept as one table rather than scattered through the controller, because §9's
 * rule is that *every* code has a defined recovery. A `switch` with a `default`
 * that logs is how a code acquires the recovery "nothing happens", and a client
 * that swallows a refusal appears to work and silently is not (§13.13).
 */

/** Refusals from §7's ingest validation and the hub. */
export const REJECTION = {
  notFound: 'not_found',
  forbidden: 'forbidden',
  unauthenticated: 'unauthenticated',
  malformed: 'malformed',
  messageTooLarge: 'message_too_large',
  batchTooLarge: 'batch_too_large',
  runTooLong: 'run_too_long',
  replicaMismatch: 'replica_mismatch',
  sequenceGap: 'sequence_gap',
  unknownOrigin: 'unknown_origin',
  documentFull: 'document_full',
  tooManyReplicas: 'too_many_replicas',

  /**
   * §5's GC watermark: the referenced id is at or below it and is gone.
   *
   * Specified before anything emits it. The server side arrives with GC in
   * Phase 7; defining the client contract now means that implementation is
   * written against a stated shape rather than inventing one late, when the
   * pressure will be to make it whatever the client already tolerates.
   */
  resyncRequired: 'resync_required',
} as const;

/** What the controller does with a refusal. */
export type Recovery =
  /** Reconcile with the server, then submit the same batch once more. */
  | 'catch-up-and-retry'
  /** Throw local state away, take a snapshot, and report the lost work. */
  | 'resync'
  /** Keep receiving, refuse to author, keep the outbox. */
  | 'read-only'
  /** Reconnect later; the condition is expected to clear on its own. */
  | 'reconnect'
  /** Stop. Retrying cannot help and the user has to be told. */
  | 'stop';

/**
 * §9's table, as code.
 *
 * @remarks
 * `sequence_gap` and `replica_mismatch` are `stop` rather than `resync`
 * deliberately: both mean this client's idea of its own identity or its own
 * sequence disagrees with the server's, which is a bug here rather than a state
 * to recover from. Resyncing would paper over it and lose the evidence, and the
 * same batch would be rebuilt and refused again.
 */
export function recoveryFor(code: string): Recovery {
  switch (code) {
    case REJECTION.unknownOrigin:
      return 'catch-up-and-retry';

    case REJECTION.resyncRequired:
      return 'resync';

    case REJECTION.forbidden:
      return 'read-only';

    case REJECTION.tooManyReplicas:
    case REJECTION.unauthenticated:
      return 'reconnect';

    default:
      // Everything else — not_found, malformed, the size caps, a sequence gap,
      // a replica mismatch, and any code a future server adds — stops. An
      // unknown code is the one case where guessing is worst: the safe
      // assumption about a refusal you do not understand is that repeating it
      // will not help.
      return 'stop';
  }
}
