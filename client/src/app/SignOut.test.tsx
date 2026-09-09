import { act, fireEvent, render, screen } from '@testing-library/react';

import { SignOut } from './SignOut';
import { SignOutUnavailable } from '../auth/tokenSource';

describe('SignOut', () => {
  it('signs out immediately when nothing is unsent', async () => {
    let signedOut = 0;
    render(<SignOut unsent={0} signOut={async () => { signedOut += 1; }} />);

    await click(screen.getByTestId('sign-out'));

    expect(signedOut).toBe(1);
    expect(screen.queryByTestId('sign-out-warning')).toBeNull();
  });

  it('will not discard unsent work without saying how much', async () => {
    // §7: signing out with a non-empty outbox tells the user what will be lost.
    // The assertion is that the first click signs nothing out — a confirmation
    // that appeared *and* signed out would look identical on screen.
    let signedOut = 0;
    render(<SignOut unsent={3} signOut={async () => { signedOut += 1; }} />);

    await click(screen.getByTestId('sign-out'));

    expect(signedOut).toBe(0);
    expect(screen.getByTestId('sign-out-warning').textContent).toContain('3 changes');

    await click(screen.getByTestId('sign-out-confirm'));
    expect(signedOut).toBe(1);
  });

  it('counts one unsent change in the singular', async () => {
    render(<SignOut unsent={1} signOut={async () => {}} />);

    await click(screen.getByTestId('sign-out'));

    expect(screen.getByTestId('sign-out-warning').textContent).toContain('1 change has');
  });

  it('keeps editing when the confirmation is declined', async () => {
    let signedOut = 0;
    render(<SignOut unsent={2} signOut={async () => { signedOut += 1; }} />);

    await click(screen.getByTestId('sign-out'));
    await click(screen.getByTestId('sign-out-cancel'));

    expect(signedOut).toBe(0);
    expect(screen.getByTestId('sign-out')).toBeDefined();
    expect(screen.queryByTestId('sign-out-warning')).toBeNull();
  });

  it('says so when the provider cannot end its session', async () => {
    // §7's rule that this is surfaced rather than swallowed. From inside this
    // application a local-only sign-out looks exactly like a real one — the
    // token is gone either way — and the difference only appears on the next
    // load, as the same person signed in without being asked.
    render(<SignOut unsent={0} signOut={() => Promise.reject(new SignOutUnavailable())} />);

    await click(screen.getByTestId('sign-out'));

    const problem = await screen.findByTestId('sign-out-problem');
    expect(problem.textContent).toContain('kept its session');
  });

  it('reports any other sign-out failure rather than appearing to work', async () => {
    render(<SignOut unsent={0} signOut={() => Promise.reject(new Error('network down'))} />);

    await click(screen.getByTestId('sign-out'));

    const problem = await screen.findByTestId('sign-out-problem');
    expect(problem.textContent).toContain('network down');
  });
});

/** A click, with React's updates flushed before the assertion runs. */
async function click(element: HTMLElement): Promise<void> {
  await act(async () => {
    fireEvent.click(element);
  });
}
