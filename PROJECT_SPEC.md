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

**The bound is expressed in operations and in seconds**, not in bytes. Bytes are
the natural unit for a transport buffer (§8's outbound channel) and the wrong one
here: what makes a pending set dangerous is the number of distinct missing
dependencies being tracked and how long they are held, and a thousand one-code-point
inserts cost the same memory as a thousand large ones. Age is measured from
when an operation entered the set, not from its arrival, so a cascade that
releases and re-buffers does not reset the clock.

**Closing for a bound violation must be distinguishable, by the client, from the
connection dropping.** §13.13: a rejection the rejected party cannot observe is
not a rejection, and a client that reads "pending set overflowed" as a network
blip will reconnect and do it again.

### Duplicate delivery

**A client will receive operations it has already applied.** This is guaranteed,
not incidental: the backplane can deliver the same broadcast twice, catch-up
after a reconnect re-sends from a version vector that overlaps what was already
applied, and a client dropped for backpressure (§8) recovers by being sent state
it partly has. Every one of those paths is required for other reasons.

The server side is covered by the primary key on
`(document_id, replica_id, seq)`, which makes a duplicate insert a no-op at the
database (§6). The client side is a decision, and it is made here rather than
left to be inferred:

> **The client dedupes explicitly before applying, and application remains
> idempotent underneath it.** Both. Neither alone.

The dedupe is the fast path: an operation whose `Seq` is below the applied
watermark for its replica is dropped without being applied, and the drop is
**counted**. The counter is the point — a sudden rise in duplicate deliveries is
how a resend loop or a misbehaving backplane announces itself, and it is
invisible if duplicates are silently absorbed.

**That watermark test is complete, and the reason is worth writing down because
it is not local.** Readiness requires the *exact* next sequence number for the
replica, not merely that an operation's structural dependencies are present. So
a replica's operations are applied strictly in order, the applied set never
contains a per-replica gap, and "below the watermark" is exactly "already
applied" rather than an approximation of it. The two rules are coupled across
two private methods and nothing about either says so, which is why
`CausalReadinessTests` pins it: an operation whose only structural dependency is
the root is still buffered when it skips a sequence number.

**The second check is the pending set's, and it is the one that is genuinely
load-bearing.** A duplicate that arrives while the original is still buffered is
below no watermark — it has not been applied — so the watermark says nothing
about it, and the pending set has to recognise it by id. Buffering it twice
applies it twice when the gap closes.

Idempotent application is the floor beneath both. It is not currently reachable:
between the watermark and the pending set, no duplicate gets as far as being
applied. It is stated as a constraint rather than a tested path precisely
because of that — a test for an unreachable path asserts nothing, and writing
one would be §12's vacuity risk landing on a specification claim rather than on
code.

> **The trigger.** If the density rule in readiness is ever relaxed — if an
> operation is applied because its structural dependencies are present, despite
> skipping a sequence number — then the applied set acquires per-replica gaps,
> the watermark stops being a complete test for "already applied", and
> **idempotent application moves from constraint to load-bearing.** At that
> point it needs its own tests, and they must land in the same change, not
> after it.

That is written as a trigger rather than a caution because of how the change
would arrive. Relaxing readiness looks like a latency optimisation — an
operation that could be applied now is being made to wait — and it is local, one
comparison in one method. Nothing at the site of the change mentions duplicate
detection. The coupling is pinned from the readiness side, in
`CausalReadinessTests` on both implementations, so the relaxation fails a test
that explains why; this paragraph is what that test's failure should send the
reader to.

Three consequences worth stating, because they bind future changes:

- **Applying an operation must stay idempotent.** Applying an insert whose id is
  already present, or a delete whose target is already tombstoned, is a no-op.
  Anything added to the apply path that is not idempotent — a counter, an
  activity log, an undo stack, a metric incremented per operation — breaks a
  guarantee nothing else checks, and breaks it silently.
- **Dedupe is not a substitute for causal readiness.** An operation may be new
  *and* not ready; the two tests are independent, and the dedupe check runs
  first only because it is cheaper.
- **Relaxing readiness is not a local change.** Applying an operation whose
  structural dependencies are present but whose sequence skips one looks like a
  latency optimisation and would silently break duplicate detection, because the
  watermark test depends on density holding at apply time.

The reason this is written down rather than left to the algorithm: CRDT
idempotency is real, but it is a property of §5's data structure, not a decision
the transport made. An assumption nobody wrote down breaks silently the first
time either path changes — and 3b changes both of them.

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
- **`retired_at` must actually be set**, by a background job on `T_retire` of
  inactivity. The column has existed since Phase 2 and nothing writes it, which
  makes every rule above true on paper and inert in fact. Two things depend on
  it: §9's offline window, which warns a user before their work is discarded and
  cannot be verified end to end while no replica is ever retired; and Phase 7's
  GC, whose causal-stability frontier never advances while one abandoned tab
  counts as live. **Owned by Phase 7** and named here so it is not rediscovered
  there — a Phase 4 client that warns correctly about a discard that never
  happens is the shape §13.15 warns about.

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

### Two encodings, two roles

The project carries **two** encodings of the same data, and which one is
normative is not the same question as which one is stored.

**Normalised JSON (§9) is normative.** It defines what a correct serialisation
*is*. The conformance corpus is JSON, the cross-implementation comparison is
byte-for-byte over JSON, and JSON is the form a human reads when diagnosing a
divergence. Nothing about this changes.

**Binary is the storage and wire form.** `document_snapshots.state` holds
binary, and operations travel the wire as binary. The layout is specified below
under *Binary encoding*.

The two are tied together by a rule, not by convention:

> **Binary correctness derives from JSON correctness.** The corpus asserts
> `binary → JSON → binary` is byte-identical, and `JSON → binary → JSON` is
> byte-identical, on **both** implementations. A binary codec that round-trips
> but disagrees with the normative form fails the build.

This is what preserves the cross-implementation guarantee through the format
change. Without it, binary would be a second definition of correctness that
nothing checks against the first, and the two would drift the way any unchecked
pair of implementations drifts.

**Why binary at all.** Phase 2 measured the JSON snapshot at 100k live elements:
22,277,866 bytes — 222.8 bytes per element — loading in 501–941 ms against §8's
500 ms target, with no tombstones. §13.9 has the numbers and the reasoning. The
short version is that 222.8 bytes carries on the order of 50 bytes of actual
data, and §8's stated case adds 500k tombstones, stored in full: roughly 130 MiB
to move before it reaches a browser. Phase 1 established that tombstones cannot
be collected on causal stability alone, because a `RightOrigin` can name one
(§5), so 500k is a realistic accumulation rather than a pessimistic one.

**Why now, and not in Phase 4.** Phase 4 binds the format into the client's
IndexedDB schema. After that, changing it is a migration on every user's
machine; before it, it is a codec swap behind two tests.

#### The hub protocol carries opaque bytes — a constraint, not a description

**The transport protocol frames messages. It does not encode operations.** A hub
message carries the §6 binary form as an **opaque byte string**, and §6 remains
the *sole* authoritative encoding of an operation or a snapshot. This holds
whatever protocol the hub negotiates, MessagePack included.

Concretely, and these are prohibitions rather than preferences:

- An operation, an element, an id, or a version vector **must not** be passed to
  the transport's serialiser as a structured object, however convenient. It is
  encoded by §6's codec first and handed over as bytes.
- The transport's own type system — MessagePack's maps, arrays, extension types,
  or any successor's — **must not** appear in the definition of what an operation
  is. A hub method's parameters carry a document id, a replica id, and a byte
  string; nothing structural travels outside those bytes.
- **No canonical-form rule may live in the transport layer.** §6 has exactly one
  set of canonicality rules and one place they are enforced.

The reason is §13.11, which is where the last serious bug came from. Two
encodings of the same data, each with its own notion of a correct spelling, is
precisely the shape that produced a rule both implementations read the same way
and both got wrong. MessagePack is adopted for framing because it moves a byte
string without base64-inflating it; adopting its *object model* would buy nothing
and would recreate that shape — a second encoding, with its own canonical form,
checked against nothing.

Stated as a constraint because the failure is a later convenience rather than a
present mistake: the moment someone finds it easier to send an object than to
call the codec, the second encoding exists, and it will look like a
simplification in the diff that introduces it.

### Binary encoding — normative layout

Written before either implementation, for the same reason §9's trace schema was:
two codecs written from one description agree or the build says so, whereas a
second codec written from the first only inherits its mistakes.

All integers are **unsigned LEB128 varints** unless stated otherwise: seven bits
per byte, least significant group first, high bit set on every byte but the last.
A varint encoding a value in more bytes than necessary is invalid. Replica ids
are the raw sixteen bytes in the §5 order — never a text form.

#### Header

| Bytes | Meaning |
|---|---|
| 4 | magic `43 52 44 54` (`CRDT`) |
| 1 | format version, currently `01` |
| 1 | body kind: `01` snapshot, `02` operation batch |

**A reader that does not recognise the version rejects the input** with an error
naming the versions it supports, and reads no further. Never a best-effort parse
— §9 says why, and it is the one rule in this section with no exceptions.
Unrecognised *kind* is the same.

#### Replica table

Both body kinds begin with it.

| Field | Encoding |
|---|---|
| count | varint |
| ids | `count` × 16 raw bytes, **ascending in §5 order**, no duplicates |

Every replica named anywhere in the body — element ids, parents, right origins,
delete targets, version vector — appears exactly once here, and every reference
afterwards is a varint index into this table. This is the first of the three
structural savings: a 16-byte id becomes one byte at every reference, and a
document has a handful of replicas and hundreds of thousands of references.

#### Element flags

One byte, in element and insert records alike.

| Bit | Meaning |
|---|---|
| 0 | side: `0` left child, `1` right child |
| 1 | deleted |
| 2–3 | parent: `0` root, `1` **the element immediately before this one in document order**, `2` explicit, `3` invalid |
| 4 | right origin, **only when bit 0 is set**: `0` end of document, `1` explicit |
| 5–7 | reserved, must be zero |

A left child has no right-origin field at all, so "absent because left child" and
"absent because end of document" are distinguished by *shape* rather than by a
flag value. The pair that §6's `right_origin_is_end` CHECK constraint exists to
keep apart, and that trace `0050` exists to catch, is unrepresentable here.

Reserved bits must be zero and a reader rejects a record that sets them. That is
the forward-compatibility trap: a future version that assigns them is a version
bump, and an old reader must refuse rather than ignore what it cannot see.

#### Snapshot body (kind `01`)

After the replica table:

| Field | Encoding |
|---|---|
| vector count | varint |
| version vector | `count` × (replica index varint, count varint), ascending by index |
| element count | varint |
| records | element and run records, in document order, totalling exactly `element count` elements |

The version vector carries exactly the entries the replica holds — an absent
replica is not written as zero, because a round trip must reproduce the input
byte for byte and "absent" and "zero" are different inputs even though §5 treats
them alike.

**Element record**

| Field | Encoding |
|---|---|
| tag | `00` |
| flags | 1 byte |
| id | replica index varint, seq varint |
| parent | present only when flags bits 2–3 are `2`: replica index varint, seq varint |
| right origin | present only when bit 0 is set and bit 4 is set: replica index varint, seq varint |
| value | byte length varint (1–4), then that many UTF-8 bytes of one code point |

**Run record** — the third structural saving, and the one that pays for
sequential typing.

| Field | Encoding |
|---|---|
| tag | `01` |
| count | varint, ≥ 2 |
| flags | 1 byte, describing the **first** element |
| first id | replica index varint, seq varint |
| parent | present only when flags bits 2–3 are `2` |
| deleted bitmap | ⌈count/8⌉ bytes, element *i* at bit *i* mod 8 of byte *i* / 8; **bits past the last element must be zero** |
| values | total byte length varint, then the concatenated UTF-8 of all `count` code points |

A run stands for `count` elements whose ids are `(r, s)`, `(r, s+1)`, …
`(r, s+count-1)`, where every element after the first is a **right child of the
one before it with its right origin at end of document**. The bitmap carries
every element's deleted state, the first included, and bit 4 must be clear
because a run's interior right origins are end-of-document by construction and
the first element's must be too for the run to be one shape. Bit 1 must be zero, since the bitmap already carries the first
element's deleted state and two spellings of one document is what canonical form
forbids. Bit 0 is the first element's own side: a run may begin at a left child,
and every element after it is a right child regardless.

That is exactly the shape typing left to right produces: each character a right
child of the previous one, nothing following it at the time. It is also, not
coincidentally, the shape whose tree depth equals document length (§13.10).

The deleted bitmap is what keeps §8's stress case affordable. Five hundred
thousand tombstones cannot be collected — a `RightOrigin` can name one (§5) — so
they are stored, and in a run they cost one bit each rather than a record.

#### Operation batch body (kind `02`)

After the replica table:

| Field | Encoding |
|---|---|
| op count | varint |
| ops | `op count` × operation record |

**Insert** — tag `00`, then flags, id, parent, right origin and value exactly as
an element record. Parent flag `1` refers to the element inserted by the
immediately preceding operation in this batch.

**Delete** — tag `01`, then id (replica index varint, seq varint) and target
(replica index varint, seq varint).

**Run insert** — an insert record under a different tag, followed by a count and
the remaining values. Everything up to and including the first element's value
is byte-for-byte an insert record, which is what lets one reader serve both.

| field | type |
|---|---|
| tag | `02` |
| flags | 1 byte, as an insert record |
| id | replica index varint, seq varint — the **first** element |
| parent | absent for flags 0 and 1, else replica index varint + seq varint |
| right origin | present only when bit 4 is set — the **first** element's |
| value | the **first** element's, as an insert record |
| count | varint, ≥ 2 and ≤ the §7 run cap |
| values | `count − 1` × UTF-8 code point, for elements 1 … `count − 1` |

Element *i* of the run has id `(replica, seq + i)`. Element 0 takes the record's
parent, side and right origin. Every later element is a **right child of the
element before it, with no right origin** — the placement a client produces by
typing left to right, which is what §5's rule yields for consecutive insertions
at one position.

Unlike a snapshot run, the first element **may** carry an explicit right origin.
A snapshot run record has nowhere to put one; this record does, and the case it
serves — a paste into the middle of a document — is the common one rather than
an edge case. Later elements never carry one, because each is placed against the
element before it.

A decoder **expands the run before anything else sees it** and rejects a `count`
over the §7 cap **before allocating**, not after. A run naming four billion code
points is one varint; expanding it first and checking the cap afterwards is a
denial of service written into the format.

Canonical form for the operation batch, in addition to the rules below: a run
has `count` ≥ 2, runs are maximal **up to the cap**, and an insert record that
could have joined the run before it is invalid. The cap is the one exception to
maximality and it has to be: a paste of 300 code points is a run of 256 followed
by a record that continues it, and a maximality rule with no exception would
make that batch unencodable. So a record boundary is valid exactly where the
record before it is a run already at the cap. The run shape here is **not** the snapshot's
pairwise rule. It is one-sided: the later element must be a right child of the
earlier one, with the next sequence number on the same replica and no right
origin of its own. Nothing is required of the earlier element, because a run's
first element may carry a right origin — it starts the record rather than
continuing one, and a mid-document paste is precisely the case where it does.
Only the snapshot form needs the two-sided rule, because a snapshot run record
has nowhere to put a right origin at all.

#### Canonical form

There is **exactly one** valid encoding of a given document, because §9 requires
`binary → JSON → binary` to be byte-identical and that is not a property an
encoder can have if it may choose between encodings.

1. The replica table is ascending, duplicate-free, and contains exactly the
   replicas referenced by the body — no more.
2. Version vector entries ascend by replica index.
3. **Runs are maximal.** Two or more adjacent elements that satisfy the run shape
   are one run record, and a run is extended as far as the shape holds. A single
   element record that could have joined an adjacent run is invalid, and so is a
   run that could have been longer. "Satisfy the run shape" is a condition on
   **both** elements: the earlier one must be able to be in a run at all — no
   right origin — and the later one must continue it. An element carrying an
   explicit right origin can neither start a run nor sit inside one, so whatever
   follows it begins a new record however well it would otherwise continue.
4. A run record has `count` ≥ 2. One element is an element record.
5. Parent flag `1` is used whenever it applies; spelling the same parent out as
   flag `2` is invalid.
6. Varints are minimally encoded.
7. **A run's deleted bitmap has zero in every bit past the last element.** A run
   of five occupies five bits of one byte; the other three are not spare, they
   are required to be zero. Left free they would give one document eight
   spellings per partial byte, which is the one thing canonical form forbids.
8. No trailing bytes after the last record.

A reader **rejects** every violation above, all of which are checkable while
decoding. Maximality reduces to one local rule, stated over a *pair*:

> The first element of any record must not be able to continue the element
> immediately before it — where "able to" requires the earlier element to have
> **no right origin**, and the later one to be a right child of it with
> consecutive sequence number on the same replica and no right origin of its own.

An element record that could have joined the previous element fails it, and so
does a run whose first element could have extended the preceding run, which is
the same condition stated once.

**The right-origin half of that condition is not decoration.** An earlier
draft omitted it, and both codecs — written independently from that draft, as
§13.11 records — implemented a decoder that rejected documents its own encoder
produced. The shape is an element carrying an explicit right origin followed by
a right child of it with the next sequence number: the encoder cannot begin a
run there, so it writes two records, and a rule that ignores the earlier
element's right origin then calls its own correct output non-canonical.

Rejecting non-canonical input is not pedantry: a reader that accepts two
spellings of one document turns the byte-identity check into a check of
whichever spelling the writer happened to choose.

#### Measuring it honestly

The 100k document §13.9 measured is a **single forward chain** — one replica
typing left to right, never deleting. That is the best case this format has: it
collapses to one run record plus a bitmap, roughly one byte per element, and
reporting it alone would overstate the format by a wide margin.

So the metric reports **both**:

- the **chain** case, which is what the JSON number was measured on and the only
  way to compare like with like; and
- a **fragmented** case — several replicas interleaved, a realistic proportion of
  tombstones, backward runs among the forward ones — where most elements cannot
  join a run and pay the full element-record price.

The fragmented figure is the one to quote when asking whether this reaches §8.
A format whose headline number comes from its best case is a format nobody has
measured.

**Predicted from the layout above, before any codec existed**, at 100k elements,
with the measurement beside it:

| Case | Predicted | Measured |
|---|---|---|
| chain (one maximal run) | 1.13 bytes/element | **1.13** |
| fully fragmented (no runs at all, every parent and right origin explicit, four replicas) | 16.00 bytes/element | **8.45** on the fragmented document actually built |

The prediction was recorded so the measurement could disagree with it. The chain
matched exactly. The fragmented document came in under its bound because the
bound assumed no run ever forms and every parent is spelled out, while a real
fragmented document still chains most parents to the previous element — the
prediction was a worst case, and it holds as one. §13.9 has the full table
including §8's 600k stress case.

#### What a reader rejects

Every one of these is an error naming what was wrong, never a partial document:

- Unrecognised magic, version, body kind, record tag or operation tag.
- Reserved flag bits set; parent kind `3`.
- Bit 4 set on a run record.
- A replica index past the end of the table.
- Parent kind `1` on the first record of a body.
- A value that is not exactly one UTF-8 code point, or is a lone surrogate.
- A non-zero bit past the last element of a run's deleted bitmap.
- Truncated input, or bytes remaining after the declared element or operation
  count is met.
- Any canonical-form violation from the list above.

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

### JSON encoding — normative

All 64-bit values (`Seq`, `server_seq`, and any counter that can exceed 2^53)
serialize as **decimal strings** in JSON — in snapshots read for diagnosis, in
conformance traces, and anywhere else the normative form appears. The TypeScript
side parses them as `BigInt`.

JSON numbers are IEEE 754 doubles. Values above 2^53 do not round-trip, which
would break the byte-identical requirement in §9 silently and only after a
replica had been running long enough to matter.

**This rule binds the JSON form only.** It exists because JSON cannot represent
a 64-bit integer, which is a fact about JSON. The binary form of §6 encodes the
same values as varints and has no such problem — restating the decimal-string
rule there would be cargo cult, not consistency.

### Versioning — both forms

Every operation and message carries a protocol version from the first commit.
Adding one after the conformance corpus and the client's IndexedDB schema exist
is a migration; adding it now is a field.

It appears differently in each form, carrying the same meaning:

- **JSON:** a `v` field.
- **Binary:** the header's version byte (§6).

**An unrecognised version is rejected in both forms**, with a structured error
naming the versions the reader supports. Never a best-effort parse, in either
form, for any reason.

A codec that guesses at a format it does not know produces a document that is
wrong but well-formed — and this system's entire job is to make every replica
agree. Every replica agreeing on a corrupt document is not a degraded outcome;
it is the precise failure the project exists to prevent, arrived at by a path
that leaves no error behind. Refusing to parse is loud, local, and recoverable.

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

Because expansion produces exactly the chain a left-to-right typist produces, a
batch decoded from runs and one decoded from individual inserts are the same
operations; §6's canonical form then re-encodes both to the same bytes. That is
what makes the run form a transport optimisation rather than a second dialect,
and it is testable: expanding a run and encoding the result must reproduce the
run.

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

  **The ticket lives in Redis and is redeemed atomically.** Not in process
  memory: §8 forbids sticky sessions, so the instance that issues a ticket is
  usually not the one that redeems it. "Single-use" must be a single atomic
  operation — `GETDEL`, not a read followed by a delete — because two
  simultaneous connects against a read-then-delete both find the ticket present
  and both succeed, which is exactly the replay the single-use rule exists to
  stop.

  **The ticket carries the binding, and the server chooses it.** A ticket names
  the user, the document, and the **replica id the server assigned**, and the
  role check happens at `negotiate` before the ticket is issued. The client does
  not pick its own replica id.

  That last point is load-bearing rather than tidy. §5 makes `ReplicaId` the
  tie-break that orders concurrent insertions, and this section rejects
  operations whose replica id does not match the connection's — but a binding
  the client chose is not a check, it is a formality. A client that picked
  another live replica's id would be authenticated, bound, and able to author
  operations attributed to someone else, and every replica would converge on the
  result. Server assignment, recorded in `document_replicas` against the user,
  is what makes the per-operation comparison mean anything.

  **A client may ask to resume a replica it already owns, and the server
  verifies rather than trusts.** `negotiate` accepts an optional claimed replica
  id. The server reissues it only if every one of these holds:

  1. the `document_replicas` row exists;
  2. its `user_id` is the caller;
  3. its `document_id` is the document being negotiated;
  4. its `retired_at` is null;
  5. no live connection currently holds it.

  Check 4 is not redundant with the others and follows from §5: a retired
  replica's operations may already have been collected, so resuming one would
  let it keep authoring under an id the GC has forgotten. §5 says a retired
  replica that reconnects is told to resync and discard local state, and minting
  a fresh id is how that instruction reaches the client — the response names an
  id the client did not ask for, which is the signal to discard.

  If any check fails the server mints a fresh id instead of refusing, and the
  response always names the id that was actually assigned. It is never an error
  to ask: a client whose stored replica has been retired, or whose tab crashed
  and left a stale claim, needs to get a working session rather than a 4xx it
  cannot act on (§13.13). It also never tells the caller *which* check failed —
  "that replica belongs to someone else" is a fact about another user's session.

  **The security property, stated so it cannot be eroded by a later
  convenience: resumption is authorization to CONTINUE a replica, not
  authorization to author as one.** Tier-1 stays exactly what it is — the
  submitted `ReplicaId` must equal *this connection's binding*, not "one of the
  ids you own". Widening it to a set would reopen §13.12's attack from inside
  the owner's own account: a user with two documents, or two replicas on one
  document, could attribute operations to whichever replica suited them, and
  every peer would converge on it.

  Check 5 is what keeps the two statements consistent, and it is a claim on the
  replica taken at `negotiate` — not at connect. The ticket exists before the
  connection does, so a claim taken only when the socket opens leaves a window
  in which two `negotiate` calls both succeed for one replica. The claim is
  therefore atomic (Redis `SET … NX`), scoped to `(document, replica)`, taken
  before the ticket is issued, held while a connection is bound, refreshed by
  the hub, and released on disconnect — with a TTL, because a process that dies
  holding a claim must not strand the replica forever. Two tabs cannot hold one
  replica id, which is the whole point: §5's tie-break assumes one author per
  id, and two live authors sharing one id can produce two different operations
  with the same `ElementId`.

  **Why resumption rather than the alternatives.** A client that reloads holds
  an outbox of operations it authored under its previous replica id, and tier-1
  will reject every one of them under a fresh binding. Re-authoring the outbox
  under the new id changes operation identity, so any operation the server
  already received arrives a second time under a new name and the CRDT — which
  deduplicates by id and is right to — inserts the characters twice. That is
  silent text corruption, and it happens precisely in the case the outbox exists
  for: a partially-delivered batch. Accepting submissions from a
  retired-but-owned replica is the same widening of tier-1 described above.
  Resumption is the only one of the three that leaves both the outbox and the
  anti-forgery check intact.
- The ticket and any token must never be written to logs. Configure request
  logging to redact them explicitly, and add a test that drives a connection
  using a **known sentinel value** through the real logging pipeline —
  including request logging and exception paths — asserting the sentinel
  appears in no sink. "No log line contains a token" is not testable as stated;
  this is.

**How the browser obtains a token (Phase 4.9)**
- **Authorization Code with PKCE, and no client secret.** A public client cannot
  keep one; shipping a secret to a browser is publishing it. The code verifier
  is generated per attempt from a CSPRNG, the challenge is `S256`, and the
  verifier is discarded the moment the code is redeemed.

  PKCE's mechanism is that the token endpoint refuses a code presented without
  the verifier that produced its challenge — which means a login that succeeds
  proves nothing about PKCE, because a compliant server enforces it and a client
  that omitted the verifier entirely would simply fail later. What has to be
  tested is the challenge derivation, the freshness of the verifier across
  attempts, and one exchange against an endpoint that actually checks.
- **The access token lives in memory only.** Never `localStorage`, never
  `sessionStorage`, never IndexedDB, never a cookie this application sets,
  never a URL. The requirement is a sweep rather than a lookup: after a complete
  login, no browser store and no URL the page can reach contains the token or
  the code verifier.
- **The token must never reach the SignalR connection URL.** §7 puts a
  single-use 60-second ticket in the `access_token` query parameter precisely so
  a bearer JWT is not there — URLs reach proxy logs, browser history and
  `Referer`, none of which this application's redaction controls. 4.9 changes
  how tokens are obtained and therefore has to re-assert that guarantee rather
  than inherit it: the hub URL carries the ticket and nothing else.
- **Refresh is delegated to the provider's library, not hand-rolled.** Token
  refresh looks simple and has a long history of subtle bugs — clock skew,
  concurrent refreshes racing, a rotated refresh token discarded on retry — and
  none of them are what this project is for.
- **A refresh that fails is a client state, not an exception.** §9's contract:
  the client goes offline with its own problem code, keeps its outbox, and says
  a sign-in is needed. Anything else loses unsent work while a login prompt is
  pending, which is the §9 failure this whole path exists to avoid.
- The redirect URI is exact-match, and the `state` parameter is verified on
  return.

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

  Those are HTTP statuses and they apply to `negotiate`, which is where the
  membership decision is made. A hub method has no status code, so it fails with
  an error carrying the equivalent **code** — `not_found` or `forbidden` — and
  the same rule about what may be revealed. The distinction is not cosmetic: a
  hub that answers "forbidden" for a document the caller cannot see has leaked
  its existence just as surely as a 403 would.

**Input validation**
- Reject operations whose `ReplicaId` does not match the one bound to the
  authenticated connection. A client must not be able to forge operations
  attributed to another replica.
- Reject operations whose `Seq` is not the next dense value for that replica.
  Density is a correctness property of the version vector (§5), not a
  convention.

  The expected next value is **reconstructible from Postgres** — the maximum
  `seq` stored for that `(document_id, replica_id)` — and any in-memory copy is
  a cache. §8 requires exactly that: an app server may hold per-document state
  for speed, and must not need it for correctness after a failover.
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

  **"Converge" is asserted against §9's normalised JSON of each client's full
  element state — tombstones included — not against the visible text.** Two
  replicas can render identical text while disagreeing about the tree beneath it,
  and that disagreement is exactly what produces divergence on the *next*
  concurrent edit, so comparing rendered text would pass on documents that are
  already broken. This is the comparison the conformance corpus already makes,
  which means a convergence failure is diagnosable with the tooling that exists.

  "Stateless" was the original wording and it is not achievable: validating an
  operation without loading the document requires a cached version vector.
  Reconstructible is the property that actually matters.
- Hot path (receive op → validate → persist → broadcast) must not load full
  document state. Validate against the version vector and the referenced parent
  and right origin only.
- **Broadcast carries `server_seq` and is not ordered by it.** The server assigns
  `server_seq` under the per-document advisory lock (§6) and sends it with every
  operation, but the fan-out makes no ordering promise: the backplane does not
  guarantee order across instances, and building on the assumption that it does
  would make correctness depend on a property Redis pub/sub does not offer.
  Ordering is the receiver's problem and it already has the machinery — causal
  readiness (§5) is what makes an out-of-order arrival safe, and `server_seq` is
  what makes catch-up queries answerable. Requiring ordered fan-out would also
  serialise it, which is the opposite of what §8 is for.
- Batch operation persistence: buffer for up to 50 ms or 100 ops, whichever
  comes first, then write in one round trip under the per-document advisory lock
  (§6).
- Backpressure: bounded per-connection outbound channel, **bounded in bytes**,
  because what exhausts an app server here is buffered payload rather than
  message count. If a client cannot keep up, drop it to catch-up-via-snapshot
  rather than growing the buffer unbounded.

  **"Drop to catch-up" means the connection is closed and the client reconnects
  and resyncs**, not that the server quietly stops sending and hopes. A client
  that is silently starved cannot tell it is missing operations and will render a
  document that is wrong without knowing; closing is observable, starving is not
  (§13.13). The close carries a reason the client can act on, and the client's
  response is the catch-up path — one of the three guaranteed sources of
  duplicate delivery (§5).
- **Cross-instance delivery carries the batch, not a group send.** Each
  instance subscribes to a Redis channel per document, while it holds a
  connection for that document, and publishes every batch it accepts. Every
  instance then fans out to its own connections under its own §8 deadline.

  SignalR's own backplane would deliver a group send across instances and is
  deliberately not what carries this: a group send lands on the remote instance
  as a write into each member's channel with no timeout, so one slow client
  there stalls that instance's backplane consumer — the stall the per-connection
  deadline exists to prevent, moved one hop away and invisible from the sender.

  Publishing is best effort. The operations are already committed before the
  fan-out, so a lost publication costs a remote client latency until its next
  catch-up rather than an operation; failing the submission instead would turn a
  backplane hiccup into a rejected keystroke for an operation the server holds.
- **Catch-up is answered from the client's version vector, never from a
  `server_seq` watermark.** The client says what it holds, per replica, and the
  server returns what that does not cover. A watermark would be wrong for the
  same reason the bullet above makes broadcast unordered: a client can hold
  `server_seq` 105 without holding 100, so "everything after your highest" skips
  whatever fell in the gap — and skips it silently, leaving a client that
  renders a plausible document and converges with nobody. What a client actually
  knows is per replica and dense (§5), which is exactly what a version vector
  expresses and what a single number cannot.

  Above a configured operation count the answer is a snapshot instead, because
  replaying a week of deltas costs more than sending the state. The threshold is
  configuration, not a constant: where the two cross depends on the document.
  The snapshot path is also reachable on demand, which is not a convenience —
  it is the only way to exercise a floor that otherwise runs solely behind a
  working fast path (§13.14).

  Catch-up returns the whole document, so it is a read and is authorized like
  one: the §7 role check runs on every call, not once at connect.
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
| Document load, **browser**, 100k live characters + 500k tombstones | reported, no threshold — see below |

**Document load is two numbers, not one.** The original §8 scoped the 500 ms
target to the server and said explicitly that a browser target, if wanted later,
"constrains the snapshot format and must be specified separately". That
condition has now fired: Phase 2's measurement is what moved the storage and
wire form to binary (§6), so the browser figure is specified here.

Both numbers are required, and each names where it is measured, because
"document load" without a measurement point is not a falsifiable claim:

- **Server-side**, C#, from snapshot plus tail, cold cache. Target < 500 ms.
- **Browser**, the TypeScript core of §9, in **headless Chromium** on a
  **standard GitHub-hosted `ubuntu-latest` runner** (2 vCPU, 7 GB) — the
  hardware class is part of the number, and the exact Chromium version is
  recorded alongside each measurement. **Cold** means a fresh page load with
  **empty IndexedDB**: the first-time-user case, where the document arrives over
  the network and nothing is cached. A warm figure may be reported alongside it
  but the cold one is the number that matters.

The browser figure carries **no threshold in this phase** — it is reported, the
way §6's snapshot metric was reported, because setting a bound before anyone has
seen the number is how §8 acquired a 500 ms target nothing had measured. It will
be worse than the server figure and it is the one that decides whether this
works on a slow connection.

Measured by `scripts/browser-metrics.sh`, and by the `browser-metrics` CI job on
the runner class named above. §13.9 carries the first readings; the short version
is that §8's document takes about six seconds in a browser, and that placement —
not parsing, and not the network — is where it goes.

Note also that 500k accumulated tombstones implies GC is not keeping up — this
is a stress target, not a steady state.

## 9. Client

- React 19, TypeScript strict mode.
- Ships its own FugueMax replica implementing the identical algorithm. Local
  edits apply optimistically to the local replica and render immediately; remote
  ops merge in. There is no server round trip in the typing path.
- IndexedDB persists the local replica and an outbox of unsent operations, so a
  full offline session survives a page reload and syncs on reconnect.

  **Both are stored in §6's binary encoding**: the replica as a snapshot body,
  the outbox as operation-batch bodies. Not a JSON shape invented for the
  browser. §6 is the sole authoritative encoding and a second one acquires
  canonical-form rules of its own, which is where §13.11's bug came from — and
  this store is read by a *different build* from the one that wrote it more
  often than any other artefact in the system, because a browser holds whatever
  version the user last loaded.

  The store carries its own schema version alongside §6's format version, and
  **an unrecognised version at either level is rejected explicitly** — §6's rule,
  which that section calls the one with no exceptions, applies here with the
  most force. A best-effort parse of a store written by a newer build produces a
  replica that is subtly wrong and then submits operations derived from it.
  Rejection means discarding local state and resyncing, which loses unsent work
  and says so; a bad parse loses correctness and does not.

  **A reload resumes the replica rather than becoming a new one** (§7). The
  outbox holds operations authored under the stored replica id, and they are
  submittable only under a binding for that same id.

- **Every rejection the server can return has exactly one defined client
  recovery, and each produces a visible change of state.** A code the client
  swallows is a client that appears to work and silently is not (§13.13).

  | Code | Recovery |
  |---|---|
  | `not_found` | The document is gone or access was revoked. Stop the session, surface it, do not retry. |
  | `forbidden` | Demoted to viewer mid-session. Drop to read-only, keep receiving, surface it; the outbox is unsendable and must not be discarded silently. |
  | `malformed` | A bug in this client. Stop submitting, surface it, keep local state for diagnosis; retrying cannot help. |
  | `too_many_replicas` | §7's cap. Retry with backoff, having released any claim held; surface it if it persists. |
  | `unknown_origin` | The server does not have an operation this batch references. Catch up by version vector, then resubmit once. Repeated occurrence is a bug, not a race. |
  | `resync_required` | §5's GC watermark: the referenced id is at or below it and is gone. Discard local state, take a snapshot, and report the unsent operations as lost — this is the one case where §5's "do not drop" rule has an exception, so it is the one the user has to be told about. |

  `resync_required` is specified here before anything emits it. §5 defines the
  condition and the server side arrives with GC; defining the client contract
  now means that implementation is written against a stated shape rather than
  inventing one late, when the pressure will be to make it whatever the client
  already happens to tolerate.

  **`sign_in_required` joins the table** and is the one entry no server emits:
  it is raised by the client itself when the token source cannot produce a
  valid token — the refresh failed, the session expired, the user signed out
  elsewhere. It behaves like a lost connection and not like a rejection: state
  goes `offline`, the outbox is kept in full, submission stops, and the message
  says a sign-in is needed. §7 requires this rather than an exception, because
  an unhandled rejection in the refresh path discards unsent work at exactly
  the moment the user is being asked to log in again.

- Reconnect with exponential backoff and jitter. On reconnect, send the local
  version vector and receive only the missing operations.
- Presence (remote cursors) is ephemeral and never persisted. **Deferred beyond
  Phase 4**, explicitly: it needs a hub surface that does not exist, §11's Phase
  4 done-when does not require it, and leaving it implied invites it being
  half-built as a side effect of the editor. It is its own task when it comes.
- **Cursors are anchored to `ElementId`, not to integer indices**, with a
  left/right bias for the gap between elements. An integer index is invalidated
  by any concurrent edit earlier in the document, which makes remote cursors
  jump and local selections drift.
- **Code point boundaries.** The CRDT operates on code points; the DOM,
  `Selection`, and JavaScript strings operate on UTF-16 code units. The client
  owns an explicit translation layer between the two, and it is unit-tested with
  astral-plane characters. Deleting an emoji ZWJ sequence removes one code
  point, not the whole visible glyph — this is accepted behaviour, not a bug.

  **The layer lives above the core, not inside it.** §1 makes the core
  code-points-only and dependency-free; a core that knew about UTF-16 offsets
  would be a core that knew about the DOM, and the same code has to run in the
  conformance runner where there is no DOM at all.

### Offline window

Offline editing is supported for up to `T_retire` (7 days, §5). Beyond that the
replica is retired server-side and its local state is discarded on reconnect.

The client **records the last successful sync time and surfaces the remaining
window in the UI.** It must warn as the window nears expiry and must not
silently accept edits that will be discarded. Accepting an hour of offline work
and then throwing it away without warning is a data-loss bug, not a limitation.

The client half of this is Phase 4 and the server half is not: nothing sets
`retired_at` until Phase 7 (§5). So the warning can be built and tested against
a clock, and **the discard it warns about cannot be observed end to end until
retirement exists.** That is stated rather than glossed, because a client that
warns correctly about something that never happens passes every test anyone
would write for it — §13.15's shape, and §12's rule that a task whose
verification needs infrastructure that does not exist yet is written, not done.

### Conformance testing

`tests/Conformance/traces/` holds shared JSON traces. Both implementations replay
every trace and write a normalised result file; a separate comparison step
asserts the two files are byte-identical. Any divergence fails the build.

**Two runners, one corpus.** The C# runner is an xUnit project; the TypeScript
runner is a vitest suite. Neither invokes the other — coupling the .NET test run
to a Node toolchain buys nothing and makes each side harder to run alone. The
comparison is a third step over two artefacts.

**And a second check the corpus cannot make: the two implementations meeting
over a real socket.** The conformance runner compares two files; it never opens
a connection, never frames a message, and never exercises the path where one
implementation's bytes reach the other's decoder. Neither does the .NET suite,
which drives the server over an in-memory transport with the C# core on both
ends — an arrangement that agrees with itself by construction.

So the interop suite starts the published API as its own process and connects
the TypeScript core to it over TCP, with three requirements that are what make
it worth running:

- **The harness uses the shipped core's own codec.** A second encoder written
  for the test would check the harness against the server rather than the
  product against it.
- **The decisive assertion is on bytes the server produced** — above all the §6
  snapshot the C# core encodes from state it rebuilt out of Postgres, which the
  TypeScript core has to decode into an identical normalised document. Two
  TypeScript replicas agreeing with each other is not interoperability.
- **Authentication is real, with no bypass added to the product.** §7's rules
  hold in this configuration too, so the harness generates a certificate and
  serves OIDC metadata over genuine HTTPS rather than relaxing the requirement
  that metadata be fetched over TLS. A development bypass is a permanent
  weakness bought to make a test easier, and it would also make every other
  assertion in the suite a statement about a configuration nobody deploys.

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

#### Normalised result format (v2)

Each runner writes exactly this, and the comparison is `diff` over the bytes:

```jsonc
{
  "v": 2,
  "implementation": "csharp",              // or "typescript"; EXCLUDED from comparison
  "results": [
    {
      "name": "rga-backward-interleaving",
      "snapshot": "4352445401010100…",     // §6 binary snapshot, lowercase hex
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

**`snapshot` is what makes the binary encoding a build requirement rather than a
local assertion.** Each runner encodes the document per §6 and writes the bytes
as hex, so two implementations that disagree about the encoding produce different
artefacts and the comparison fails — exactly as it does for an algorithm
disagreement. Asserting the round trips separately on each side would prove each
codec self-consistent and say nothing about whether the two agree.

Before writing that hex, each runner checks both directions locally, because a
failure there names the document rather than showing up as an opaque hex diff:

- `binary → JSON → binary` is byte-identical.
- `JSON → binary → JSON` is byte-identical.

Version 1 of this format had no `snapshot` field; it was added when §6 made
binary the storage and wire form.

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
| 2.5 | Binary storage and wire encoding; scale in the generator; mutation ratchet | `binary → JSON → binary` byte-identical on both implementations; invariants run at 150k; a score decrease fails CI; both §8 load numbers reported |
| 3 | SignalR hub, auth, ingest validation | Auth tests pass; every §7 ingest cap has a test proving it rejects |
| 3b | Wire protocol, causal delivery, scale-out | Protocol settled and measured — wire bytes **and** client bundle size — **before** any throughput number; two real clients converge on §9 normalised state; an instance killed mid-session and clients still converge |
| 4 | React client wrapping the Phase 1 TS core | Offline edit, reconnect, converge on §9 normalised state — real disconnection, simulated clock for the window arithmetic only; a reload resumes its replica (§7) and its outbox survives; a store written by an unrecognised version is rejected, never best-effort parsed; **and the client exists as a client** — a browser loads the app, authenticates, opens a document and types, with the text visible (§13.22) |
| 5 | Conformance corpus at scale | 1,000 generated traces match across both implementations; runner fuzzes in CI |
| 6 | Security hardening | Every requirement in §7 has a passing test **against the application as Compose starts it**, not only against a test host (§13.22); **the §13.19 guard audit is done** — every textual guard has been asked what defeats it without matching its pattern, and each answer is either fixed or recorded |
| 7 | Scale + observability | Load test hits the §8 targets; **a §8 target is deliberately broken and the dashboards alone say which one and on which instance** — existence is not observability (§13.22); **`retired_at` is set on `T_retire` inactivity and `resync_required` is emitted** against §9's stated client contract |

**Phase 0 done when:** CI is green on a clean clone, and that run has
(a) built every project with warnings-as-errors, (b) run at least one real
assertion in each test project, (c) proven `Crdt.Core` references nothing
outside the BCL, (d) started Postgres and Redis via Testcontainers and connected
to both, (e) brought the Compose stack up and received 200 from `/health/live`,
and (f) run a secret scan.

An empty test suite passing proves nothing, and `vitest` exits non-zero with no
test files unless told otherwise.

**Phase 2.5 exists because the hub and the codec must not change together.**
Phase 2's measurement moved the storage and wire form to binary (§6, §13.9), and
the wire form is an input to Phase 3's hub. Doing both in one phase means a red
convergence test cannot say which of the two moved. The scale dimension and the
mutation ratchet ride along because Phase 3's fan-out is where the
correct-at-test-size, fatal-at-real-size class recurs, so the instrument has to
exist before the phase that needs it.

**Phase 3 is split by failure mode, not by size.** In Phase 3 a failing test
means a security property is absent — that is the only thing it can mean. In
Phase 3b a failing test might mean the property is absent, or the test is wrong,
or a timing assumption slipped. Those want different reviewer attention, and
reviewing them together means the security tests get skimmed on the way to the
concurrency work. Run expansion sits in Phase 3 because it is ingest-path
validation, and because 3b needs it working.

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

### Sabotage the checks, on a schedule

**Every check that exists to catch divergence gets deliberately broken on one
side, periodically, to confirm it fires.** Not as a habit someone remembers — as
a standing practice with a record of when it was last done.

This has been performed twice and found something both times. In Phase 0 the
architecture test that proves `Crdt.Core` references nothing outside the BCL was
sabotaged by adding a forbidden reference, and it turned out the reflection check
missed a *declared* reference the compiler had elided; both halves exist now
because of it. In Phase 2.5 the cross-implementation binary comparison was
sabotaged by dropping a run-encoding guard, the comparison did **not** fire, and
investigating why produced a real bug in §6 that both codecs had implemented
identically (§13.11).

A green check is evidence about the code only if the check can go red. That is
not a property anyone can read off the source; it has to be demonstrated. When
sabotage does *not* produce a failure, the finding is not "the check is broken" —
it is "the corpus does not reach this shape", and that is the more useful of the
two answers.

**The practice applies to a new test as much as to an established check**, and
that is where it pays most often. Phase 3 ran sixteen sabotages; fifteen
confirmed a check and one caught a *test* — a shutdown-race test written against
the wrong path, which passed whether or not the code was correct and would have
sat in the suite forever counted as coverage of a race it never touched. Right
subject, wrong path is the most common way a test passes for no reason, and no
amount of reading finds it: a test asserting something true either way looks
exactly like a test asserting something true because the code is right.
Coverage that would be relied on is worse than none.

So: **a test written for a specific defect is not finished until the fix has
been removed and the test has been seen to fail.**

**A sabotage run reports on whatever was built, not on whatever is on disk.**
Restore the file and rebuild before believing the next result — and restore it
in a way the build system can see. The 3b.6 harness restored with `mv`, which
preserves the backup's modification time; the restored source was therefore
*older* than the artefacts compiled from the sabotaged version, so MSBuild
skipped the recompile and every following run silently executed the previous
sabotage. See §13.17: the direction that matters is not the one that showed up.

**When a sabotage survives, the first hypothesis is that the test does not reach
the code — not that the code is unreachable.** 4.8's end-to-end test stayed green
with the reconnect catch-up removed entirely, which reads at first like a claim
about the code: perhaps catch-up is redundant when broadcast covers the same
ground. It was a claim about the setup. The author had nothing to catch up on,
because nobody had written anything while it was away, so convergence held for a
reason unrelated to the mechanism. Giving the other client an edit to make during
the outage — reaching the author only by catch-up, since broadcast went to a group
it had left — made the same sabotage fail.

This is the Phase 3 shutdown-race test again (right subject, wrong path), and it
is now twice. The order matters because the two hypotheses lead opposite ways:
"the code is redundant" invites deleting the mechanism, and "the test does not
reach it" invites fixing the test. Prefer the second until the setup has been
shown to exercise the path — a surviving sabotage is evidence about the test
first and about the code only after that.

### Name the vacuity risk before writing the test

**Every task in a phase breakdown states how its test could pass meaninglessly,
written before the test exists.** Not after, and not as a review step — as part
of proposing the work.

Sabotage catches a vacuous check afterwards, by breaking the code and watching
nothing happen. That works and it is why sabotage is a standing practice, but it
only fires on checks someone thought to sabotage, and it costs a full cycle each
time. Naming the risk up front is the same question asked earlier and cheaper:
*what would make this test pass whether or not the code is right?*

The Phase 3b breakdown was the first written this way and it paid immediately.
The wire-protocol task's stated risk was "measuring `byte[]` length rather than
the framed message" — which would have shown two protocols as identical, because
the base64 inflation being measured lives in the frame and not in the payload.
That measurement would have looked correct, produced a plausible number, and
decided the protocol wrongly. Nothing about the resulting code would have looked
wrong afterwards.

A second thing this format surfaces: **a task whose verification needs
infrastructure that does not exist yet is written, not done.** The breakdown says
so, and the task stays open until the later task closes it, rather than being
marked complete on the strength of a suite that cannot fail.

3b.2 is the worked example and it paid. Its broadcast tests could not distinguish
per-instance fan-out from a working backplane, so they were held open until
3b.7's two-instance test existed — and the fan-out really was per-instance. A
client connected to another server received nothing. Sabotaging the publish
fails four of 3b.7's five tests and would have failed none of 3b.2's. Marked
done in isolation, that ships covered by green tests until Phase 7's load
testing puts two instances behind one load balancer.

### A guard runs on a schedule something else keeps

**A check whose invocation is a judgement call is a check that is skipped exactly
when it matters.** Attach every guard to something that happens anyway.

§12's phase preflight was built after Phase 2.5's six silent red pushes, for
precisely the failure that then recurred across seven tasks of Phase 3b
(§13.20). The tool was not missing. Running it was left to discretion, and
discretion said "the local suite is green" seven times.

So the fix for that class is never a stronger intention. `check-workflows.sh`
detects the specific defect, but the reason it will keep working is where it
sits: first among the preflight's local gates, and the preflight runs at every
task boundary rather than at the phase report. The same shape applies to any
guard added later — bind it to the commit, the gate, or the build, not to
someone remembering it is relevant.

### Hand-written fixtures for every canonical form

**Wherever this specification defines a canonical form, the suite carries
hand-written documents the specification says are VALID that neither
implementation generates.**

Round-trip testing defines codec correctness as encoder-decoder agreement, which
is circular: an encoder that never emits a legal shape and a decoder that rejects
it agree perfectly and are both wrong. The property that actually matters is that
**a decoder accepts every document the format admits**, and only a fixture
written by hand from the specification can test it — by construction, the encoder
cannot produce the cases that would expose the gap.

The refusal fixtures are the mirror of this and are not a substitute: they prove
a decoder rejects what the format forbids. Both directions are needed, and only
the acceptance direction is circular without hand-written input.

### No phase is reported complete without a CI preflight

**`scripts/phase-preflight.sh` runs before any phase report, and a phase with any
red job is not reported complete.** The script queries the actual CI status of
the branch head; its output goes in the report.

This is structural rather than a matter of diligence, deliberately. Phase 2.5's
mutation gate was red for six consecutive pushes while six of seven jobs were
green, and it went unnoticed because the report format had a slot for what was
built and no slot for whether the build agreed. A checklist item that exists only
in someone's intention is a checklist item that gets skipped exactly when things
are busy.

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
| 6 | 85.44% | **nothing was added** — Phase 2's changes to `Replica` (iterative traversal, the `Import` rewrite) created new mutants and no tests reached them |
| 7 | **87.74%** | those: `Import`'s argument guards, its refusal of a snapshot with a dangling reference, and its taking the next sequence from its own vector entry |

Round 6 is the other one. The gate is a ratchet only if it is read after every
change to the mutated project: Phase 2 touched `Crdt.Core` for performance
reasons alone, added no behaviour it thought worth testing, and gave back 1.02
points without any test failing. It stayed above 85% by 0.44 points, so nothing
went red — the score reports a slide the build cannot.

Round 3 is the instructive one: four plausible tests, written against the right
file, moved coverage by exactly zero. Reaching the branch needed an argument
about when the code could execute, not more scenarios.

**The gate is now a ratchet.** The committed score is the floor, and any
decrease fails CI regardless of the absolute number. An 85.44% that clears an
85% threshold after a 1.02-point drop is exactly the erosion a threshold is
supposed to catch and structurally cannot: a fixed bar only notices the last
step of a slide, and by then the headroom that would have paid for the fix is
gone.

The known-undetected list lives in `mutation-floor.json` and
`scripts/mutation.sh` enforces it. Improving coverage is an ordinary commit:
cover a mutant, drop its entry. Giving coverage up requires adding an entry
*and* an argued exception recorded in this section, naming what was removed and
why the coverage it provided is not worth keeping. There is no third option,
and in particular "the score went down but it still passes" is not a sentence
the build will accept — nor is "the score went up", which is now known to mean
very little on its own.

A set comparison is exact by nature, which is what a tolerance could never be: a
tolerance on a percentage would have to be about the size of the erosion being
detected, and would therefore defeat the check.

**The score is stable on one machine and is not stable across machines, and this
was got wrong once.** An earlier draft of this section claimed the same commit
produces identical status counts locally and on CI. That claim was made from
four runs on a single machine plus one CI run predating the §13.10 scale cases,
and CI disproved it immediately: commit `d20bc0c` scored **88.12%** locally
(220 killed, 10 timed out, 17 survived) and **88.89%** on CI (216 killed, 16
timed out, 15 survived). Two mutants that survive here time out there.

The mechanism is that **a timeout counts as a detection**, which is right — a
mutant that hangs the suite has been caught — but it means a slower machine
detects mutants a faster one does not, and reports a *higher* score for
identical code. It is not the scale cases: bounding them to 100 elements changes
neither the score nor the timeout count here. It is the runner.

That was first answered by enforcing the ratchet **in CI only**, so that the
comparison at least happened between comparable runs. It was the right diagnosis
and an insufficient fix, for the reason recorded below. The identity-based
ratchet that replaced it is enforced **everywhere**, because it does not depend
on timing at all. The two permanent guards — no tests discovered, nothing
killed — always did, and still do.

**The floor was hardware-coupled, and the very next commit proved it.** Pinning
the ratchet to CI made the number comparable between runs; it did not make it a
property of the code. A change to the runner image, its CPU allocation, or how
loaded it is moves the timeout count and therefore the score, with no commit
touching `Crdt.Core` at all — upward when the runner gets slower, since a slower
machine detects mutants a faster one does not.

The prediction was written into this section and disproved within the hour.
Commit `9ffe234` changed `PROJECT_SPEC.md`, a script, and two files in
`Editor.Infrastructure` and the client — **not one line of `Crdt.Core` or of
`Crdt.Core.Tests`, which are the only things Stryker mutates or runs.** CI's
score went 88.89% to 89.27%, because it timed out eight more mutants than the
run before. A gate that fails on a diff it cannot possibly be measuring is not a
gate, it is a coin toss with a good reputation.

**So the ratchet stopped keying on the score.** `mutation-floor.json` now lists
*which* mutants are known to go undetected, by file, line, column and mutator,
and the build fails when a mutant appears outside that list. Coverage erosion is
"something stopped being caught", and that is what the list measures directly:

- A mutant flipping between `Killed` and `Timeout` never appears in the list at
  all, so the runner's speed cannot move the check.
- The list is the **union** of what has been observed undetected, so a fast
  machine surfacing a mutant a slow one timed out is already accounted for —
  every machine's undetected set is a subset of it.
- Entries that *were* detected this run are reported, never failed. The script
  cannot tell "a new test kills it" from "this runner timed it out", so it hands
  the judgement over instead of guessing. Cleanup is a deliberate act.
- The failure message names the mutants rather than a percentage, which is the
  practical difference: "three mutants at `Replica.cs:317-319` stopped being
  caught" is actionable where "86.97%, was 88.89%" is a starting point for an
  investigation.

The score is still computed and printed. It is a useful number to watch and a
bad thing to gate on, and this section is the record of learning that the
expensive way — twice. The first version compared it across machines and flapped;
the second pinned it to CI and still moved with the runner. Stryker's own 85%
break threshold stays as an absolute backstop underneath all of it.

**The rule that survives all of this:** if the timeout count climbs, the score is
measuring the clock. Running the §13.10 scale cases at full size once read 89.66%
with thirty timeouts, five of them mutants that had survived moments earlier —
the score rose while nothing new was caught. `scripts/mutation.sh` bounds
document size under mutation (`CRDT_SCALE_ELEMENTS`) for that reason, and it is
still worth doing even though it turned out not to be the cross-machine cause.

**Demonstrated, not assumed.** Deleting one of the three `Import` tests still
clears Stryker's own 85% break threshold — 86.97%, so Stryker exits 0 and the
build would have passed. The ratchet fails it, and says why:

```
Coverage eroded: 3 mutant(s) went undetected that are not known:
    Replica.cs:317:17:Statement mutation
    Replica.cs:318:21:String mutation
    Replica.cs:319:23:String mutation
```

Those three are the `Import` guard against a snapshot naming a parent it does not
contain. Naming them is the practical gain over a percentage: the message is the
diagnosis rather than the start of one.

`scripts/mutation.sh` keeps both guards — no tests found, and nothing killed —
permanently, alongside the ratchet. They are what caught the false 0.00%, and a
gate that cannot fail loudly is not a gate.

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

### 13.9 What the 100k snapshot metric measured, and the two bugs it found

§6 required Phase 2 to build a 100k-element document, snapshot it, and report
size and load time with no threshold. The number is now measured rather than
assumed.

Size is deterministic: **22,277,866 bytes — 21.25 MiB, 222.8 bytes per live
element.** Timings vary with machine load, so they are given as observed ranges
over four runs of the same test on one machine, Release build, Postgres 16 on
loopback:

| stage | observed |
| --- | --- |
| serialise | 467–591 ms |
| deserialise (cold) | 733–924 ms |
| write to Postgres | 483–730 ms |
| load from Postgres | **501–941 ms** |

**Load is at or over §8's 500 ms target on every sample, and that is the easy
case.** The document has no tombstones and no operations after the snapshot;
§8's stated case adds 500k tombstones, which the encoding stores in full. The
cost is dominated by JSON parsing, not by the database — the row read is a
single indexed lookup, and standalone deserialisation of the same string takes
longer than the whole load does once the parser is warm.

This does not by itself decide the format. It bounds the decision: normalised
JSON as specified in §6 will not reach §8's target at §8's document size, so
either the target moves, the snapshot stops being the whole document (a chunked
or incremental encoding), or the encoding changes. Deciding is cheap now and
expensive after Phase 4 binds the format into the client's IndexedDB schema,
which is why §6 asked for the number in Phase 2.

**Resolved: the encoding changes.** §6 now splits the roles — JSON stays
normative, binary becomes the storage and wire form. Three facts decided it.

*The overhead is structural, not incidental.* An element carries an id, a
value, a parent, a side, an optional right origin, and a deleted flag: on the
order of 50 bytes of actual information. JSON spends 222.8. The gap is field
names repeated per element, 16-byte replica ids written out per reference,
64-bit counters as decimal strings, and punctuation — none of which a tokeniser
can skip, which is why parsing dominates the load time rather than the database
does.

*The stress case is four times worse than the case measured.* §8 asks for 100k
live characters **and 500k tombstones**, and a tombstone is a full element in
this encoding — it must be, because it can still be named as a `RightOrigin`.
Six hundred thousand elements at 222.8 bytes is roughly 130 MiB to move before
it reaches a browser.

*Those tombstones are realistic, not pessimistic.* Phase 1 established that
causal stability alone does not license collecting a tombstone, precisely
because a `RightOrigin` can name one (§5, and the collection rule's four
conditions). Accumulation is the expected behaviour of a correct implementation,
not evidence of a broken one.

The savings are structural too, and that is the reason to expect them to hold:
interning replica ids into a per-snapshot table replaces a 16-byte value with a
small index at every reference, varints replace decimal strings, and sequential
typing — the most common editing pattern there is — produces long runs of
consecutive `(replica, seq)` along a parent chain, which the run form collapses.

**Measured, after the codecs existed.** Three documents, the same machine as
above:

| Document | JSON | Binary | Ratio |
|---|---|---|---|
| chain — 100k, one replica typing | 22,277,866 B (222.78/el) | 112,541 B (**1.13**/el) | 198× |
| fragmented — 100k, four replicas, explicit right origins, 75% deleted | 23,379,876 B (233.80/el) | 845,359 B (**8.45**/el) | 27.7× |
| **§8's case** — 600k: 100k live + 500k tombstones | 141,163,181 B (235.27/el) | 5,512,023 B (**9.19**/el) | 25.6× |

The chain came out at 1.13 bytes per element, which is what the layout's own
arithmetic predicted before a codec existed. The fragmented figure beat the
predicted 16.00 worst case, because even a run-hostile document still chains
most parents to the previous element.

**§8's case is 141 MiB of JSON and 5.3 MiB of binary.** That is the number the
decision was about, and it is settled.

**What is not settled: the load target is still missed, for a different
reason.** §8's document loads server-side in **1.3–2.1 s** against a 500 ms
target. Splitting the cost says why:

| | parse (bytes → elements) | place (elements → tree) |
|---|---|---|
| §8's 600k case | 540 ms | **1,164 ms** |

Parsing 5.3 MiB is no longer the problem — the equivalent JSON parse was
4,613 ms. **Placement is**, and no encoding change touches it: `Import` replays
the §5 sibling ordering for every element, deliberately, so that a snapshot
written wrongly builds a different tree rather than restoring a corrupt one
(§13.9's `Import` note). Six hundred thousand replays is simply a lot of work.

This is recorded, not fixed, and it is not Phase 2.5's to fix: §8's targets are
load-test targets and Phase 7 owns them. What Phase 2.5 owed was the format
decision and the number, and both are now here.

**The two options are written out now so that the decision arrives with them
already on the table.** Both change what a snapshot *means*, not how it is
spelled, which is why neither belongs beside a codec swap.

*Option A — a snapshot stores placement results rather than replaying them.*
Today `Import` re-derives every element's position from the §5 sibling rule, so a
snapshot is a set of claims that the reader checks. Under A it becomes a
description of a tree the reader trusts: sibling order is stored, and loading is
a linear rebuild instead of 600,000 comparisons.

> What it costs: the guarantee that a wrongly written snapshot produces a
> different tree rather than a quietly corrupt one. That guarantee is the whole
> reason `Import` replays placement (§13.9 above), so trading it away needs
> something in its place — a checksum over the stored order, a periodic audit
> that re-derives and compares, or restricting trust to snapshots this replica
> wrote itself, where the writer and reader share a version. The last is the
> narrowest and probably the right one: a snapshot arriving from elsewhere is
> exactly the case the replay defends against.

*Option B — a snapshot stops being the whole document.* Load the visible text
plus the elements needed to place incoming operations, and fetch the rest lazily
or in chunks. §8's case is 100k live characters among 600k elements, so five
sixths of the work is tombstones nobody is about to read.

> What it costs: a replica that has not loaded everything cannot answer every
> question about the document, and §5's placement rule can reach any element —
> a `RightOrigin` may name a tombstone in an unloaded chunk. That makes
> chunking a change to the algorithm's preconditions, not just to I/O, and it
> interacts with GC (§5) which is the other mechanism for making tombstones stop
> costing anything.

They are not exclusive. A does more for the common case and B does more for §8's
stress case, and the honest reading of the measurement is that A alone probably
reaches the target while B is what makes the target insensitive to how long a
document has been alive.

**And the browser is worse, as expected.** §8's second number, from the
`browser-metrics` CI job on the `ubuntu-latest` runner §8 names — the §9
TypeScript core in **headless Chromium 151.0.7922.34**, 4 vCPU, cold meaning a
fresh context with empty IndexedDB:

| Case | fetch | parse | place | text | **cold total** | warm total |
|---|---|---|---|---|---|---|
| chain, 100k | 4 ms | 38 ms | 236 ms | 19 ms | **302 ms** | 297 ms |
| §8's 600k case | 38 ms | 493 ms | 1,683 ms | 291 ms | **2,508 ms** | 2,045 ms |

Two and a half seconds for §8's document, on a fast network with a 5.3 MiB
payload. Reading the same bytes back from IndexedDB saves almost nothing,
because the fetch was never the cost — which is the useful part: a warm cache
does not rescue this, and neither will a faster network.

**The hardware matters enough to be part of the number, which is why §8 names
it.** The same commit measured **5,964 ms** for the 600k case on the 4 vCPU
development container this was written in — more than twice CI's figure on a
nominally identical core count. Quote the CI figure; treat a local run as the
shape of the answer rather than the answer.

**The same term dominates on both sides.** Placement is 1,683 ms of the
browser's 2,508 and 1,164 ms of the server's ~1,700, in two independently
written implementations. That is not two performance bugs; it is the cost of
replaying §5's placement rule 600,000 times, and it is the thing to fix when
§8's targets are addressed. The encoding was worth changing — 141 MiB became
5.3 MiB, JSON parsing fell from 4.6 s to 0.5 s server-side, and the browser's
parse is 493 ms of a 2,508 ms load — and it was not the whole problem.

**The metric also found two defects that no correctness test had reached.**
Both were only visible at this size, which is the argument for measuring at
realistic scale rather than at test scale:

1. **Traversal overflowed the stack in both implementations.** In-order
   traversal of the element tree was recursive, and a document typed left to
   right is a chain of right children 100k deep — so the recursion depth equals
   the document length. It crashed the process rather than throwing, in C# and
   in TypeScript alike, and every existing test was small enough to miss it.
   Both are now iterative with an explicit stack, with a 150k-element regression
   test on each side.
2. **`Import` was quadratic.** Each placement pass removed placed elements from
   the unplaced list one at a time; rebuilding the list per pass instead took
   the .NET suite from 1m37s to 16s.

Neither is a specification change. They are recorded because the conclusion is:
the correctness suite verifies the algorithm at sizes where a linear-space bug
and a quadratic-time bug are both invisible, and only a test that builds a
realistic document exposed them.

### 13.10 The generator explored shape exhaustively and scale not at all

The stack overflow in §13.9 is the most instructive failure in the project so
far, and the instructive part is not the bug. It is that eight invariants at
10,000 randomised cases each, a nine-trace cross-implementation corpus, and an
87% mutation score all passed over it.

**Depth equals document length on left-to-right typing** — each character is a
right child of the previous one — so a recursive traversal overflows at a
document length users reach in an afternoon. That is the single most common
thing anyone does to a text editor.

It survived because every generated scenario built a few dozen elements. The
magnitudes were literals in the generator: runs of two to four characters, at
most five edits a round, a prefix of at most three. Nothing in the suite was
wrong; the dimension simply was not there.

Worse, randomisation works *against* finding it. A generator that picks
positions uniformly produces balanced trees. The pathological shape is the
degenerate one — a single chain — and that is precisely what uniform random
insertion does not produce. More cases would never have found this. Only a
different dimension would.

**Scale is now drawn explicitly** (`ScenarioScale`), reported on every scenario,
and printed in failure output beside the seed. Large scenarios are rare on
purpose — roughly one seed in 250, about forty across the 10,000-case gate,
reaching around 2,000 elements — because their value is in being reached at all,
and because typing costs O(n²) in this implementation. `ScaleTests` carries the
few very large cases: 10,000 characters through real typing, and 50,000 and
150,000 through the import path that Phase 2 proved equivalent to typing.

The shrinker gained a size phase for the same reason. Delta debugging a
two-thousand-step scenario needs O(n log n) replays of an O(n²) simulation and
never finishes; truncating to a prefix costs O(log n) replays and answers "was
this about the tail?" immediately. A shrinker that hangs turns a reproducible
failure into an unreadable one, so the shape phase also runs against a replay
budget.

**One asymmetry is worth knowing.** On .NET a stack overflow cannot be caught:
reintroducing the recursive traversal kills the test host rather than failing an
assertion. That is still a red build and it is the only signal the platform
offers. The TypeScript side throws a catchable `RangeError`, and its regression
test asserts on it directly — which is how that fix was verified.

**The general form, for Phase 3 onward:** a property suite that passes at every
size it tests is evidence about those sizes only. Where cost is superlinear in
something — document length, connection count, fan-out width — the suite must
name that quantity as a dimension and draw it, because the failure mode is
correct at every tested size and fatal at real ones, and nothing in a green
build distinguishes the two.

### 13.11 Writing the spec first put the same bug in both implementations

§6's binary layout was written before either codec, on the reasoning that two
implementations derived from one description disagree loudly when either is
wrong, while a second derived from the first inherits its mistakes silently.
That reasoning is sound and the approach is kept. It has a failure mode worth
naming.

**A mistake in the description reaches both implementations, and they agree.**
The canonical-form rule for run maximality was drafted as: *the first element of
any record must not be able to continue the element immediately before it*. Both
codecs implemented exactly that, agreed byte for byte on the whole corpus, and
were both wrong.

The rule needs a condition on the *earlier* element too. An element carrying an
explicit right origin can neither start a run nor sit inside one, so whatever
follows it begins a new record however well it would otherwise continue. Without
that half, a decoder rejects documents its own encoder produces: encode an
element with an explicit right origin followed by a right child of it with the
next sequence number, and the encoder correctly writes two records while the
decoder correctly-by-the-draft calls them non-canonical.

**How it was found is the point.** Not by the corpus, which passed. By
deliberately breaking one implementation to check the cross-implementation
comparison would notice — the first sabotage chosen was dropping exactly this
guard, and the corpus did *not* notice, which meant the shape was uncovered.
Investigating why produced the real bug. The check works: a second sabotage,
dropping the deleted flag, failed the build immediately.

Two things follow, both now in place.

1. **The shape has a test on each side**, not a trace. Ordinary typing cannot
   reach it — a right origin records what followed at insert time and tombstones
   keep it there — so it needs either garbage collection (§5) or a directly built
   snapshot. A user-level trace cannot express it, which is why the corpus was
   silent.
2. **Agreement between two implementations is not evidence of correctness when
   both were written from one description.** It is evidence they read it the same
   way. The corpus catches divergence; only reasoning about the specification,
   or an independent check like the round trips against the normative JSON,
   catches a shared misreading. This is the same lesson as §13.6, where a
   100.00% hold rate turned out to measure the generator, arriving from the
   other direction.

**The practice this produced immediately found a second bug of the same class.**
§12 now requires hand-written fixtures for every canonical form — documents the
specification says are valid that neither implementation generates. Writing the
first batch turned up that a run's deleted bitmap had **unconstrained bits past
its last element**: a run of five occupies five bits of one byte, §6 said nothing
about the other three, and both codecs ignored them on read. Two byte strings
differing in those bits decoded to the same document, which is precisely the
canonical-form violation the whole rule exists to prevent — and it makes
`binary → JSON → binary` byte-identity a check of whichever spelling the writer
chose. §6 now requires them to be zero and both readers reject a non-zero one.

The fixtures caught something else, less serious and more instructive: the first
draft of the two-byte-replica-index fixture used a 130-entry table over a
one-element document, which §6's canonical form forbids — the table holds exactly
the replicas the body names. The *re-encode* half of the fixture check caught it,
which is the argument for that half existing. A fixture that is merely accepted
proves the decoder is permissive; a fixture that is accepted and re-encodes to
the same bytes proves it agrees with the encoder about what the document is.

### 13.12 Phase 3 decisions taken before the hub existed

Four things §7 left open that had to be settled before any of it could be
written, recorded here rather than discovered in the diff.

**The client does not choose its replica id.** §7 already said an operation's
replica id must match the connection's binding, and §5 already made `ReplicaId`
the tie-break that orders concurrent insertions. Together those look like a
complete defence and are not: if the client supplies the binding, the check
compares a value against itself. A client naming another live replica's id would
pass authentication, pass the per-operation comparison, and author operations
attributed to that replica — and every other replica would converge on the
forgery, because convergence is exactly what the algorithm guarantees. The
server assigns the id at `negotiate` and records it against the user in
`document_replicas`; §7 now says so.

**The ticket is redeemed with `GETDEL`, not a read then a delete.** §8 forbids
sticky sessions, so the issuing instance is usually not the redeeming one and the
ticket has to be shared state. Under a read-then-delete, two connects arriving
together both observe the ticket and both proceed: single-use that is not atomic
is not single-use. This is the kind of thing that passes every test written
against one client.

**Dense `Seq` validation caches, it does not own.** The next expected value is
the maximum stored for that `(document_id, replica_id)` in Postgres. An in-memory
copy makes the hot path affordable (§8 forbids loading the document to validate
an operation), and losing it on failover must cost a query rather than
correctness.

**A hub method has no HTTP status.** §7's 404-versus-403 rule is about not
leaking document existence, and it survives the move to SignalR only if the hub
carries the same distinction in its error codes. Answering `forbidden` for a
document the caller cannot see would leak exactly what the 404 rule protects.

None of these are changes of direction. They are the places where §7's rules,
read literally, could each be satisfied by an implementation that defeated their
purpose.

### 13.13 A rejection the rejected party cannot observe is not a rejection

Phase 3 refused an unauthenticated hub connection in `OnConnectedAsync`, first
with `Context.Abort()` and then by throwing. Both are the documented way to
refuse a SignalR connection. Neither is observable to the client: SignalR
completes its handshake *before* invoking the hub, so `StartAsync` has already
returned success by the time the connection is torn down. Every rejected client
believed it was connected, and what it saw afterwards — a connection closing
shortly after opening — is indistinguishable from a network blip.

The server was correct throughout. It redeemed no ticket, established no
binding, and would have refused every subsequent call. A server-side test suite
asserting on server state would have passed, and did.

**The general form: a rejection that is not observable to the rejected party is
not a rejection, and no amount of server-side testing can detect that class of
defect.** The server's own view is identical in both worlds. What separates them
lives entirely in what the *client* can observe, so the test has to be written
from the client's side and has to assert on what the client is told — not on
what the server recorded.

This is a security finding rather than a usability one, and the reason is the
direction the failure runs in. A client that cannot tell refusal from a blip
retries, and a retrying client is indistinguishable from an attacker probing;
worse, a client that believes it is connected will surface a working editor to
someone holding no valid credential, and only fail when they type. The failure
is deferred to the moment of most confusion and attributed to the wrong cause.

The fix was to move the observable part of the refusal to where the client can
see it — SignalR's own negotiate request, before a transport exists, answering
401 — while leaving the authoritative single-use redemption in the hub. Note the
shape: the check that *enforces* and the check the client can *see* ended up in
two different places, and both are needed. An enforcement point is not
automatically a signalling point.

**It applies again in 3b and Phase 4.** Every rejection either phase adds is
subject to it: a batch dropped for exceeding a pending-set bound, a client
refused during a scale-out failover, an offline client whose queued operations
are refused on reconnect. In each case the question is not "did the server
refuse" but "can the client tell it was refused, and tell it apart from a
network failure". The second is what needs the test.

### 13.13a MessagePack for framing, and the measurement that decides it

§7 caps a hub message at 64 KB. Phase 3 found that the default JSON hub protocol
base64-encodes a `byte[]` argument, so 64 KB of message admits roughly 47 KB of
operations and every keystroke batch pays a third of itself in encoding overhead.

Two fixes were on the table. **Raise the cap to ~88 KB** so 64 KB of payload fits
inside base64: no protocol change, no client work, no new dependency, and 33% more
bandwidth forever. Or **switch the hub protocol to MessagePack**, which carries a
byte string without inflating it.

MessagePack is chosen, on a narrower argument than "binary is better". The hub
payload is already a single opaque `byte[]` holding §6's format, so MessagePack is
used for **framing only** — its object model is not used, and §6 stays the sole
authoritative encoding (see §6, *The hub protocol carries opaque bytes*). The
alternative reading, where operations become MessagePack objects, would create a
second encoding with its own canonical-form rules; §13.11 is what happens then.

**The decision is contingent on two measurements, not one.** Wire bytes are the
obvious one. The second is **client bundle size** with
`@microsoft/signalr-protocol-msgpack`: bandwidth saved per keystroke is paid for
once per page load, and on the slow connection this project keeps citing, a
materially larger bundle is a real cost in the same currency. Both figures get
reported, and the choice is made against both. If the bundle cost turns out to
dominate, raising the cap is the honest answer and this entry gets amended rather
than quietly ignored.

The measurement itself carries the trap: **the inflation lives in the frame, not
in the payload.** Measuring `byte[]` length would show the two protocols as
identical and decide this wrongly, with a plausible number and nothing about the
result looking wrong afterwards. What gets measured is the framed message on the
wire, via `IHubProtocol.WriteMessage`, which is exactly what the connection
sends minus transport framing that is identical for both.

**The numbers.** Framed hub message, bytes:

| document | payload | JSON frame | MessagePack frame | saved |
|---|---|---|---|---|
| one keystroke | 30 | 209 | 161 | 23.0% |
| keystroke batch (16) | 61 | 253 | 192 | 24.1% |
| paste at the run cap (256) | 542 | 893 | 674 | 24.5% |
| 256 separate inserts, no run | 1,500 | 2,169 | 1,632 | 24.8% |
| a batch near the cap | 121,772 | 162,533 | 121,907 | 25.0% |

Base64 adds a third to the payload, so the saving relative to the JSON frame is
a quarter — 0.33/1.33 — approached from below as the fixed frame overhead
(method name, document id, replica id) is amortised.

Under §7's 64 KB message cap, **JSON admits 49,023 payload bytes and MessagePack
65,403**. Phase 3's "about 47 KB" was an estimate; 49,023 is the measurement.

Client bundle, gzipped bytes:

| bundle | minified | gzipped | delta |
|---|---|---|---|
| CRDT core alone | 8,399 | 3,135 | — |
| core + SignalR | 64,167 | 17,317 | +14,182 |
| core + SignalR + MessagePack | 94,192 | 25,390 | +22,255 |

**The MessagePack protocol costs 8,073 gzipped bytes, 46.6% on top of the
SignalR client.** That is not trivial and it is the honest case against this
decision.

**The call, against both figures: MessagePack.** Three reasons, in order of
weight.

The bundle cost is paid once per page load and is cacheable; the wire saving
accrues per message and is not. At 253 versus 192 bytes for a keystroke batch,
8 KB of bundle is repaid after roughly 130 batches — a few minutes of typing —
and every batch after that is profit.

Second, and this is what actually decides it: on the slow connection the bundle
argument is about, **the document dominates the bundle by three orders of
magnitude.** §8's case is 5.3 MiB of binary snapshot (§13.9). Against that,
8 KB is noise, and optimising it while shipping 5.3 MiB would be a strange place
to economise.

Third, the cap becomes honest. A "64 KB message limit" that admits 49 KB of
operations is a number that will mislead whoever next reasons about batch sizing;
under MessagePack the cap means what it says.

**JSON is withdrawn from the hub's supported protocols, not merely deprioritised.**
Supporting both would let a client negotiate JSON and silently take the worse
wire and the smaller effective cap — a downgrade nobody would observe. A client
that cannot speak MessagePack now fails to connect, and §13.13 is the reason a
loud failure is the better one. A test asserts the refusal rather than assuming
it.

### 13.14 Test a bound where it is the only guarantee

§7 bounds role-cache staleness at five seconds and gets there two ways: eager
pub/sub invalidation makes the usual case immediate, and a five-second TTL is
the fallback. Two tests were written — one revoking through the writer, one
deleting the membership row behind the writer's back so no invalidation is ever
published.

Only the second found the bug, and the bug was worth finding: a local cache
entry refreshed from a Redis hit took a fresh five seconds regardless of how
little was left on the shared entry it read, so an expiry landing near the
boundary restarted the clock. Worst case was very nearly ten seconds — double
§7's bound. The eager-invalidation test stayed green throughout, because eager
invalidation was working perfectly.

**The rule: where a guarantee has a fast path and a fallback, the bound must be
tested with the fast path disabled.** Testing it with both running measures the
fast path and says nothing about the bound, and the fast path is precisely the
thing that is unavailable in the situations the bound exists for — a lost
message, an instance partitioned from Redis, a row changed by an operator with
`psql`. A test that only ever exercises the happy path is measuring the
optimisation and reporting it as the guarantee.

This generalises past caches. Anywhere this system has a fast path and a
correctness floor — causal delivery's buffer against its resend, a reconnecting
client's delta against a full snapshot — the floor gets its own test with the
fast path switched off.

### 13.15 A mechanism whose absence still converges must be asserted directly

**Wherever removing a mechanism leaves the system still producing the right
answer, that mechanism has to be observable and asserted on its own terms. Never
inferred from the outcome.**

This is the third form of one discovery, and writing it once generally is
overdue.

- **§13.7, the mutation gate.** Stryker reported 0.00% across 227 mutants and
  exited zero while the same suite killed those mutations by hand. The suite's
  *outcome* — green — was identical whether or not the gate was measuring
  anything.
- **§13.11, the canonical-form bug.** Two implementations agreed byte for byte
  on every trace in the corpus. The agreement was real and the shared reading of
  §6 was wrong; only deliberately breaking one side showed that the comparison
  could not have noticed.
- **§5's duplicate counter.** Delete the dedupe entirely and every convergence
  test still passes, because the CRDT is idempotent and re-applying a duplicate
  is a no-op. The mechanism's whole purpose is to make a resend loop *visible*,
  and a resend loop is invisible in the outcome by construction.

The common shape: the observable result is the same on both sides of the change,
so no assertion phrased in terms of the result can distinguish them. Convergence
is the weakest of these — it holds under a large family of wrong
implementations, including several that do no work at all — which is exactly why
it is the assertion that feels most reassuring to write.

The rule in practice: when adding a mechanism, ask what test fails if it is
deleted. If the honest answer is "none, the system still gets the right answer",
then either the mechanism is unnecessary, or it exists for an operational reason
— speed, load, a signal for a human — and that reason is what has to be measured
and asserted. Counting the drops, timing the path, or asserting the call did not
happen. Not "and the document still matches".

This is what §12's vacuity rule is for at the level of a single mechanism, and
what the sabotage practice catches when the rule was not applied.

**Its next instance was this same counter** (§13.21). `DuplicatesDropped` was
asserted directly, in three suites — and not in the one the mutation gate
drives, so the gate saw a counter nothing asserted and said so the first time it
ran. Assert the mechanism directly, *in the suite that measures it*.

### 13.16 The server has no pending set, and a query that matched nothing

Two findings from 3b.4, related only in that the second was found while
implementing the first.

**The server rejects a non-ready operation rather than buffering one.** §5
describes a bounded pending set and justifies the bound by noting that origins
are client-supplied — an unbounded set is a denial-of-service vector. That
reasoning is sound for a *peer* receiving a broadcast. It does not transfer to
the ingest path, and following it literally would have added the vector it
warns about.

A client can only reference an element it knows about, and under this
architecture it knows about exactly two kinds. Its own earlier operations: §7's
density rule already guarantees the server holds them, because it refused
anything else. And other replicas' operations: it learned of those from a
broadcast, and §8 sends a broadcast only after the write commits. There is no
third kind and no race between them — an operation referencing something the
server does not have is a bug or an attack, never a legitimate ordering
accident.

So buffering one means holding an id that may never arrive, indexed by a key an
attacker chooses. Rejecting removes the vector instead of bounding it, and
leaves the server with no pending set to bound at all. §7's pending-set cap
therefore has no server-side subject; the bound lives on the client, where §5
puts it, and where the operations being buffered came from a source the client
does not control.

The bound is on the *connection*, not on the replica. A replica replaying a
stored trace or importing a snapshot legitimately buffers as much as the input
demands, and a core that refused would fail the property suite for a reason
having nothing to do with the property. `MaxPending` is unbounded by default and
set by whoever attaches a replica to a network. Exceeding it throws rather than
dropping the oldest: dropping would leave the replica permanently missing an
operation with nothing to indicate it, which is divergence arrived at quietly,
and quiet divergence is the one outcome this project exists to prevent.

**And the query that matched nothing.** Implementing the origin check meant
writing a second query against `document_ops`, which is when the first one was
read closely. §7's document-size cap has, since Phase 3, filtered on
`op_type = 'ins'` against a writer that stores `'insert'`. It matched no rows.
Ever.

Nothing went red. Every test filled a document through a single instance, where
the in-memory counter incremented by `Accepted` did the work, and the Postgres
seed of zero was never the number under test. §8 requires exactly the opposite
property — the cache must be reconstructible and must not be required for
correctness after a failover — and what was actually shipped was a cap that
reset to zero on every restart, so a document already over its limit would have
accepted writes on any cold instance.

This is §13.15 again, from the other direction. The mechanism's absence still
produced the right *outcome* in every test, because a second mechanism covered
for it. The test that catches it drops the cache and asserts the reconstructed
number, which is the only arrangement where the query is the thing being tested.
The `op_type` literals are now interpolated from the writer's own constants, so
the two cannot drift again.

### 13.17 The verification apparatus is not exempt

The 3b.6 sabotage run reported that the snapshot-floor test failed under two
sabotages that could not reach it — one removed a role check, the other a
negative-sequence guard, and neither is on the floor's path. Run alone, the
floor test passed five times out of five. The failure was not in the code and
not in the test. It was in the harness.

Each sabotage backed the file up with `cp`, patched it, built, ran, and restored
with `mv`. `mv` preserves the *backup's* modification time, so the restored
source came back older than the artefacts built from the sabotaged version.
MSBuild compared timestamps, concluded nothing had changed, and skipped the
recompile. Every run after the first sabotage executed the previous sabotage's
binary. The "clean" baseline in between was not clean.

The direction that showed up is the harmless one: a sabotage credited to the
wrong test, noticed because the attribution made no sense. The direction that
matters is the opposite one, and it is silent. Sabotage a check, build nothing,
watch the *previous* clean build pass, and record "sabotage caught by nothing —
the corpus does not reach this shape." That is a finding about the corpus, it
reads as a real result, and it is the exact conclusion §12 says to take
seriously. It would have certified a test that catches nothing, using the
practice whose whole purpose is to prevent that.

So the rule is general, and wider than one build system:

> **Every claim about a check's behaviour under sabotage is a claim about a
> specific binary. Establish that the binary is the one you think it is —
> restore in a way the build system can observe, and rebuild — or the result is
> about a state you are no longer in.**

Three earlier entries (§13.13, §13.15, §13.16) are all instances of a mechanism
that could not be observed failing. This is the same shape one level up: the
apparatus that observes the mechanisms was itself unobserved. Nothing checks the
checker, so the only defence is that its results have to *make sense* — an
attribution that cannot be explained by the code is a finding about the harness,
not a flake to re-run until it goes away. The tell here was that a sabotage and
the test it broke had no path between them; the temptation was to call the floor
test flaky, and running it in isolation appeared to confirm that.

### 13.18 A wait that is already satisfied is not a wait

3b.7's subscription test asserted that an instance keeps carrying a document
after one of its two connections leaves, and drops it after the second. Both
assertions were on a counter, each preceded by a poll:

```csharp
await one.DisposeAsync();
await WaitFor(() => Backplane(factory).Carrying == 1);
Assert.Equal(1, Backplane(factory).Carrying);
```

The sabotage that unsubscribes on the *first* departure — stranding every
remaining client on that instance — went straight through.

`Carrying` is already 1 when the client is disposed. Disconnect handling runs
server-side with nothing to await, so the poll's condition was true on entry, it
returned immediately, and the assertion ran before the mechanism had a chance to
do anything at all. The test asserted a state that had not yet changed and could
not yet be wrong. It would have passed against an instance that unsubscribes on
every departure, and it did.

Two rules come out of it.

**Wait on a transition, not on a state.** A poll for a condition that already
holds is a no-op with the shape of synchronisation. The second half of the same
test — waiting for `Carrying == 0` after the last client leaves — is sound for
exactly this reason: it starts false and has to become true. Where the value
under test does not change, find one that does; 3b.7 waits on the connection
count going from two to one, which is a transition the disconnect must complete
before the assertion means anything.

**Prefer the functional assertion to the counter.** The counter was there to
make the mechanism observable, which is right (§13.15), but "still subscribed"
is a proxy. What the rule protects is that the remaining client keeps receiving,
so the test now has a second instance publish and the remaining client receive
it. That fails under the sabotage without depending on any timing at all, and it
states the property in the terms someone would actually complain about.

This is the second time sabotage has caught a *test* rather than the code, and
both times the test was aimed at the right subject and asserted something that
was true regardless. That remains the most common way a test passes for no
reason, and reading it is not how it gets found.

### 13.19 The sentinel matched the syntax, not the property

§7 forbids turning a token check off anywhere, and `TokenValidationTests` has
enforced that since Phase 3 by scanning every `.cs` and `.json` file in the
repository for one of the named switches assigned `false`. It has been treated
as covering the rule.

It covers one spelling of it. Sabotaging the interop suite meant weakening
signature validation, and the way to do that is not to write `false` anywhere:

```csharp
SignatureValidator = (token, _) => new JsonWebToken(token),
```

`ValidateIssuerSigningKey` stays `true`, `RequireSignedTokens` stays `true`, the
options test still passes, and the scanner sees nothing — because the check has
been *replaced* rather than switched off. The same shape exists for
`IssuerValidator`, `AudienceValidator`, `LifetimeValidator` and the rest: each
is a framework extension point that runs instead of the check it is named for.

The scanner now flags an assignment to any of them. None is assigned anywhere
today, so it is a ratchet rather than a cleanup, and if one is ever genuinely
needed the argument belongs in §7 before the code.

The general point is the one worth keeping. **A guard written as a pattern match
covers the instances of the pattern, not the property.** It is easy to read such
a guard as enforcing the rule, because the rule is what its name says; what it
actually enforces is "nobody wrote it that way". This one had gone unexamined
for a phase and a half because it had never been the target: the sabotages that
found things were aimed at the code the check protects, not at the check itself.

**Every textual guard in this repository has the same weakness.** The
architecture test looks for project references; a reflection-loaded assembly is
not one. The secret scan looks for secret-shaped strings; a credential assembled
from parts at runtime is not one. The redaction sentinel looks for a sentinel
value; a field that reaches the log by a path the sentinel never travels is not
one. Each checks an instance and is read as checking the property.

**Scheduled: a guard audit pass**, asking of each check in turn — the
architecture test, the secret scan, the redaction sentinel, the mutation
ratchet, the workflow check, this one — *what is the `SignatureValidator`
equivalent here? What defeats this without matching its pattern?* It is a
distinct piece of work rather than a note, because the answer for each guard is
specific and reading the guard is not how the answer is found.

Which is the strongest justification the sabotage practice has yet had.
**Sabotage is the only technique in this project that tests a property rather
than a pattern.** Every other check — the scanners above, the type system, the
tests themselves — asserts something written down in advance, and therefore
asserts the form it was written in. Sabotage asks the different question: given
an implementation that is actually wrong, does anything go red? It is the only
one that can discover the gap between "the guard matches" and "the guard holds",
because it approaches from outside the guard's own vocabulary.

**The same shape, in the process rather than the code.** Twice in Phase 4 a
commit went out with a client gate red, because the verification was chained
onto the same command line as the commit and its output read afterwards. The
habit was the defect, and the structural fix was to put the client's gates in
the preflight — but the diagnosis is the part worth keeping. The preflight ran
the .NET suite, conformance, interop, the mutation gate and the workflow check,
and never ran the client's lint, typecheck, unit tests or build. **The one area
of the repository where these mistakes were being made was the one area the
preflight could not see.**

That is this entry's shape exactly, one level up: the guard covered a set of
instances and was read as covering the property "the gates are green". Ask of a
process guard what this entry asks of a textual one — not "does it check
something" but "what passes it without being correct" — and the answer here was
available before the fact: everything the guard does not look at.

### 13.20 Seven tasks reported against a workflow that never ran

Editing `ci.yml` during 3b.1 inserted a step above a trailing
`working-directory:` line that belonged to the step before it, leaving two
`working-directory` keys on one step. GitHub Actions rejects a duplicate mapping
key and fails the run before scheduling anything.

Every push from 3b.1 through 3b.8 was a startup failure. Eight jobs — the .NET
suite, cross-implementation conformance, the mutation gate, the secret scan —
did not run at all for seven consecutive tasks, each of which was reported as
complete.

Nothing about that is visible without going and looking. A startup failure has
no failing step, no log, and no annotation; the run list shows a red mark like
any other. Its one tell is cosmetic: with the file unparseable the run cannot
read the workflow's `name:`, so it is listed by path instead — `.github/workflows/ci.yml`
where every green run above it says `CI`. And locally nothing goes red, because
no local tool reads this file. Even a YAML parser is no help: PyYAML, and every
other library in common use, accepts duplicate keys silently with the last value
winning. "It parses" was true and meant nothing.

Two things follow, and only the second is new.

**§12's preflight already covers this and was not run.** It requires a status
file naming the pushed commit and refuses one that lists no jobs — which is
exactly what these runs produced. The rule existed, was written after the same
class of failure in Phase 2.5 (§12), and was skipped for seven tasks because
each one ended in a local green suite that felt like enough. A gate that is only
consulted when someone remembers is not a gate; the preflight is now run at
every task boundary rather than at the phase report.

**And the failure should never have needed a remote check to find.** A workflow
file that the CI provider will reject is a local defect in a local file, and
`scripts/check-workflows.sh` now fails on it before the push — duplicate keys
specifically, plus a missing `name`, since an unnamed workflow is listed by path
and therefore looks exactly like a startup failure in the one place the
difference shows. It runs first among the preflight's local gates, because a
green job table is meaningless if the run that produced it executed nothing.

The wider point is the one §13.17 made about the sabotage harness, arriving from
the other direction: **the machinery that reports on the work is not covered by
the work's own tests.** A test suite says nothing about whether CI ran it. Both
findings are the same shape — an apparatus trusted because its output looked
normal — and in both cases the output was normal precisely because nothing had
happened.

### 13.21 A ratchet keyed on line numbers is not a ratchet

With the workflow fixed (§13.20), the mutation gate ran for the first time since
3b.1 and reported 34 newly-undetected mutants — apparent coverage collapse
across `Replica.cs`.

Thirty of them were the same mutants as before, moved. The floor keyed each
entry as `file:line:column:mutator`, and 3b.4 added 54 lines near the top of
`Replica.cs`, so every known entry below the insertion arrived at a new address
and read as new.

That direction is only noise, and its danger is what the noise invites: the
obvious response is to paste the new list into the floor, which is how a
ratchet becomes a rubber stamp. The direction that actually matters is the
silent one. After a shift, a genuinely new undetected mutant that lands on a
line number a moved entry used to occupy is absorbed as already-known. The gate
stays green while coverage falls, which is the exact failure the floor exists to
prevent, caused by the floor's own key.

The key is now the mutated source line's **text**, with the mutator and its
replacement: `file:mutator:replacement:line text`. It changes when the code
changes and not before, which is when re-review is wanted. Entries are counted
rather than set-membership, because two mutants of the same shape on identical
lines are two gaps and collapsing them would let one become covered while the
other quietly took its place.

**And under the noise there was a real finding**, which is the argument against
re-baselining without reading. Four mutants were genuinely new, and two of them
were `DuplicatesDropped++` — deleted, and turned into a decrement. §13.15 was
written about that very counter: a mechanism whose absence still converges has
to be asserted directly. It *was* asserted — in `Editor.Api.Tests` and in the
TypeScript suite, but not in `Crdt.Core.Tests`, which is the only suite the
mutation gate drives. The watermark-path increment had no assertion where it is
measured, and the gate said so the first time it was allowed to run.

Worth reading with §13.15 rather than beside it: that entry named a class of
defect, and this is that class producing its next instance, in the very
mechanism the entry was written about. "Assert the mechanism directly" turns out
to carry an unstated second half — *in the suite that measures it*. A counter
asserted in three suites and unasserted in the one the gate drives is, to the
gate, a counter nothing asserts.

The general rule: **a ratchet's key has to be as stable as the property it
tracks.** A key that moves for reasons unrelated to the property produces false
alarms, which train the reader to clear them in bulk, and false silence, which
is unobservable. Position is the most tempting such key and the least stable
one.

### 13.22 A done-when the phase can satisfy while its deliverable does not exist

Phase 4's done-when was met in full and Phase 4's deliverable did not exist.
Offline edit, reconnection, convergence on §9 normalised state, resumption
across a reload, a store version rejected rather than guessed — all verified
against a real server over a real socket. And `App.tsx` still rendered *"No
editor yet — see PROJECT_SPEC.md §11, Phase 4"*. Every part of a React editor
was written and tested; nothing composed them into something a person could
open.

Neither half was wrong on its own. The criterion tested the properties that are
hard to get right, which is what a criterion should do. The deliverable column
said "React client". What was missing is that satisfying the first does not
produce the second, and nothing in the table says it must.

**This is the project's recurring shape, applied to the contract instead of to a
test.** §13.15: a mechanism whose absence still converges. §13.19: a sentinel
matching the syntax rather than the property. §13.21: a ratchet keyed on
position rather than on the property. Each time, the check was adjacent to the
thing that mattered and was read as covering it. A done-when is a test of a
phase, and it fails the same way.

**The audit this triggered**, over the phases not yet built:

- **Phase 5** — no gap. "1,000 generated traces match across both
  implementations; runner fuzzes in CI" is the deliverable, stated as a
  behaviour.
- **Phase 6** — the same gap. "Every requirement in §7 has a passing test" is
  satisfiable entirely against a test host, while the application as actually
  started ships without the header, the cap or the TLS requirement the test
  proved. The criterion now names the configuration under test.
- **Phase 7** — the same gap, in its plainest form. "Dashboards exist" is
  satisfied by a JSON file nobody has ever read. Existence is not observability;
  the criterion now requires a named failure to be diagnosable from them.

The general rule: **a done-when must be unsatisfiable while the deliverable is
absent.** Write it so that the only way to make it true is to build the thing —
and when a criterion tests a property *of* the deliverable rather than its
existence, say so and add the clause that requires the deliverable itself. The
question to ask of every criterion in the table is the §13.19 question in
another costume: what makes this true without making the deliverable real?

### 13.23 A harness that cannot explain its own failure

The interop job failed once, on 4.7, with the whole of its evidence being:

```
API did not start:
warn: Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager[35]
      No XML encryptor configured.
```

No elapsed time, no exit status, no address, no indication whether the process
had crashed, was still starting, or was listening and refusing. It did not
reproduce on the next two heads, and nothing between 4.5 and 4.7 touched the
server — so the honest conclusion is that nothing was learned.

The harness had also chosen its port by `5000 + random(3000)` and hoped. A lost
guess and a genuine startup bug produce the identical sentence, which means the
one occurrence that matters is indistinguishable from the many that do not.

Both are now fixed at the source rather than at the symptom: the port is bound
as 0 and read back from Kestrel's own announcement, so there is no guess to
lose; and the failure carries how long it waited, what the process is doing or
exited with, the address it polled, and — stated explicitly when it happens —
that the process logged nothing at all, which is itself a finding.

The rule: **a harness that cannot explain its own failure costs more than the
failure it reports.** An unexplained failure is filed as flakiness, and once a
job has been filed as flaky, its next real failure is filed the same way. The
diagnosability of a check is part of the check, not a convenience for whoever
reads it — and a retry, a longer timeout or a wider deadline treats the symptom
while leaving the next occurrence exactly as illegible as this one.

### 13.24 The running count: tests that pass for the wrong reason

The dominant defect class in this project is not a wrong implementation. It is a
**check that cannot fail** — a test whose assertion is true whether or not the
code is correct, a guard that matches a pattern instead of a property, a
criterion satisfiable without the thing it names. This entry keeps the count,
because the frequency is the argument. Anyone asking why this project spends so
much of its time on tests that test tests should be shown this list rather than
an opinion.

| # | Phase | What passed for the wrong reason | How it was found |
|---|---|---|---|
| 1 | 3 | A shutdown-race test written against the wrong path; passed whether or not the code was correct | Sabotage |
| 2 | 2.5 | The cross-implementation comparison did not fire when a run-encoding guard was dropped — the corpus never reached the shape (§13.11) | Sabotage |
| 3 | 3b | `DuplicatesDropped++` asserted in three suites and not in the one the mutation gate drives — to the gate, a counter nothing asserts (§13.15, §13.21) | Mutation gate, once §13.20 let it run |
| 4 | 4 | 4.8's end-to-end test stayed green with the reconnect catch-up removed: the author had nothing to catch up on (§12) | Sabotage |
| 5 | 4 | "Retries once after a catch-up" was in fact "never retries" — the recovery called `drain()` from inside `drain()` and hit the re-entrancy guard. The assertion passed, about a recovery that never happened | Writing the paired test |
| 6 | 4 | The editor was uncontrolled-equivalent and passed 98 tests, because the remote-edit test fed `session.text` back into the component | Sabotage (`value` → `defaultValue`) |

Six occurrences across four phases, and the two most recent are the same shape
as each other: an assertion that was *about* a mechanism, satisfied by a path
that did not involve the mechanism. Note what found them. Four of six were found
by deliberately breaking something; one by the mutation gate, which is sabotage
mechanised; one by writing the pair a rule already required. **Reading found
none of them.** That is not a comment on care — each of these was read, by
someone who had just written it and knew what it was for.

The related family, where the same shape appears somewhere other than a test:
§13.19 (a guard matching syntax rather than property), §13.21 (a ratchet keyed
on position rather than property), §13.22 (a done-when satisfiable while the
deliverable is absent), and §13.19's process instance below. They are the same
defect wearing different clothes, and they are why the practices in §12 are
practices rather than preferences.

**Append to this table whenever another is found.** A count that stops being
maintained becomes an anecdote, which is the genre this project is trying to
leave.

### 13.25 Report what was compared, not what was intended

Every valid record in the client's IndexedDB store failed to load, reporting:

> unsupported store version 1; this build understands 1

The message is not merely unhelpful; it is evidence *against* the actual cause,
because it names the one thing that was fine. The real failure was two lines
away: `instanceof Uint8Array` returns false for a `Uint8Array` that crossed the
structured-clone boundary into another realm, so the payload check failed and
the error path chosen was the version one.

This is the one defect in Phase 4 that reading would never have found. The code
is correct-looking in the strongest sense — `instanceof` is the idiomatic check,
and it works everywhere except across the boundary this code exists to cross.

The generalisation is about the message rather than the bug: **an error must
report what was actually compared, not what the author intended to compare.**
"Unsupported version 1; this build understands 1" is a sentence that cannot be
true, and a message that cannot be true is worse than no message, because it
sends the reader to the wrong file with confidence. Where a check has several
failure modes, say which one fired and with what values — and if the values
make the sentence absurd, that absurdity is the finding.

Same family as §13.23: a failure that cannot explain itself costs more than the
failure. There it was a harness, here a product error path, and the cost is
identical — time spent in the wrong place, and a real cause filed as something
else.
