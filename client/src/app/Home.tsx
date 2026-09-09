import { useCallback, useEffect, useState } from 'react';

import { ApiRefusal, type DocumentApi, type DocumentSummary, type Identity } from './api';

export interface HomeProps {
  readonly api: DocumentApi;
  readonly me: Identity;
  readonly signOut: () => Promise<void>;

  /** Where a document is opened. Injected so tests do not navigate. */
  readonly open?: (documentId: string) => void;
}

/**
 * What a signed-in user sees before they have opened anything (§9).
 *
 * @remarks
 * <p>
 * Register rows 15 and 16 were about this page not existing. The application
 * opened `/d/{id}` and had no way to produce an id, so both harnesses seeded
 * through `psql` and the walk stopped at sign-in — a hole *between* criteria
 * rather than a deferral (§13.27).
 * </p><p>
 * The caller's own user id is shown because §9's grant names a user id and
 * there is no directory: the way a person is invited is that they read their id
 * here and pass it to an owner. Showing it is not a convenience — without it
 * the grant path has an input nothing in the product produces.
 * </p>
 */
export function Home(props: HomeProps): React.JSX.Element {
  const { api } = props;
  const [documents, setDocuments] = useState<DocumentSummary[] | null>(null);
  const [title, setTitle] = useState('');
  const [problem, setProblem] = useState<string | null>(null);

  const refresh = useCallback(() => {
    api.list().then(
      (found) => { setDocuments(found); setProblem(null); },
      (error: unknown) => { setProblem(messageFor(error)); },
    );
  }, [api]);

  useEffect(refresh, [refresh]);

  const create = () => {
    const wanted = title.trim();
    if (wanted === '') {
      setProblem('A document needs a title.');
      return;
    }

    api.create(wanted).then(
      (created) => {
        setTitle('');
        setProblem(null);
        (props.open ?? ((id: string) => { window.location.assign(`/d/${id}`); }))(created.id);
      },
      (error: unknown) => { setProblem(messageFor(error)); },
    );
  };

  return (
    <>
      <p data-testid="me">
        You are <strong>{props.me.displayName}</strong>. Your user id is{' '}
        <code data-testid="my-user-id">{props.me.userId}</code> — give it to someone to be
        invited to their document.
      </p>

      <h2>New document</h2>
      <label>
        Title
        <input
          data-testid="new-title"
          value={title}
          onChange={(event) => setTitle(event.target.value)}
        />
      </label>
      <button type="button" data-testid="create" onClick={create}>Create</button>

      <h2>Your documents</h2>
      {problem === null ? null : <p role="alert" data-testid="home-problem">{problem}</p>}
      {documents === null
        ? <p>Loading…</p>
        : documents.length === 0
          ? <p data-testid="no-documents">You have no documents yet.</p>
          : (
            <ul data-testid="documents">
              {documents.map((document) => (
                <li key={document.id}>
                  <a data-document={document.id} href={`/d/${document.id}`}>{document.title}</a>
                </li>
              ))}
            </ul>
          )}
    </>
  );
}

/** A refusal in words the reader can act on (§13.13). */
export function messageFor(error: unknown): string {
  if (error instanceof ApiRefusal) {
    // 404 and 403 mean different things and §7 drew the distinction on purpose.
    return error.status === 404
      ? 'That document is gone, or you no longer have access to it.'
      : error.message;
  }

  return error instanceof Error ? error.message : String(error);
}
