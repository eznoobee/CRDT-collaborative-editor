# Phase 7 — GC and lifecycle

Tasks 7.0 through 7.7. Branch `phase-4-react-client`.

---

## The headline: GC is transparent, proven against a replica that never collected

§5's done-when for this phase is **transparency, not convergence** — a replica
that has collected and one that has not must produce the identical §9 normalised
form in every field except `elements`. That comparison is
`A_collected_replica_and_one_that_never_collected_agree_after_a_continuation`,
and it is the phase's result:

- A document is typed and partly deleted through the product's own path, the
  frontier advances, and the collector removes **7 elements**, asserted as that
  number rather than as "more than zero".
- A replica that never collected is rebuilt from the log; a client joins and
  resyncs from the collected snapshot.
- **The two then each compose an insert at the same position, from their own
  view, and exchange.** Their normalised forms match in `text`, `v` and
  `versionVector`; the exclusion of `elements` is asserted to be non-vacuous by
  requiring that field to *differ*.

**The first version of that test could not have failed, and the sabotage is what
found it.** It composed the continuation on the collected replica and applied
that same operation to the control — so whatever right origin the collected
replica computed, the control simply accepted, and agreement was true by
construction. It **passed with §5's rule 4 deleted from `Replica.Collect`**, the
rule whose entire purpose is to keep that comparison honest; only the count
assertion beside it went red. A right origin exists to order an insert *against a
competing one*, so the continuation has to be concurrent. Made concurrent, the
sabotage is caught in the strongest available form: the collected replica names
end-of-document where the other names the retained tombstone, the resulting
operation cannot be applied at all, and the texts differ outright —
`"collect m!"` against `"collect m?!"`.

That is now **§13.42** and §12's third standing question: *in this comparison,
does each side decide for itself?*

---

## What the phase found

Seven defects, five of them in code that already existed and passed its tests.

### 1. The frontier let a client decide what GC destroys

`StabilityFrontier` took the pointwise minimum of client-supplied
acknowledgements and nothing bounded it by the log. The minimum bounds a liar
only while an honest replica is also live — so **a client alone on a document
controls it outright.** Measured before the fix: three operations in the log, one
client, one acknowledgement claiming a thousand, and a **stored frontier of
1000**. Collection tests `Seq < F[s]` and §5 requires the frontier never to move
backwards, so that single message made every tombstone in the document
permanently collectable. `F[s]` is now the smaller of what the log holds and what
replicas claim: the log is evidence, an acknowledgement is a claim.

### 2. The frontier could not say "holds nothing"

It stored the highest sequence held. Sequence numbers start at zero, so `0`
already means "holds `(s,0)`" and there was no value left for "holds none of
them"; the hub dropped zero entries instead, which the minimum reads back as that
same zero. The first operation of every author was therefore stable while a live
replica had never seen it — in the window between a replica appearing and its
first catch-up landing, which is exactly when a reconnecting client holds offline
state the server cannot see. Fixed by carrying next-expected form throughout and
deleting the conversion, so there is nothing left to get wrong.

### 3. Nothing in the product ever reported what a replica held

The largest one. §5 names three paths by which the server learns `A(r)`: catch-up,
a piggyback on submission, and a timed acknowledgement. **The piggyback was never
built, and the timed acknowledgement was a hub method whose only caller was a C#
test client.** That leaves catch-up, which runs once per connection and reports
what the client held *before* it received anything — nothing, for a fresh
replica.

So in the product the frontier stayed where catch-up left it for as long as
anyone had the document open, and collection could only ever run on a document
whose replicas had all been retired: seven days of nobody touching it. Every
server-side test passed throughout, because they drive the hub method directly.

7.2 added that hub method *because a viewer never submits* — and then the defect
it was written to prevent arrived anyway, one layer out, with every replica in
the viewer's position. **§13.41's question has to be asked across the
client/server boundary, not only within one side of it:** "does anything invoke
this" means anything in the product, and the product is both halves.

### 4. The offline-window discard was silent

`SyncController` handled a declined resumption by emptying the outbox and
reporting nothing. §9 is explicit that this is a data-loss bug rather than a
limitation. It had been in the code since Phase 4, waiting for retirement to
exist so it could start losing work.

**The branch that reported the loss correctly was the unreachable one.** §9's
`resync` recovery is reached only by a server-sent `resync_required`, which
cannot fire while the log is intact (below); the branch that actually runs on a
retired replica's return reported nothing. Two mechanisms for one condition, and
the tests covered the one that never fires.

The test that covered the path asserted only that the queue emptied, over a
fixture holding a single **empty** batch — so a correct implementation and a
silent one produced identical observations. It now uses nine real batches and
asserts the reported number.

The fix needed no new code path: it reports `resync_required` with the count, the
code §9 already defines for this condition, and `App.tsx` already had the
sentence written for it — *"This client was offline too long. N unsent changes
could not be recovered."* Written in Phase 4 against a contract, unreachable
until now. The contract-first decision paying off exactly as intended.

### 5. Removing a document would have left live editors editing it

Deletion is enforced by a `DeletedAt == null` filter on every path that **reads
the document row**, so the legitimate user who never performs that action is the
one already connected. Predicted before the code; confirmed by taking the
invalidation back out, which turns **three** tests red — a held connection stays
open (2507 ms and counting), the server goes on accepting operations into the
removed document, and **`negotiate` still grants a brand-new connection to it**,
because it asks the role cache rather than the row. For everything on the live
path the cache is not a detail beside the deletion check; it is the deletion
check.

### 6. Collection reclaims far less than it sounds

Rule 2 (a collectable element must be a leaf) plus the right-child chain that
forward typing builds means **a tombstone in the middle of text always has a
visible right child, is never a leaf, and is never collected.** Only trailing
runs collect. Measured: a mid-document run of four collects 0; a trailing run of
four collects 3.

This is correct behaviour and must not be "fixed" by relaxing rule 2 — the
placeholder is what keeps a concurrent insert ordering the same way everywhere.
But every collection test written before this phase deleted from the end, so the
suite could not distinguish "GC works" from "GC works on the one shape we
tested", and 7b would have measured a reclamation rate that does not occur in
use. Pinned as a test, carried as register row 29.

### 7. §6's periodic snapshot is never taken

`SnapshotPolicy` and `DocumentStore.SaveSnapshotAsync` are implemented, correct,
and called **only from tests**. Every document is rebuilt by full replay of its
log, and the collector's own write is now the only snapshot the product stores.
Found because a sabotage of that write was *not* caught: the conflict it would
cause cannot arise. §13.40 at the scale of a subsystem. Register row 28.

---

## `resync_required`, and what it cannot yet do

It is emitted only where the server can say the element **existed and was
collected**: below the frontier, for an author the frontier knows, with no
operation of that sequence number in the log. Three corrections to §9's table,
which was written from the client's side before any server existed —

1. the comparison is strict, against a count, not `≤` against a highest-held;
2. the server has no pending set, so its answer for the other rows is
   `unknown_origin`, not "buffer";
3. an id naming a *delete's* sequence number is a client bug, not a collection,
   and answering `resync_required` there would tell a user to destroy unsent work
   over a bug.

**And the honest part: with the log intact and the frontier clamped, this
condition cannot arise from any client action.** Collection shrinks the snapshot,
never the log; any id below the frontier from a known author is present by
density. The clamp in finding 1 closed the one path that *was* reachable, and
that path was a bug rather than a route. So every classifier test constructs the
frontier directly and says so, and the end-to-end test is register row 30,
landing with log truncation in 7b.

---

## Register

**Closed:** row 1 (`retired_at` set by a background job), row 2 (§9's
offline-window discard), row 3 (`resync_required` emitted server-side), row 23
(removing a document). Row 27's Phase 7 half — `T_retire` and the GC watermark —
is covered by
`Someone_who_keeps_a_document_open_for_longer_than_T_retire_is_not_retired`.

**Opened:** 28 (§6's snapshot never taken), 29 (mid-document deletions reclaim
nothing), 30 (`resync_required` has no reachable path until truncation), 31 (the
offline-window discard observed in a browser), 32 (§5's submission piggyback), 33
(a walk step observing GC on the deployed stack).

**Row 33 needs a decision.** It was asked for when Phase 7 was approved, and it
cannot be written honestly yet. The walk is black-box — HTTP and a browser, no
database access — and §5 *requires* collection to be invisible through the
product: identical text, identical version vector, by design. The only observable
evidence is the collector's own counters, and §10's metric surface does not exist
(row 6). A step that opened a collected document and found the text correct would
pass identically whether or not anything had been collected. It is blocked on row
6 rather than deferred by preference; pulling a counter endpoint forward now
would mean inventing a metric surface late, ahead of the one §10 specifies.

---

## The walk

Step 10 no longer stops. It makes a document, removes it, and checks the URL no
longer opens one — the first time the walk has run out of *gaps* rather than out
of product.

Its previous assertion is worth recording as a near miss: it checked that no
`[data-delete-document]` element existed, and **that selector never matched
anything, before or after.** The Remove control is `data-remove`, so the step
would have gone on passing beside a working feature. Replaced rather than
amended.

---

## Process

Two §12 failures of my own, both recorded.

**I broke the sabotage-from-a-committed-tree rule one task after writing it
down.** Two source files carried 7.4's whole implementation uncommitted; three
sabotages were reverted with `git checkout` and took the implementation with
them. The tell was **three different sabotages producing identical failure
lists** — nothing in the output said so. The mechanical form is now in §12:
*commit before the first sabotage, not before the first revert*, because the
temptation is to sabotage the moment the tests go green. Plus the signature: when
two different sabotages fail the same set of tests, doubt the tree.

**A sabotage that does not compile proves nothing.** Renaming a C# property to
test a wire-shape assertion broke the build, and the test run then reported green
off a stale assembly — §13.17's case. Caught because the build output said `4`
errors. The sabotage that proves something is the one that compiles; the working
version changed only the JSON name.

---

## Preflight

Run against `600845e1a0c9e1b09a14506a94a24fd7a1881e19`, the pushed head, with a
clean working tree. Status file assembled from GitHub's own run and job lists;
the expected job set is derived by the script from `.github/workflows/`, not
from anything written here.

```
==> Preflight for phase-4-react-client
    head 600845e1a0c9e1b09a14506a94a24fd7a1881e19 is pushed and the tree is clean
    CI run 34759509453: 9 jobs for 600845e1a0c9
         success  .NET build and test
         success  Browser document load metric
         success  Client lint, typecheck and test
         success  Cross-implementation conformance
         success  Secret scan
         success  The application in a browser
         success  The walk — cold start to the first step that cannot be taken
         success  TypeScript core against the running server
         success  §7 against the deployed stack
    Mutation run 34759509454: 1 jobs for 600845e1a0c9
         success  Crdt.Core mutation score
    CI is green for this exact commit

==> Local gates
--- workflows
    ok
--- format
    ok
--- tests
    ok
--- client
    ok
--- conformance
    ok
--- interop
    ok
--- e2e
    ok
--- mutation
    ok

PREFLIGHT PASSED for 600845e1a0c9e1b09a14506a94a24fd7a1881e19. The job table above goes in the report.
```

**Ten of ten jobs `success`, across both workflows, on this exact commit**, with
no superseding run. The mutation gate ran to completion on the same push rather
than being cancelled by a following one — the failure run 79 produced, and the
reason `.github/workflows/mutation.yml` has a per-SHA concurrency group that does
not cancel in progress.

### The local gates

All nine pass, and the first attempt at this preflight is worth recording
because it failed and the failure was mine rather than the code's.

`tests`, `interop` and `e2e` came back `FAILED`. My first reading was that `e2e`
needed a Docker daemon this environment does not have — which would have been a
comfortable answer, and was wrong. All three share one cause: the preflight's
shell had no `EDITOR_TEST_POSTGRES` or `EDITOR_TEST_REDIS`, because those were
exported in an earlier command and shell state does not carry between them here.
`interop.sh` and `e2e.sh` refuse outright without them; `run-tests.sh` fails
against a database that is not there. With the environment set, `run-tests.sh`
exits 0 over Conformance 14, `Crdt.Core` 77 and `Editor.Api` 328, and every gate
is green.

`e2e` does drive a browser, but against an API it starts as a process, using the
pre-installed Chromium — not Compose. The "Failed to connect to Docker endpoint"
lines in the log come from Testcontainers-based suites falling back to the local
Postgres and Redis, and they pass.

The lesson is the same one §12 keeps recording in a different costume: **a
failure is evidence about the invocation before it is evidence about the code.**
I reached for the environment-limitation explanation first because it required
nothing of me.
