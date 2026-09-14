import { describe, expect, it } from 'vitest';
import { HubConnectionState, HttpTransportType, HubConnectionBuilder, type HubConnection, type IHttpConnectionOptions } from '@microsoft/signalr';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';
import { startApi, startOidc, provision, type Api, type Oidc } from '../interop/harness';
import { describe as describeProvenance, percentiles, provenance, residentMiB } from './provenance';

/**
 * §8's third target: 1,000 concurrent connections per instance at < 2 GB RSS,
 * spread over 100 documents, 10 each.
 *
 * @remarks
 * <p>
 * <b>Out of process, and that is the whole reason this file exists rather than
 * a C# one.</b> RSS names a process. A measurement holding a thousand clients
 * in the same process as the server would add the generator's memory to the
 * server's and call the sum the instance — which is not a conservative error,
 * it is a wrong number in the direction that fails the target.
 * </p><p>
 * <b>The population is idle readers, and §12's first question is why.</b> For a
 * mechanism keyed on an action, who is the legitimate user that never performs
 * it? Here: the person who has a document open and is not typing. They cost a
 * connection, a replica claim and an outbound buffer, and produce no
 * throughput. A thousand writers would be a population that does not exist at
 * that scale, and measuring it would report capacity for the wrong thing.
 * </p><p>
 * <b>A thousand distinct users, not a thousand connections from one.</b> §7
 * caps connections per user, and raising that cap to make the measurement fit
 * would be measuring a configuration that will not exist. A hundred documents
 * with ten members each is what §8 describes and what the cap allows.
 * </p><p>
 * <b>The generator's own numbers are reported beside the server's.</b> §8's
 * second rule: if the harness saturates first, the number describes the
 * harness. A thousand WebSockets in one Node process is a real load on the
 * generator, so its RSS and CPU are part of the result.
 * </p><p>
 * <b>The vacuity risk is that this target passes most easily when the server
 * refuses the work.</b> A server that dropped nine hundred of the thousand
 * would report a small RSS and clear the threshold. So the count of successful
 * starts is not the evidence: every connection is checked to be still
 * <i>Connected</i> at the moment RSS is read, and each has caught up, which is
 * what a client with a document open has actually done. The documents carry
 * text for the same reason — a connection to an empty document is a floor, not
 * a measurement.
 * </p>
 */
describe("§8's connection target", () => {
  /** §8's stated spread. */
  const DOCUMENTS = 100;
  const PER_DOCUMENT = 10;
  const TOTAL = DOCUMENTS * PER_DOCUMENT;

  /** Connections opened at once. */
  const RAMP = 20;

  /** Characters in each document, so a connection has state behind it. */
  const SEED_TEXT = 'the quick brown fox jumps over the lazy dog, repeatedly and at length';

  const report: string[] = [];
  const say = (line: string) => {
    report.push(line);
    // eslint-disable-next-line no-console
    console.log(line);
  };

  interface Joined {
    readonly connection: HubConnection;

    /**
     * The replica the server assigned, carried out of negotiate.
     *
     * @remarks
     * Not looked up again later. §7's tier-1 check compares a batch's replica
     * id against the connection's binding, and a second negotiate mints a
     * second replica — so seeding that negotiated twice submitted under an id
     * this connection was not bound to and was refused, correctly, as
     * `forbidden`.
     */
    readonly replicaId: string;
  }

  async function join(api: Api, oidc: Oidc, subject: string, documentId: string): Promise<Joined> {
    const negotiated = await fetch(`${api.baseUrl}/documents/${documentId}/negotiate`, {
      method: 'POST',
      headers: { authorization: `Bearer ${oidc.mint(subject)}` },
    });

    if (!negotiated.ok) {
      throw new Error(`negotiate as ${subject}: ${negotiated.status} ${await negotiated.text()}`);
    }

    const { ticket, replicaId } = (await negotiated.json()) as {
      ticket: string;
      replicaId: string;
    };

    const options: IHttpConnectionOptions = {
      transport: HttpTransportType.WebSockets,
      skipNegotiation: true,
    };

    const connection = new HubConnectionBuilder()
      .withUrl(`${api.baseUrl}/hub/editor?access_token=${encodeURIComponent(ticket)}`, options)
      .withHubProtocol(new MessagePackHubProtocol())
      .build();

    await connection.start();

    // What a client with a document open has actually done. A connection that
    // never caught up leaves the server holding no document state for it, and
    // the memory that state costs is most of what this target is about.
    await connection.invoke('CatchUpAsync', {}, false);

    return { connection, replicaId };
  }

  /**
   * Types into every document, so the server holds state worth measuring.
   *
   * @remarks
   * Through the hub, not through the database: what costs memory is the
   * server's in-memory replica of each document, and that is built by ingest.
   */
  async function seed(api: Api, oidc: Oidc, documents: { id: string; members: string[] }[]): Promise<void> {
    const { encodeOperations, parseReplicaId, Replica } = await import('../crdt');

    for (const document of documents) {
      const owner = document.members[0]!;
      const { connection, replicaId } = await join(api, oidc, owner, document.id);
      try {
        const replica = new Replica(parseReplicaId(replicaId));
        const operations = [...SEED_TEXT].map((value, index) => replica.insert(index, value));

        const result = await connection.invoke<{ Code: string | null }>('SubmitAsync', {
          DocumentId: document.id,
          ReplicaId: replicaId,
          Operations: encodeOperations(operations),
        });

        if (result.Code !== null) {
          throw new Error(`seeding ${document.id} was refused: ${result.Code}`);
        }
      } finally {
        await connection.stop();
      }
    }
  }

  it('holds 1,000 idle connections under 2 GB', async () => {
    const log: string[] = [];
    const oidc = await startOidc();
    const api = await startApi(oidc, log, { configuration: 'Release' });

    const connections: HubConnection[] = [];
    try {
      const where = provenance('Release');

      // Before anything connects. Every later figure is quoted against this, so
      // "the server uses 1.4 GB" is never confused with "a thousand connections
      // cost 1.4 GB".
      const idle = residentMiB(api.pid);

      const documents: { id: string; members: string[] }[] = [];
      for (let d = 0; d < DOCUMENTS; d++) {
        const owner = `load-conn-${d}-0`;
        const members = Array.from({ length: PER_DOCUMENT - 1 }, (_, m) => `load-conn-${d}-${m + 1}`);
        const id = await provision(api.baseUrl, oidc, {
          owner,
          title: `load ${d}`,
          members: members.map((subject) => ({ subject, role: 'viewer' as const })),
        });

        documents.push({ id, members: [owner, ...members] });
      }

      // Text in every document, typed through the product by one member, so
      // the server holds real state rather than a hundred empty replicas.
      await seed(api, oidc, documents);

      const provisioned = residentMiB(api.pid);

      const startedCpu = process.cpuUsage();
      const startedAt = Date.now();
      const connectMs: number[] = [];

      const pending: { subject: string; documentId: string }[] = [];
      for (const document of documents) {
        for (const subject of document.members) {
          pending.push({ subject, documentId: document.id });
        }
      }

      for (let i = 0; i < pending.length; i += RAMP) {
        const slice = pending.slice(i, i + RAMP);
        const opened = await Promise.all(slice.map(async (one) => {
          const began = Date.now();
          const joined = await join(api, oidc, one.subject, one.documentId);
          connectMs.push(Date.now() - began);
          return joined.connection;
        }));

        connections.push(...opened);
      }

      expect(connections).toHaveLength(TOTAL);

      // Settled, not measured the instant the last socket opened. A server that
      // has just accepted a thousand connections has allocation it has not
      // collected, and reporting the peak of that as steady state would fail
      // the target on garbage.
      await new Promise((done) => setTimeout(done, 15_000));

      // THE CHECK THAT MAKES THE NUMBER MEAN ANYTHING. A server that dropped
      // most of these would report a small RSS and clear the threshold; "a
      // thousand starts succeeded" is a statement about the past.
      const live = connections.filter((c) => c.state === HubConnectionState.Connected).length;
      expect(live).toBe(TOTAL);

      const held = residentMiB(api.pid);
      const cpu = process.cpuUsage(startedCpu);
      const wallSeconds = (Date.now() - startedAt) / 1000;
      const generatorCpu = (cpu.user + cpu.system) / 1e6;

      say('§8 target 3 — 1,000 concurrent connections per instance at < 2 GB RSS');
      say(`  build      ${describeProvenance(where)}`);
      say(`  load       ${TOTAL} idle connections over ${DOCUMENTS} documents, ${PER_DOCUMENT} each`);
      say(`  live       ${live} of ${TOTAL} still connected when RSS was read`);
      say(`  server rss ${idle.toFixed(0)} MiB idle → ${provisioned.toFixed(0)} MiB provisioned → ${held.toFixed(0)} MiB holding ${TOTAL}`);
      say(`  per conn   ${((held - provisioned) / TOTAL * 1024).toFixed(0)} KiB`);
      say(`  connect ms ${percentiles(connectMs)}`);
      say(`  generator  ${(generatorCpu / (wallSeconds * where.cores) * 100).toFixed(0)} % of ${where.cores} cores, `
        + `${(process.memoryUsage().rss / 1024 ** 2).toFixed(0)} MiB rss`);

      expect(connectMs.length).toBe(TOTAL);
      expect(held).toBeLessThan(2048);
    } finally {
      await Promise.all(connections.map((connection) => connection.stop().catch(() => {})));
      await api.close();
      await oidc.close();

      if (process.env.EDITOR_LOAD_REPORT !== undefined && report.length > 0) {
        const { appendFileSync } = await import('node:fs');
        appendFileSync(process.env.EDITOR_LOAD_REPORT, `${report.join('\n')}\n`);
      }
    }
  });
});
