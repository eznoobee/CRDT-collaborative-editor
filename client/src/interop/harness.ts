import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { createHash, createSign, generateKeyPairSync, randomUUID, timingSafeEqual } from 'node:crypto';
import { createServer, type Server } from 'node:https';
import type { IncomingMessage, ServerResponse } from 'node:http';
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

/** The client id the harness issuer has registered. */
export const CLIENT_ID = 'editor-spa';

/** The OIDC issuer this run trusts, and the tokens it mints. */
export interface Oidc {
  readonly issuer: string;
  readonly metadataAddress: string;
  readonly caFile: string;

  /** SHA-256 of the certificate's SubjectPublicKeyInfo, base64. */
  readonly spki: string;

  /** Mints an access token directly, bypassing the code flow. */
  mint(subject: string, expiresInSeconds?: number): string;

  /** Redirect URIs the issuer will accept, exactly. Mutable per test. */
  readonly redirectUris: Set<string>;

  /** Browser origins the token endpoint answers CORS preflights for. */
  readonly origins: Set<string>;

  /** Access-token lifetime for the code and refresh grants, in seconds. */
  accessTokenLifetime: number;

  /**
   * Who `/authorize` signs in when the request carries no `login_hint`.
   *
   * @remarks
   * This is how a test chooses a user, and it is deliberately here rather than
   * in the application: an app that read its subject from a query parameter
   * would be shipping a test affordance, and the browser test would then be
   * exercising a path no user takes. A real issuer asks; this one is told.
   */
  subject: string;

  /** Every token request this issuer has answered or refused. */
  readonly tokenRequests: TokenRequest[];

  close(): Promise<void>;
}

/** One call to the token endpoint, as the issuer saw it. */
export interface TokenRequest {
  readonly grant: string;
  readonly form: Record<string, string>;

  /** `issued`, or the OAuth error code it was refused with. */
  readonly outcome: string;
}

/** An authorization code the issuer has handed out and not yet redeemed. */
interface PendingCode {
  readonly challenge: string;
  readonly subject: string;
  readonly redirectUri: string;
  readonly nonce: string | null;
  readonly expiresAt: number;
  used: boolean;
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

/**
 * SHA-256 of the certificate's SubjectPublicKeyInfo, base64.
 *
 * @remarks
 * What a browser is told to trust, and nothing wider. Chromium's
 * `--ignore-certificate-errors-spki-list` pins exactly this key; the
 * alternative on offer, `ignoreHTTPSErrors`, turns certificate validation off
 * for the whole browser, which would make every later assertion about TLS in
 * these tests meaningless. Same reasoning as `SSL_CERT_FILE` for the API: name
 * the certificate, do not disable the check.
 */
function spkiPin(certFile: string): string {
  const spki = spawnSync(
    'openssl',
    ['x509', '-in', certFile, '-pubkey', '-noout'],
    { encoding: 'utf8' },
  );

  if (spki.status !== 0) {
    throw new Error(`openssl x509 failed: ${spki.stderr}`);
  }

  const der = Buffer.from(
    spki.stdout.replace(/-----[A-Z ]+-----/g, '').replace(/\s+/g, ''),
    'base64',
  );

  return createHash('sha256').update(der).digest('base64');
}

/** Reads a form-encoded request body. */
function formBody(request: IncomingMessage): Promise<Record<string, string>> {
  return new Promise((resolve_) => {
    let body = '';
    request.on('data', (chunk: Buffer) => (body += chunk.toString()));
    request.on('end', () => {
      const form: Record<string, string> = {};
      for (const [key, value] of new URLSearchParams(body)) {
        form[key] = value;
      }

      resolve_(form);
    });
  });
}

/** RFC 7636's S256 transformation, which is the whole of PKCE. */
function challengeFor(verifier: string): string {
  return createHash('sha256').update(verifier).digest('base64url');
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
  const codes = new Map<string, PendingCode>();
  const refreshTokens = new Map<string, string>();
  const redirectUris = new Set<string>();
  const origins = new Set<string>();
  const tokenRequests: TokenRequest[] = [];
  const state = { accessTokenLifetime: 300, subject: 'anonymous' };

  function sign(claims: Record<string, unknown>): string {
    const header = base64url(JSON.stringify({ alg: 'RS256', typ: 'JWT', kid: signing.kid }));
    const payload = base64url(JSON.stringify(claims));
    const signer = createSign('RSA-SHA256');
    signer.update(`${header}.${payload}`);
    return `${header}.${payload}.${signer.sign(signing.privateKey, 'base64url')}`;
  }

  function accessToken(subject: string, lifetime: number): string {
    const now = Math.floor(Date.now() / 1000);
    return sign({
      iss: issuer,
      aud: 'editor-api',
      sub: subject,
      // §7 requires an expiry and validates it with no clock skew, so a token
      // minted here is genuinely short-lived.
      iat: now - 5,
      nbf: now - 5,
      exp: now + lifetime,
    });
  }

  const server: Server = createServer(
    { cert: readFileSync(certFile), key: readFileSync(keyFile) },
    (request, response) => {
      const json = (body: unknown, status = 200) => {
        response.writeHead(status, {
          'content-type': 'application/json',
          'cache-control': 'no-store',
        });
        response.end(JSON.stringify(body));
      };

      const url = new URL(request.url ?? '/', issuer || 'https://127.0.0.1');

      // A real identity provider answers the browser's cross-origin token POST;
      // an allow-list of the origins this run actually serves, never '*'.
      const origin = request.headers.origin;
      if (typeof origin === 'string' && origins.has(origin)) {
        response.setHeader('access-control-allow-origin', origin);
        response.setHeader('vary', 'origin');
      }

      if (request.method === 'OPTIONS') {
        response.setHeader('access-control-allow-methods', 'POST, GET, OPTIONS');
        response.setHeader('access-control-allow-headers', 'content-type');
        response.writeHead(204).end();
        return;
      }

      if (url.pathname.startsWith('/.well-known/openid-configuration')) {
        json({
          issuer,
          jwks_uri: `${issuer}/jwks.json`,
          authorization_endpoint: `${issuer}/authorize`,
          token_endpoint: `${issuer}/token`,
          response_types_supported: ['code'],
          grant_types_supported: ['authorization_code', 'refresh_token'],
          subject_types_supported: ['public'],
          id_token_signing_alg_values_supported: ['RS256'],
          code_challenge_methods_supported: ['S256'],
          scopes_supported: ['openid', 'profile', 'offline_access'],
        });
        return;
      }

      if (url.pathname.startsWith('/jwks.json')) {
        json({ keys: [signing.jwk] });
        return;
      }

      if (url.pathname === '/authorize') {
        authorize(url, response);
        return;
      }

      if (url.pathname === '/token' && request.method === 'POST') {
        void formBody(request).then((form) => token(form, json));
        return;
      }

      response.writeHead(404).end();
    },
  );

  /**
   * The authorization endpoint, auto-approving whoever `login_hint` names.
   *
   * @remarks
   * There is no consent screen and no password, because neither is this
   * project's. What is *not* skipped is every check that makes the code flow a
   * flow: the redirect URI is matched exactly against a registered list, S256
   * is the only challenge method accepted, and a request without a challenge is
   * refused outright. A harness that waved those through would let a client
   * that never computed a challenge pass its PKCE test.
   */
  function authorize(url: URL, response: ServerResponse): void {
    const redirectUri = url.searchParams.get('redirect_uri') ?? '';
    const requestedState = url.searchParams.get('state');
    const challenge = url.searchParams.get('code_challenge');
    const method = url.searchParams.get('code_challenge_method');

    // Answered here rather than by redirect: an unregistered redirect URI is
    // the one error that must not be sent to the URI in question.
    if (!redirectUris.has(redirectUri)) {
      response.writeHead(400, { 'content-type': 'text/plain' });
      response.end(`unregistered redirect_uri: ${redirectUri}`);
      return;
    }

    const fail = (error: string) => {
      const back = new URL(redirectUri);
      back.searchParams.set('error', error);
      if (requestedState !== null) {
        back.searchParams.set('state', requestedState);
      }

      response.writeHead(302, { location: back.toString() }).end();
    };

    if (url.searchParams.get('response_type') !== 'code') {
      fail('unsupported_response_type');
      return;
    }

    if (challenge === null || method !== 'S256') {
      fail('invalid_request');
      return;
    }

    const code = randomUUID();
    codes.set(code, {
      challenge,
      subject: url.searchParams.get('login_hint') ?? state.subject,
      redirectUri,
      nonce: url.searchParams.get('nonce'),
      expiresAt: Date.now() + 60_000,
      used: false,
    });

    const back = new URL(redirectUri);
    back.searchParams.set('code', code);
    if (requestedState !== null) {
      back.searchParams.set('state', requestedState);
    }

    response.writeHead(302, { location: back.toString() }).end();
  }

  /** The token endpoint, which is where PKCE is actually enforced. */
  function token(
    form: Record<string, string>,
    json: (body: unknown, status?: number) => void,
  ): void {
    const record = (outcome: string) =>
      tokenRequests.push({ grant: form['grant_type'] ?? '', form, outcome });

    const refuse = (error: string) => {
      record(error);
      json({ error }, 400);
    };

    if (form['grant_type'] === 'refresh_token') {
      const subject = refreshTokens.get(form['refresh_token'] ?? '');
      if (subject === undefined) {
        refuse('invalid_grant');
        return;
      }

      record('issued');
      json({
        access_token: accessToken(subject, state.accessTokenLifetime),
        token_type: 'Bearer',
        expires_in: state.accessTokenLifetime,
        refresh_token: form['refresh_token'],
      });
      return;
    }

    if (form['grant_type'] !== 'authorization_code') {
      refuse('unsupported_grant_type');
      return;
    }

    const pending = codes.get(form['code'] ?? '');
    if (pending === undefined || pending.used || pending.expiresAt < Date.now()) {
      refuse('invalid_grant');
      return;
    }

    if (pending.redirectUri !== form['redirect_uri']) {
      refuse('invalid_grant');
      return;
    }

    // THE CHECK. Everything else here is bookkeeping; this line is what makes a
    // stolen code useless to whoever stole it, and therefore the only reason
    // PKCE exists. Compared in constant time because it is a secret comparison,
    // and length-checked first because timingSafeEqual throws on a mismatch.
    const verifier = form['code_verifier'] ?? '';
    const derived = Buffer.from(challengeFor(verifier));
    const expected = Buffer.from(pending.challenge);
    if (derived.length !== expected.length || !timingSafeEqual(derived, expected)) {
      refuse('invalid_grant');
      return;
    }

    pending.used = true;

    const now = Math.floor(Date.now() / 1000);
    const refresh = randomUUID();
    refreshTokens.set(refresh, pending.subject);

    record('issued');
    json({
      access_token: accessToken(pending.subject, state.accessTokenLifetime),
      id_token: sign({
        iss: issuer,
        aud: form['client_id'] ?? CLIENT_ID,
        sub: pending.subject,
        iat: now - 5,
        nbf: now - 5,
        exp: now + 3600,
        ...(pending.nonce === null ? {} : { nonce: pending.nonce }),
      }),
      token_type: 'Bearer',
      expires_in: state.accessTokenLifetime,
      refresh_token: refresh,
      scope: 'openid profile offline_access',
    });
  }

  await new Promise<void>((done) => server.listen(0, '127.0.0.1', done));
  const { port } = server.address() as AddressInfo;
  issuer = `https://127.0.0.1:${port}`;

  return {
    issuer,
    metadataAddress: `${issuer}/.well-known/openid-configuration`,
    caFile,
    spki: spkiPin(certFile),
    redirectUris,
    origins,
    tokenRequests,

    get accessTokenLifetime(): number {
      return state.accessTokenLifetime;
    },
    set accessTokenLifetime(seconds: number) {
      state.accessTokenLifetime = seconds;
    },

    get subject(): string {
      return state.subject;
    },
    set subject(who: string) {
      state.subject = who;
    },

    mint: (subject: string, expiresInSeconds = 300) => accessToken(subject, expiresInSeconds),
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
export interface ApiOptions {
  /** Directory of a built client to serve from this origin (§9). */
  readonly spaRoot?: string;
}

export async function startApi(
  oidc: Oidc,
  log: string[],
  options: ApiOptions = {},
): Promise<Api> {
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
      Oidc__ClientId: CLIENT_ID,
      Oidc__MetadataAddress: oidc.metadataAddress,
      Postgres__ConnectionString: postgres,
      Redis__Configuration: redis,
      Logging__LogLevel__Default: 'Warning',
      ...(options.spaRoot === undefined ? {} : { Spa__RootPath: options.spaRoot }),

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
