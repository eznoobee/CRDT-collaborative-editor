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
