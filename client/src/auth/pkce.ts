import { UserManager, InMemoryWebStorage, WebStorageStateStore, type User } from 'oidc-client-ts';

import { SignInRequired, type TokenSource } from './tokenSource';

export interface PkceOptions {
  /** The OIDC issuer, read from the server's /config (§7). */
  readonly authority: string;

  /** The public client id. No secret exists; a browser cannot keep one. */
  readonly clientId: string;

  /** Exact-match, and the same value the issuer has registered. */
  readonly redirectUri: string;
}

/**
 * Authorization Code with PKCE, over `oidc-client-ts` (§7).
 *
 * @remarks
 * <p>
 * **The access token lives in memory and nowhere else.** `oidc-client-ts`
 * defaults its user store to `sessionStorage`; that default is replaced here
 * with an in-memory store, which is the whole reason this class configures the
 * library rather than using it directly. A token in `sessionStorage` survives
 * script access from anything running on this origin, and §7's rule is that
 * the token never lands anywhere a later bug can read it back.
 * </p><p>
 * The **state store stays in `sessionStorage`, and must**: the login is a full
 * page navigation, so the `state` value and the PKCE code verifier have to
 * outlive the document that created them. `oidc-client-ts` removes that entry
 * when the callback consumes it, which is why §7 states the rule as *after a
 * complete login* rather than *never* — a rule that forbade it outright would
 * forbid the redirect flow itself, and would be quietly worked around.
 * </p><p>
 * The cost of in-memory tokens is a redirect on every page load, since nothing
 * survives the reload. That is the trade §7 chose, and it is invisible in
 * practice: the identity provider holds the session, so the round trip does not
 * ask the user for anything.
 * </p><p>
 * Refresh is **not implemented here.** `oidc-client-ts` owns it, per §7:
 * refresh looks like four lines and has a long history of subtle bugs — clock
 * skew, concurrent refreshes racing each other, a rotated refresh token
 * discarded on a retry — and none of them are what this project is about.
 * </p>
 */
export class PkceTokenSource implements TokenSource {
  private readonly users: UserManager;
  private inFlight: Promise<User | null> | null = null;

  constructor(options: PkceOptions) {
    this.users = new UserManager({
      authority: options.authority,
      client_id: options.clientId,
      redirect_uri: options.redirectUri,
      response_type: 'code',

      // offline_access is what asks for a refresh token; without it every
      // expiry becomes a redirect, which §9 would surface as a sign-in prompt
      // in the middle of typing.
      scope: 'openid profile offline_access',

      userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),

      // The library's own renewal, on a timer it owns. Turned on explicitly
      // because the alternative is this class noticing an expiry and doing it,
      // which is the hand-rolling §7 forbids.
      automaticSilentRenew: true,

      // No iframe. Silent renew via a hidden iframe needs third-party cookies
      // at the issuer, which browsers no longer reliably send; the refresh
      // token path works without them.
      silentRequestTimeoutInSeconds: 10,
    });
  }

  /**
   * Sends the browser to the issuer. Does not return: the page navigates.
   *
   * @param returnTo - Where to come back to. Carried in the OIDC `state`, which
   * the issuer echoes and the library verifies — so it survives a redirect that
   * lands on the one registered callback URI, and a tampered value is rejected
   * rather than followed.
   * @param loginHint - Which account to sign in as, where the issuer supports
   * it. Used by the tests to choose a subject without a login form.
   */
  async signIn(returnTo: string, loginHint?: string): Promise<void> {
    await this.users.signinRedirect({
      state: returnTo,
      ...(loginHint === undefined ? {} : { extraQueryParams: { login_hint: loginHint } }),
    });
  }

  /**
   * Completes the redirect back from the issuer.
   *
   * @returns Where the sign-in started, so the caller can restore it.
   */
  async completeSignIn(): Promise<string> {
    const query = new URLSearchParams(window.location.search);
    if (query.has('error')) {
      throw new SignInRequired(query.get('error'));
    }

    const user = await this.users.signinRedirectCallback();

    // The code and state leave the address bar immediately. They are spent, but
    // a URL lands in history and in Referer headers, which is the leak §7's
    // connect-ticket design exists to avoid — the same reasoning applied to the
    // other credential in the flow.
    const returnTo = typeof user.state === 'string' ? user.state : '/';
    window.history.replaceState({}, '', returnTo);
    return returnTo;
  }

  async token(): Promise<string> {
    // One in-flight lookup at a time. Two submissions racing an expiry would
    // otherwise each trigger a renewal, and a provider that rotates refresh
    // tokens invalidates the first when the second lands.
    this.inFlight ??= this.users.getUser();

    let user: User | null;
    try {
      user = await this.inFlight;
    } catch (error) {
      throw new SignInRequired(error);
    } finally {
      this.inFlight = null;
    }

    if (user === null || user.expired === true) {
      throw new SignInRequired();
    }

    return user.access_token;
  }

  /** Drops the tokens held in memory. */
  async forget(): Promise<void> {
    await this.users.removeUser();
  }
}
