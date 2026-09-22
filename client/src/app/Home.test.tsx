import { act, fireEvent, render, screen } from '@testing-library/react';

import { Home } from './Home';
import { ApiRefusal, ROLE, type DocumentApi, type DocumentSummary } from './api';

/** Enough of the API for this component, and no more. */
function api(overrides: Partial<DocumentApi>): DocumentApi {
  return {
    list: () => Promise.resolve([]),
    create: () => Promise.reject(new Error('not stubbed')),
    ...overrides,
  } as unknown as DocumentApi;
}

const me = { userId: 'a1b2c3', displayName: 'Ada' };

function summary(id: string, title: string): DocumentSummary {
  return { id, title, role: ROLE.owner, createdAt: '', updatedAt: '' };
}

describe('Home', () => {
  it('shows the user their own id, which is what a grant names', async () => {
    // §9 has no directory: an owner grants by user id, and the only way the
    // invitee can supply one is to read it here. Without this the grant path
    // has an input nothing in the product produces — register row 15's shape.
    render(<Home api={api({})} me={me} signOut={() => Promise.resolve()} />);

    await settle();

    expect(screen.getByTestId('my-user-id').textContent).toBe('a1b2c3');
  });

  it('lists what the caller can reach', async () => {
    render(
      <Home
        api={api({ list: () => Promise.resolve([summary('one', 'First'), summary('two', 'Second')]) })}
        me={me}
        signOut={() => Promise.resolve()}
      />,
    );

    await settle();

    expect(screen.getByTestId('documents').textContent).toContain('First');
    expect(screen.getByTestId('documents').textContent).toContain('Second');
  });

  it('says so when there is nothing yet rather than looking broken', async () => {
    // The state every new user is in, and the one a client that treated an
    // empty list as a failure would render as an error.
    render(<Home api={api({})} me={me} signOut={() => Promise.resolve()} />);

    await settle();

    expect(screen.getByTestId('no-documents')).toBeDefined();
  });

  it('opens what it just created', async () => {
    const opened: string[] = [];

    render(
      <Home
        api={api({ create: (title: string) => Promise.resolve(summary('fresh', title)) })}
        me={me}
        signOut={() => Promise.resolve()}
        open={(id) => opened.push(id)}
      />,
    );

    await settle();

    fireEvent.change(screen.getByTestId('new-title'), { target: { value: 'Notes' } });
    fireEvent.click(screen.getByTestId('create'));
    await settle();

    expect(opened).toEqual(['fresh']);
  });

  it('refuses an empty title here rather than at the server', async () => {
    let created = 0;

    render(
      <Home
        api={api({ create: () => { created += 1; return Promise.reject(new Error('unused')); } })}
        me={me}
        signOut={() => Promise.resolve()}
      />,
    );

    await settle();

    fireEvent.click(screen.getByTestId('create'));
    await settle();

    expect(created).toBe(0);
    expect(screen.getByTestId('home-problem').textContent).toContain('title');
  });

  it('shows the server\'s own words for a refusal', async () => {
    // §13.13: the caller has to be able to act on it. "Request failed" is not
    // something anyone can do anything about.
    render(
      <Home
        api={api({ list: () => Promise.reject(new ApiRefusal(400, 'A title is required.')) })}
        me={me}
        signOut={() => Promise.resolve()}
      />,
    );

    await settle();

    expect(screen.getByTestId('home-problem').textContent).toContain('A title is required.');
  });

  it('removes a document and refreshes what is left', async () => {
    // Register row 23: a person could make documents and could not get rid of
    // any of them. The listing is re-read rather than the row spliced out
    // locally, because the server decides what is left — splicing would show a
    // removal that failed as though it had worked.
    const removed: string[] = [];
    let listings = 0;

    render(
      <Home
        api={api({
          list: () => {
            listings += 1;
            return Promise.resolve(listings === 1 ? [summary('doomed', 'Doomed')] : []);
          },
          remove: (id: string) => { removed.push(id); return Promise.resolve(); },
        })}
        me={me}
        signOut={() => Promise.resolve()}
      />,
    );

    await settle();

    confirming(true);
    fireEvent.click(screen.getByText('Remove'));
    await settle();

    expect(removed).toEqual(['doomed']);
    expect(screen.getByTestId('no-documents')).toBeTruthy();
  });

  it('does not remove anything when the confirmation is declined', async () => {
    // The pair. Without it, "removes on click" is satisfied by a button that
    // removes whatever the person meant — and this one sits beside the link
    // they click all day.
    const removed: string[] = [];

    render(
      <Home
        api={api({
          list: () => Promise.resolve([summary('safe', 'Safe')]),
          remove: (id: string) => { removed.push(id); return Promise.resolve(); },
        })}
        me={me}
        signOut={() => Promise.resolve()}
      />,
    );

    await settle();

    confirming(false);
    fireEvent.click(screen.getByText('Remove'));
    await settle();

    expect(removed).toEqual([]);
    expect(screen.getByText('Safe')).toBeTruthy();
  });

  it('offers no remove control to someone who is not the owner', async () => {
    // The server refuses an editor's DELETE, so this is not the enforcement —
    // it is not showing someone a button that exists to fail.
    render(
      <Home
        api={api({
          list: () => Promise.resolve([
            { ...summary('theirs', 'Theirs'), role: ROLE.editor },
          ]),
        })}
        me={me}
        signOut={() => Promise.resolve()}
      />,
    );

    await settle();

    expect(screen.getByText('Theirs')).toBeTruthy();
    expect(screen.queryByText('Remove')).toBeNull();
  });

  it('says what went wrong when a removal is refused', async () => {
    render(
      <Home
        api={api({
          list: () => Promise.resolve([summary('gone', 'Gone')]),
          remove: () => Promise.reject(new ApiRefusal(404, 'nope')),
        })}
        me={me}
        signOut={() => Promise.resolve()}
      />,
    );

    await settle();

    confirming(true);
    fireEvent.click(screen.getByText('Remove'));
    await settle();

    expect(screen.getByTestId('home-problem').textContent).toContain('That document is gone');
  });
});

/** Answers the browser's confirmation for one click. */
function confirming(answer: boolean): void {
  vi.spyOn(window, 'confirm').mockReturnValue(answer);
}

/**
 * Lets queued promise callbacks run inside React's act() window.
 *
 * @remarks
 * These components load through the API on mount, so an assertion made in the
 * same tick as the render is an assertion about the loading state — which would
 * pass whatever the component eventually showed.
 */
async function settle(): Promise<void> {
  await act(async () => {
    await Promise.resolve();
  });
}
