import { act, fireEvent, render, screen } from '@testing-library/react';

import { Share } from './Share';
import { ApiRefusal, ROLE, type DocumentApi, type MemberEntry, type RoleValue } from './api';

function api(overrides: Partial<DocumentApi>): DocumentApi {
  return {
    members: () => Promise.resolve([]),
    grant: () => Promise.reject(new Error('not stubbed')),
    revoke: () => Promise.reject(new Error('not stubbed')),
    ...overrides,
  } as unknown as DocumentApi;
}

function member(userId: string, displayName: string, role: RoleValue = ROLE.editor): MemberEntry {
  return { userId, displayName, role, grantedAt: '' };
}

describe('Share', () => {
  it('draws nothing when the server refuses the member listing', async () => {
    // The visibility is the server's decision, not a local role check. A panel
    // hidden by a client-side rule and nothing else is §13.19's shape: it looks
    // like the rule and enforces only its own appearance.
    render(
      <Share
        api={api({ members: () => Promise.reject(new ApiRefusal(403, 'forbidden')) })}
        documentId="doc"
        meId="me"
      />,
    );

    await settle();

    expect(screen.queryByTestId('share')).toBeNull();
  });

  it('lists members once the server allows it', async () => {
    render(
      <Share
        api={api({ members: () => Promise.resolve([member('me', 'Ada', ROLE.owner), member('bo', 'Bo')]) })}
        documentId="doc"
        meId="me"
      />,
    );

    await settle();

    expect(screen.getByTestId('members').textContent).toContain('Ada');
    expect(screen.getByTestId('members').textContent).toContain('Bo');
  });

  it('offers no way to revoke yourself', async () => {
    // The server refuses it too. Both, because a document whose owner has no
    // membership is one nobody can grant on again.
    render(
      <Share
        api={api({ members: () => Promise.resolve([member('me', 'Ada', ROLE.owner), member('bo', 'Bo')]) })}
        documentId="doc"
        meId="me"
      />,
    );

    await settle();

    expect(screen.queryByTestId('members')?.querySelector('[data-revoke="me"]')).toBeNull();
    expect(screen.queryByTestId('members')?.querySelector('[data-revoke="bo"]')).not.toBeNull();
  });

  it('grants by user id and reloads the list', async () => {
    const granted: { user: string; role: number }[] = [];
    let listed = 0;

    render(
      <Share
        api={api({
          members: () => {
            listed += 1;
            return Promise.resolve([member('me', 'Ada', ROLE.owner)]);
          },
          grant: (_document: string, userId: string, role: number) => {
            granted.push({ user: userId, role });
            return Promise.resolve(member(userId, 'Bo', ROLE.viewer));
          },
        })}
        documentId="doc"
        meId="me"
      />,
    );

    await settle();

    fireEvent.change(screen.getByTestId('grant-user'), { target: { value: ' bo ' } });
    fireEvent.change(screen.getByTestId('grant-role'), { target: { value: String(ROLE.viewer) } });
    fireEvent.click(screen.getByTestId('grant'));
    await settle();

    expect(granted).toEqual([{ user: 'bo', role: ROLE.viewer }]);
    expect(listed).toBe(2);
  });

  it('shows the refusal for a user who has never signed in', async () => {
    // §9's stated limitation reaches the person who has to act on it, in the
    // server's own words.
    const detail = 'No such user. A role can be granted only to someone who has signed in at least once.';

    render(
      <Share
        api={api({ grant: () => Promise.reject(new ApiRefusal(400, detail)) })}
        documentId="doc"
        meId="me"
      />,
    );

    await settle();

    fireEvent.change(screen.getByTestId('grant-user'), { target: { value: 'nobody' } });
    fireEvent.click(screen.getByTestId('grant'));
    await settle();

    expect(screen.getByTestId('share-problem').textContent).toContain('signed in at least once');
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
