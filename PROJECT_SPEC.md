# Collaborative text editor with a CRDT core — project specification

This document is the contract for the project. Every change must be justifiable
against it. If a requirement here is wrong or impossible, say so and propose a
change to this file rather than silently deviating.

Amendments are recorded in §13. Read it before arguing with §5 — several
requirements are the way they are on purpose.

---

## 1. Goal

A production-grade collaborative text editor where multiple users edit the same
document simultaneously, work offline, and converge on reconnect without a
central sequencer resolving conflicts.

The CRDT implementation is the point of this project. It must be written from
scratch and property-tested. Do not add Yjs, Automerge, ShareDB, Loro, Collabs,
or any other CRDT/OT library as a dependency — not for the server, not for the
client, not "temporarily to unblock." **This explicitly includes existing Fugue
and FugueMax implementations.** The algorithm is implemented from the paper
(Weidner & Kleppmann 2023); reference code may be read to resolve ambiguity in
the paper, but not vendored, copied, or depended upon.

## 2. Non-goals

Explicitly out of scope. Do not build these, and do not add abstractions in
anticipation of them:

- Rich text (bold, headings, embeds). Plain UTF-8 text only.
- Comments, suggestions, or track-changes.
- File upload, images, or attachments.
- Mobile apps. Web client only.
- Multi-region active-active deployment.
- Public document sharing or anonymous access.
- **Undo/redo.** Undo in a CRDT is not "apply the inverse" and is a research
  problem in its own right. Out of scope for every phase.

## 3. Stack

Pin these. Do not substitute without asking.

- **.NET 10 (LTS), C# 14**, nullable reference types enabled, warnings as errors
- ASP.NET Core Minimal APIs for REST, SignalR for realtime transport
- PostgreSQL 16, accessed via Npgsql; EF Core for schema and non-hot-path queries
- Redis 7 for the SignalR backplane and rate-limit counters
- React 19 + TypeScript 5.x + Vite for the client
- xUnit + Testcontainers (integration tests); property tests use a hand-rolled
  generator/shrinker (see §5)
- Stryker.NET for mutation testing of `Crdt.Core`
- Serilog structured logging, OpenTelemetry traces and metrics
- Docker Compose for local dev

If a pinned package has no .NET 10 compatible release, stop and report it.
Do not downgrade the runtime and do not silently drop the package.

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

Dependencies point inward and nothing points outward:

```
Editor.Api            → Editor.Infrastructure, Editor.Domain, Crdt.Core
Editor.Infrastructure → Editor.Domain, Crdt.Core
Editor.Domain         → Crdt.Core
Crdt.Core             → BCL only
```

The project graph is the first line of enforcement; an assembly-reference test
(§11 Phase 0) is the second, because the project graph alone does not stop
someone adding an infrastructure NuGet package to `Editor.Domain`. Do not
weaken either with a shared "common" project.

The server is a **relay plus durable log**, not an authority on document content.
It validates, persists, orders causally, and fans out. It does not transform or
resolve operations.

## 5. The CRDT

Implement **FugueMax** for a sequence of Unicode code points, as described in
"The Art of the Fugue: Minimizing Interleaving in Collaborative Text Editing"
(Weidner & Kleppmann, 2023).

FugueMax replaces RGA, which was specified here originally. The reason is
recorded in §13.1: RGA cannot satisfy invariant 8.

### Model

The document is a tree. Each element is a node; the visible text is the in-order
traversal of the tree with tombstones skipped.

```
Node:
  Id          ElementId          immutable, globally unique
  Value       Rune               exactly one Unicode code point
  Parent      ElementId          the node this one attaches to
  Side        'L' | 'R'          which child list of Parent it belongs to
  RightOrigin ElementId | null   present only when Side = 'R';
                                 null means "end of document"
  IsDeleted   bool               tombstone flag
```

A single root sentinel node exists implicitly in every replica with
`Id = (ReplicaId.Empty, 0)`. It is always tombstoned and is never transmitted.

**Traversal.** In-order: for each node, emit its left children in sibling order,
then the node's own value if not tombstoned, then its right children in sibling
order.

### Identifiers

Each node carries an immutable `ElementId` of `(ReplicaId: ReplicaId, Seq: ulong)`.

`Seq` is a **dense, per-replica, monotonic counter** starting at 1. Dense means
gapless: a replica's operations are numbered 1, 2, 3, … with no holes. This is
what makes a compact version vector `{ReplicaId → maxSeq}` sound — `maxSeq = n`
implies every operation 1..n from that replica has been observed.

There is no Lamport clock and no ordering timestamp. FugueMax's sibling
comparator never consults `Seq`; ordering comes from tree position,
`RightOrigin`, and `ReplicaId` alone. Do not add one "for safety" — it would be
a field nothing reads. See §13.2.

`Seq` participates in identity and in version vectors. It must never be used to
order siblings.

### ReplicaId comparison — normative

`ReplicaId` is a 128-bit value. It is compared as the **lexicographic ordering of
its 16 bytes in RFC 4122 big-endian order**, unsigned, most significant byte
first.

This is load-bearing: `ReplicaId` ordering is the sole tie-break in FugueMax's
sibling comparator, so any disagreement between the C# and TypeScript
implementations directly reorders user text.

- Do **not** use `System.Guid.CompareTo`. .NET compares `_a` (int32), `_b`
  (int16), `_c` (int16), then bytes `_d`–`_k` individually, which does not match
  big-endian byte order.
- Do **not** compare string forms, and do not rely on Postgres `uuid` collation.
- Implement `ReplicaId` as a wrapper over 16 bytes on both sides with a
  hand-written comparator, and pin it with a conformance trace whose expected
  output changes if the comparator changes.

### Operations

```
Insert(id, parent, side, rightOrigin, value)
Delete(id)
```

There is no `originId` and no index. Indices are meaningless across replicas.

**Placement rule.** To insert a new node between visible neighbours `L` (the
node to the left, or the root sentinel at the start of the document) and the
position after it:

- If `L` has **no right children**, the new node becomes a **right child of `L`**
  (`Parent = L.Id`, `Side = 'R'`), and `RightOrigin` is set to the next node
  after `L` in traversal order that is not a descendant of `L`, or `null` if
  none exists.
- Otherwise, the new node becomes a **left child of `R`** (`Parent = R.Id`,
  `Side = 'L'`, `RightOrigin` unset), where `R` is the next node after `L` in
  traversal order **including tombstones**.

**Sibling ordering — normative.**

- **Right children** of a node are ordered by the **reverse traversal order of
  their `RightOrigin`**, ties broken by `ReplicaId` **ascending**.
- **Left children** of a node are ordered by `ReplicaId` **ascending**.

Comparing two `RightOrigin` values is a comparison of tree positions, not of
ids: walk both nodes up to their common parent, then compare — a left-side
ancestor precedes a right-side ancestor, and same-side siblings compare by their
index in the parent's child list.

Two nodes from the same replica cannot collide as same-parent, same-side,
same-`RightOrigin` siblings; the placement rule prevents it. Assert this as an
invariant in the implementation rather than adding `Seq` as a further tie-break.

### Causal delivery

An operation is **ready** when every id it references exists locally:

- `Insert` depends on `Parent`, and additionally on `RightOrigin` when
  `Side = 'R'` and `RightOrigin` is not null. **Two dependencies, not one.**
- `Delete` depends on the node it tombstones. Deletes buffer on the same rules
  as inserts; do not apply a delete for an unknown id.

Buffer non-ready operations in a pending set keyed by each missing dependency
and retry when it arrives, cascading. Do not drop them except under the GC
watermark rule below, and do not apply them out of order "because it usually
works."

The pending set is bounded per connection and per document (size and age, both
configurable). Exceeding the bound is a protocol violation: reject, log, and
close. An unbounded pending set is a denial-of-service vector, because origins
are client-supplied.

### Tombstones and garbage collection

Deleted elements are tombstoned, not removed. Implement GC based on **causal
stability**: an operation is collectable only when every non-retired replica's
version vector shows it has been observed. Track a per-document version vector.
GC runs as a background job, never on the request path. If you cannot prove an
element is causally stable, keep it.

A tombstone that is still referenced as another live node's `Parent` or
`RightOrigin` cannot be collected outright; collect its value and mark it a
structural placeholder, or skip it. Correctness first — a lower reclamation rate
is acceptable, a broken tree is not.

**Replica retirement.** Causal stability over an open-ended replica set never
converges: one browser tab that never returns blocks GC forever. A replica is
**retired** from the known set after `T_retire` of inactivity.

- `T_retire = 7 days.`
- Replica liveness is tracked in `document_replicas`.
- A retired replica that reconnects is told to resync from a snapshot and
  discard local state.

**GC watermark.** Each document has a watermark: the causal-stability frontier
below which elements may have been collected. An operation referencing an
unknown id is buffered if that id is above the watermark, and **rejected with a
structured `resync-required` response** if it is at or below it. This is the one
case where a pending operation is dropped, and it is why §5's "do not drop"
rule has an exception rather than a contradiction.

`T_retire` therefore also bounds offline editing. See §9.

### Required invariants

These are the acceptance criteria for `Crdt.Core`. Write them as executable
property tests before writing the implementation.

1. **Convergence** — for any set of operations delivered in any order, with any
   duplicates, all replicas that have seen the same set produce identical text.
2. **Idempotency** — applying any operation twice equals applying it once.
3. **Commutativity** — concurrent operations produce the same result in either order.
4. **Causal readiness** — an operation is never applied before its dependencies.
5. **Intention preservation** — a character inserted between X and Y remains
   between X and Y after any concurrent remote operations.
6. **No resurrection** — a deleted element never reappears, including when a
   concurrent insert references it as parent or right origin.
7. **GC safety** — collecting causally stable tombstones does not change the
   text produced by any subsequent legal operation sequence. "Legal" means an
   operation from a non-retired replica referencing an id above the watermark;
   operations below the watermark are rejected, not silently mishandled.
8. **No interleaving** — when two replicas concurrently insert runs of
   characters at the same position, the merged result contains each run as a
   contiguous block, in one order or the other. This holds for runs typed in
   either direction (left-to-right and right-to-left).

Invariant 8 is why the algorithm is FugueMax and not RGA. RGA satisfies it only
for left-to-right runs; see §13.1.

### Testing approach

- **Property tests:** generate random operation sets, apply to N simulated
  replicas in randomized orders with random duplication, assert every invariant
  above.
- **Hand-rolled generator and shrinker.** Not FsCheck. FsCheck's C# API makes
  custom shrinking of structured operation traces awkward, and the shrinker is
  the part that determines whether a failure is debuggable. Owning it is worth
  more than the framework.
- **Deterministic simulation:** all randomness comes from a seed printed on
  failure, so any failure is reproducible by rerunning with that seed. This is
  non-negotiable — a non-reproducible CRDT bug is unfixable.
- **Shrinking:** on failure, minimize the operation trace before reporting.
- **Invariant 8 must be tested from its definition, not from the algorithm.**
  Generate two concurrent insertion runs at the same position, in both
  directions, and assert each run appears as a contiguous block. Do not assert
  a specific tree shape and do not derive the expectation from the
  implementation. The conformance harness compares two implementations written
  by the same author from the same paper, so a misreading would agree with
  itself; a definitional test is the only thing that catches that.
- **Coverage:** ≥85% **mutation score** on `Crdt.Core`, measured with
  Stryker.NET. Line coverage is not a goal and is not tracked — it is trivially
  satisfiable by tests that assert nothing interesting about a CRDT.

### Insertion runs — direction terminology

A **forward run** is characters inserted left-to-right, each after the previous
(ordinary typing). A **backward run** is characters inserted right-to-left at
the same position, each before the previous.

**Backward runs have nothing to do with right-to-left scripts.** Typing Arabic
or Hebrew appends in *logical* order and produces forward runs; RTL is a bidi
*rendering* concern handled by the browser and never by the CRDT. Backward runs
come from caret-left editing (typing, pressing Left, typing again), certain
paste implementations, and some IME composition paths. Do not conflate the two.

## 6. Persistence

Append-only operation log plus periodic snapshots.

```sql
users              (id, oidc_subject, oidc_issuer, display_name, created_at)
documents          (id, owner_id, title, created_at, updated_at, deleted_at)
document_ops       (document_id, replica_id, seq, op_type,
                    parent_replica, parent_seq, side,
                    right_origin_replica, right_origin_seq, right_origin_is_end,
                    value, server_seq, created_at)
document_snapshots (document_id, server_seq, state, version_vector, created_at)
document_members   (document_id, user_id, role, granted_at, granted_by)
document_replicas  (document_id, replica_id, user_id, last_seen_at, max_seq,
                    retired_at)
```

- `users` exists because `owner_id`, `user_id`, and `granted_by` must reference
  something. OIDC subject is unique per issuer, not globally.
- Primary key on `document_ops` is `(document_id, replica_id, seq)` — this makes
  duplicate submission a no-op at the database level, which is the cheapest
  correct place to enforce idempotency.
- A `CHECK` constraint enforces the shape of each `op_type`: inserts carry
  `parent_*`, `side`, and `value`; deletes carry none of them.
  `right_origin_*` is non-null only when `side = 'R'`, with
  `right_origin_is_end` distinguishing "end of document" from "not applicable".
- Index on `(document_id, server_seq)` for catch-up. The primary key does not
  serve that query.
- `document_replicas` backs both the per-document version vector and replica
  retirement (§5). `retired_at` is set by the background job after `T_retire`.
- Snapshot every N operations (configurable, default 500). Loading a document
  reads the latest snapshot plus operations after its `server_seq`.
- `document_ops` is partitioned by `document_id` hash. Include the migration.
  The partition key is part of the primary key, as Postgres requires.
- All writes through parameterized commands. Zero string-concatenated SQL
  anywhere in the codebase.

### server_seq

`server_seq` is a per-document sequence used for catch-up queries. It is a
delivery optimization, not a causal order — never use it to determine CRDT
semantics.

The requirement is **monotonic visibility, not gaplessness**. Gaps are fine. A
reader observing `server_seq` 101 before 100 is not: it would silently skip an
operation during catch-up.

Assign `server_seq` in a single per-document writer holding a Postgres advisory
lock keyed by `document_id`, reusing the 50 ms batching window from §8 so the
lock is taken once per batch rather than once per operation. Ops become visible
in `server_seq` order because the batch commits under the lock.

### Wire and trace encoding — normative

All 64-bit values (`Seq`, `server_seq`, and any counter that can exceed 2^53)
serialize as **decimal strings** in JSON — on the wire, in snapshots, and in
conformance traces. The TypeScript side parses them as `BigInt`.

JSON numbers are IEEE 754 doubles. Values above 2^53 do not round-trip, which
would break the byte-identical requirement in §9 silently and only after a
replica had been running long enough to matter.

Every operation and message carries a `v` protocol-version field from the first
commit. Adding one after the conformance corpus and client IndexedDB schema
exist is a migration; adding it now is a field.

### Run operations

The core algorithm operates on a single code point per element (§5). The wire
format **reserves** a run form — an insert covering `n` contiguous code points
with ids `(replica, seq), …, (replica, seq+n-1)` — so that large pastes need not
become `n` messages. Reserving it now avoids a breaking wire change later.

**The server expands runs into one row per element on ingest.** `document_ops`
stores one row per element and the primary key `(document_id, replica_id, seq)`
continues to make deduplication a plain upsert. The alternative — `seq_start` /
`seq_end` columns with range-overlap dedup — was rejected: it turns idempotency
from a unique-index property into application logic that must handle partial
overlap, which is the exact thing the schema was designed to avoid.

Expansion must **replay the placement rule** (§5) for each element in the run,
chaining each element onto the previous one. It must not assign every element in
the run the same parent and side — that would make them siblings and reintroduce
exactly the interleaving invariant 8 forbids.

## 7. Security

Treat every one of these as a hard requirement with a corresponding test.

**Authentication**
- OIDC with JWT bearer tokens. Validate issuer, audience, lifetime, and
  signature. No `ValidateIssuer = false` anywhere, including in dev config.
- SignalR connections authenticate via a **short-lived connect ticket**, not the
  OIDC JWT. The client calls `negotiate` with a normal `Authorization` header
  and receives an opaque, single-use ticket valid for ≤60 seconds; that ticket
  goes in the `access_token` query parameter.

  Browsers cannot set headers on WebSocket handshakes, so something must travel
  in the URL. A URL lands in reverse-proxy access logs, browser history, and
  `Referer` headers — none of which your own log redaction controls. A ticket
  that is single-use and expires in a minute is a bounded loss; a bearer JWT is
  not.
- The ticket and any token must never be written to logs. Configure request
  logging to redact them explicitly, and add a test that drives a connection
  using a **known sentinel value** through the real logging pipeline —
  including request logging and exception paths — asserting the sentinel
  appears in no sink. "No log line contains a token" is not testable as stated;
  this is.

**Authorization**
- Every hub method and every endpoint re-checks document membership.
- Two checks, at different costs:
  1. **Uncached, in-memory, every operation:** the operation's document id must
     match the document bound to this connection at connect time. This is a
     field comparison, costs nothing, and stops a client submitting into a
     document it never joined.
  2. **Role lookup, cached ≤5s:** the caller's role on that document, cached in
     Redis with a TTL of 5 seconds and invalidated eagerly via pub/sub on any
     membership change. Revocation must take effect within 5 seconds, proven by
     a test.

  A literal uncached role lookup per operation is a database round trip per
  keystroke per connection and cannot meet §8. Bounded staleness is the real
  requirement; 5 seconds is the bound.
- Roles: `Owner`, `Editor`, `Viewer`. Viewers receive broadcasts but any write
  operation from a viewer is rejected and logged.
- Authorization failures return 404, not 403, for documents the caller cannot
  see — do not leak document existence. A viewer attempting a write gets 403:
  they can already see the document, so there is nothing to conceal.

**Input validation**
- Reject operations whose `ReplicaId` does not match the one bound to the
  authenticated connection. A client must not be able to forge operations
  attributed to another replica.
- Reject operations whose `Seq` is not the next dense value for that replica.
  Density is a correctness property of the version vector (§5), not a
  convention.
- Caps: single element value = exactly 1 code point (≤4 bytes UTF-8); run op
  ≤ 256 code points; batch ≤ 256 ops; message ≤ 64 KB; document ≤ 5 MB of live
  text; ≤ 50 concurrent replicas per document; pending-set bound per connection.
  All configurable, all enforced server-side, all with a test proving the limit
  rejects.
- Validate UTF-8 and reject lone surrogates. **Normalize nothing** — the CRDT
  operates on code points and normalization would break element identity.

**Abuse resistance**
- Per-user and per-connection rate limits on operation submission, backed by
  Redis so limits hold across instances. Return a structured throttle response,
  do not silently drop. Rate limits are expressed in code points per interval,
  not messages, so run ops cannot bypass them.
- Connection limits per user. Reject new connections past the cap.
- A malformed or oversized message closes the connection after logging.

**Client**
- Content Security Policy with no `unsafe-inline`. HSTS. `X-Content-Type-Options`.
- The editor renders text into a DOM text node — never `innerHTML`, never
  `dangerouslySetInnerHTML`. Add a test that a document containing
  `<script>alert(1)</script>` renders as literal text.

**Secrets**
- Nothing in `appsettings.json` but non-secret defaults. Local secrets via
  `dotnet user-secrets`, deployed secrets via environment variables. A CI step
  fails the build on committed secrets (gitleaks).

## 8. Scalability

- App servers hold **no authoritative state**. Any in-memory per-document cache
  (version vector, recent element ids, membership) must be reconstructible from
  Postgres and must not be required for correctness after a failover. Any client
  may connect to any instance; no sticky sessions. Redis backplane fans out
  SignalR messages. Proven by a test that kills an instance mid-session and
  asserts clients still converge.

  "Stateless" was the original wording and it is not achievable: validating an
  operation without loading the document requires a cached version vector.
  Reconstructible is the property that actually matters.
- Hot path (receive op → validate → persist → broadcast) must not load full
  document state. Validate against the version vector and the referenced parent
  and right origin only.
- Batch operation persistence: buffer for up to 50 ms or 100 ops, whichever
  comes first, then write in one round trip under the per-document advisory lock
  (§6).
- Backpressure: bounded per-connection outbound channel. If a client cannot keep
  up, drop it to catch-up-via-snapshot rather than growing the buffer unbounded.
- Snapshot compaction and tombstone GC run in a background service, jittered and
  guarded by an advisory lock so multiple instances do not duplicate work.

**Performance targets.** Measured by a load test in `tests/`, not asserted by
vibes. Each target names where it is measured, because "end-to-end" without a
measurement point is not a falsifiable claim.

| Target | Measured |
|---|---|
| p99 < 25 ms, server-side `receive → broadcast enqueue` | 20 concurrent editors on one document |
| p99 < 150 ms, client keystroke → remote client render | 20 concurrent editors, loopback network |
| 1,000 concurrent connections per instance at < 2 GB RSS | connections spread over 100 documents, 10 each |
| Document load < 500 ms, **server-side**, 100k live characters + 500k tombstones | cold cache, from snapshot + tail |

The document-load target is a **server-side** number. The browser replica has no
equivalent target in this phase; if one is wanted later it constrains the
snapshot format and must be specified separately. Note also that 500k
accumulated tombstones implies GC is not keeping up — this is a stress target,
not a steady state.

## 9. Client

- React 19, TypeScript strict mode.
- Ships its own FugueMax replica implementing the identical algorithm. Local
  edits apply optimistically to the local replica and render immediately; remote
  ops merge in. There is no server round trip in the typing path.
- IndexedDB persists the local replica and an outbox of unsent operations, so a
  full offline session survives a page reload and syncs on reconnect.
- Reconnect with exponential backoff and jitter. On reconnect, send the local
  version vector and receive only the missing operations.
- Presence (remote cursors) is ephemeral and never persisted.
- **Cursors are anchored to `ElementId`, not to integer indices**, with a
  left/right bias for the gap between elements. An integer index is invalidated
  by any concurrent edit earlier in the document, which makes remote cursors
  jump and local selections drift.
- **Code point boundaries.** The CRDT operates on code points; the DOM,
  `Selection`, and JavaScript strings operate on UTF-16 code units. The client
  owns an explicit translation layer between the two, and it is unit-tested with
  astral-plane characters. Deleting an emoji ZWJ sequence removes one code
  point, not the whole visible glyph — this is accepted behaviour, not a bug.

### Offline window

Offline editing is supported for up to `T_retire` (7 days, §5). Beyond that the
replica is retired server-side and its local state is discarded on reconnect.

The client **records the last successful sync time and surfaces the remaining
window in the UI.** It must warn as the window nears expiry and must not
silently accept edits that will be discarded. Accepting an hour of offline work
and then throwing it away without warning is a data-loss bug, not a limitation.

### Conformance testing

`tests/Conformance/` holds shared JSON operation traces. A test runner feeds each
trace through the C# implementation and the TypeScript implementation and
asserts byte-identical output text and identical version vectors. Any divergence
fails the build.

The corpus is built in this order:

1. **Transcribed fixed traces first.** The worked examples from the Fugue paper,
   including the backward-run case, transcribed by hand before any generated
   trace exists. Also the canonical interleaving test (two replicas concurrently
   insert `----------` and `##########` between `<` and `>`; the result must be
   `<----------##########>` or `<##########---------->`, never interleaved) and
   a trace pinning `ReplicaId` byte ordering (§5).
2. **Generated traces**, from the property-test generator, so the corpus grows
   with the fuzzer.

Fixed traces come first because a generated corpus only proves the two
implementations agree with each other, which they would even if both were wrong.

## 10. Observability

- Structured logs, no PII, no document content, no tokens or tickets.
  Correlation id per connection, propagated to every log line and span.
- Metrics: ops received/applied/rejected, pending-buffer depth, propagation
  latency histogram, active connections, GC reclaimed elements, snapshot age,
  retired replicas, resync-required responses.
- Traces spanning receive → validate → persist → broadcast.
- `/health/live` and `/health/ready`; readiness checks Postgres and Redis.

## 11. Build phases

Do not start a phase before the previous one is committed, green in CI, and
reviewed. At the end of each phase, stop and report.

| Phase | Deliverable | Done when |
|---|---|---|
| 0 | Repo, solution, Docker Compose, CI, `AGENTS.md` | See below — the original "empty suites" gate had no signal |
| 1 | `Crdt.Core` **and the TypeScript core**, property tests, conformance runner | All 8 invariants pass 10,000 randomized cases; ≥85% mutation score; transcribed fixed traces match across both implementations |
| 2 | Postgres schema, op log, snapshots | Integration tests via Testcontainers; crash-during-write test passes |
| 3 | SignalR hub, auth, causal delivery | Two real clients converge; auth tests pass |
| 4 | React client wrapping the Phase 1 TS core | Offline edit for 5 min, reconnect, converge |
| 5 | Conformance corpus at scale | 1,000 generated traces match across both implementations; runner fuzzes in CI |
| 6 | Security hardening | Every requirement in §7 has a passing test |
| 7 | Scale + observability | Load test hits the §8 targets; dashboards exist |

**Phase 0 done when:** CI is green on a clean clone, and that run has
(a) built every project with warnings-as-errors, (b) run at least one real
assertion in each test project, (c) proven `Crdt.Core` references nothing
outside the BCL, (d) started Postgres and Redis via Testcontainers and connected
to both, (e) brought the Compose stack up and received 200 from `/health/live`,
and (f) run a secret scan.

An empty test suite passing proves nothing, and `vitest` exits non-zero with no
test files unless told otherwise.

**Phase 1 builds both cores together.** The TypeScript replica was originally
written in Phase 4 and first compared against C# in Phase 5, which meant
building a UI on top of an implementation never checked for divergence. Since
§9 makes byte-identical behaviour a build-breaking requirement, the two cores
are developed against a shared corpus from the start. Phase 4 wraps the Phase 1
core in React rather than writing a second one.

## 12. Working agreement

- **Plan before code.** At the start of each phase, produce a task breakdown and
  wait for approval. Do not begin implementing from this document directly.
- **Tests first for `Crdt.Core`.** The invariants in §5 are written and failing
  before the implementation exists.
- **Small commits.** One logical change each, conventional commit messages,
  every commit leaves the build green.
- **Ask before adding a dependency.** State what it does and why the BCL is
  insufficient. The answer for anything CRDT-shaped is no.
- **Use new language features where they genuinely simplify, not for their own
  sake.** When a C# 14 feature is used because it makes the code clearer, say so
  in the commit message.
- **No stubs presented as done.** If something is unimplemented it throws
  `NotImplementedException` and is listed in the phase report. Never a silent
  no-op, never a hardcoded return that makes a test pass.
- **Report honestly.** If an invariant fails and you cannot fix it, say so and
  show the minimized failing trace. Do not weaken the test to make it green — if
  you believe a test is wrong, argue for changing this spec first.
- **Say when you are unsure.** Distributed systems bugs hide in the cases where
  the implementer felt "this probably works." Flag those explicitly.

## 13. Decision log

Amendments to the original specification, with reasons. These exist so a future
reader does not "fix" a deliberate decision.

### 13.1 RGA replaced by FugueMax

The original §5 specified RGA and the original invariant list had no
non-interleaving requirement. Invariant 8 was added, and RGA cannot satisfy it.

Kleppmann, Gomes, Mulligan and Beresford (PaPoC 2019) show RGA permits a lesser
interleaving anomaly when insertions are not sequential. Concretely: replica A
inserts `b` at a position, then prepends `a` at the same position (a backward
run); replica B concurrently inserts `x` there. All three share the same left
origin, so they are siblings ordered by descending timestamp, giving `axb` — B's
character splits A's run.

RGA is safe for forward runs, because each character's origin is the previous
character of the same run, forming a chain no concurrent sibling can enter. It
is unsafe for backward runs, where every character shares one anchor.

FugueMax (Weidner & Kleppmann 2023) resolves both directions by giving each
element a `RightOrigin` in addition to its parent, and ordering right-side
siblings by reverse right-origin. The change was made at zero lines of
implementation, because it propagates into the element type, wire format, trace
corpus, and client replica; later it would be a rewrite rather than a paragraph.

### 13.2 No Lamport clock

An earlier amendment required `Counter` to become a Lamport clock, because RGA's
sibling comparator orders by descending timestamp and a per-replica counter is
not causally consistent — a replica could place a character before one it had
already observed. A second amendment then split identity from ordering
(`Seq` dense for version vectors, `Lamport` for ordering), because Lamport clocks
are sparse and a sparse counter makes a compact version vector unsound.

**Adopting FugueMax obsoleted both.** FugueMax's sibling comparator uses tree
position, `RightOrigin`, and `ReplicaId` — it never consults a timestamp. With
no ordering role, there is nothing for a Lamport clock to order, and `Seq`
returns to being a single dense per-replica counter, which is exactly what a
compact version vector needs.

The `ReplicaId` byte-ordering requirement (§5) was *strengthened* by the same
change rather than obsoleted: under RGA the id comparison was a rare tie-break
reached only when timestamps collided, whereas under FugueMax it is the primary
ordering for left-side siblings and the sole tie-break for right-side siblings.
A C#/TypeScript disagreement there reorders user text directly.

### 13.3 .NET 9 replaced by .NET 10

The original §3 pinned .NET 9, which reached end of support on 2026-05-12. It is
absent from Microsoft's package feed and cannot be installed or verified. .NET 10
is LTS through November 2028. C# 13 becomes C# 14.

### 13.4 Open items

- The FugueMax placement and sibling-ordering rules in §5 were reconstructed
  from the paper's abstract, secondary sources, and the authors' reference
  implementation (`mweidner037/fugue`, `fugue-max-simple`), because the paper
  PDF is not reachable from the build environment. Before Phase 1 implementation
  begins, they must be checked against the paper itself, along with the worked
  examples required for the conformance corpus (§9).
