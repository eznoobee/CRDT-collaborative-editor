import type { TokenSource } from '../auth/tokenSource';

/** A role, as §6 and the hub carry it. */
export const ROLE = { viewer: 0, editor: 1, owner: 2 } as const;

export type RoleValue = (typeof ROLE)[keyof typeof ROLE];

/** A document as the caller can see it (§9). */
export interface DocumentSummary {
  readonly id: string;
  readonly title: string;
  readonly role: RoleValue;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/** One member of a document (§9). */
export interface MemberEntry {
  readonly userId: string;
  readonly displayName: string;
  readonly role: RoleValue;
  readonly grantedAt: string;
}

/** Who the caller is, in the terms a grant is written in (§9). */
export interface Identity {
  readonly userId: string;
  readonly displayName: string;
}

/**
 * A refusal from the document API, carrying the status the caller must act on.
 *
 * @remarks
 * The status is kept rather than flattened into a message, because §7 makes
 * 404 and 403 mean different things — "there is nothing here for you" against
 * "you can see it and may not do this" — and a client that showed one sentence
 * for both would undo the distinction the server took care to draw.
 */
export class ApiRefusal extends Error {
  readonly status: number;

  constructor(status: number, detail: string) {
    super(detail);
    this.name = 'ApiRefusal';
    this.status = status;
  }
}

/**
 * §9's document API, as the browser calls it.
 *
 * @remarks
 * Same origin as this application, so there is no CORS to configure and no
 * base URL to get wrong; the token goes in a header, never in a URL (§7).
 */
export class DocumentApi {
  private readonly origin: string;
  private readonly tokens: TokenSource;

  constructor(origin: string, tokens: TokenSource) {
    this.origin = origin;
    this.tokens = tokens;
  }

  me(): Promise<Identity> {
    return this.call<Identity>('GET', '/me');
  }

  list(): Promise<DocumentSummary[]> {
    return this.call<DocumentSummary[]>('GET', '/documents');
  }

  create(title: string): Promise<DocumentSummary> {
    return this.call<DocumentSummary>('POST', '/documents', { title });
  }

  members(documentId: string): Promise<MemberEntry[]> {
    return this.call<MemberEntry[]>('GET', `/documents/${documentId}/members`);
  }

  grant(documentId: string, userId: string, role: RoleValue): Promise<MemberEntry> {
    return this.call<MemberEntry>('PUT', `/documents/${documentId}/members/${userId}`, { role });
  }

  async revoke(documentId: string, userId: string): Promise<void> {
    await this.call<null>('DELETE', `/documents/${documentId}/members/${userId}`);
  }

  private async call<T>(method: string, path: string, body?: unknown): Promise<T> {
    const token = await this.tokens.token();
    const response = await fetch(`${this.origin}${path}`, {
      method,
      headers: {
        authorization: `Bearer ${token}`,
        ...(body === undefined ? {} : { 'content-type': 'application/json' }),
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });

    if (!response.ok) {
      // The server's own words where it gave any. A validation problem says
      // which field and why — "a role can be granted only to someone who has
      // signed in" is the whole point of that refusal, and replacing it with
      // "request failed" would leave the caller with nothing to do (§13.13).
      throw new ApiRefusal(response.status, await detailOf(response));
    }

    return response.status === 204 ? (null as T) : ((await response.json()) as T);
  }
}

async function detailOf(response: Response): Promise<string> {
  const text = await response.text().catch(() => '');
  if (text === '') {
    return `The server answered ${response.status}.`;
  }

  try {
    const problem = JSON.parse(text) as { errors?: Record<string, string[]>; detail?: string };
    const first = Object.values(problem.errors ?? {})[0]?.[0];
    return first ?? problem.detail ?? `The server answered ${response.status}.`;
  } catch {
    return `The server answered ${response.status}.`;
  }
}
