import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
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

/** The last `lines` lines, because a failure is at the end and a build log is not. */
function tail(text: string, lines: number): string {
  return text.split('\n').slice(-lines).join('\n').trim();
}

/** An address on this host that a container can route to. */
function hostAddress(): string {
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

  // The proxy's certificate, made by the same script a developer would run.
  execFileSync(join(REPO, 'scripts/dev-cert.sh'), { cwd: REPO, stdio: 'ignore' });
  const proxyPin = spkiOf(join(REPO, 'deploy/tls/cert.pem'));

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
    const logs = compose([...files, 'logs', '--no-color', '--tail', '60'], env).output;
    throw new Error(
      'the stack did not come up.\n'
      + `--- the last of what the build said ---\n${tail(up.output, 60)}\n`
      + `--- what the services said ---\n${tail(logs, 60) || '(no service ever started)'}`,
    );
  }

  const baseUrl = `https://${host}:${port}`;
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
