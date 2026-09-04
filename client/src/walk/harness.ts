import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, writeFileSync } from 'node:fs';
import { networkInterfaces, tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

import { startOidc, CLIENT_ID, type Oidc } from '../interop/harness';
import { startBrowser, type Browsing } from '../e2e/browser';

const REPO = resolve(import.meta.dirname, '../../..');

/**
 * The walk's stack: `docker compose up`, and nothing else (§13.27, §12).
 *
 * @remarks
 * <p>
 * §12's rule for this phase is that nothing is verified by anything running
 * outside the Compose stack. So the schema is not applied here, the client is
 * not built here, no row is inserted here, and the API is not started here —
 * every one of those is the deployment's job, and doing any of them from the
 * test is how eleven phases of green accumulated over an artefact that could
 * not start (§13.28).
 * </p><p>
 * The one thing supplied from outside is the identity provider, because a real
 * deployment's is outside too. It runs on the host and is reached by the host's
 * own address, so the browser and the containerised API resolve the same
 * absolute issuer URL — an OIDC issuer that differs between the two fails at
 * token validation rather than at startup.
 * </p>
 */
export interface Walk {
  readonly oidc: Oidc;
  readonly browsing: Browsing;

  /** Where a browser reaches the stack. TLS, through the proxy (§4). */
  readonly baseUrl: string;

  logs(): string;
  close(): Promise<void>;
}

/** Polls until the URL answers, or throws saying how long it waited (§13.23). */
async function reachable(url: string, deadlineMs: number): Promise<void> {
  const deadline = Date.now() + deadlineMs;
  let last = 'never attempted';

  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.ok) {
        return;
      }

      last = `HTTP ${response.status}`;
    } catch (error) {
      last = error instanceof Error ? error.message : String(error);
    }

    await new Promise((done) => setTimeout(done, 500));
  }

  throw new Error(`${url} never answered in ${deadlineMs}ms; last attempt said: ${last}`);
}

/** The last `lines` lines, because a failure is at the end and a build log is not. */
function tail(text: string, lines: number): string {
  return text.split('\n').slice(-lines).join('\n').trim();
}

/**
 * The address this stack is reached by.
 *
 * @remarks
 * Supplied by `scripts/walk.sh` when it runs, and that is load-bearing: the
 * script generates the TLS certificate naming this address and exports
 * `NODE_EXTRA_CA_CERTS` so this process trusts it, both of which must happen
 * before Node starts. Two independent computations of "the host's address" that
 * disagreed would produce a certificate for one address and a request to
 * another — which surfaces as an unhelpful `fetch failed`, and did.
 */
function hostAddress(): string {
  const supplied = process.env['WALK_HOST'];
  if (supplied !== undefined && supplied !== '') {
    return supplied;
  }

  for (const addresses of Object.values(networkInterfaces())) {
    for (const address of addresses ?? []) {
      if (address.family === 'IPv4' && !address.internal) {
        return address.address;
      }
    }
  }

  throw new Error('No non-loopback IPv4 address; a container cannot reach this host.');
}

function compose(args: string[], env: NodeJS.ProcessEnv): { status: number; output: string } {
  const result = spawnSync('docker', ['compose', ...args], {
    cwd: REPO,
    encoding: 'utf8',
    env: { ...process.env, ...env },
  });

  return { status: result.status ?? -1, output: `${result.stdout ?? ''}${result.stderr ?? ''}` };
}

/** SHA-256 of a certificate's SubjectPublicKeyInfo, base64 — what Chromium pins. */
function spkiOf(certFile: string): string {
  const pem = spawnSync('openssl', ['x509', '-in', certFile, '-pubkey', '-noout'], {
    encoding: 'utf8',
  });

  if (pem.status !== 0) {
    throw new Error(`openssl x509 failed: ${pem.stderr}`);
  }

  const der = Buffer.from(
    pem.stdout.replace(/-----[A-Z ]+-----/g, '').replace(/\s+/g, ''),
    'base64',
  );

  return createHash('sha256').update(der).digest('base64');
}

export async function startWalk(): Promise<Walk> {
  const host = hostAddress();
  const port = Number(process.env['WALK_HTTPS_PORT'] ?? 8443);

  // The issuer a real deployment would have, outside the stack.
  const oidc = await startOidc({
    bind: '0.0.0.0',
    reachableAs: host,
    sans: [`IP:${host}`],
  });

  // Made by scripts/walk.sh before this process started, because Node reads
  // NODE_EXTRA_CA_CERTS at startup and the certificate has to name `host`.
  const certFile = join(REPO, 'deploy/tls/cert.pem');
  if (!existsSync(certFile)) {
    throw new Error(
      `${certFile} does not exist. Run the walk through scripts/walk.sh, which makes `
      + 'the certificate for this host and trusts it before Node starts.',
    );
  }

  const proxyPin = spkiOf(certFile);

  const work = mkdtempSync(join(tmpdir(), 'editor-walk-'));
  const envFile = join(work, 'walk.env');
  writeFileSync(envFile, [
    'POSTGRES_DB=editor',
    'POSTGRES_USER=editor',
    `POSTGRES_PASSWORD=${Math.random().toString(36).slice(2)}${Date.now()}`,
    `HTTPS_PORT=${port}`,
    'TLS_CERT_FILE=./deploy/tls/cert.pem',
    'TLS_KEY_FILE=./deploy/tls/key.pem',
    'PROXY_ADDRESS=0.0.0.0/0',
    `OIDC_ISSUER=${oidc.issuer}`,
    'OIDC_AUDIENCE=editor-api',
    `OIDC_METADATA_ADDRESS=${oidc.metadataAddress}`,
    `OIDC_CLIENT_ID=${CLIENT_ID}`,
    'REDIS_CONFIGURATION=redis:6379',
    `WALK_CA_FILE=${oidc.caFile}`,
    '',
  ].join('\n'));

  const env = { COMPOSE_ENV_FILE: envFile };
  const files = ['-f', 'docker-compose.yml', '-f', 'deploy/docker-compose.walk.yml',
    '--env-file', envFile];

  // A FRESH VOLUME, ASSERTED RATHER THAN ASSUMED. Compose volumes persist, so a
  // migration test runs forever against a database an earlier run migrated and
  // breaks only on somebody's first clone. A precondition a test depends on is
  // a thing the test checks (§12).
  compose([...files, 'down', '--volumes', '--remove-orphans'], env);

  const volumes = spawnSync('docker', ['volume', 'ls', '--quiet'], { encoding: 'utf8' });
  const survivors = (volumes.stdout ?? '').split('\n')
    .filter((name) => name.includes('collaborative-editor'));

  if (survivors.length > 0) {
    throw new Error(
      `down --volumes left ${survivors.join(', ')} behind, so the schema this walk `
      + 'is about to test may have been applied by an earlier run rather than by this one.',
    );
  }

  // --progress plain: the tty renderer redraws layer percentages tens of
  // thousands of times and buries the one line that says why (§13.23), while
  // `quiet` suppresses the build's own stdout — which is where the reason
  // actually is. Plain prints each step's output once, linearly. The harness is
  // not exempt from the rule it exists to enforce, in either direction.
  const up = compose(
    ['--progress', 'plain', ...files, 'up', '--build', '--detach', '--wait', '--wait-timeout', '420'],
    env,
  );

  if (up.status !== 0) {
    // This project's own services only. Postgres alone emits forty lines of
    // initdb chatter that has never once been the cause, and it interleaves
    // with the lines that are (§13.23).
    const logs = compose(
      [...files, 'logs', '--no-color', '--tail', '40', 'migrator', 'api', 'proxy'],
      env,
    ).output;

    // Services first, because once the images build the cause is always in a
    // container and never in the layer stream — and the layer stream is a
    // thousand times longer. §13.23, learned twice: the previous version of
    // this message led with the build and truncated the exception that
    // explained everything.
    throw new Error(
      'the stack did not come up.\n'
      + `--- what the services said ---\n${logs.trim() || '(no service ever started)'}\n`
      + `--- how compose ended ---\n${tail(up.output, 12)}`,
    );
  }

  const baseUrl = `https://${host}:${port}`;

  // The proxy has no container healthcheck (see docker-compose.yml), so `--wait`
  // returns when it is running rather than when it is serving. Waited on here
  // from outside, over TLS, which is the assertion that matters anyway.
  await reachable(`${baseUrl}/health/live`, 60_000);

  oidc.redirectUris.add(`${baseUrl}/callback`);
  oidc.origins.add(baseUrl);

  // Both certificates are PINNED, never ignored: the issuer's and the proxy's.
  // `ignoreHTTPSErrors` would switch validation off for the whole browser, and
  // this is the one phase whose subject is whether TLS is actually there (§4).
  const browsing = await startBrowser(oidc, [proxyPin]);

  return {
    oidc,
    browsing,
    baseUrl,
    logs: () => compose([...files, 'logs', '--no-color', '--tail', '80'], env).output,
    async close() {
      await browsing.close();
      compose([...files, 'down', '--volumes', '--remove-orphans'], env);
      await oidc.close();
    },
  };
}
