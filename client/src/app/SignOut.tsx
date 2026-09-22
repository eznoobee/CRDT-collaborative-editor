import { useState } from 'react';

import { SignOutUnavailable } from '../auth/tokenSource';

/**
 * §7's sign-out, and the confirmation unsent work requires.
 *
 * @remarks
 * <p>
 * The count comes from the outbox rather than from a flag, because §7's rule is
 * about work that has not reached the server and the outbox is what that means.
 * Signing out with a non-empty one either sends it first or says what will be
 * lost; discarding it silently is the same failure as an unhandled refresh
 * rejection, arriving from the other direction — there the session ended
 * without being asked, here the user asked.
 * </p><p>
 * Sending it first is not offered, and the reason is that it cannot be
 * promised: the outbox is unsent precisely when the connection is not carrying
 * it, so a "sign out once this has sent" button would hang exactly when it
 * mattered. What is offered is the count and an explicit discard.
 * </p>
 */
export function SignOut(props: { unsent: number; signOut: () => Promise<void> }): React.JSX.Element {
  const [confirming, setConfirming] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  const go = () => {
    setFailure(null);
    props.signOut().catch((error: unknown) => {
      // §7: a provider that cannot end its session is a thing the user is told.
      // The token is already gone, so this application is signed out and the
      // provider is not, and only saying so distinguishes that from a sign-out
      // that worked.
      setFailure(
        error instanceof SignOutUnavailable
          ? 'Signed out here, but your identity provider kept its session: '
            + 'the next sign-in may not ask who you are.'
          : `Sign-out failed: ${error instanceof Error ? error.message : String(error)}`,
      );
    });
  };

  if (confirming) {
    return (
      <>
        <p role="alert" data-testid="sign-out-warning">
          {props.unsent === 1
            ? '1 change has not reached the server and will be lost.'
            : `${props.unsent} changes have not reached the server and will be lost.`}
        </p>
        <button type="button" data-testid="sign-out-confirm" onClick={go}>
          Sign out and discard
        </button>
        <button type="button" data-testid="sign-out-cancel" onClick={() => setConfirming(false)}>
          Keep editing
        </button>
      </>
    );
  }

  return (
    <>
      {failure === null
        ? null
        : <p role="alert" data-testid="sign-out-problem">{failure}</p>}
      <button
        type="button"
        data-testid="sign-out"
        onClick={() => (props.unsent > 0 ? setConfirming(true) : go())}
      >
        Sign out
      </button>
    </>
  );
}
