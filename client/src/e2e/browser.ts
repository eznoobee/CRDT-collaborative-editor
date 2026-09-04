import { spawnSync } from 'node:child_process';
import { chromium, type Browser, type BrowserContext, type Page } from 'playwright';

import type { Oidc } from '../interop/harness';

/**
 * A real browser, for the parts of §9 that only a browser can answer.
 *
 * @remarks
 * <p>
 * jsdom carries the rest of the client's tests and cannot carry these. §7's
 * login is a redirect — the page navigates away to the issuer and the issuer
 * navigates back — and jsdom implements neither navigation nor a URL bar. A
 * test that stubbed both would be asserting against its own stub, which is the
 * failure this project keeps finding (§13.24).
 * </p><p>
 * The certificate is **pinned, not ignored**. Chromium is told to accept
 * exactly the public key the harness issuer generated for this run, via
 * `--ignore-certificate-errors-spki-list`. Playwright offers
 * `ignoreHTTPSErrors`, which turns certificate validation off for the whole
 * browser and would silently invalidate every later claim these tests make
 * about TLS. Same rule as the API's `SSL_CERT_FILE` (§7): name the certificate,
 * never disable the check.
 * </p>
 */
export interface Browsing {
  readonly browser: Browser;

  /** A fresh context, so nothing carries between cases. */
  open(): Promise<{ context: BrowserContext; page: Page }>;

  close(): Promise<void>;
}

/**
 * Where Chromium lives.
 *
 * @remarks
 * Resolved rather than downloaded. The sandbox this project is developed in
 * ships a browser and forbids fetching one; CI installs its own through
 * Playwright and needs no path at all. A hard-coded path would break one of the
 * two, and a download would break the other.
 */
function executablePath(): string | undefined {
  const configured = process.env['E2E_CHROMIUM'];
  if (configured !== undefined && configured !== '') {
    return configured;
  }

  const shipped = '/opt/pw-browsers/chromium';
  return spawnSync('test', ['-e', shipped]).status === 0 ? shipped : undefined;
}

export async function startBrowser(
  oidc: Oidc,
  extraPins: readonly string[] = [],
): Promise<Browsing> {
  const found = executablePath();
  const browser = await chromium.launch({
    // The pin. One key, this run's, and nothing else about certificate
    // validation changes.
    args: [`--ignore-certificate-errors-spki-list=${[oidc.spki, ...extraPins].join(',')}`],
    ...(found === undefined ? {} : { executablePath: found }),
  });

  const contexts: BrowserContext[] = [];

  return {
    browser,

    async open() {
      const context = await browser.newContext();
      contexts.push(context);
      return { context, page: await context.newPage() };
    },

    async close() {
      for (const context of contexts) {
        await context.close();
      }

      await browser.close();
    },
  };
}
