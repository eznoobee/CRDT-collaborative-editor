import { useCallback, useSyncExternalStore } from 'react';

import { Editor } from './editor/Editor';
import { describeWindow, offlineWindow } from './editor/offlineWindow';
import type { Bootstrap } from './app/bootstrap';
import type { OpenDocument } from './app/openDocument';

/**
 * The application (PROJECT_SPEC.md §9, §11's Phase 4 deliverable).
 *
 * @remarks
 * <p>
 * Everything Phase 4 built, connected: §7's PKCE sign-in, a document opened
 * from the URL, the local store, the sync controller on a live connection, and
 * the editor rendering the replica. Until this existed each of those had tests
 * and none of them had a user (§13.22).
 * </p><p>
 * The API and this application are the **same origin**. That is a deliberate
 * choice against CORS: an allow-list of browser origins on a service that
 * accepts a bearer token and issues connect tickets is one more piece of
 * configuration that fails open when it is wrong, and a reverse proxy routing
 * two paths to one host buys everything it would have.
 * </p>
 */
export interface AppProps {
  /** What the bootstrap produced, or undefined while it is still running. */
  readonly result?: Bootstrap;
}

export function App(props: AppProps = {}): React.JSX.Element {
  const result = props.result;

  if (result === undefined) {
    return <Shell><p>Loading…</p></Shell>;
  }

  switch (result.kind) {
    case 'signing-in':
      return <Shell><p>Signing in…</p></Shell>;

    case 'no-document':
      return (
        <Shell>
          <p data-testid="no-document">Open a document at /d/&lt;document id&gt;.</p>
        </Shell>
      );

    case 'failed':
      return (
        <Shell>
          <p role="alert" data-testid="failure">{result.message}</p>
        </Shell>
      );

    case 'open':
      return <Shell><Document open={result.document} /></Shell>;
  }
}

function Shell(props: { children: React.ReactNode }): React.JSX.Element {
  return (
    <main>
      <h1>Collaborative Editor</h1>
      {props.children}
    </main>
  );
}

/** The editor and everything the user has to be told (§9, §13.13). */
function Document(props: { open: OpenDocument }): React.JSX.Element {
  const { sync } = props.open;

  useSyncExternalStore(
    useCallback((notify) => sync.subscribe(notify), [sync]),
    () => `${sync.state}:${sync.problem?.code ?? ''}:${String(sync.readOnly)}:${sync.pending.length}`,
    () => 'starting::false:0',
  );

  const session = sync.session;
  const syncedAt = props.open.restored?.lastSyncedAt ?? null;

  // The window is a countdown, so it is read from a clock that ticks rather
  // than from one read during render. Rendering `Date.now()` would also be
  // impure — the same output twice from the same inputs is what React assumes.
  const now = useClock(sync.state === 'offline' && syncedAt !== null);

  return (
    <>
      <p data-testid="state">{sync.state}</p>
      {sync.readOnly ? <p data-testid="read-only">Read-only.</p> : null}
      {sync.problem === null
        ? null
        : <p role="alert" data-testid="problem">{describe(sync.problem.code, sync.problem.lost)}</p>}
      {sync.state === 'offline' && syncedAt !== null
        ? (
          <p data-testid="offline-window">
            {describeWindow(offlineWindow(syncedAt, now))}
          </p>
        )
        : null}
      {session === null
        ? <p>Connecting…</p>
        : <Editor session={session} readOnly={sync.readOnly} />}
    </>
  );
}

/**
 * Epoch milliseconds, refreshed each minute while `active`.
 *
 * @remarks
 * An external store rather than state set from an effect. The clock is not
 * this component's state — it is a value that changes on its own, which is
 * exactly what `useSyncExternalStore` is for, and reading `Date.now()` during
 * render would make the render impure.
 */
function useClock(active: boolean): number {
  return useSyncExternalStore(
    useCallback(
      (notify: () => void) => {
        if (!active) {
          return () => {};
        }

        const tick = setInterval(notify, 60_000);
        return () => clearInterval(tick);
      },
      [active],
    ),
    () => minute(),
    () => minute(),
  );
}

/**
 * The current minute, as epoch milliseconds.
 *
 * @remarks
 * Rounded because `useSyncExternalStore` compares snapshots by identity and
 * calls this more than once per render: a raw `Date.now()` returns a different
 * number each call and React would report an infinite loop.
 */
function minute(): number {
  return Math.floor(Date.now() / 60_000) * 60_000;
}

/**
 * A refusal in words.
 *
 * @remarks
 * §13.13: a rejection the rejected party cannot observe is not a rejection.
 * Every code the client can hold reaches the screen — including the codes that
 * mean a bug here, because "something is wrong and it is not your network" is
 * more actionable than a client that looks connected and is not.
 */
function describe(code: string, lost: number): string {
  switch (code) {
    case 'sign_in_required':
      return 'Your session ended. Sign in again — nothing you typed has been lost.';
    case 'forbidden':
      return 'You no longer have permission to edit this document.';
    case 'not_found':
      return 'This document is gone, or your access was revoked.';
    case 'too_many_replicas':
      return 'Too many sessions are open on this document. Reconnecting…';
    case 'resync_required':
      return `This client was offline too long. ${lost} unsent change${lost === 1 ? '' : 's'} could not be recovered.`;
    default:
      return `The server refused this client: ${code}.`;
  }
}
