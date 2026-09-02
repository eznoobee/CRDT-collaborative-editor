/**
 * PROJECT_SPEC.md §8: the browser half of the document-load measurement.
 *
 * §8 requires two numbers, not one. The server-side figure is measured by
 * Editor.Api.Tests; this is the other half — the TypeScript core of §9 loading a
 * document in a real browser, which is what decides whether this works on a slow
 * connection. It carries no threshold in this phase: setting a bound before
 * anyone has seen the number is how §8 acquired a 500 ms target nothing had
 * measured.
 *
 * "Cold" is §8's definition: a fresh page load with empty IndexedDB — the
 * first-time-user case, where the document arrives over the network and nothing
 * is cached. Each case gets its own browser context, so nothing carries over.
 * The warm figure is reported beside it because §8 defines cold in terms of
 * IndexedDB being empty, which only means anything if there is a case where it
 * is not.
 *
 * The snapshot is encoded in Node and fetched by the page rather than built in
 * it: building 600k elements in the browser would measure construction, and what
 * is wanted is what a user waits for.
 */
import { build } from 'esbuild';
import { chromium } from 'playwright';
import { createServer } from 'node:http';
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, '..', '..');
const outDir = mkdtempSync(join(tmpdir(), 'crdt-browser-'));

const READER = '00000000-0000-0000-0000-0000000000ff';

/** §8's own case, and the plain document beside it for comparison. */
const CASES = [
  { name: 'chain', total: 100_000, replicas: 1, keepEvery: 1 },
  { name: 'stress', total: 600_000, replicas: 4, keepEvery: 6 },
];

async function bundle() {
  await build({
    entryPoints: [join(repoRoot, 'client', 'src', 'crdt', 'index.ts')],
    outfile: join(outDir, 'crdt.js'),
    bundle: true,
    format: 'esm',
    target: 'es2022',
    logLevel: 'warning',
  });
}

function replicaId(n) {
  return `00000000-0000-0000-0000-${n.toString(16).padStart(12, '0')}`;
}

/**
 * The same two shapes the server-side metric builds, for the same reasons: a
 * chain is the format's best case and would overstate it alone, and the stress
 * case is §8's own — 100k live characters and 500k tombstones, which §5 says
 * cannot be collected on causal stability alone.
 */
function buildElements(crdt, { total, replicas, keepEvery }) {
  const ids = Array.from({ length: replicas }, (_, i) => crdt.parseReplicaId(replicaId(i + 1)));
  const seqs = new Array(replicas).fill(0n);
  const elements = [];

  for (let i = 0; i < total; i++) {
    const r = i % replicas;
    const id = { replica: ids[r], seq: seqs[r]++ };
    elements.push({
      id,
      value: String.fromCodePoint(97 + (i % 26)),
      parent: i === 0 ? null : elements[i - 1].id,
      side: 'R',
      rightOrigin: replicas > 1 && i > 2 && i % 3 === 0 ? elements[i - 2].id : null,
      isDeleted: i % keepEvery !== 0,
    });
  }

  const vector = ids.map((replica) => ({ replica, count: BigInt(total) }));
  return { elements, vector };
}

const PAGE = `<!doctype html>
<meta charset="utf-8">
<title>CRDT browser load metric</title>
<script type="module">
import { Replica, decodeSnapshot, parseReplicaId } from './crdt.js';

const READER = parseReplicaId(${JSON.stringify(READER)});
const DB = 'crdt-metric';

function idb(mode, run) {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open(DB, 1);
    open.onupgradeneeded = () => open.result.createObjectStore('snapshots');
    open.onerror = () => reject(open.error);
    open.onsuccess = () => {
      const tx = open.result.transaction('snapshots', mode);
      const result = run(tx.objectStore('snapshots'));
      tx.oncomplete = () => { open.result.close(); resolve(result.result ?? null); };
      tx.onerror = () => reject(tx.error);
    };
  });
}

function load(bytes) {
  const t0 = performance.now();
  const decoded = decodeSnapshot(bytes);
  const t1 = performance.now();
  const replica = Replica.import(READER, decoded.elements, decoded.versionVector);
  const t2 = performance.now();
  const text = replica.text;
  const t3 = performance.now();
  return { parse: t1 - t0, place: t2 - t1, text: t3 - t2, live: [...text].length };
}

window.measureCold = async (file) => {
  const t0 = performance.now();
  const response = await fetch(file);
  const bytes = new Uint8Array(await response.arrayBuffer());
  const fetched = performance.now();

  const stored = await idb('readonly', (s) => s.get('doc'));
  if (stored !== null) {
    throw new Error('IndexedDB was not empty, so this is not a cold load (§8).');
  }

  const timings = load(bytes);
  const total = performance.now() - t0;

  await idb('readwrite', (s) => s.put(bytes, 'doc'));
  return { bytes: bytes.length, fetch: fetched - t0, ...timings, total };
};

window.measureWarm = async () => {
  const t0 = performance.now();
  const bytes = await idb('readonly', (s) => s.get('doc'));
  const read = performance.now();
  const timings = load(new Uint8Array(bytes));
  return { bytes: bytes.byteLength, read: read - t0, ...timings, total: performance.now() - t0 };
};

window.ready = true;
</script>
<p>See the console.</p>
`;

function serve(dir) {
  const types = { '.js': 'text/javascript', '.html': 'text/html', '.bin': 'application/octet-stream' };
  const server = createServer((req, res) => {
    const path = req.url === '/' ? '/index.html' : req.url.split('?')[0];
    try {
      const body = readFileSync(join(dir, path));
      const ext = path.slice(path.lastIndexOf('.'));
      res.writeHead(200, { 'content-type': types[ext] ?? 'application/octet-stream' });
      res.end(body);
    } catch {
      res.writeHead(404).end('not found');
    }
  });
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(server)));
}

function ms(value) {
  return `${value.toFixed(0).padStart(6)} ms`;
}

async function main() {
  await bundle();
  const crdt = await import(pathToFileURL(join(outDir, 'crdt.js')).href);

  const built = [];
  for (const shape of CASES) {
    const { elements, vector } = buildElements(crdt, shape);
    const bytes = crdt.encodeSnapshot(elements, vector);
    writeFileSync(join(outDir, `${shape.name}.bin`), bytes);
    built.push({ ...shape, size: bytes.length });
  }

  writeFileSync(join(outDir, 'index.html'), PAGE);
  const server = await serve(outDir);
  const { port } = server.address();

  // Prefer a Chromium the environment already has — some sandboxes ship one and
  // forbid the download — and fall back to Playwright's own, which is what CI
  // installs. §8 names the runner class as part of the number, so where this
  // executes is not an implementation detail.
  const preinstalled = process.env.CHROMIUM_PATH ?? '/opt/pw-browsers/chromium';
  const browser = await chromium.launch(
    existsSync(preinstalled) ? { executablePath: preinstalled } : {},
  );

  console.log(`Browser document load (PROJECT_SPEC.md §8, no threshold)`);
  console.log(`  Chromium ${browser.version()}`);
  console.log(`  ${process.arch} ${process.platform}, ${CPU_LABEL}`);
  console.log('');

  for (const shape of built) {
    // A fresh context per case is what makes the cold number cold: its own
    // IndexedDB, its own HTTP cache, nothing carried over from the last one.
    const context = await browser.newContext();
    const page = await context.newPage();
    page.on('pageerror', (error) => {
      throw error;
    });

    await page.goto(`http://127.0.0.1:${port}/`);
    await page.waitForFunction('window.ready === true');

    const cold = await page.evaluate((file) => window.measureCold(file), `${shape.name}.bin`);
    const warm = await page.evaluate(() => window.measureWarm());
    await context.close();

    console.log(`${shape.name}: ${shape.total.toLocaleString()} elements, ${cold.live.toLocaleString()} live`);
    console.log(`  snapshot                 ${cold.bytes.toLocaleString()} bytes`);
    console.log(`  cold  fetch              ${ms(cold.fetch)}`);
    console.log(`  cold  parse              ${ms(cold.parse)}`);
    console.log(`  cold  place              ${ms(cold.place)}`);
    console.log(`  cold  text               ${ms(cold.text)}`);
    console.log(`  cold  TOTAL              ${ms(cold.total)}`);
    console.log(`  warm  read (IndexedDB)   ${ms(warm.read)}`);
    console.log(`  warm  TOTAL              ${ms(warm.total)}`);
    console.log('');
  }

  console.log(
    '§8 targets 500 ms server-side and reports the browser figure without a threshold.\n' +
      'Cold is a fresh page load with empty IndexedDB: the first-time-user case.',
  );

  await browser.close();
  server.close();
}

const CPU_LABEL = `${(await import('node:os')).cpus().length} vCPU`;

await main();
