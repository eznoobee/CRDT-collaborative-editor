import { describe, expect, it } from 'vitest';
import type { Page } from 'playwright';
import { HttpTransportType, HubConnectionBuilder, type HubConnection, type IHttpConnectionOptions } from '@microsoft/signalr';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';
import { startSystem, type System } from '../e2e/harness';
import { pick } from '../e2e/browser';
import { provision } from '../interop/harness';
import { decodeOperations, encodeOperations, parseReplicaId, Replica } from '../crdt';
import { cpuBetween, describe as describeProvenance, percentiles, provenance, sampleCpu } from './provenance';

/**
 * §8's second target: p99 < 150 ms, client keystroke → remote client render,
 * 20 concurrent editors, loopback network.
 *
 * @remarks
 * <p>
 * <b>Both ends are real browsers, because §8 says "render".</b> Measuring
 * keystroke to remote <i>apply</i> — the operation reaching the other client's
 * replica — would be a different segment, shorter than the one named, and
 * passing it would say nothing about what a person sees. That is §8's first
 * failure mode, a length measured on the payload rather than the frame, in the
 * place it is easiest to commit.
 * </p><p>
 * <b>The other eighteen editors are protocol-level clients, and that is stated
 * rather than hidden.</b> §8 asks for twenty concurrent editors; two of them
 * here are browsers and eighteen are SignalR clients submitting at the same
 * rate. What they contribute to the measured pair is server load and fan-out
 * fan-in, which is all the target needs from them — but it does mean this
 * measures two browsers under the load of twenty editors, not twenty browsers.
 * </p><p>
 * <b>Both timestamps are taken inside the pages, on one clock.</b> Loopback
 * means one machine, so the two pages share a wall clock and the difference
 * between them is meaningful. Driving the stopwatch from Node instead would put
 * two rounds of Playwright's own protocol inside the number.
 * </p><p>
 * <b>The reader's sampling granularity is part of the result.</b> A textarea
 * whose value React replaces fires no input event, so the reader polls. The
 * interval it polls at is an error bar on every sample and is reported beside
 * the percentiles rather than left for someone to discover.
 * </p><p>
 * <b>Keystrokes are correlated by a character only the writer types.</b> The
 * first version matched on the textarea's length, which on the reader grows
 * from the other eighteen editors as well — so the two ends almost never agreed
 * on a length and 89 of 1,000 keystrokes correlated. The p50 it then reported,
 * 2.7 seconds, was drawn from whichever few happened to line up. That is the
 * shape of a measurement improving as the system gets worse, which is why the
 * match rate is asserted and not merely printed.
 * </p>
 */
describe("§8's keystroke-to-render target", () => {
  /** §8's stated load. */
  /**
   * §8's twenty, overridable for one discriminator and nothing else.
   *
   * @remarks
   * 9.5 needed to tell two explanations apart for a shortfall that got *worse*
   * after the server got faster: the send loop's one-batch-per-round-trip cap,
   * or the writer browser's main thread being consumed applying everyone else's
   * broadcasts. Holding the writer, the rate and the keystroke count fixed and
   * removing only the background load separates them — the same trick target 1
   * uses with one document per editor. §8's own figure is the default and the
   * override is not used by `scripts/load.sh`.
   */
  const EDITORS = Number(process.env.EDITOR_LOAD_EDITORS ?? 20);

  /** Of those, the ones that are real browsers. */
  const BROWSERS = 2;

  /** Keystrokes measured. */
  const KEYSTROKES = 1_000;

  /** Batches per second per background editor — a fast typist (see 7b.4). */
  const BACKGROUND_RATE = 8;

  /** How often the reader page looks at its textarea, in milliseconds. */
  const POLL_MS = 2;

  /** The measured writer types this; nobody else does. */
  const MARK = 'Z';

  /** What the eighteen background editors type, so their work never matches MARK. */
  const NOISE = 'x';

  const report: string[] = [];
  const say = (line: string) => {
    report.push(line);
    console.log(line);
  };

  async function background(
    system: System,
    subject: string,
    documentId: string,
  ): Promise<{ connection: HubConnection; replicaId: string }> {
    const negotiated = await fetch(`${system.api.baseUrl}/documents/${documentId}/negotiate`, {
      method: 'POST',
      headers: { authorization: `Bearer ${system.oidc.mint(subject)}` },
    });

    const { ticket, replicaId } = (await negotiated.json()) as {
      ticket: string;
      replicaId: string;
    };

    const options: IHttpConnectionOptions = {
      transport: HttpTransportType.WebSockets,
      skipNegotiation: true,
    };

    const connection = new HubConnectionBuilder()
      .withUrl(`${system.api.baseUrl}/hub/editor?access_token=${encodeURIComponent(ticket)}`, options)
      .withHubProtocol(new MessagePackHubProtocol())
      .build();

    await connection.start();
    return { connection, replicaId };
  }

  it('carries a keystroke to another browser', async () => {
    const system = await startSystem();
    const connections: HubConnection[] = [];

    try {
      const where = provenance('Release');

      const members = Array.from({ length: EDITORS - 1 }, (_, i) => ({
        subject: `load-key-${i}`,
        role: 'editor' as const,
      }));

      const documentId = await provision(system.api.baseUrl, system.oidc, {
        owner: 'load-key-writer',
        members,
      });

      async function open(subject: string): Promise<Page> {
        system.oidc.accounts.add(subject);
        const { page } = await system.browsing.open();
        await page.goto(`${system.api.baseUrl}/d/${documentId}`);
        await pick(page, subject);
        await page.waitForSelector('textarea', { timeout: 120_000 });
        await page.waitForFunction(
          () => document.querySelector('[data-testid="state"]')?.textContent === 'live',
          undefined,
          { timeout: 120_000 },
        );

        return page;
      }

      const writer = await open('load-key-writer');
      const reader = await open('load-key-0');

      // The reader records, in its own page, the moment its textarea first
      // shows each new length. Polling because React replaces the value and no
      // input event fires; the interval is reported as the error bar it is.
      // Counted by the writer's own mark, not by total length: the reader's
      // text also grows from the other eighteen, so length is not a key both
      // ends can agree on.
      await reader.evaluate(({ pollMs, mark }) => {
        const seen = new Map<number, number>();
        (window as unknown as { __seen: Map<number, number> }).__seen = seen;

        let last = -1;
        setInterval(() => {
          const value = document.querySelector('textarea')?.value ?? '';
          let marks = 0;
          for (const character of value) {
            if (character === mark) {
              marks++;
            }
          }

          if (marks !== last) {
            last = marks;
            if (!seen.has(marks)) {
              seen.set(marks, Date.now());
            }
          }
        }, pollMs);
      }, { pollMs: POLL_MS, mark: MARK });

      await writer.evaluate((mark) => {
        const sent = new Map<number, number>();
        (window as unknown as { __sent: Map<number, number> }).__sent = sent;

        document.querySelector('textarea')?.addEventListener('input', (event) => {
          const value = (event.target as HTMLTextAreaElement).value;
          let marks = 0;
          for (const character of value) {
            if (character === mark) {
              marks++;
            }
          }

          if (!sent.has(marks)) {
            sent.set(marks, Date.now());
          }
        });
      }, MARK);

      // The other eighteen, typing at a person's rate for the duration.
      let backgroundRunning = true;
      const backgroundTasks: Promise<void>[] = [];
      for (const member of members.slice(1)) {
        const { connection, replicaId } = await background(system, member.subject, documentId);
        connections.push(connection);

        backgroundTasks.push((async () => {
          const replica = new Replica(parseReplicaId(replicaId));
          let at = 0;
          while (backgroundRunning) {
            const operations = [replica.insert(at++, NOISE)];
            await connection.invoke('SubmitAsync', {
              DocumentId: documentId,
              ReplicaId: replicaId,
              Operations: encodeOperations(operations),
            }).catch(() => { /* a refusal is load too; the measured pair is what matters */ });

            await new Promise((done) => setTimeout(done, 1000 / BACKGROUND_RATE));
          }
        })());
      }

      await writer.click('textarea');

      // Paced like a person, for the same reason the background editors are:
      // §8's target says twenty concurrent editors, and an editor is someone
      // typing, not a loop pressing keys as fast as Playwright will carry them.
      // §8's rule 2, which this harness did not satisfy until 9.5: the
      // generator's utilisation is reported beside the result, or the result is
      // not one. Whole-box rather than this process, because the browsers where
      // the rendering happens are separate processes — a figure covering only
      // the generator would have reported it idle while the box was saturated.
      const cpuBefore = sampleCpu();
      const generatorBefore = process.cpuUsage();
      const started = Date.now();
      for (let i = 0; i < KEYSTROKES; i++) {
        const due = started + (i * 1000) / BACKGROUND_RATE;
        const wait = due - Date.now();
        if (wait > 0) {
          await new Promise((done) => setTimeout(done, wait));
        }

        await writer.keyboard.press(MARK);
      }

      const typedFor = (Date.now() - started) / 1000;
      const boxBusy = cpuBetween(cpuBefore, sampleCpu());
      const generatorCpu = process.cpuUsage(generatorBefore);
      const generatorBusy =
        ((generatorCpu.user + generatorCpu.system) / 1e6) / (typedFor * where.cores) * 100;

      // Waited for, but not with waitForFunction: a bare timeout there says
      // "the reader did not get there" and nothing about how far it got or
      // whether the writer sent them all (§13.23). This settles for a bounded
      // period and then reports what both ends actually hold, so a shortfall
      // arrives as numbers rather than as a stack trace.
      const countMarks = async (page: Page) => page.evaluate((mark) => {
        const value = document.querySelector('textarea')?.value ?? '';
        let marks = 0;
        for (const character of value) {
          if (character === mark) {
            marks++;
          }
        }

        return marks;
      }, MARK);

      const settleUntil = Date.now() + 120_000;
      let readerMarks = 0;
      while (Date.now() < settleUntil) {
        readerMarks = await countMarks(reader);
        if (readerMarks >= KEYSTROKES) {
          break;
        }

        await new Promise((done) => setTimeout(done, 250));
      }

      const writerMarks = await countMarks(writer);

      // What the application itself says about both ends. A shortfall that is
      // the client reporting a problem and a shortfall that is silent loss are
      // different bugs, and the difference is on screen.
      const stateOf = async (page: Page) => page.evaluate(() => ({
        state: document.querySelector('[data-testid="state"]')?.textContent ?? '(none)',
        problem: document.querySelector('[data-testid="problem"]')?.textContent ?? '(none)',
        body: document.body.innerText.slice(0, 300),
      }));

      const writerState = await stateOf(writer);
      const readerState = await stateOf(reader);

      // A third party, catching up from scratch after everything has settled.
      // Writer, reader and server are three answers to "how much of this was
      // typed": whichever two agree says where the missing text is. Without it
      // a shortfall is "somewhere between two browsers", which is not a
      // diagnosis.
      const observer = await background(system, 'load-key-1', documentId);
      connections.push(observer.connection);

      const caught = await observer.connection.invoke<{
        Snapshot: Uint8Array | null;
        Operations: Uint8Array;
      }>('CatchUpAsync', {}, true);

      const { decodeSnapshot } = await import('../crdt');
      const server = caught.Snapshot === null
        ? new Replica(parseReplicaId(observer.replicaId))
        : (() => {
            const parts = decodeSnapshot(caught.Snapshot);
            return Replica.import(parseReplicaId(observer.replicaId), parts.elements, parts.versionVector);
          })();

      for (const operation of decodeOperations(caught.Operations)) {
        server.apply(operation);
      }

      const serverMarks = [...server.text].filter((c) => c === MARK).length;

      backgroundRunning = false;
      await Promise.all(backgroundTasks);

      const sent = await writer.evaluate(() =>
        [...(window as unknown as { __sent: Map<number, number> }).__sent.entries()]);
      const seen = await reader.evaluate(() =>
        [...(window as unknown as { __seen: Map<number, number> }).__seen.entries()]);

      const arrivals = new Map(seen);
      const samples: number[] = [];
      for (const [length, at] of sent) {
        const arrived = arrivals.get(length);
        if (arrived !== undefined) {
          samples.push(arrived - at);
        }
      }

      say('§8 target 2 — p99 client keystroke → remote client render < 150 ms');
      say(`  build      ${describeProvenance(where)}`);
      say(`  load       ${EDITORS} editors (${BROWSERS} browsers, ${EDITORS - BROWSERS} protocol clients at ${BACKGROUND_RATE}/s)`);
      say(`  typed      ${KEYSTROKES} keystrokes over ${typedFor.toFixed(1)}s`);
      say(`  arrived    writer holds ${writerMarks} marks, reader holds ${readerMarks} of ${KEYSTROKES} typed`);
      say(`  writer     state=${writerState.state} problem=${writerState.problem}`);
      say(`  reader     state=${readerState.state} problem=${readerState.problem}`);
      say(`  server     holds ${serverMarks} of ${KEYSTROKES} marks, read by a third client catching up from scratch`);
      say(`  api log    ${system.log.join('').split('\n').filter((l) => /warn|error|fail|drop|rate/i.test(l)).slice(-8).join(' | ') || '(nothing notable)'}`);
      say(`  matched    ${samples.length} of ${sent.length} keystrokes seen at both ends`);
      say(`  latency ms ${percentiles(samples)}`);
      say(`  host       ${boxBusy === null ? 'utilisation unavailable' : `${boxBusy.toFixed(0)} % of ${where.cores} cores busy while typing`}`);
      say(`  generator  ${generatorBusy.toFixed(0)} % of ${where.cores} cores in this process (server and browsers are not in it)`);
      say(`  error bar  reader polls every ${POLL_MS} ms, so each sample carries up to +${POLL_MS} ms`);

      // The match rate is the guard. A run where most keystrokes never
      // correlated would report a p99 over whichever few did, and those are the
      // fast ones — the number would improve as the system got worse.
      // Three answers to "how much of this was typed", and the two that agree
      // say where the rest is. The reader having what the server has, and the
      // server not having what the writer holds, puts the missing text in the
      // writer's outbox — not in the network, not in the reader, and not lost.
      expect(
        readerMarks,
        `writer holds ${writerMarks}, server holds ${serverMarks}, reader holds ${readerMarks}. `
        + `${serverMarks < writerMarks * 0.9
          ? "The server never received them: the writer's outbox is the constraint, "
            + 'and SyncController.drain submits one batch at a time, awaiting each.'
          : 'The server had them and the reader did not.'}`,
      ).toBeGreaterThan(KEYSTROKES * 0.9);

      expect(samples.length).toBeGreaterThan(KEYSTROKES * 0.9);

      const sorted = [...samples].sort((a, b) => a - b);
      const p99 = sorted[Math.ceil(0.99 * sorted.length) - 1]!;
      expect(p99).toBeLessThan(150);
    } finally {
      await Promise.all(connections.map((c) => c.stop().catch(() => {})));
      await system?.close();

      if (process.env.EDITOR_LOAD_REPORT !== undefined && report.length > 0) {
        const { appendFileSync } = await import('node:fs');
        appendFileSync(process.env.EDITOR_LOAD_REPORT, `${report.join('\n')}\n`);
      }
    }
  });
});
