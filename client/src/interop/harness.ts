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

/** The cookie the harness issuer keeps its session in. */
const SESSION_COOKIE = 'harness_session';

/**
 * The chooser's button for one account, as a Playwright selector.
 *
 * @remarks
 * Exported so the browser tests name the account the way a person would —
 * by clicking it — rather than by setting a variable the application cannot
 * see. A test that chose an identity out of band would prove nothing about
 * whether the session it established can be ended.
 */
export function accountButton(subject: string): string {
  return `[data-account="${subject}"]`;
}

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
   * The accounts the chooser offers. Mutable per test.
   *
   * @remarks
   * <p>
   * This replaces a single mutable `subject` that `/authorize` signed in
   * whenever nothing else said otherwise, and the replacement is the point.
   * With a default subject the issuer has no session to end: sign out, load
   * again, and the next `/authorize` mints for the same person because that is
   * what the default says to do — so a test of "the issuer's session ended"
   * would pass against a client that never called the end-session endpoint.
   * </p><p>
   * So identity here works the way it does at a real provider. A browser with
   * no session is shown a chooser and has to pick; picking establishes a
   * session cookie; a browser that has one is signed in silently, which is the
   * behaviour that makes signing out matter at all. Two browsers therefore have
   * two identities at the same time, which is what testing an account switch
   * needs.
   * </p>
   */
  readonly accounts: Set<string>;

  /** How many sessions this issuer currently holds. */
  readonly sessionCount: number;

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

/** How the issuer is reachable. Defaults to loopback, as every suite but the walk wants. */
export interface OidcOptions {
  /** Address to bind. `0.0.0.0` when containers must reach it. */
  readonly bind?: string;

  /** Extra `subjectAltName` entries, e.g. `IP:10.1.0.4`. */
  readonly sans?: readonly string[];

  /**
   * The host clients use to reach it, if not `127.0.0.1`.
   *
   * @remarks
   * An OIDC issuer is an absolute URL that both the browser and the API compare
   * exactly, so when they are on different sides of a container boundary there
   * has to be one name that works from both. Getting this wrong does not fail
   * at startup; it fails at token validation, as an issuer mismatch.
   */
  readonly reachableAs?: string;
}

/** A certificate for 127.0.0.1, so the metadata really is served over TLS. */
function certificate(
  directory: string,
  sans: readonly string[] = [],
): { certFile: string; keyFile: string } {
  const certFile = join(directory, 'oidc-cert.pem');
  const keyFile = join(directory, 'oidc-key.pem');

  const openssl = spawnSync(
    'openssl',
    [
      'req', '-x509', '-newkey', 'rsa:2048', '-sha256', '-days', '1', '-nodes',
      '-keyout', keyFile, '-out', certFile,
      '-subj', '/CN=127.0.0.1',
      '-addext', ['subjectAltName=IP:127.0.0.1,DNS:localhost', ...sans].join(','),
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
export async function startOidc(options: OidcOptions = {}): Promise<Oidc> {
  const directory = mkdtempSync(join(tmpdir(), 'editor-interop-'));
  const { certFile, keyFile } = certificate(directory, options.sans ?? []);
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
  const accounts = new Set<string>();

  // Cookie value -> subject. A real session, so that ending it is observable.
  const sessions = new Map<string, string>();
  const state = { accessTokenLifetime: 300 };

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
          end_session_endpoint: `${issuer}/logout`,
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
        authorize(url, request, response);
        return;
      }

      if (url.pathname === '/logout') {
        endSession(url, request, response);
        return;
      }

      if (url.pathname === '/token' && request.method === 'POST') {
        void formBody(request).then((form) => token(form, json));
        return;
      }

      response.writeHead(404).end();
    },
  );

  /** The subject this browser's session names, or null. */
  function sessionOf(request: IncomingMessage): string | null {
    const header = request.headers.cookie ?? '';
    for (const part of header.split(';')) {
      const [name, ...rest] = part.trim().split('=');
      if (name === SESSION_COOKIE) {
        return sessions.get(rest.join('=')) ?? null;
      }
    }

    return null;
  }

  /**
   * The account chooser: this issuer's login form.
   *
   * @remarks
   * A real page with real links, because the alternative — a default subject
   * the issuer signs in whenever nothing says otherwise — is what made "the
   * issuer's session ended" untestable. Each link re-runs the same
   * authorization request with a `login_hint`, so every parameter the client
   * sent, the PKCE challenge included, survives the choice.
   */
  function chooser(url: URL, response: ServerResponse): void {
    const options = [...accounts]
      .map((who) => {
        const pick = new URL(url.toString());
        pick.searchParams.set('login_hint', who);
        return `<li><a data-account="${who}" href="${pick.pathname}${pick.search}">${who}</a></li>`;
      })
      .join('');

    response.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
    response.end(`<!doctype html><title>Sign in</title><h1>Choose an account</h1><ul>${options}</ul>`);
  }

  /**
   * The end-session endpoint (§7).
   *
   * @remarks
   * Drops the session and sends the browser back. Signing out of the
   * application without reaching here leaves this cookie in place, and the next
   * authorization request is answered silently for the same person — which is
   * the defect register row 21 describes, and the reason this endpoint exists
   * in the harness at all.
   */
  function endSession(url: URL, request: IncomingMessage, response: ServerResponse): void {
    const header = request.headers.cookie ?? '';
    for (const part of header.split(';')) {
      const [name, ...rest] = part.trim().split('=');
      if (name === SESSION_COOKIE) {
        sessions.delete(rest.join('='));
      }
    }

    const back = url.searchParams.get('post_logout_redirect_uri');
    const requestedState = url.searchParams.get('state');
    const clear = `${SESSION_COOKIE}=; Path=/; HttpOnly; Secure; SameSite=None; Max-Age=0`;

    if (back === null || !redirectUris.has(back)) {
      response.writeHead(200, { 'content-type': 'text/plain', 'set-cookie': clear });
      response.end('signed out');
      return;
    }

    const target = new URL(back);
    if (requestedState !== null) {
      target.searchParams.set('state', requestedState);
    }

    response.writeHead(302, { location: target.toString(), 'set-cookie': clear }).end();
  }

  /**
   * The authorization endpoint: silent with a session, a chooser without one.
   *
   * @remarks
   * <p>
   * There is no password, because passwords are not this project's. What is
   * *not* skipped is every check that makes the code flow a flow: the redirect
   * URI is matched exactly against a registered list, S256 is the only
   * challenge method accepted, and a request without a challenge is refused
   * outright. A harness that waved those through would let a client that never
   * computed a challenge pass its PKCE test.
   * </p><p>
   * Nor is the session skipped, and that is what changed in Phase 6. A browser
   * carrying this issuer's cookie is signed in without being asked — the
   * behaviour that makes signing out mean something — and a browser without one
   * is shown a chooser it has to click. Ending the session is therefore
   * observable: the next load stops at the chooser instead of arriving back at
   * the application as the same person.
   * </p>
   */
  function authorize(url: URL, request: IncomingMessage, response: ServerResponse): void {
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

    // A hint picks the account and establishes the session, which is what the
    // chooser's links do. Otherwise the session cookie decides, and a browser
    // with neither is asked.
    const hinted = url.searchParams.get('login_hint');
    const existing = sessionOf(request);
    const subject = hinted ?? existing;

    if (subject === null) {
      chooser(url, response);
      return;
    }

    const code = randomUUID();
    codes.set(code, {
      challenge,
      subject,
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

    const headers: Record<string, string> = { location: back.toString() };
    if (hinted !== null) {
      const session = randomUUID();
      sessions.set(session, hinted);

      // SameSite=None because the issuer and the application are different
      // origins and the callback is a cross-site navigation, which is exactly
      // the shape a real provider's session cookie has.
      headers['set-cookie'] =
        `${SESSION_COOKIE}=${session}; Path=/; HttpOnly; Secure; SameSite=None`;
    }

    response.writeHead(302, headers).end();
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

  const bind = options.bind ?? '127.0.0.1';
  await new Promise<void>((done) => server.listen(0, bind, done));
  const { port } = server.address() as AddressInfo;

  // The issuer URL a *client* uses, which is not always the bind address: the
  // walk runs the API in a container that cannot reach the host's loopback, and
  // an OIDC issuer is an absolute URL both sides must agree on exactly.
  issuer = `https://${options.reachableAs ?? '127.0.0.1'}:${port}`;

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

    accounts,

    get sessionCount(): number {
      return sessions.size;
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

/**
 * A document and its memberships, created through the product's own API.
 *
 * @remarks
 * <p>
 * This used to be a block of `INSERT` statements piped to `psql`, and that was
 * register rows 15 and 16: nothing in the product created a document, so every
 * harness reached past it into the database, and the absence was invisible from
 * inside a suite that had been green for eleven phases (§13.27).
 * </p><p>
 * **The rule that keeps it closed is checkable by grep rather than by
 * judgement**, and `scripts/client-gates.sh` runs that grep: a raw insert
 * against the documents table, anywhere in a harness, fails the build.
 * Judgement at the end of a long phase is what produced those rows in the
 * first place.
 * </p><p>
 * The rule is written out in PROJECT_SPEC.md §12 rather than here, and
 * deliberately: spelled out in full in this file it would *be* the string the
 * grep looks for, so the comment explaining the guard would defeat it. §13.19
 * in miniature, and it turned up while checking that the grep actually
 * returned nothing.
 * </p><p>
 * Tokens are minted directly rather than obtained through the code flow,
 * because this harness stands in for *clients* and the flow itself is what the
 * browser tests exercise. What is not stood in for is the API: every document
 * and every membership below is created by the same endpoints a user reaches.
 * </p>
 */
export async function provision(
  baseUrl: string,
  oidc: Oidc,
  spec: {
    readonly owner: string;
    readonly title?: string;
    readonly members?: readonly { subject: string; role: 'viewer' | 'editor' | 'owner' }[];
  },
): Promise<string> {
  const roles = { viewer: 0, editor: 1, owner: 2 };

  const call = async (subject: string, method: string, path: string, body?: unknown) => {
    const response = await fetch(`${baseUrl}${path}`, {
      method,
      headers: {
        authorization: `Bearer ${oidc.mint(subject)}`,
        ...(body === undefined ? {} : { 'content-type': 'application/json' }),
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });

    if (!response.ok) {
      throw new Error(
        `${method} ${path} as ${subject} answered ${response.status}: ${await response.text()}`,
      );
    }

    return response.status === 204 ? null : ((await response.json()) as unknown);
  };

  const created = (await call(spec.owner, 'POST', '/documents', {
    title: spec.title ?? 'harness',
  })) as { id: string };

  for (const member of spec.members ?? []) {
    // The member reads their own id first, which is how §9's grant is meant to
    // work: there is no directory, so the invitee supplies it. It also
    // provisions their user row, which is what makes them grantable at all.
    const who = (await call(member.subject, 'GET', '/me')) as { userId: string };

    await call(spec.owner, 'PUT', `/documents/${created.id}/members/${who.userId}`, {
      role: roles[member.role],
    });
  }

  return created.id;
}
