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
});

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
