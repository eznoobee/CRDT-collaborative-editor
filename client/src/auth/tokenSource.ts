/**
 * Where the client gets a bearer token (PROJECT_SPEC.md §7).
 *
 * @remarks
 * An interface, so everything above it depends on "give me a token" and not on
 * an identity library. That is not a taste preference: §7 requires refresh to
 * be delegated to the provider's library rather than hand-rolled, which means
 * the library is load-bearing and replacing it must not be a rewrite of the
 * transport.
 */
export interface TokenSource {
  /**
   * A valid access token, refreshing if the current one has expired.
   *
   * @throws SignInRequired - No token can be obtained without the user.
   */
  token(): Promise<string>;
}

/**
 * Ending a session, as §7 defines it: three things, not one.
 *
 * @remarks
 * Separate from {@link TokenSource} because everything that merely needs a
 * bearer token should not depend on the ability to end a session — the
 * transport asks for tokens and has no business signing anyone out.
 */
export interface SessionSource extends TokenSource {
  /** Drops the tokens held in memory. */
  forget(): Promise<void>;

  /**
   * Ends the session at the identity provider and navigates there.
   *
   * @throws SignOutUnavailable - The provider advertises no end-session
   * endpoint, so this application cannot end its session and must say so
   * rather than report a sign-out it did not perform.
   */
  endSession(returnTo: string): Promise<void>;
}

/**
 * The provider cannot be asked to end its session.
 *
 * @remarks
 * §7 makes this a thing the user is told, not a thing that is swallowed. A
 * local-only sign-out looks identical to a real one from inside this
 * application — the token is gone either way — and the difference only shows up
 * on the next load, as the same person signed in without being asked. On a
 * shared machine that is the defect, so it is surfaced at the moment it
 * happens.
 */
export class SignOutUnavailable extends Error {
  constructor() {
    super('This identity provider does not support ending its session.');
    this.name = 'SignOutUnavailable';
  }
}

/**
 * The user has to log in again, and no amount of retrying changes that.
 *
 * @remarks
 * A distinct type rather than a generic failure, because §9 gives it a distinct
 * recovery: the client goes offline, keeps its outbox in full, and says a
 * sign-in is needed. Treating it as a transport error would retry it forever;
 * letting it escape as an unhandled rejection would discard unsent work at the
 * exact moment the user is being asked to log in again.
 */
export class SignInRequired extends Error {
  constructor(cause?: unknown) {
    super('Sign-in required.');
    this.name = 'SignInRequired';
    this.cause = cause;
  }
}
