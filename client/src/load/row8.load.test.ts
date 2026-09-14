import { describe, expect, it } from 'vitest';
import { HttpTransportType, HubConnectionBuilder, type HubConnection, type IHttpConnectionOptions } from '@microsoft/signalr';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';
import { startApi, startOidc, provision, type Api, type Oidc } from '../interop/harness';
import { encodeOperations, parseReplicaId, Replica } from '../crdt';

/**
 * Row 8: two instances, a break on one, and the dashboards read afterwards.
 *
 * @remarks
 * <p>
 * This file <b>runs the stack and applies load</b>. It deliberately does not
 * assert what the dashboards should show, and it prints no verdict: the
 * diagnosis is performed afterwards by following
 * `docs/row-8-diagnosis.md`'s procedure against `scripts/dashboard.sh`, and a
 * test that asserted the expected symptom would be the diagnosis written in
 * advance by the party that applied the break.
 * </p><p>
 * The break is not a switch in the product. Two directories, two builds, one of
 * which was compiled from sabotaged source — a fault-injection hook shipped so
 * an exercise can use it is a permanent weakness bought for convenience.
 * </p>
 */
describe('row 8 — two instances, one broken', () => {
  const ALPHA_ADMIN = 9101;
  const BETA_ADMIN = 9102;

  /** Clients per instance. Equal, so a difference is not a load artefact. */
  const CLIENTS = 6;

  /**
   * How often the instances refresh their state-derived readings.
   *
   * @remarks
   * Shorter than the product default, because this exercise finishes inside one
   * default interval and the dashboards would show the reading taken at startup
   * against an empty database — all zeros, which reads exactly like a frontier
   * nobody has touched. The lag is real and is a property of the design; the
   * dashboard reports the reading's age beside the numbers so it is visible
   * rather than inferred.
   */
  const READING_INTERVAL = '00:00:03';

  async function join(api: Api, oidc: Oidc, subject: string, documentId: string) {
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
    return { connection, replicaId };
  }

  it('runs load across both instances and leaves them up to be read', async () => {
    const alphaDll = process.env.ROW8_ALPHA_DLL ?? '';
    const betaDll = process.env.ROW8_BETA_DLL ?? '';
    expect(alphaDll, 'ROW8_ALPHA_DLL must point at the broken build').not.toBe('');
    expect(betaDll, 'ROW8_BETA_DLL must point at the clean build').not.toBe('');

    const oidc = await startOidc();

    // A log buffer each. startApi learns its own address by scanning the buffer
    // for the line Kestrel prints, so two instances sharing one buffer both
    // find the FIRST address in it — and the second instance is handed the
    // first one's URL. The run that found this reported twelve connections on
    // alpha and none on beta: a two-instance stack that was one instance twice,
    // which would have made every comparison below meaningless while looking
    // exactly like the break.
    const alphaLog: string[] = [];
    const betaLog: string[] = [];

    const alpha = await startApi(oidc, alphaLog, {
      dllPath: alphaDll,
      env: {
        Admin__Port: String(ALPHA_ADMIN),
        Instance__Id: 'alpha',
        StateReadings__Interval: READING_INTERVAL,
      },
    });

    const beta = await startApi(oidc, betaLog, {
      dllPath: betaDll,
      env: {
        Admin__Port: String(BETA_ADMIN),
        Instance__Id: 'beta',
        StateReadings__Interval: READING_INTERVAL,
      },
    });

    // Asserted, not assumed. Two instances that answer on one address are the
    // failure this exercise cannot survive, and it is silent.
    expect(alpha.baseUrl, 'the two instances answered on one address').not.toBe(beta.baseUrl);

    const connections: HubConnection[] = [];
    try {
      const members = Array.from({ length: CLIENTS * 2 }, (_, i) => ({
        subject: `row8-${i}`,
        role: 'editor' as const,
      }));

      const documentId = await provision(alpha.baseUrl, oidc, {
        owner: 'row8-owner',
        members,
      });

      // Equal load on both, and every one of §5's three report paths exercised:
      // catch-up on join, the vector on each submission, and an explicit
      // acknowledgement standing in for the client's timer.
      for (const [index, member] of members.entries()) {
        const api = index % 2 === 0 ? alpha : beta;
        const { connection, replicaId } = await join(api, oidc, member.subject, documentId);
        connections.push(connection);

        const replica = new Replica(parseReplicaId(replicaId));
        await connection.invoke('CatchUpAsync', {}, false);

        for (let n = 0; n < 8; n++) {
          const operations = [replica.insert(n, 'x')];
          const known: Record<string, number> = {};
          for (const [id, next] of replica.versionVector) {
            known[id] = Number(next);
          }

          await connection.invoke('SubmitAsync', {
            DocumentId: documentId,
            ReplicaId: replicaId,
            Operations: encodeOperations(operations),
            Known: known,
          });
        }

        // Once more with everything it now holds, the way the client's timer
        // reports (§5's third path).
        const finalVector: Record<string, number> = {};
        for (const [id, next] of replica.versionVector) {
          finalVector[id] = Number(next);
        }

        await connection.invoke('AcknowledgeAsync', finalVector);
        await connection.invoke('CatchUpAsync', finalVector, false);
      }

      // Long enough for a reading to land after the load. The state-derived
      // gauges are refreshed on a schedule, so reading them the instant the
      // last client finishes shows the state before it started.
      await new Promise((done) => setTimeout(done, 6_000));

      // Held open while the dashboards are read. The connection gauge is a live
      // count, and an instance whose clients have all gone home reads zero for
      // a reason that has nothing to do with the break.
      const { execFileSync } = await import('node:child_process');
      const rendered = execFileSync(
        'scripts/dashboard.sh',
        [`http://127.0.0.1:${ALPHA_ADMIN}`, `http://127.0.0.1:${BETA_ADMIN}`],
        { cwd: new URL('../../..', import.meta.url).pathname, encoding: 'utf8' },
      );

      console.log(rendered);

      if (process.env.ROW8_REPORT !== undefined) {
        const { writeFileSync } = await import('node:fs');
        writeFileSync(process.env.ROW8_REPORT, rendered);
      }
    } finally {
      await Promise.all(connections.map((c) => c.stop().catch(() => {})));
      await alpha.close();
      await beta.close();
      await oidc.close();
    }
  });
});
