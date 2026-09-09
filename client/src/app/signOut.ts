/** The three things §7 says signing out is. */
export interface SignOutSteps {
  /** Erases this browser's local copy of the document and its outbox. */
  forgetLocal(): Promise<void>;

  /** Drops the access token held in memory. */
  forgetTokens(): Promise<void>;

  /**
   * Ends the identity provider's session. Navigates, so nothing after it runs.
   */
  endSession(): Promise<void>;
}

/**
 * Signs out, in the only order that is safe (PROJECT_SPEC.md §7).
 *
 * @remarks
 * <p>
 * A function rather than four lines inline, because the order is the
 * requirement and an order is exactly what a test can pin. Local state first,
 * since it is the part that outlives everything else — IndexedDB survives the
 * tab, the token does not. The token next. The provider's session last, because
 * that call navigates away and nothing after it runs: putting it first would
 * leave the local replica and the outbox on disk under someone else's next
 * sign-in.
 * </p><p>
 * The failure mode this exists to prevent is the tidy-looking version — end the
 * session, then clean up — which works every time it is tried by hand and never
 * cleans up in production, because the browser has already left.
 * </p>
 */
export async function signOut(steps: SignOutSteps): Promise<void> {
  await steps.forgetLocal();
  await steps.forgetTokens();
  await steps.endSession();
}
