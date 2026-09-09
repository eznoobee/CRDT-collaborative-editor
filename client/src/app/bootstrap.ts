import { PkceTokenSource } from '../auth/pkce';
import { SignInRequired } from '../auth/tokenSource';
import { CALLBACK_PATH, SIGNED_OUT_PATH, documentIdIn, loadConfiguration } from './config';
import { DocumentApi, type Identity } from './api';
import { openDocument, type OpenDocument } from './openDocument';
import { signOut } from './signOut';

export type Bootstrap =
  /** The browser is on its way to the issuer; this document is about to go. */
  | { kind: 'signing-in' }
  | { kind: 'no-document' }
  | { kind: 'signed-out' }
  | {
      kind: 'home';
      api: DocumentApi;
      me: Identity;
      signOut: () => Promise<void>;
    }
  | { kind: 'failed'; message: string }
  | {
      kind: 'open';
      document: OpenDocument;
      api: DocumentApi;
      me: Identity;

      /**
       * §7's three things, in order: local state, then the token, then the
       * provider's session.
       */
      signOut: () => Promise<void>;
    };

/**
 * Everything that has to happen before the application can render (§7, §9).
 *
 * @remarks
 * <p>
 * Outside React, deliberately. Completing an OIDC redirect consumes a
 * single-use authorization code and a single-use PKCE verifier: run it twice
 * and the second call fails with "no matching state found in storage", because
 * the first spent it. React runs effects more than once — Strict Mode does it
 * on purpose in development, and a remount does it for real in any build — so
 * an effect is the wrong place for an operation that can only happen once.
 * </p><p>
 * That was not a theoretical worry. The end-to-end test found exactly this,
 * and only because the artefact it was serving turned out to be a development
 * build of React (§13.26); in a production build the same fragility would have
 * sat there waiting for the first remount.
 * </p>
 */
export async function bootstrap(location: Location = window.location): Promise<Bootstrap> {
  try {
    const origin = location.origin;
    const config = await loadConfiguration(origin);

    const auth = new PkceTokenSource({
      authority: config.issuer,
      clientId: config.clientId,
      redirectUri: `${origin}${CALLBACK_PATH}`,
      postLogoutUri: `${origin}${SIGNED_OUT_PATH}`,
    });

    // A path, not a query-string sniff: §7 registers exactly one redirect URI,
    // so anything arriving here is a return from the issuer and anything else
    // is an ordinary load.
    let path = location.pathname;
    if (path === CALLBACK_PATH) {
      path = await auth.completeSignIn();
    }

    // Where the issuer sends the browser after ending its session. Nothing is
    // opened and no sign-in is started: arriving here signed in again would be
    // a sign-out that immediately undid itself.
    if (path === SIGNED_OUT_PATH) {
      return { kind: 'signed-out' };
    }

    // The home page is a signed-in page now, so a path that is neither a
    // document nor the root is the only unrecognised one left.
    const documentId = documentIdIn(path);
    if (documentId === null && path !== '/') {
      return { kind: 'no-document' };
    }

    try {
      await auth.token();
    } catch (error) {
      if (!(error instanceof SignInRequired)) {
        throw error;
      }

      // Where the browser leaves. Nothing after this runs in this document.
      await auth.signIn(path);
      return { kind: 'signing-in' };
    }

    const api = new DocumentApi(origin, auth);
    const me = await api.me();
    const leave = () => signOut({
      // Nothing local to erase: no document is open on the home page.
      forgetLocal: () => Promise.resolve(),
      forgetTokens: () => auth.forget(),
      endSession: () => auth.endSession(SIGNED_OUT_PATH),
    });

    if (documentId === null) {
      return { kind: 'home', api, me, signOut: leave };
    }

    const document = await openDocument({ origin, documentId, tokens: auth });

    return {
      kind: 'open',
      document,
      api,
      me,
      signOut: () => signOut({
        forgetLocal: () => document.forget(),
        forgetTokens: () => auth.forget(),
        endSession: () => auth.endSession(SIGNED_OUT_PATH),
      }),
    };
  } catch (error) {
    return { kind: 'failed', message: error instanceof Error ? error.message : String(error) };
  }
}
