import {
  HubConnection,
  HubConnectionBuilder,
  HttpTransportType,
  type IHttpConnectionOptions,
} from '@microsoft/signalr';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';

import {
  Replica,
  decodeOperations,
  decodeSnapshot,
  encodeOperations,
  parseReplicaId,
  type ElementId,
  type Operation,
} from '../crdt';
import { serializeSnapshot } from '../crdt/snapshotJson';

/** What the server hands back from negotiate (§7). */
interface Negotiated {
  ticket: string;
  documentId: string;
  replicaId: string;
  role: number;
}

/** What the hub answers a submission with (§7). */
export interface SubmitResult {
  Code: string | null;
  Accepted: number;
}

/** What the hub answers a catch-up with (§8). */
export interface CatchUpResult {
  Code: string | null;
  Snapshot: Uint8Array | null;
  Operations: Uint8Array;
  ServerSeq: number;
}

/** A batch the server broadcast to this connection (§8). */
export interface Broadcast {
  DocumentId: string;
  Operations: Uint8Array;
  ServerSeq: number;
}

/**
 * The TypeScript core connected to the running C# server.
 *
 * @remarks
 * Payloads stay opaque across the boundary: `Operations` and `Snapshot` are §6
 * byte strings that MessagePack frames without inspecting, which is §13.13a's
 * constraint. Every field named by hand here belongs to the *envelope*, and the
 * envelope is the only thing the two implementations describe twice.
 */
export class InteropClient {
  private readonly buffered: Broadcast[] = [];
  private readonly waiting: ((broadcast: Broadcast) => void)[] = [];
  private seq = 0n;
  private tail: ElementId | null = null;
  private current: Replica;

  readonly connection: HubConnection;
  readonly negotiated: Negotiated;

  private constructor(connection: HubConnection, negotiated: Negotiated) {
    this.connection = connection;
    this.negotiated = negotiated;
    this.current = new Replica(parseReplicaId(negotiated.replicaId));
  }

  /** This client's own copy of the document. */
  get replica(): Replica {
    return this.current;
  }

  /** §9's normalised form of what this client believes the document is. */
  get normalised(): string {
    return serializeSnapshot(
      this.current.export(),
      this.current.versionVectorEntries,
      this.current.text,
    );
  }

  /** Everything received so far and not yet taken. */
  get pending(): number {
    return this.buffered.length;
  }

  static async join(baseUrl: string, token: string, documentId: string): Promise<InteropClient> {
    const response = await fetch(`${baseUrl}/documents/${documentId}/negotiate`, {
      method: 'POST',
      headers: { authorization: `Bearer ${token}` },
    });

    if (!response.ok) {
      throw new Error(`negotiate failed: ${response.status} ${await response.text()}`);
    }

    const negotiated = (await response.json()) as Negotiated;

    const options: IHttpConnectionOptions = {
      transport: HttpTransportType.WebSockets,
      skipNegotiation: true,
    };

    const connection = new HubConnectionBuilder()
      .withUrl(
        `${baseUrl}/hub/editor?access_token=${encodeURIComponent(negotiated.ticket)}`,
        options,
      )
      .withHubProtocol(new MessagePackHubProtocol())
      .build();

    const client = new InteropClient(connection, negotiated);

    connection.on('ReceiveOperations', (broadcast: Broadcast) => {
      const next = client.waiting.shift();
      if (next) {
        next(broadcast);
      } else {
        client.buffered.push(broadcast);
      }
    });

    await connection.start();
    return client;
  }

  /**
   * Builds this client's next batch, appending after what it typed last.
   *
   * @remarks
   * Encoded by the core's own encoder, not by a copy written for the harness:
   * a second encoder here would make the test a check of the harness against
   * the server rather than of the shipped core against it.
   */
  build(text: string): Uint8Array {
    const replica = parseReplicaId(this.negotiated.replicaId);
    const operations: Operation[] = [];

    for (const character of [...text]) {
      const id = { replica, seq: this.seq++ };
      operations.push({
        kind: 'insert',
        id,
        value: character,
        parent: this.tail,
        side: 'R',
        rightOrigin: null,
      });
      this.tail = id;
    }

    return encodeOperations(operations);
  }

  submit(batch: Uint8Array): Promise<SubmitResult> {
    return this.connection.invoke<SubmitResult>('SubmitAsync', {
      DocumentId: this.negotiated.documentId,
      ReplicaId: this.negotiated.replicaId,
      Operations: batch,
    });
  }

  catchUp(forceSnapshot = false): Promise<CatchUpResult> {
    const known: Record<string, number> = {};
    for (const [replica, next] of this.current.versionVector) {
      known[replica] = Number(next);
    }

    return this.connection.invoke<CatchUpResult>('CatchUpAsync', known, forceSnapshot);
  }

  /** Applies a batch of §6 bytes into this replica. */
  apply(operations: Uint8Array): void {
    for (const operation of decodeOperations(operations)) {
      this.current.apply(operation);
    }
  }

  /** Adopts a catch-up answer the way a reconnecting client would. */
  applyCatchUp(result: CatchUpResult): void {
    if (result.Snapshot) {
      const decoded = decodeSnapshot(result.Snapshot);
      this.current = Replica.import(
        parseReplicaId(this.negotiated.replicaId),
        decoded.elements,
        decoded.versionVector,
      );
    }

    this.apply(result.Operations);
  }

  /** Waits for the next broadcast, or rejects. */
  next(withinMs = 15_000): Promise<Broadcast> {
    const ready = this.buffered.shift();
    if (ready) {
      return Promise.resolve(ready);
    }

    return new Promise<Broadcast>((done, fail) => {
      const timer = setTimeout(
        () => fail(new Error('no broadcast arrived within the timeout')),
        withinMs,
      );

      this.waiting.push((broadcast) => {
        clearTimeout(timer);
        done(broadcast);
      });
    });
  }

  async close(): Promise<void> {
    await this.connection.stop();
  }
}
