/**
 * The harness issuer, run as a service for a local stack. LOCAL DEVELOPMENT
 * ONLY — see `deploy/docker-compose.dev-oidc.yml` for why that is not a
 * disclaimer but a boundary.
 *
 * @remarks
 * <p>
 * <b>Why this exists.</b> `docker-compose.yml` requires `OIDC_ISSUER` and
 * `OIDC_METADATA_ADDRESS` and supplies no default, deliberately: §7 forbids an
 * issuer that falls back to something convenient, because a stack that comes up
 * authenticating against the wrong provider looks exactly like one that works.
 * The cost is that a developer with no identity provider to hand cannot start
 * the stack at all, which is the gap this closes.
 * </p><p>
 * <b>What it does not do.</b> It adds no development bypass to the product and
 * disables no check. The API keeps `RequireHttpsMetadata` at its secure
 * default, validates issuer, audience, lifetime and signing key, and gets its
 * keys from a JWKS fetched over real TLS — which is why this serves HTTPS and
 * writes a CA bundle rather than serving plaintext and asking the API to be
 * relaxed about it. Nothing in `src/` knows this service exists.
 * </p><p>
 * <b>The same issuer the test suites use</b>, `startOidc`, rather than a second
 * implementation. A dev-only issuer written separately would be a copy that
 * drifts, and the drift would be in exactly the code that decides whether a
 * token is valid.
 * </p>
 */
import { readFileSync, statSync, writeFileSync } from 'node:fs';

import { startOidc } from '../../client/src/interop/harness.ts';

/** Reads a variable that has no safe default. */
function required(name: string): string {
  const value = process.env[name];
  if (value === undefined || value === '') {
    throw new Error(`${name} is not set; deploy/docker-compose.dev-oidc.yml sets it`);
  }

  return value;
}

/**
 * A variable naming a file this process must be able to read.
 *
 * @remarks
 * <p>
 * <b>§13.23, and it cost a cycle to learn again.</b> Without this the first
 * failure was an `EISDIR` stack trace out of `readFileSync` inside `startOidc`,
 * which says that something, somewhere, read a directory — and names neither
 * the path, nor the variable that supplied it, nor the reason a directory is
 * there at all.
 * </p><p>
 * <b>A directory is the expected wrong answer, not an exotic one.</b> Docker
 * creates a directory at a bind mount's source when the host path does not
 * exist, and then mounts that. So a `.env` pointing at a certificate nobody has
 * generated yet does not fail as "missing file"; it fails as "your certificate
 * is a folder", several layers from the cause. The message below says which
 * variable, which path inside the container, what was found there, and what to
 * run.
 * </p>
 */
function requiredFile(name: string): string {
  const path = required(name);

  let stats;
  try {
    stats = statSync(path);
  } catch (error) {
    const reason = (error as NodeJS.ErrnoException).code === 'ENOENT'
      ? 'nothing exists at that path'
      : String(error);

    throw new Error(
      `${name}=${path} cannot be read: ${reason}.\n`
      + '  This path is a bind mount from the host. Check the matching\n'
      + '  TLS_CERT_FILE / TLS_KEY_FILE in your .env, and run\n'
      + '  ./scripts/dev-cert.sh deploy/tls if you have not generated a\n'
      + '  certificate yet.',
    );
  }

  if (stats.isDirectory()) {
    throw new Error(
      `${name}=${path} is a DIRECTORY, not a certificate file.\n`
      + '  Docker creates a directory at a bind mount whose host path does not\n'
      + '  exist, so this almost always means TLS_CERT_FILE or TLS_KEY_FILE in\n'
      + '  your .env points at a file that has not been generated.\n'
      + '\n'
      + '  Fix it on the host:\n'
      + '    docker compose -f docker-compose.yml -f deploy/docker-compose.dev-oidc.yml down\n'
      + '    rm -rf deploy/tls/cert.pem deploy/tls/key.pem   # the empty directories Docker made\n'
      + '    ./scripts/dev-cert.sh deploy/tls',
    );
  }

  return path;
}

/**
 * The accounts the chooser offers.
 *
 * @remarks
 * A list, not a default subject. The harness issuer has no implicit identity on
 * purpose — with one, signing out and signing back in returns the same person,
 * and §7's account switch becomes untestable. The same property is worth having
 * by hand: this stack can demonstrate two people sharing a document from two
 * browser profiles.
 */
const accounts = (process.env['DEV_OIDC_ACCOUNTS'] ?? 'alice,bob')
  .split(',')
  .map((who) => who.trim())
  .filter((who) => who !== '');

const port = Number(required('DEV_OIDC_PORT'));
if (!Number.isInteger(port) || port <= 0 || port > 65_535) {
  throw new Error(`DEV_OIDC_PORT must be a port number, not ${JSON.stringify(process.env['DEV_OIDC_PORT'])}`);
}

const issuerHost = required('DEV_OIDC_HOST');
const appOrigin = required('DEV_OIDC_APP_ORIGIN');

const oidc = await startOidc({
  // 0.0.0.0, because the whole point is that another container and a browser
  // on the host both reach it.
  bind: '0.0.0.0',
  port,

  // ONE hostname, resolving to this service from both sides of the container
  // boundary. An OIDC issuer is an absolute URL that the browser, the API and
  // the token's `iss` claim must all agree on exactly, and the metadata
  // document's `jwks_uri` is derived from it — so a URL that only works from
  // one side leaves the API fetching signing keys from itself. The README says
  // which line goes in /etc/hosts and why there is no way around it.
  reachableAs: issuerHost,

  // The certificate the developer already trusts for the application. Not one
  // generated per run: that would put an untrusted issuer in front of the
  // browser on every `docker compose up`, and the ways past that are to trust
  // a new key each time or to stop validating — the second being what §7
  // forbids.
  certFile: requiredFile('DEV_OIDC_CERT_FILE'),
  keyFile: requiredFile('DEV_OIDC_KEY_FILE'),
});

// What a test sets programmatically, set here from configuration. These are
// exact-match allow-lists in the issuer, not patterns: §7 treats a redirect URI
// echoed back without checking as an open redirect, and that check is the same
// one here.
oidc.redirectUris.add(`${appOrigin}/callback`);
oidc.redirectUris.add(`${appOrigin}/signed-out`);
oidc.origins.add(appOrigin);

for (const who of accounts) {
  oidc.accounts.add(who);
}

/**
 * Writes the CA bundle and then proves it is there.
 *
 * @remarks
 * <p>
 * <b>§13.15: the check is on the effect, not on control reaching the next
 * line.</b> This write is the one thing this process does for anybody else —
 * the API trusts the issuer's certificate through `SSL_CERT_FILE` pointing at
 * it, and nothing else produces it. A write that fails quietly would leave the
 * API with no bundle and surface, much later and somewhere else, as the API
 * being unable to verify the issuer's TLS.
 * </p><p>
 * <b>EACCES is the expected failure, not an exotic one.</b> Docker initialises
 * an empty named volume from the image's mount point, ownership included — so
 * a volume created before the image had `/run/oidc-ca` is owned by root, this
 * container runs as `node`, and the write is refused. The message says that,
 * because the fix is a `down -v` that nobody guesses from `errno: -13`.
 * </p>
 */
function writeBundle(target: string, contents: Buffer): void {
  try {
    writeFileSync(target, contents);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'EACCES') {
      throw new Error(
        `DEV_OIDC_CA_OUT=${target} is not writable by this container.\n`
        + '  It is the `oidc-ca` named volume, and Docker gave it root ownership\n'
        + '  because it was created before the image declared that directory.\n'
        + '  This container runs as `node`, deliberately, so it cannot write there.\n'
        + '\n'
        + '  Recreate the volume once — `down -v` removes it, and the rebuilt\n'
        + '  image creates it owned correctly:\n'
        + '    docker compose -f docker-compose.yml -f deploy/docker-compose.dev-oidc.yml down -v\n'
        + '    docker compose -f docker-compose.yml -f deploy/docker-compose.dev-oidc.yml up --build',
        { cause: error },
      );
    }

    throw error;
  }

  // READ IT BACK. A short write, a full filesystem or a path that silently
  // resolved somewhere else all leave writeFileSync having returned normally.
  const written = readFileSync(target);
  if (written.length !== contents.length) {
    throw new Error(
      `${target} was written but holds ${written.length} bytes, not ${contents.length}.`,
    );
  }

  if (!written.includes('BEGIN CERTIFICATE')) {
    throw new Error(
      `${target} was written and contains no certificate. The API would trust`
      + ' nothing and fail to verify the issuer.',
    );
  }
}

// The system roots PLUS this certificate, which is what the API is pointed at
// through SSL_CERT_FILE. `startOidc` already assembles it; this copies it to
// the shared volume the API reads, because SSL_CERT_FILE names one file and
// replacing the system store would leave the API unable to reach anything else.
const bundle = required('DEV_OIDC_CA_OUT');
writeBundle(bundle, readFileSync(oidc.caFile));

// eslint-disable-next-line no-console
console.log(
  [
    'dev OIDC issuer — LOCAL DEVELOPMENT ONLY',
    `  issuer     ${oidc.issuer}`,
    `  metadata   ${oidc.metadataAddress}`,
    `  accounts   ${accounts.join(', ')}`,
    `  redirects  ${[...oidc.redirectUris].join(', ')}`,
    `  ca bundle  ${bundle}`,
  ].join('\n'),
);

// Nothing to wait for: the server holds the event loop open. A signal handler
// so `docker compose down` is a clean close rather than a kill.
for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.on(signal, () => {
    void oidc.close().then(() => process.exit(0));
  });
}
