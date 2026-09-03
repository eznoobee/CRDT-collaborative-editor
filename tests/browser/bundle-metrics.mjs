// PROJECT_SPEC.md §13.13a: the second measurement the protocol decision rests on.
//
// Bandwidth saved per keystroke is paid for once per page load. On the slow
// connection this project keeps citing, those are the same currency, so the
// choice is made against both figures rather than against wire bytes alone.
//
// Measured as minified + gzipped bytes, because that is what crosses the
// network. Three bundles, each importing only what its name says, so the
// difference between them is the cost of the thing added.
import { build } from 'esbuild';
import { gzipSync } from 'node:zlib';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const client = new URL('../../client/', import.meta.url).pathname;

const entries = {
  'CRDT core alone': `
    import { Replica } from '${client}src/crdt/replica.ts';
    import { encodeOperations } from '${client}src/crdt/binary.ts';
    globalThis.keep = [Replica, encodeOperations];
  `,
  'core + SignalR (JSON)': `
    import { Replica } from '${client}src/crdt/replica.ts';
    import { encodeOperations } from '${client}src/crdt/binary.ts';
    import { HubConnectionBuilder } from '@microsoft/signalr';
    globalThis.keep = [Replica, encodeOperations, HubConnectionBuilder];
  `,
  'core + SignalR + MessagePack': `
    import { Replica } from '${client}src/crdt/replica.ts';
    import { encodeOperations } from '${client}src/crdt/binary.ts';
    import { HubConnectionBuilder } from '@microsoft/signalr';
    import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';
    globalThis.keep = [Replica, encodeOperations, HubConnectionBuilder, MessagePackHubProtocol];
  `,
};

// Inside the client, not /tmp: esbuild resolves a bare import from the
// importing file's directory, so an entry point outside the client cannot see
// the client's node_modules and the measurement fails rather than misreporting.
const directory = mkdtempSync(join(client, '.bundle-metrics-'));
const results = [];

for (const [label, source] of Object.entries(entries)) {
  const entry = join(directory, `${label.replace(/[^a-z]+/gi, '-')}.ts`);
  writeFileSync(entry, source);

  const result = await build({
    entryPoints: [entry],
    bundle: true,
    minify: true,
    format: 'esm',
    platform: 'browser',
    target: 'es2022',
    write: false,
    absWorkingDir: client,
    logLevel: 'silent',
  });

  const bytes = result.outputFiles[0].contents;
  results.push({ label, minified: bytes.length, gzipped: gzipSync(bytes).length });
}

const base = results[0];
console.log();
console.log('PROJECT_SPEC.md §13.13a — client bundle, bytes');
console.log();
console.log('| bundle | minified | gzipped | gzipped delta vs core |');
console.log('|---|---|---|---|');
for (const r of results) {
  const delta = r === base ? '—' : `+${r.gzipped - base.gzipped}`;
  console.log(`| ${r.label} | ${r.minified} | ${r.gzipped} | ${delta} |`);
}

const signalr = results[1];
const msgpack = results[2];
console.log();
console.log(
  `MessagePack protocol costs ${msgpack.gzipped - signalr.gzipped} gzipped bytes ` +
    `over SignalR alone (${(
      (100 * (msgpack.gzipped - signalr.gzipped)) /
      signalr.gzipped
    ).toFixed(1)}% of the SignalR bundle).`,
);

rmSync(directory, { recursive: true, force: true });
