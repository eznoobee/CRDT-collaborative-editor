import { PkceTokenSource } from '../auth/pkce';
import { SignInRequired } from '../auth/tokenSource';
import { CALLBACK_PATH, documentIdIn, loadConfiguration } from './config';
import { openDocument, type OpenDocument } from './openDocument';

export type Bootstrap =
  /** The browser is on its way to the issuer; this document is about to go. */
  | { kind: 'signing-in' }
  | { kind: 'no-document' }
  | { kind: 'failed'; message: string }
  | { kind: 'open'; document: OpenDocument };

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
    });

    // A path, not a query-string sniff: §7 registers exactly one redirect URI,
    // so anything arriving here is a return from the issuer and anything else
    // is an ordinary load.
    let path = location.pathname;
    if (path === CALLBACK_PATH) {
      path = await auth.completeSignIn();
    }

    const documentId = documentIdIn(path);
    if (documentId === null) {
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

    return { kind: 'open', document: await openDocument({ origin, documentId, tokens: auth }) };
  } catch (error) {
    return { kind: 'failed', message: error instanceof Error ? error.message : String(error) };
  }
}
