# Collaborative text editor with a CRDT core — project specification

This document is the contract for the project. Every change must be justifiable
against it. If a requirement here is wrong or impossible, say so and propose a
change to this file rather than silently deviating.

---

## 1. Goal

A production-grade collaborative text editor where multiple users edit the same
document simultaneously, work offline, and converge on reconnect without a
central sequencer resolving conflicts.

The CRDT implementation is the point of this project. It must be written from
scratch and property-tested. Do not add Yjs, Automerge, ShareDB, or any other
CRDT/OT library as a dependency — not for the server, not for the client, not
"temporarily to unblock."

## 2. Non-goals

Explicitly out of scope. Do not build these, and do not add abstractions in
anticipation of them:

- Rich text (bold, headings, embeds). Plain UTF-8 text only.
- Comments, suggestions, or track-changes.
- File upload, images, or attachments.
- Mobile apps. Web client only.
- Multi-region active-active deployment.
- Public document sharing or anonymous access.

## 3. Stack

Pin these. Do not substitute without asking.

- .NET 9, C# 13, nullable reference types enabled, warnings as errors
- ASP.NET Core Minimal APIs for REST, SignalR for realtime transport
- PostgreSQL 16, accessed via Npgsql; EF Core for schema and non-hot-path queries
- Redis 7 for the SignalR backplane and rate-limit counters
- React 19 + TypeScript 5.x + Vite for the client
- xUnit + FsCheck (property tests) + Testcontainers (integration tests)
- Serilog structured logging, OpenTelemetry traces and metrics
- Docker Compose for local dev

## 4. Architecture

Four projects plus the client:

```
src/
  Crdt.Core/          pure algorithm, zero I/O, zero ASP.NET references
  Editor.Domain/      documents, permissions, versioning — no infrastructure
  Editor.Infrastructure/  Postgres, Redis, EF Core mappings
  Editor.Api/         Minimal APIs, SignalR hub, auth, composition root
client/               React + TypeScript replica
tests/
  Crdt.Core.Tests/    property + fuzz tests
  Editor.Api.Tests/   integration tests against real Postgres/Redis
  Conformance/        cross-implementation trace tests (C# vs TypeScript)
```

Dependency direction is strictly inward: `Api → Infrastructure → Domain → Core`.
`Crdt.Core` references nothing but the BCL. A compile error is the enforcement
mechanism — do not weaken it with shared "common" projects.

The server is a **relay plus durable log**, not an authority on document content.
It validates, persists, orders causally, and fans out. It does not transform or
resolve operations.

## 5. The CRDT

Implement **RGA (Replicated Growable Array)** for a sequence of UTF-8 characters.

### Identifiers

Each inserted character carries an immutable `ElementId` of
`(ReplicaId: Guid, Counter: ulong)`. Counters are per-replica and monotonic.
Comparison for tie-breaking is `Counter` descending, then `ReplicaId` ascending —
deterministic and identical in both implementations.

### Operations

```
Insert(id, originId, value)   // insert `value` immediately after `originId`
Delete(id)                    // tombstone the element with this id
```

`originId` is the id of the element the new character follows, or a sentinel
document-start id. Never a numeric index — indices are meaningless across
replicas.

### Causal delivery

An `Insert` cannot be applied before the element it references exists locally.
Buffer such operations in a pending set keyed by the missing dependency and
retry when it arrives. Do not drop them, and do not apply them out of order
"because it usually works."

### Tombstones and garbage collection

Deleted elements are tombstoned, not removed. Implement GC based on **causal
stability**: an operation is collectable only when every known replica's version
vector shows it has been observed. Track a per-document version vector.
GC runs as a background job, never on the request path. If you cannot prove an
element is causally stable, keep it.

### Required invariants

These are the acceptance criteria for `Crdt.Core`. Write them as executable
property tests before writing the implementation.

1. **Convergence** — for any set of operations delivered in any order, with any
   duplicates, all replicas that have seen the same set produce identical text.
2. **Idempotency** — applying any operation twice equals applying it once.
3. **Commutativity** — concurrent operations produce the same result in either order.
4. **Causal readiness** — an operation is never applied before its dependency.
5. **Intention preservation** — a character inserted between X and Y remains
   between X and Y after any concurrent remote operations.
6. **No resurrection** — a deleted element never reappears, including when a
   concurrent insert references it as origin.
7. **GC safety** — collecting causally stable tombstones does not change the
   text produced by any subsequent legal operation sequence.

### Testing approach

- **Property tests (FsCheck):** generate random operation sets, apply to N
  simulated replicas in randomized orders with random duplication, assert every
  invariant above.
- **Deterministic simulation:** all randomness comes from a seed printed on
  failure, so any failure is reproducible by rerunning with that seed. This is
  non-negotiable — a non-reproducible CRDT bug is unfixable.
- **Shrinking:** on failure, minimize the operation trace before reporting.
- Target ≥90% line coverage on `Crdt.Core`. Coverage elsewhere is not a goal.

## 6. Persistence

Append-only operation log plus periodic snapshots.

```sql
documents        (id, owner_id, title, created_at, updated_at, deleted_at)
document_ops     (document_id, replica_id, counter, op_type, origin_replica,
                  origin_counter, value, server_seq, created_at)
document_snapshots (document_id, server_seq, state, version_vector, created_at)
document_members (document_id, user_id, role, granted_at, granted_by)
```

- Primary key on `document_ops` is `(document_id, replica_id, counter)` — this
  makes duplicate submission a no-op at the database level, which is the
  cheapest correct place to enforce idempotency.
- `server_seq` is a per-document monotonic sequence for efficient catch-up
  queries. It is a delivery optimization, not a causal order — never use it to
  determine CRDT semantics.
- Snapshot every N operations (configurable, default 500). Loading a document
  reads the latest snapshot plus operations after its `server_seq`.
- `document_ops` is partitioned by `document_id` hash. Include the migration.
- All writes through parameterized commands. Zero string-concatenated SQL
  anywhere in the codebase.

## 7. Security

Treat every one of these as a hard requirement with a corresponding test.

**Authentication**
- OIDC with JWT bearer tokens. Validate issuer, audience, lifetime, and
  signature. No `ValidateIssuer = false` anywhere, including in dev config.
- SignalR WebSocket connections authenticate via the `access_token` query
  parameter (browsers cannot set headers on WS). Because of that, the token must
  never be written to logs — configure request logging to redact it explicitly,
  and add a test asserting no log line contains a token.

**Authorization**
- Every hub method and every endpoint re-checks document membership. Do not
  trust the client's claim of which document it is in, and do not cache the
  authorization decision for the connection lifetime — check per operation.
- Roles: `Owner`, `Editor`, `Viewer`. Viewers receive broadcasts but any write
  operation from a viewer is rejected and logged.
- Authorization failures return 404, not 403, for documents the caller cannot
  see — do not leak document existence.

**Input validation**
- Reject operations whose `ReplicaId` does not match the one bound to the
  authenticated connection. A client must not be able to forge operations
  attributed to another replica.
- Cap: single op value ≤ 64 bytes, batch ≤ 256 ops, message ≤ 64 KB, document
  ≤ 5 MB of live text, ≤ 50 concurrent replicas per document. All configurable,
  all enforced server-side, all with a test proving the limit rejects.
- Validate UTF-8 and reject lone surrogates, and normalize nothing — the CRDT
  operates on code points and normalization would break identity.

**Abuse resistance**
- Per-user and per-connection rate limits on operation submission, backed by
  Redis so limits hold across instances. Return a structured throttle response,
  do not silently drop.
- Connection limits per user. Reject new connections past the cap.
- A malformed or oversized message closes the connection after logging.

**Client**
- Content Security Policy with no `unsafe-inline`. HSTS. `X-Content-Type-Options`.
- The editor renders text into a DOM text node — never `innerHTML`, never
  `dangerouslySetInnerHTML`. Add a test that a document containing
  `<script>alert(1)</script>` renders as literal text.

**Secrets**
- Nothing in `appsettings.json` but non-secret defaults. Local secrets via
  `dotnet user-secrets`, deployed secrets via environment variables. Add a CI
  step that fails on committed secrets.

## 8. Scalability

- App servers are stateless. Any client may connect to any instance; no sticky
  sessions. Redis backplane fans out SignalR messages.
- Hot path (receive op → validate → persist → broadcast) must not load full
  document state. Validate against the version vector and the referenced origin
  only.
- Batch operation persistence: buffer for up to 50ms or 100 ops, whichever comes
  first, then write in one round trip.
- Backpressure: bounded per-connection outbound channel. If a client cannot keep
  up, drop it to catch-up-via-snapshot rather than growing the buffer unbounded.
- Snapshot compaction and tombstone GC run in a background service, jittered and
  guarded by an advisory lock so multiple instances do not duplicate work.

**Performance targets** (measured with a load test in `tests/`, not asserted by
vibes):
- p99 end-to-end op propagation < 150ms at 20 concurrent editors on one document
- 1,000 concurrent connections per instance at < 2 GB RSS
- Document with 100k live characters and 500k tombstones loads in < 500ms

## 9. Client

- React 19, TypeScript strict mode.
- Ships its own RGA replica implementing the identical algorithm. Local edits
  apply optimistically to the local replica and render immediately; remote ops
  merge in. There is no server round trip in the typing path.
- IndexedDB persists the local replica and an outbox of unsent operations, so a
  full offline session survives a page reload and syncs on reconnect.
- Reconnect with exponential backoff and jitter. On reconnect, send the local
  version vector and receive only the missing operations.
- Presence (remote cursors) is ephemeral and never persisted.

### Conformance testing

`tests/Conformance/` holds shared JSON operation traces. A test runner feeds
each trace through the C# implementation and the TypeScript implementation and
asserts byte-identical output text and identical version vectors. Any divergence
fails the build. Generate new traces from the property-test generator so the
corpus grows with the fuzzer.

## 10. Observability

- Structured logs, no PII, no document content, no tokens. Correlation id per
  connection, propagated to every log line and span.
- Metrics: ops received/applied/rejected, pending-buffer depth, propagation
  latency histogram, active connections, GC reclaimed elements, snapshot age.
- Traces spanning receive → validate → persist → broadcast.
- `/health/live` and `/health/ready`; readiness checks Postgres and Redis.

## 11. Build phases

Do not start a phase before the previous one is committed, green in CI, and
reviewed. At the end of each phase, stop and report.

| Phase | Deliverable | Done when |
|---|---|---|
| 0 | Repo, solution, Docker Compose, CI, `AGENTS.md` | `dotnet test` and `npm test` pass on empty suites in CI |
| 1 | `Crdt.Core` + property tests | All 7 invariants pass 10,000 randomized cases |
| 2 | Postgres schema, op log, snapshots | Integration tests via Testcontainers; crash-during-write test passes |
| 3 | SignalR hub, auth, causal delivery | Two real clients converge; auth tests pass |
| 4 | React client + local replica | Offline edit for 5 min, reconnect, converge |
| 5 | Conformance harness | 1,000 shared traces match across both implementations |
| 6 | Security hardening | Every requirement in §7 has a passing test |
| 7 | Scale + observability | Load test hits the §8 targets; dashboards exist |

## 12. Working agreement

- **Plan before code.** At the start of each phase, produce a task breakdown and
  wait for approval. Do not begin implementing from this document directly.
- **Tests first for `Crdt.Core`.** The invariants in §5 are written and failing
  before the implementation exists.
- **Small commits.** One logical change each, conventional commit messages,
  every commit leaves the build green.
- **Ask before adding a dependency.** State what it does and why the BCL is
  insufficient. The answer for anything CRDT-shaped is no.
- **No stubs presented as done.** If something is unimplemented it throws
  `NotImplementedException` and is listed in the phase report. Never a silent
  no-op, never a hardcoded return that makes a test pass.
- **Report honestly.** If an invariant fails and you cannot fix it, say so and
  show the minimized failing trace. Do not weaken the test to make it green — if
  you believe a test is wrong, argue for changing this spec first.
- **Say when you are unsure.** Distributed systems bugs hide in the cases where
  the implementer felt "this probably works." Flag those explicitly.
