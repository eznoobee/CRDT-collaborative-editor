/** What the server says the browser needs in order to log in (§7). */
export interface ClientConfiguration {
  readonly issuer: string;
  readonly clientId: string;
}

/**
 * Reads `/config` from this origin.
 *
 * @remarks
 * From the server, not the bundle. A build that carries its issuer is a build
 * per environment, and the first time one is promoted from staging to
 * production it authenticates against the wrong identity provider — which looks
 * like a login bug and is a deployment one.
 */
export async function loadConfiguration(origin: string): Promise<ClientConfiguration> {
  const response = await fetch(`${origin}/config`);
  if (!response.ok) {
    throw new Error(`Could not read /config: ${response.status}.`);
  }

  const config = (await response.json()) as Partial<ClientConfiguration>;

  // Checked rather than assumed. §7 makes the client id optional on the server,
  // because an API deployed without this application does not need one — so the
  // client is where its absence has to become a refusal instead of an
  // undefined that starts a login it cannot finish.
  if (typeof config.issuer !== 'string' || config.issuer === '') {
    throw new Error('The server reported no OIDC issuer.');
  }

  if (typeof config.clientId !== 'string' || config.clientId === '') {
    throw new Error('The server reported no OIDC client id; sign-in is not configured.');
  }

  return { issuer: config.issuer, clientId: config.clientId };
}

/** The path a document is opened at. */
export const DOCUMENT_PATH = /^\/d\/([0-9a-fA-F-]{36})\/?$/;

/** Where the issuer sends the browser back to. Registered exactly (§7). */
export const CALLBACK_PATH = '/callback';

/** The document this URL names, or null. */
export function documentIdIn(pathname: string): string | null {
  return DOCUMENT_PATH.exec(pathname)?.[1] ?? null;
}
