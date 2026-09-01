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

Implement **FugueMax** for a sequence of Unicode code points, as defined in
Weidner & Kleppmann, "The Art of the Fugue: Minimizing Interleaving in
Collaborative Text Editing", *IEEE TPDS* 36(11), 2025 — Algorithm 1 plus
Definition 6. Both papers are committed under `docs/references/`; the TPDS
paper is normative, and the arXiv v1 extended version supplies the appendices.

Section references below are to the TPDS paper unless marked arXiv.

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

A single root sentinel node exists implicitly in every replica. Its id is a
**distinct `null` value, not a representable `ElementId`** — the paper's id type
is `(RID x N) union {null}` (Algorithm 1, line 3) and the root is
`(null, ⊥, null, null)` (line 10). Do not encode it as `(ReplicaId.Empty, 0)`:
with `Seq` starting at 0 that is a legal element id and could collide with a
real one. The root is always tombstoned and is never transmitted.

**Traversal.** In-order: for each node, emit its left children in sibling order,
then the node's own value if not tombstoned, then its right children in sibling
order.

### Identifiers

Each node carries an immutable `ElementId` of `(ReplicaId: ReplicaId, Seq: ulong)`.

`Seq` is a **dense, per-replica, monotonic counter starting at 0** (Algorithm 1,
lines 11 and 22: the counter is initially 0, and each insert assigns then
increments). Dense means gapless: a replica's operations are numbered 0, 1, 2, …
with no holes. This is what makes a compact version vector `{ReplicaId → maxSeq}`
sound — `maxSeq = n` implies every operation 0..n from that replica has been
observed.

There is **no Lamport clock and no ordering timestamp**. FugueMax orders siblings
by tree position and `RightOrigin`, falling back to the element id; none of that
is a causal clock. Do not add one "for safety" — it would be a field nothing
reads. See §13.2.

`Seq` does participate in sibling ordering, as the second component of the
`ElementId` tie-break below. That is a lexicographic comparison of an identity,
not a happens-before relation, and nothing about it needs to respect causality.

### ReplicaId comparison — normative

`ReplicaId` is a 128-bit value. It is compared as the **lexicographic ordering of
its 16 bytes in RFC 4122 big-endian order**, unsigned, most significant byte
first.

The paper says only that "the exact construction of IDs and their order is not
important" (§4), because it needs nothing beyond *some* total order. We need
more: §9 requires the C# and TypeScript implementations to agree byte for byte,
and id order is what breaks sibling ties, so a disagreement here reorders user
text. Fixing the byte order is therefore a project requirement, not a paper one.

- Do **not** delegate to `System.Guid.CompareTo`. Not because it is wrong — on
  .NET 10 it agrees with unsigned big-endian order in every case tested,
  including the signed boundaries where an earlier draft of this section
  wrongly claimed it diverged (§13.8) — but because nothing specifies that it
  must, TypeScript has no equivalent to agree with, and a rule that reorders
  user text should not rest on a framework comparison happening to match.
- Do **not** compare string forms, and do not rely on Postgres `uuid` collation,
  which is a third ordering again.
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

First compute both origins, unconditionally and in this order (Algorithm 1,
lines 23–24):

- `leftOrigin` — the node of the visible value at index `i-1`, or the root if
  `i = 0`.
- `rightOrigin` — **the next node after `leftOrigin` in the traversal that
  includes tombstones**, or `null` if none exists (arXiv §5.1 calls this
  `end`). This is computed once, before the branch below, not per branch.

Then place the node (Algorithm 1, lines 25–28):

- If `leftOrigin` has **no right children**, the new node becomes a **right
  child of `leftOrigin`** (`Parent = leftOrigin.Id`, `Side = 'R'`) and is
  **tagged with `RightOrigin`** (Definition 6, change 1).
- Otherwise, the new node becomes a **left child of `rightOrigin`**
  (`Parent = rightOrigin.Id`, `Side = 'L'`), carrying **no** `RightOrigin`.

In the first branch `leftOrigin` has no right children and its left children are
traversed before it, so the next node in traversal order is necessarily not a
descendant of `leftOrigin`; the reference implementation's `nextNonDescendant`
is an equivalent restatement, not a different rule.

**Sibling ordering — normative.**

Per Definition 6 and Algorithm 1 lines 32–37:

- **Right children** of a node are ordered by their `RightOrigin` in **reverse
  list order** — node `X` precedes sibling `Y` when `X.RightOrigin` comes *later*
  in the current list order than `Y.RightOrigin` — with ties broken by
  **`ElementId` ascending**.
- **Left children** of a node are ordered by **`ElementId` ascending**.

`ElementId` compares lexicographically as the pair `(ReplicaId, Seq)`: `ReplicaId`
first by the byte rule above, then `Seq` numerically. The paper's rule is
"lexicographic order of their IDs", and an id is the pair — **not `ReplicaId`
alone**.

Comparing two `RightOrigin` values is a comparison of tree positions, not of
ids: walk both nodes up to their common parent, then compare — a left-side
ancestor precedes a right-side ancestor, and same-side siblings compare by their
index in the parent's child list.

Two nodes from the same replica cannot become same-parent, same-side siblings:
a replica never creates a node where it already has a same-side sibling (§4), so
after its first insert at a position the placement rule sends the next one down
the other branch. Comparing `ReplicaId` alone would therefore give the same
answer in every reachable state — which is why the authors' reference
implementation does exactly that. Compare the full `ElementId` anyway: it is what
the paper specifies, it costs nothing, and it does not silently depend on that
invariant holding. Assert the invariant separately.

### Sequence numbers cover deletes too

The paper increments its counter only on insert, because it has no operation log.
This project does: §6 keys `document_ops` on `(document_id, replica_id, seq)` for
both operation types, and §7 rejects any operation whose `Seq` is not the next
dense value for that replica.

So **every operation consumes a `Seq`**, inserts and deletes alike. An insert's
id both names the operation and identifies the element it creates; a delete's id
names the operation, and a separate `Target` field names the element being
tombstoned.

The consequence is that element ids are not contiguous per replica — a replica
that deletes between two inserts leaves a gap in the ids it has assigned to
elements. Nothing requires element-id contiguity; ids only need to be unique and
totally ordered. What must stay dense is the **version vector**, which counts
operations, and it does.

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

**Causal stability is necessary but not sufficient.** It guarantees every replica
has *observed* the delete; it does not guarantee nobody will *reference* the
element again. A tombstone remains a valid anchor: `rightOrigin` is the next node
in the traversal *including tombstones*, so a fully up-to-date replica can name a
long-dead element in a brand-new insert. Collecting on stability alone strands
that insert as an undeliverable dependency — found by invariant 7, not by
reading.

A tombstone may be collected only when all of these hold:

1. It is causally stable.
2. It is a leaf — no left or right children.
3. No node references it as `Parent` or `RightOrigin`.
4. **It is unreachable as a future right origin.** A new insert names as its
   right origin the first node after a *visible* left origin, so only the
   leading tombstone of a run of consecutive tombstones can be named. Retain
   that leader; the tombstones behind it can never be named again. Collecting
   the leader would simply promote the next one into reachable position.

Rule 4 collapses a run of tombstones to one, which is where the reclamation
actually is: a deleted paragraph becomes a single anchor. Correctness first — a
lower reclamation rate is acceptable, a broken tree is not.

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
8. **Maximal non-interleaving** — the three conditions of Definition 4, which
   FugueMax satisfies (Theorem 9):

   a. **Forward.** If A is the left origin of B, and B appears earlier in the
      list than every other element with left origin A, then A and B are
      consecutive. By Lemma 3 this implies the run-level property: two runs
      typed **left to right** concurrently at the same position never
      interleave — each appears as a contiguous block. Unconditional.
   b. **Backward, with exceptions.** The mirror statement over right origins,
      *except* where the Lemma 5 exception applies (see the scope note below).
   c. **Same origins.** Two elements sharing both a left origin and a right
      origin are ordered by ascending `ElementId`.

**Scope — read this before writing the test.** Invariant 8 was originally
written as "runs never interleave, in either direction". That is not
achievable, and not just by FugueMax: arXiv Theorem 5 proves **no algorithm
satisfying the strong list specification can satisfy both forward and backward
non-interleaving.** Its counterexample (arXiv Appendix B, Fig. 7) starts from
`a`; two replicas concurrently insert `b` and `c` after it; two replicas in
state `ac` insert `e` and `g`; then `d` between `b` and `e` and `f` between `b`
and `g`. Forward non-interleaving forces `a < b < (d,f) < (e,g) < c`, whose four
permitted orders all interleave `de` with `fg`, while backward
non-interleaving demands `defg` or `fgde`. The two cannot both hold.

So the honest statement of what this project guarantees is:

- Forward runs never interleave. **Unconditional, any number of replicas.**
- Backward runs never interleave **unless** the Lemma 5 exception applies: B is
  A's right origin and A is the last element with right origin B, but A and B
  have different left origins and some C satisfies `A.leftOrigin < C < B` with C
  not a descendant of `A.leftOrigin` in the left-origin tree.
- Where that exception bites, FugueMax still produces **the** correct order:
  Theorem 10 shows maximal non-interleaving uniquely determines the list order,
  so any maximally non-interleaving algorithm is semantically equivalent to
  FugueMax. There is no better answer to reach for.

An earlier draft of this section claimed the exception "cannot arise with two
concurrent replicas". **That was wrong, and measurement disproved it** — see
§13.6. There is no replica-count threshold beyond which backward runs are safe:
the exception depends on the shape of the execution, not on how many replicas
insert at a position. Backward run contiguity is therefore never asserted, at
any concurrency. Definition 4 is what holds, and Definition 4 is what is tested.

Invariant 8 is why the algorithm is FugueMax and not RGA: RGA satisfies 8(a)
but not 8(b) at all. See §13.1.

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
  Do not assert a specific tree shape and do not derive the expectation from the
  implementation. The conformance harness compares two implementations written
  by the same author from the same paper, so a misreading would agree with
  itself; a definitional test is the only thing that catches that.

  Assert it at two levels, and keep them distinct:

  1. **Definition 4, directly** — conditions (a), (b) and (c) above, evaluated
     against the resulting list order for any generated execution, with the
     Lemma 5 exception encoded as an exception rather than assumed away. This is
     exact and applies to every execution the generator can produce.
  2. **Run-level contiguity** — the property users actually care about.
     **Forward runs: asserted unconditionally**, at any number of replicas, on
     the strength of Lemma 3. **Backward runs: observed, never asserted.** The
     papers give no backward analogue of Lemma 3 at any replica count, and
     Theorem 5 is the reason there cannot be one.

  The generator must still produce backward runs, and must produce the layered
  shape of arXiv Fig. 7 — two rounds of concurrency separated by a partial
  delivery, with the interleaving pairs typed right to left across both rounds.
  That shape is the only one in which the Lemma 5 exception arises; a single
  round of concurrent runs cannot produce it however many replicas take part.

- **Backward contiguity is measured, and the measurement is load-bearing.**
  Evaluate it on every backward run: compute it, never fail on violation, count
  how often it holds, and report the rate. Violations are the algorithm behaving
  correctly.

  Read the number, and read it as a check on the generator as much as on the
  algorithm. A rate at or near 100% means the generator is not reaching the
  Lemma 5 shape and the corpus is weaker than it looks — that is how the false
  boundary above survived its first measurement. The rate observed once the
  Fig. 7 shape was generated is roughly 89%.
- **Coverage:** ≥85% **mutation score** on `Crdt.Core`, measured with
  Stryker.NET. Line coverage is not a goal and is not tracked — it is trivially
  satisfiable by tests that assert nothing interesting about a CRDT. Enforced in
  CI; the runner split that makes it possible is recorded in §13.7.

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

### Snapshot encoding

`document_snapshots.state` holds **the normalised JSON of §9** — the same format
the conformance runners emit.

The reason is not tidiness. Sharing the format makes a snapshot directly
comparable against a conformance run, which extends the cross-implementation
check into persistence at no cost: if the C# server and the TypeScript client
ever disagree about how a document serialises, a snapshot and a conformance
artefact stop matching, and that is a build failure rather than a support
ticket.

JSON is larger and slower than a binary encoding, and §8 asks a document of
100k live characters and 500k tombstones to load in under 500 ms server-side.
**Measure it, do not assume it.** Phase 2 carries a test that builds a
100k-element document, snapshots it, and *reports* serialised size and load time
as metrics — no threshold, no assertion. The point is to know the number before
the format is baked into the client's IndexedDB schema, not to discover it in
Phase 7. If JSON turns out to be far off the target, that is a Phase 2 fact and
the decision to change format is cheap; after Phase 4 it is not.

### Serialisation lives in Editor.Infrastructure

`Crdt.Core` references nothing but the BCL (§4), so the mapping from its types to
database rows and to the wire lives in `Editor.Infrastructure`.

That mapping is now a **second implementation of the same encoding**, alongside
the TypeScript serialiser, and the two must agree for the same reason the two
algorithm cores must. §9's corpus therefore includes at least one trace that
round-trips through the **serialised form** on both sides rather than only
through the algorithm, so an encoding divergence fails the build exactly as an
algorithm divergence does. A shared format that nothing checks is a shared format
that drifts.
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

`tests/Conformance/traces/` holds shared JSON traces. Both implementations replay
every trace and write a normalised result file; a separate comparison step
asserts the two files are byte-identical. Any divergence fails the build.

**Two runners, one corpus.** The C# runner is an xUnit project; the TypeScript
runner is a vitest suite. Neither invokes the other — coupling the .NET test run
to a Node toolchain buys nothing and makes each side harder to run alone. The
comparison is a third step over two artefacts.

Because the comparison is byte-for-byte, **both the trace schema and the result
format are specified here, in full, before either runner is written.** Neither
may be inferred from whichever implementation happens to exist first.

#### Trace schema (v1)

Traces are **scripted executions in user-level terms** — insert at an index,
delete at an index, deliver — never raw CRDT nodes. A trace that named parents,
sides and origins would encode one implementation's tree shape and could bake in
a misreading; expressing intent instead makes each implementation derive the
structure itself, which is the thing under test.

```jsonc
{
  "v": 1,
  "name": "rga-backward-interleaving",     // kebab-case, matches the filename stem
  "description": "prose, free-form",
  "replicas": [                            // fixed ids so ordering is deterministic
    { "index": 0, "id": "00000000-0000-0000-0000-000000000001" },
    { "index": 1, "id": "00000000-0000-0000-0000-000000000002" }
  ],
  "ops": [
    { "op": "insert",  "replica": 0, "index": 0, "value": "b" },
    { "op": "insert",  "replica": 0, "index": 0, "value": "a" },
    { "op": "insert",  "replica": 1, "index": 0, "value": "x" },
    { "op": "delete",  "replica": 0, "index": 1 },
    { "op": "deliver", "from": 0, "to": 1 },   // flush 0's outbox into 1
    { "op": "sync" }                           // deliver everywhere until quiescent
  ],
  "expected": {
    "oneOf": ["abx", "xab"],
    "forbidden": ["axb"],
    "versionVector": { "00000000-0000-0000-0000-000000000001": "2" },
    "rationale": "FugueMax must not backward-interleave two replicas; axb is the RGA anomaly (arXiv A.1.8)."
  }
}
```

`expected` carries **at least one** of:

- `text` — the exact output, for traces where a paper fixes the answer.
- `oneOf` — the set of permitted outputs, where the papers constrain the result
  without determining it.
- `forbidden` — outputs that would demonstrate a violation.

The runner asserts, for each implementation: output equals `text` if present;
output is in `oneOf` if present; output is not in `forbidden` if present. The
comparison step additionally asserts **both implementations chose the same
member of `oneOf`** — agreeing on a permitted answer is a separate requirement
from each being permitted.

`rationale` is **required on every trace**: one line saying what the trace proves
and the paper section it comes from. A trace that fails must not be fixable by
editing its expectation without the editor seeing, in the diff, that they are
contradicting a cited paper.

`versionVector` is optional and maps replica id to a decimal-string `Seq` high
water mark (§6).

#### Normalised result format (v1)

Each runner writes exactly this, and the comparison is `diff` over the bytes:

```jsonc
{
  "v": 1,
  "implementation": "csharp",              // or "typescript"; EXCLUDED from comparison
  "results": [
    {
      "name": "rga-backward-interleaving",
      "text": "abx",                        // final text after the last op
      "replicaTexts": {                     // every replica's final text
        "00000000-0000-0000-0000-000000000001": "abx",
        "00000000-0000-0000-0000-000000000002": "abx"
      },
      "versionVector": { "00000000-0000-0000-0000-000000000001": "2" }
    }
  ]
}
```

`replicaTexts` is present so convergence is visible in the artefact itself rather
than asserted only inside a runner: a trace where replicas disagree is a failure
you can see in the diff.

Serialisation is pinned, because "byte-identical" is otherwise not well defined
across two languages:

- UTF-8, LF line endings, one trailing newline.
- Two-space indentation, one key or array element per line.
- Object keys sorted ascending by Unicode **code point**. Not by UTF-16 code
  unit — the two differ above the BMP, and C# and JavaScript disagree by default.
- `results` sorted by `name`, ordinal.
- Replica ids as lowercase canonical hyphenated UUIDs.
- Non-ASCII characters emitted **literally**, never as `\uXXXX`. C# escapes
  non-ASCII by default and JavaScript does not; both must be configured to emit
  literal UTF-8.
- Only `"`, `\` and the C0 controls are escaped, using the shortest form
  JSON allows (`\n`, `\t`, `\r`, `\b`, `\f`, and `\u00XX` otherwise). `/` is
  left literal: escaping it is legal JSON but never required, and the two
  languages disagree by default.
- The `implementation` field is excluded from the comparison — it is the one
  field that is legitimately different.

#### Corpus order

1. **Transcribed fixed traces first**, by hand, from the papers, before any
   generated trace exists:
   - arXiv A.1 forward canonical — `a` then `b` concurrent with `x`; `axb` is
     the forward anomaly.
   - arXiv A.1 backward canonical — `b` then prepend `a`, concurrent with `x`;
     `axb` is the backward anomaly, and is what RGA produces (A.1.8).
   - arXiv A.1.9 multi-replica backward.
   - TPDS Fig. 6 — three replicas, forced result `AXBC`.
   - arXiv Thm 5 / Appendix B counterexample — four permitted orders, all of
     which interleave; the case where interleaving is unavoidable.
   - The Kleppmann interleaving test: two replicas concurrently insert
     `----------` and `##########` between `<` and `>`; permitted results are
     `<----------##########>` and `<##########---------->`.
   - A trace pinning `ReplicaId` byte ordering (§5).
   - A trace exercising the **serialised** round trip: operations encoded to the
     wire form, decoded, and replayed, on both implementations. This checks the
     encoding rather than the algorithm, and is what keeps the
     `Editor.Infrastructure` mapping in step with the TypeScript serialiser (§6).
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
position, `RightOrigin`, and the element id — it never consults a timestamp.
With no ordering role, there is nothing for a Lamport clock to order, and `Seq`
returns to being a single dense per-replica counter, which is exactly what a
compact version vector needs.

**The claim this log originally made was wrong, and it is left on the record
rather than quietly replaced.** It read:

> FugueMax's sibling comparator never consults `Seq`; ordering comes from tree
> position, `RightOrigin`, and `ReplicaId` alone. […] `Seq` participates in
> identity and in version vectors. It must never be used to order siblings.

`Seq` is in fact the second component of the sibling tie-break, which is
lexicographic on the pair `(ReplicaId, Seq)` (Definition 6; Algorithm 1, lines
33 and 36). The corrected claim is narrower: nothing in the comparator is a
*causal clock*. It compares identities, and nothing requires it to respect
happens-before — which is precisely why a dense counter suffices where RGA
needed a Lamport timestamp.

Why it survived is the part worth keeping visible. The claim was written from
the authors' reference implementation, which compares only the replica component
of an id — and that implementation is *correct*, because a replica can never
create two same-side siblings of one parent, so the components below `ReplicaId`
are never reached. Every observable behaviour agreed with the false claim.
Reading the paper to check it would have found the sentence "breaking ties using
the lexicographic order of their IDs" and, having already concluded ties break
on `ReplicaId`, read straight past it. A spot-check confirms what it sets out to
confirm; only re-deriving the rule from Definition 6 without the previous answer
in hand surfaced it. That is the reason §5 is re-derived rather than reviewed
whenever a source changes.

The `ReplicaId` byte-ordering requirement (§5) was *strengthened* by the same
change rather than obsoleted: under RGA the id comparison was a rare tie-break
reached only when timestamps collided, whereas under FugueMax it is the primary
ordering for left-side siblings and the sole tie-break for right-side siblings.
A C#/TypeScript disagreement there reorders user text directly.

### 13.3 .NET 9 replaced by .NET 10

The original §3 pinned .NET 9, which reached end of support on 2026-05-12. It is
absent from Microsoft's package feed and cannot be installed or verified. .NET 10
is LTS through November 2028. C# 13 becomes C# 14.

### 13.4 Verification of §5 against the papers

§5 was originally reconstructed from secondary sources and the authors'
reference implementation, because no copy of the paper was reachable from the
build environment. Both papers are now committed under `docs/references/`, and
§5 has been **re-derived from Algorithm 1 and Definition 6 directly** rather
than spot-checked. Every reconstructed rule resolves below. No rule is left
unverified.

| # | Rule | Resolution | Reference |
|---|---|---|---|
| 1 | Tree of `(id, value, parent, side)` nodes | CONFIRMED | Alg 1, line 7 |
| 2 | In-order traversal; tombstones skipped but descendants still traversed | CONFIRMED | Alg 1, lines 14–20 |
| 3 | No right children → right child of `leftOrigin`; else left child of `rightOrigin` | CONFIRMED | Alg 1, lines 25–28 |
| 4 | `rightOrigin` = next node after `leftOrigin` in the traversal *including tombstones* | **CORRECTED** — computed once, unconditionally, before the branch. §5 had described it per branch, using "next non-descendant" in the right-child case. Equivalent there, but a restatement rather than the rule. | Alg 1, line 24 |
| 5 | `RightOrigin` carried on right children only | CONFIRMED | Def 6, change 1 |
| 6 | `RightOrigin` null means end of document | CONFIRMED — the paper's `end` symbol | arXiv §5.1 |
| 7 | Right siblings ordered by reverse *list order* of `RightOrigin` | CONFIRMED — `≻` is explicitly the existing list order, not an id comparison | Def 6, change 2 |
| 8 | Right-sibling tie-break | **CORRECTED** — the tie-break is the full `ElementId` `(ReplicaId, Seq)`, not `ReplicaId` alone | Def 6; Alg 1, line 33 |
| 9 | Left siblings ordered by `ReplicaId` ascending | **CORRECTED** — ordered by full `ElementId` ascending | Alg 1, line 36 |
| 10 | Root sentinel id | **CORRECTED** — the root's id is a distinct `null`, not `(ReplicaId.Empty, 0)`. With `Seq` from 0 the latter is a legal element id and could collide. | Alg 1, lines 3 and 10 |
| 11 | `Seq` starts at 1 | **CORRECTED** — the counter is initially 0 and is assigned before incrementing, so the first id is `Seq = 0` | Alg 1, lines 11, 22 |
| 12 | "`Seq` must never order siblings" | **CORRECTED** — false. `Seq` is the second component of the id tie-break. The surviving claim is narrower: no *Lamport clock* is needed, because the tie-break compares identities rather than causal clocks. See §13.2. | Def 6 |
| 13 | Two causal dependencies per insert (`Parent`, `RightOrigin`) | CONFIRMED | Alg 1, line 29; Def 6 |
| 14 | Deletes buffer until their target exists | CONFIRMED in substance — the paper assumes causal broadcast; our pending set is how that assumption is discharged over an untrusted network | Alg 1, lines 39–44 |
| 15 | A replica cannot create two same-side siblings of one parent | CONFIRMED | §4 |
| 16 | `ReplicaId` byte ordering is normative | CONFIRMED as compatible, but it is **our** requirement, not the paper's: the paper says id construction and order "is not important". §9 needs it for cross-implementation determinism. | §4 |
| 17 | Invariant 8 holds in both directions | **CORRECTED** — unachievable by any algorithm. Rewritten as maximal non-interleaving with an explicit scope note. | arXiv Thm 5; Def 4; Thm 9 |

### 13.6 The replica-count boundary for backward runs was wrong

§5 claimed the Lemma 5 exception "cannot arise with two concurrent replicas",
inferred from the exception's preconditions and labelled as an inference rather
than a theorem. Observational mode was added to measure it. It is false.

The first measurement reported backward contiguity holding in 6581 of 6581
applicable cases — 100.00%. That looked like confirmation that the boundary was
merely too generous. It was not: the generator only produced a single round of
concurrency, and the Lemma 5 exception cannot arise in that shape at all. The
measurement was of the generator, not the algorithm.

Adding the arXiv Fig. 7 shape — two rounds separated by a partial delivery, with
the interleaving pairs typed right to left across both rounds — immediately
produced a violated backward run at **concurrency two**, inside a four-replica
execution. Definition 4 held throughout, so the algorithm was right and the
boundary was wrong.

Two things follow. Backward contiguity is now never asserted at any concurrency,
because the exception depends on the shape of the execution rather than on a
count of replicas. And a near-100% hold rate is read as evidence that the
generator is missing a shape, not as evidence that a property holds.

### 13.7 The mutation gate, and what it cost to get one

Stryker.NET 4.16.0 — the current release — does not support
Microsoft.Testing.Platform (stryker-net#3094), which .NET 10 requires and which
xunit.v3 uses. The failure is quiet in two stages, and both are worth knowing.

Pointed at the test projects as they were, Stryker found **zero** tests, reported
nothing, and **exited 0**. A passing gate that measured nothing.

Giving `Crdt.Core.Tests` a VSTest adapter alongside xunit.v3's own runner
restored discovery — 12 tests, 321 mutants created, 227 tested. The score was
**0.00%**: every tested mutant reported as Survived, none killed, after Stryker
logged that coverage capture had failed.

That score is not real. The same suite kills those mutations when they are
injected by hand:

| Injected mutation | Result |
|---|---|
| Reverse right-sibling ordering (`byOrigin > 0` → `< 0`) | 1 invariant fails |
| Left siblings descending instead of ascending | 1 invariant fails |
| Ignore the right-origin tie-break condition | 6 invariants fail |

So the suite kills load-bearing mutations and Stryker is not observing it.

**Resolved by migrating `Crdt.Core.Tests` to xunit v2 on VSTest**, which Stryker
can drive. That means two test stacks in the repository, documented in
`AGENTS.md` with the condition for reverting. It also means `global.json` can no
longer pin a test runner: pinning one makes the SDK reject every VSTest project
in the solution, so `scripts/run-tests.sh` dispatches per project instead.

The first credible score was **54.63%**, and closing the gap took four rounds:

| Round | Score | What moved it |
|---|---|---|
| 1 | 54.63% | first score after the migration |
| 2 | 76.86% | unit tests for `ReplicaId` and `ElementId`, whose parsing and formatting only the Conformance project exercised |
| 3 | 76.86% | **nothing** — deep-tree tests that built nesting but never made two right siblings disagree about their right origin |
| 4 | 81.22% | the ancestor case, constructed by reasoning about when that disagreement is possible at all |
| 5 | **86.46%** | comparison operators at equality, argument validation, and the GC frontier boundary |

Round 3 is the instructive one: four plausible tests, written against the right
file, moved coverage by exactly zero. Reaching the branch needed an argument
about when the code could execute, not more scenarios.

`scripts/mutation.sh` keeps both guards — no tests found, and nothing killed —
permanently. They are what caught the false 0.00%, and a gate that cannot fail
loudly is not a gate.

One survivor found along the way is genuine rather than tooling: flipping the
`Seq` half of the `ElementId` comparison changes nothing observable, because two
same-side siblings of one parent can never share a replica id (§13.5). It is an
equivalent mutant, and evidence that §13.5's reasoning is sound.

### 13.8 The stated reason for avoiding Guid.CompareTo was wrong

From the very first review, §5 justified the hand-written `ReplicaId` comparator
by asserting that `System.Guid.CompareTo` "compares `_a` (int32), `_b` (int16),
`_c` (int16), then bytes `_d`–`_k` individually, which does not match big-endian
byte order". The `replica-id-byte-ordering` conformance trace repeated it, and so
did `AGENTS.md`.

It is false on .NET 10. Measured across the signed boundaries where it should
have diverged — `7fffffff`/`80000000` in each of the first three groups, and
`01000000`/`ff000000` — `Guid.CompareTo` agrees with unsigned big-endian byte
order every time.

The claim was carried for the whole of Phase 0 and Phase 1 without being run. It
survived because everything downstream of it was correct: the comparator is
hand-written, the ordering is right, the trace passes. Only writing a unit test
that asserted the divergence exposed it — the test failed, because the
divergence does not exist.

The decision itself stands, on better grounds: nothing specifies that
`Guid.CompareTo` must order this way, TypeScript has no `Guid` to agree with,
and Postgres `uuid` collation is a third ordering. A rule whose violation
reorders user text should not depend on a framework comparison continuing to
match by coincidence. What changed is that the specification now says something
true about why.

### 13.5 Where the papers and the reference implementation differ

Two places. Neither is a contradiction — in both the implementation is a sound
specialisation — but they are recorded because the earlier §5 followed the
implementation and the paper is what governs.

1. **Sibling tie-break.** The paper orders siblings by full id; the reference
   implementation compares only the replica component. These agree in every
   reachable state, because a replica never creates two same-side siblings of one
   parent (§4), so a tie between two ids sharing a replica cannot occur. §5
   follows the paper: comparing the full id costs nothing and does not depend on
   that invariant silently holding.
2. **`rightOrigin` in the right-child branch.** The paper computes "next node in
   the traversal including tombstones" once; the implementation computes
   `nextNonDescendant(leftOrigin)`. Equal in that branch, since a node with no
   right children has no descendant following it in traversal order. §5 follows
   the paper's formulation because it is the one the proofs use.

Where the two ever genuinely conflict, the papers govern, and the conflict gets
recorded here rather than resolved silently.

**A known equivalent mutant follows from point 1.** Reversing the `Seq` half of
`ElementId.CompareTo` changes no observable behaviour, and mutation testing
reports it as a survivor. That is correct, not a gap in the suite: the `Seq`
component of a sibling comparison is only reached when two same-side siblings of
one parent share a replica id, which the placement rule makes impossible. It is
the same fact that lets the authors' reference implementation compare only the
replica component.

Do not "fix" this survivor. Narrowing the comparison to match — dropping `Seq`
from `ElementId.CompareTo` — would contradict the paper, which specifies
lexicographic order on whole ids, and would make correctness depend silently on
an invariant holding rather than on a comparison being total. The survivor is
evidence the reasoning above is sound.
