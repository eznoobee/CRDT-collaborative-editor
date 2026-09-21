import { useCallback, useSyncExternalStore } from 'react';

import { Home } from './app/Home';
import { Share } from './app/Share';
import { SignOut } from './app/SignOut';
import type { DocumentApi, Identity } from './app/api';
import { Editor } from './editor/Editor';
import { describeWindow, offlineWindow } from './editor/offlineWindow';
import type { SyncState } from './editor/SyncController';
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

    case 'home':
      return (
        <Shell>
          <Home api={result.api} me={result.me} signOut={result.signOut} />
          <SignOut unsent={0} signOut={result.signOut} />
        </Shell>
      );

    case 'signed-out':
      return (
        <Shell>
          <p data-testid="signed-out">You are signed out.</p>
        </Shell>
      );

    case 'failed':
      return (
        <Shell>
          <p role="alert" data-testid="failure">{result.message}</p>
        </Shell>
      );

    case 'open':
      return (
        <Shell>
          <Document
            open={result.document}
            api={result.api}
            me={result.me}
            signOut={result.signOut}
          />
        </Shell>
      );
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

/**
 * Queued batches at which the outbox is worth mentioning (§8 target 2, §13.13).
 *
 * @remarks
 * <p>
 * <b>The measurement that asked for this.</b> §8's target 2 run has a person
 * typing at eight characters a second alongside nineteen other editors, and
 * 725 of their 1,000 keystrokes never reached the server — they sat in this
 * outbox. The line the report carries is the reason this exists: <i>the UI says
 * `live`, with no problem, the whole time</i>. A person would see their own
 * text, because local edits apply locally, and nothing at all to say that most
 * of it was going nowhere.
 * </p><p>
 * <b>Eight, because a healthy outbox is nearly empty.</b> `SyncController.drain`
 * sends one batch per round trip and a round trip is tens of milliseconds, so
 * even a fast typist leaves at most a batch or two queued at any moment. Eight
 * is past anything ordinary typing produces and far below the point at which
 * the backlog is minutes of work. It is deliberately not zero: an indicator
 * that flickers on every keystroke is one people learn to ignore, which would
 * make it worse than nothing.
 * </p><p>
 * <b>Shown only while `live`.</b> Offline already says so, and has its own
 * window countdown; a second message about unsent work there would be saying
 * the same thing twice.
 * </p><p>
 * This is a report, not a fix. What 9.5 measured is that the backlog builds
 * because the writer's own browser cannot keep up with applying everyone else's
 * operations, and telling the user is the part that belongs in a close-out.
 * </p>
 */
const VISIBLE_BACKLOG = 8;

/**
 * What to tell the user about unsent work, or null to say nothing.
 *
 * @remarks
 * A function rather than a condition inside the JSX so the rule can be tested
 * without rendering the whole document view. The rule is the part with a
 * decision in it; the `<p>` is not.
 */
export function backlogMessage(state: SyncState, queued: number): string | null {
  if (state !== 'live' || queued < VISIBLE_BACKLOG) {
    return null;
  }

  return `${queued} edits not sent yet.`;
}

/** The editor and everything the user has to be told (§9, §13.13). */
function Document(props: {
  open: OpenDocument;
  api: DocumentApi;
  me: Identity;
  signOut: () => Promise<void>;
}): React.JSX.Element {
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
      {(() => {
        const backlog = backlogMessage(sync.state, sync.pending.length);
        return backlog === null ? null : <p data-testid="backlog">{backlog}</p>;
      })()}
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
      <Share api={props.api} documentId={props.open.documentId} meId={props.me.userId} />
      <p>
        <a href="/" data-testid="home-link">All documents</a>
      </p>
      <SignOut unsent={sync.pending.length} signOut={props.signOut} />
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
