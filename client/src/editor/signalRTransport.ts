import {
  HubConnectionBuilder,
  HttpTransportType,
  type HubConnection,
  type IHttpConnectionOptions,
} from '@microsoft/signalr';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';

import type { CatchUpOutcome, Session, SubmitOutcome, Transport } from './SyncController';

/** What `negotiate` answers (§7). */
interface Negotiated {
  ticket: string;
  documentId: string;
  replicaId: string;
  role: number;
  resumed: boolean;
}

/** What the hub answers a submission with. */
interface SubmitResult {
  Code: string | null;
  Accepted: number;
}

/** What the hub answers a catch-up with. */
interface CatchUpResult {
  Code: string | null;
  Snapshot: Uint8Array | null;
  Operations: Uint8Array;
}

/** A batch the server broadcast (§8). */
interface Broadcast {
  DocumentId: string;
  Operations: Uint8Array;
  ServerSeq: number;
}

/**
 * How the client fetches; overridable so a harness can supply credentials.
 */
export type Fetcher = (url: string, init: RequestInit) => Promise<Response>;

export interface SignalRTransportOptions {
  readonly baseUrl: string;
  readonly documentId: string;
  readonly fetch?: Fetcher;

  /** Builds the connection. Overridable for a non-browser harness. */
  readonly build?: (url: string) => HubConnection;
}

/**
 * The real connection, as a {@link Transport} (§7, §8).
 *
 * @remarks
 * <p>
 * Deliberately thin. Everything worth reasoning about — when to reconnect, what
 * to do with a refusal, when to catch up — lives in `SyncController`, where it
 * can be driven through failures a real socket only produces by accident. This
 * class turns hub calls into promises and nothing else.
 * </p><p>
 * Payloads cross as opaque bytes in §6's encoding, which MessagePack frames
 * without inspecting (§13.13a). The only things named here belong to the
 * envelope.
 * </p><p>
 * SignalR's own automatic reconnect is **not** used. It would reconnect the
 * socket without re-running `negotiate`, so the client would come back with a
 * ticket already redeemed and a replica claim it no longer holds — and §8's
 * catch-up would never run, leaving a client that is connected and silently
 * behind. Reconnection is a session-level concern, and the controller owns it.
 * </p>
 */
export class SignalRTransport implements Transport {
  private readonly options: SignalRTransportOptions;
  private assigned: string | null = null;
  private readonly http: Fetcher;
  private connection: HubConnection | null = null;
  private broadcastHandler: ((operations: Uint8Array) => void) | null = null;
  private closedHandler: (() => void) | null = null;
  private closing = false;

  constructor(options: SignalRTransportOptions) {
    this.options = options;
    this.http = options.fetch ?? ((url, init) => fetch(url, init));
  }

  async connect(replicaId: string | null): Promise<Session> {
    await this.close();
    this.closing = false;

    const response = await this.http(
      `${this.options.baseUrl}/documents/${this.options.documentId}/negotiate`,
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ replicaId }),
      },
    );

    if (!response.ok) {
      throw new Error(`negotiate failed: ${response.status}`);
    }

    const negotiated = (await response.json()) as Negotiated;
    const url = `${this.options.baseUrl}/hub/editor?access_token=${encodeURIComponent(negotiated.ticket)}`;

    const connection = this.options.build?.(url) ?? defaultConnection(url);

    connection.on('ReceiveOperations', (broadcast: Broadcast) => {
      this.broadcastHandler?.(broadcast.Operations);
    });

    connection.onclose(() => {
      // Suppressed while this class is closing on purpose: a deliberate stop is
      // not a lost connection, and reporting it as one would have the
      // controller schedule a reconnect to something it just left.
      if (!this.closing) {
        this.closedHandler?.();
      }
    });

    await connection.start();
    this.connection = connection;

    // Kept because every submission carries it: §7's tier-1 check compares the
    // batch's replica id against this connection's binding, and the binding is
    // whatever negotiate assigned — which is not necessarily what was asked
    // for.
    this.assigned = negotiated.replicaId;

    return { replicaId: negotiated.replicaId, resumed: negotiated.resumed };
  }

  async submit(operations: Uint8Array): Promise<SubmitOutcome> {
    const connection = this.require();
    if (this.assigned === null) {
      throw new Error('Not connected.');
    }

    const result = await connection.invoke<SubmitResult>('SubmitAsync', {
      DocumentId: this.options.documentId,
      ReplicaId: this.assigned,
      Operations: operations,
    });

    return { code: result.Code };
  }

  async catchUp(
    known: Record<string, number>,
    forceSnapshot: boolean,
  ): Promise<CatchUpOutcome> {
    const connection = this.require();
    const result = await connection.invoke<CatchUpResult>('CatchUpAsync', known, forceSnapshot);

    return {
      code: result.Code,
      snapshot: result.Snapshot,
      operations: result.Operations,
    };
  }

  onBroadcast(handler: (operations: Uint8Array) => void): void {
    this.broadcastHandler = handler;
  }

  onClosed(handler: () => void): void {
    this.closedHandler = handler;
  }

  async close(): Promise<void> {
    const connection = this.connection;
    this.connection = null;

    if (connection === null) {
      return;
    }

    this.closing = true;
    await connection.stop();
  }

  /** Drops the socket without saying goodbye, as a network does. */
  async simulateNetworkLoss(): Promise<void> {
    const connection = this.connection;
    this.connection = null;

    if (connection !== null) {
      await connection.stop();
    }
  }

  private require(): HubConnection {
    if (this.connection === null) {
      throw new Error('Not connected.');
    }

    return this.connection;
  }
}

function defaultConnection(url: string): HubConnection {
  const options: IHttpConnectionOptions = {
    transport: HttpTransportType.WebSockets,
    skipNegotiation: true,
  };

  return new HubConnectionBuilder()
    .withUrl(url, options)
    .withHubProtocol(new MessagePackHubProtocol())
    .build();
}
