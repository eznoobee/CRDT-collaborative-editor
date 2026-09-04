import { spawnSync } from 'node:child_process';
import { readdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { startApi, startOidc, CLIENT_ID, type Api, type Oidc } from '../interop/harness';
import { startBrowser, type Browsing } from './browser';

const CLIENT = resolve(import.meta.dirname, '../..');

/**
 * The whole system, running: issuer, API, built application, browser (§11).
 *
 * @remarks
 * The application is **built and served by the API**, not by a dev server.
 * `vite dev` transforms modules on demand and proxies whatever it is told to;
 * a test against it would be a test of a development convenience, and the
 * artefact users receive would remain unexercised. What is served here is the
 * output of `npm run build`, from the same static-file path a deployment uses.
 */
export interface System {
  readonly oidc: Oidc;
  readonly api: Api;
  readonly browsing: Browsing;
  readonly log: string[];
  close(): Promise<void>;
}

/**
 * Builds the client, and checks that what came out is what ships.
 *
 * @remarks
 * <p>
 * **`NODE_ENV` is set explicitly, and this is not a formality.** Vitest sets
 * `NODE_ENV=test` in its own process; a build spawned from a test inherits it,
 * Vite resolves React through its development export condition, and the
 * artefact under test becomes a development build — larger, slower, and, most
 * importantly, running Strict Mode's deliberate double-invocation of effects.
 * The end-to-end suite would then be passing judgement on a bundle no user ever
 * receives (§13.26).
 * </p><p>
 * The assertion after it is the durable half. Setting the variable fixes
 * today's cause; a check on the output fails whatever tomorrow's cause is —
 * a changed default, a new wrapper script, a plugin that reads the environment
 * itself. What defeats it (§13.19): a future React whose development build
 * drops this string, or a bundler that strips console text. It is a marker, not
 * a proof, and it is here because the property it stands for has no marker of
 * its own.
 * </p>
 */
function build(): string {
  const result = spawnSync('npm', ['run', 'build'], {
    cwd: CLIENT,
    encoding: 'utf8',
    env: { ...process.env, NODE_ENV: 'production' },
  });

  if (result.status !== 0) {
    throw new Error(`client build failed:\n${result.stdout}\n${result.stderr}`);
  }

  const dist = resolve(CLIENT, 'dist');
  const assets = resolve(dist, 'assets');
  const bundles = readdirSync(assets).filter((name) => name.endsWith('.js'));

  for (const bundle of bundles) {
    const contents = readFileSync(resolve(assets, bundle), 'utf8');
    if (contents.includes(REACT_DEVELOPMENT_MARKER)) {
      throw new Error(
        `${bundle} contains React's development build. The end-to-end suite would `
        + 'be testing an artefact no user receives: Strict Mode double-invokes '
        + `effects there and not in production. NODE_ENV was ${process.env['NODE_ENV'] ?? '(unset)'}.`,
      );
    }
  }

  return dist;
}

/** A string React DOM ships only in its development build. */
const REACT_DEVELOPMENT_MARKER = 'Download the React DevTools';

export async function startSystem(): Promise<System> {
  const log: string[] = [];
  const spaRoot = build();

  const oidc = await startOidc();
  const api = await startApi(oidc, log, { spaRoot });

  // Registered exactly, as §7 requires: one URI, matched literally. The issuer
  // refuses anything else outright rather than redirecting to it.
  oidc.redirectUris.add(`${api.baseUrl}/callback`);
  oidc.origins.add(api.baseUrl);

  const browsing = await startBrowser(oidc);

  return {
    oidc,
    api,
    browsing,
    log,
    async close() {
      await browsing.close();
      await api.close();
      await oidc.close();
    },
  };
}

export { CLIENT_ID };
