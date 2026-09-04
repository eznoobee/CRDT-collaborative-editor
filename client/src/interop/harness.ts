import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { createSign, generateKeyPairSync, randomUUID } from 'node:crypto';
import { createServer, type Server } from 'node:https';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import type { AddressInfo } from 'node:net';

/**
 * A real server, a real socket, and real tokens (PROJECT_SPEC.md §7, §8).
 *
 * Everything above this file talks to the C# core through a test fixture or to
 * the server through an in-memory transport. This starts the published API as
 * its own process and connects the TypeScript core to it over TCP, which is the
 * only arrangement in which "the two implementations interoperate" is a claim
 * about the shipped artefacts rather than about a shared test harness.
 *
 * Authentication is real too, and that is a deliberate cost. §7 forbids
 * disabling a token check anywhere, dev configuration included, and
 * `RequireHttpsMetadata` stays at its secure default here — so the harness
 * serves OIDC metadata over genuine HTTPS from a certificate it generates, and
 * hands the API that certificate through `SSL_CERT_FILE`. The alternative was a
 * development bypass in the product, which is a permanent weakness bought to
 * make a test easier.
 */

const REPO = resolve(import.meta.dirname, '../../..');

interface Signing {
  readonly privateKey: string;
  readonly jwk: Record<string, unknown>;
  readonly kid: string;
}

/** The OIDC issuer this run trusts, and the tokens it mints. */
export interface Oidc {
  readonly issuer: string;
  readonly metadataAddress: string;
  readonly caFile: string;
  mint(subject: string): string;
  close(): Promise<void>;
}

function base64url(value: Buffer | string): string {
  return Buffer.from(value).toString('base64url');
}

function keys(): Signing {
  const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const jwk = publicKey.export({ format: 'jwk' }) as Record<string, unknown>;
  const kid = randomUUID();
  return {
    privateKey: privateKey.export({ type: 'pkcs8', format: 'pem' }).toString(),
    jwk: { ...jwk, kid, alg: 'RS256', use: 'sig' },
    kid,
  };
}

/** A certificate for 127.0.0.1, so the metadata really is served over TLS. */
function certificate(directory: string): { certFile: string; keyFile: string } {
  const certFile = join(directory, 'oidc-cert.pem');
  const keyFile = join(directory, 'oidc-key.pem');

  const openssl = spawnSync(
    'openssl',
    [
      'req', '-x509', '-newkey', 'rsa:2048', '-sha256', '-days', '1', '-nodes',
      '-keyout', keyFile, '-out', certFile,
      '-subj', '/CN=127.0.0.1',
      '-addext', 'subjectAltName=IP:127.0.0.1,DNS:localhost',
    ],
    { encoding: 'utf8' },
  );

  if (openssl.status !== 0) {
    throw new Error(`openssl failed: ${openssl.stderr}`);
  }

  return { certFile, keyFile };
}

/** Starts the issuer: discovery, JWKS, and a signer for this run only. */
export async function startOidc(): Promise<Oidc> {
  const directory = mkdtempSync(join(tmpdir(), 'editor-interop-'));
  const { certFile, keyFile } = certificate(directory);
  const signing = keys();

  // The system roots plus this run's certificate. Replacing the system store
  // rather than adding to it would leave the API unable to reach anything else.
  const caFile = join(directory, 'ca-bundle.pem');
  const system = ['/etc/ssl/certs/ca-certificates.crt', '/etc/pki/tls/certs/ca-bundle.crt']
    .map((path) => {
      try {
        return readFileSync(path, 'utf8');
      } catch {
        return '';
      }
    })
    .join('\n');
  writeFileSync(caFile, `${system}\n${readFileSync(certFile, 'utf8')}`);

  let issuer = '';
  const server: Server = createServer(
    { cert: readFileSync(certFile), key: readFileSync(keyFile) },
    (request, response) => {
      const json = (body: unknown) => {
        response.writeHead(200, { 'content-type': 'application/json' });
        response.end(JSON.stringify(body));
      };

      if (request.url?.startsWith('/.well-known/openid-configuration')) {
        json({
          issuer,
          jwks_uri: `${issuer}/jwks.json`,
          authorization_endpoint: `${issuer}/authorize`,
          token_endpoint: `${issuer}/token`,
          response_types_supported: ['code'],
          subject_types_supported: ['public'],
          id_token_signing_alg_values_supported: ['RS256'],
        });
        return;
      }

      if (request.url?.startsWith('/jwks.json')) {
        json({ keys: [signing.jwk] });
        return;
      }

      response.writeHead(404).end();
    },
  );

  await new Promise<void>((done) => server.listen(0, '127.0.0.1', done));
  const { port } = server.address() as AddressInfo;
  issuer = `https://127.0.0.1:${port}`;

  return {
    issuer,
    metadataAddress: `${issuer}/.well-known/openid-configuration`,
    caFile,
    mint(subject: string): string {
      const now = Math.floor(Date.now() / 1000);
      const header = base64url(JSON.stringify({ alg: 'RS256', typ: 'JWT', kid: signing.kid }));
      const payload = base64url(
        JSON.stringify({
          iss: issuer,
          aud: 'editor-api',
          sub: subject,
          // §7 requires an expiry and validates it with no clock skew, so a
          // token minted here is genuinely short-lived.
          iat: now - 5,
          nbf: now - 5,
          exp: now + 300,
        }),
      );

      const signer = createSign('RSA-SHA256');
      signer.update(`${header}.${payload}`);
      return `${header}.${payload}.${signer.sign(signing.privateKey, 'base64url')}`;
    },
    close: () => new Promise<void>((done) => server.close(() => done())),
  };
}

/** One running instance of the published API. */
export interface Api {
  readonly baseUrl: string;
  close(): Promise<void>;
}

async function reachable(url: string, deadlineMs: number): Promise<boolean> {
  const deadline = Date.now() + deadlineMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.ok) {
        return true;
      }
    } catch {
      // Not listening yet.
    }

    await new Promise((done) => setTimeout(done, 100));
  }

  return false;
}

/**
 * Waits for Kestrel to say which port it actually bound.
 *
 * @remarks
 * Asked for rather than guessed. Picking a port and hoping is a harness that
 * fails for a reason unrelated to anything it tests, and the failure it produces
 * — "API did not start" — looks identical to a genuine startup bug, so the one
 * time it matters nobody can tell them apart. Port 0 makes the kernel choose and
 * Kestrel announce, which cannot collide.
 */
function listeningOn(log: string[], deadlineMs: number): Promise<string> {
  const deadline = Date.now() + deadlineMs;

  return new Promise((resolve, reject) => {
    const poll = setInterval(() => {
      const found = /Now listening on:\s*(http:\/\/\S+)/.exec(log.join(''));
      if (found?.[1] !== undefined) {
        clearInterval(poll);
        resolve(found[1].replace(/\/$/, ''));
        return;
      }

      if (Date.now() >= deadline) {
        clearInterval(poll);
        reject(new Error('never announced a listening address'));
      }
    }, 50);
  });
}

/**
 * Starts the API as its own process.
 *
 * @remarks
 * The built assembly is run directly rather than through `dotnet run`, which
 * would put a build and a launcher process between the test and the thing under
 * test — and would make killing the instance kill the launcher instead.
 */
export async function startApi(oidc: Oidc, log: string[]): Promise<Api> {
  const postgres = process.env.EDITOR_TEST_POSTGRES;
  const redis = process.env.EDITOR_TEST_REDIS;

  if (!postgres || !redis) {
    throw new Error('EDITOR_TEST_POSTGRES and EDITOR_TEST_REDIS must be set.');
  }

  const dll = join(REPO, 'src/Editor.Api/bin/Debug/net10.0/Editor.Api.dll');
  const started = Date.now();

  const child: ChildProcess = spawn('dotnet', [dll], {
    cwd: join(REPO, 'src/Editor.Api'),
    env: {
      ...process.env,
      // Port 0: the kernel picks, Kestrel announces, and nothing here guesses.
      ASPNETCORE_URLS: 'http://127.0.0.1:0',
      ASPNETCORE_ENVIRONMENT: 'Development',
      SSL_CERT_FILE: oidc.caFile,
      Oidc__Issuer: oidc.issuer,
      Oidc__Audience: 'editor-api',
      Oidc__MetadataAddress: oidc.metadataAddress,
      Postgres__ConnectionString: postgres,
      Redis__Configuration: redis,
      Logging__LogLevel__Default: 'Warning',

      // The one category kept at Information, because the address it prints is
      // how this harness learns where to connect.
      'Logging__LogLevel__Microsoft.Hosting.Lifetime': 'Information',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  child.stdout?.on('data', (chunk: Buffer) => log.push(chunk.toString()));
  child.stderr?.on('data', (chunk: Buffer) => log.push(chunk.toString()));

  // Everything the next person needs to tell a real startup bug from a sick
  // runner: how long it waited, whether the process died, and what it said.
  const died = () => (child.exitCode === null && child.signalCode === null
    ? 'still running'
    : `exited with code ${child.exitCode} signal ${child.signalCode}`);
  const evidence = (why: string) =>
    new Error(
      `API did not start (${why}) after ${Date.now() - started}ms; process ${died()}.\n`
      + `${log.join('') || '(the process logged nothing at all)'}`,
    );

  let baseUrl: string;
  try {
    baseUrl = await listeningOn(log, 60_000);
  } catch {
    child.kill('SIGKILL');
    throw evidence('never announced a listening address');
  }

  if (!(await reachable(`${baseUrl}/health/live`, 30_000))) {
    child.kill('SIGKILL');
    throw evidence(`${baseUrl}/health/live never answered`);
  }

  return {
    baseUrl,
    close: () =>
      new Promise<void>((done) => {
        if (child.exitCode !== null) {
          done();
          return;
        }

        child.once('exit', () => done());
        child.kill('SIGKILL');
      }),
  };
}

/** A document and its memberships, written the way an admin path would. */
export function seed(
  issuer: string,
  members: readonly { subject: string; role: 'viewer' | 'editor' | 'owner' }[],
): string {
  const roles = { viewer: 0, editor: 1, owner: 2 };
  const documentId = randomUUID();
  const owner = randomUUID();

  const statements = [
    `INSERT INTO users (id, oidc_issuer, oidc_subject, display_name, created_at)
       VALUES ('${owner}', '${issuer}', 'interop-owner-${owner}', 'owner', now());`,
    `INSERT INTO documents (id, owner_id, title, created_at, updated_at)
       VALUES ('${documentId}', '${owner}', 'interop', now(), now());`,
  ];

  for (const member of members) {
    const id = randomUUID();
    statements.push(
      `INSERT INTO users (id, oidc_issuer, oidc_subject, display_name, created_at)
         VALUES ('${id}', '${issuer}', '${member.subject}', '${member.subject}', now());`,
      `INSERT INTO document_members (document_id, user_id, role, granted_at, granted_by)
         VALUES ('${documentId}', '${id}', ${roles[member.role]}, now(), '${owner}');`,
    );
  }

  const postgres = process.env.EDITOR_TEST_POSTGRES ?? '';
  const value = (key: string) =>
    postgres.split(';').find((part) => part.toLowerCase().startsWith(`${key}=`))?.split('=')[1] ?? '';

  const psql = spawnSync(
    'psql',
    [
      '-h', value('host') || 'localhost',
      '-p', value('port') || '5432',
      '-U', value('username') || 'editor',
      '-d', value('database') || 'editor',
      '-v', 'ON_ERROR_STOP=1',
      '-c', statements.join('\n'),
    ],
    { encoding: 'utf8', env: { ...process.env, PGPASSWORD: value('password') } },
  );

  if (psql.status !== 0) {
    throw new Error(`seed failed: ${psql.stderr}`);
  }

  return documentId;
}
