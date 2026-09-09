import { signOut } from './signOut';

describe('signOut', () => {
  it('erases local state and the token before the browser leaves', async () => {
    // The order is the requirement, so the order is what is asserted. Ending
    // the provider's session navigates away: anything sequenced after it does
    // not run, and the local replica and outbox would stay in IndexedDB for
    // whoever signs in next.
    const done: string[] = [];

    await signOut({
      forgetLocal: async () => { done.push('local'); },
      forgetTokens: async () => { done.push('tokens'); },
      endSession: async () => { done.push('session'); },
    });

    expect(done).toEqual(['local', 'tokens', 'session']);
  });

  it('does not end the session when local state could not be erased', async () => {
    // A sign-out that navigated away regardless would report success while
    // leaving the document on disk — the failure this ordering exists for, and
    // it has to survive the step failing rather than only the steps racing.
    const reached: string[] = [];

    await expect(signOut({
      forgetLocal: () => Promise.reject(new Error('the store is locked')),
      forgetTokens: async () => { reached.push('tokens'); },
      endSession: async () => { reached.push('session'); },
    })).rejects.toThrow('the store is locked');

    expect(reached).toEqual([]);
  });

  it('does not end the session when the token could not be dropped', async () => {
    const reached: string[] = [];

    await expect(signOut({
      forgetLocal: async () => { reached.push('local'); },
      forgetTokens: () => Promise.reject(new Error('user store failed')),
      endSession: async () => { reached.push('session'); },
    })).rejects.toThrow('user store failed');

    expect(reached).toEqual(['local']);
  });
});
