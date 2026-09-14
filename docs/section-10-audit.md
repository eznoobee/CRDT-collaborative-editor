# §10 audit — which instruments can be derived from state

Row 8 found that `editor.acknowledgements` read identically on an instance that
wrote nothing and one that wrote everything: the break deleted the frontier
write and left the increment beside it standing. The single fix is not the
finding. **Every counter adjacent to a write has this property**, and this is the
audit of the rest.

## The rule

> A counter next to a write measures that control reached the line after it.
> Relocating the increment buys one fix and keeps the class — it is correct
> against today's code and wrong again the first time a branch, an early return
> or a retry is inserted between the two. **A reading derived from state cannot
> decouple from what it reports, because it is what it reports.**

Two consequences that are easy to miss:

- **State-derived readings detect; per-instance counters localise.** Every
  instance reads the same Postgres, so a derived gauge is identical everywhere
  by construction. It says a thing is wrong and cannot say where. §10 needs both
  kinds, and row 8 asks for both halves.
- **A derived reading still needs a window and an age.** Without a window,
  "silent replicas" accumulates every abandoned session until `T_retire` and is
  permanently red — row 8's *first* finding arriving inside the fix for its
  second. Without an age beside it, a scheduled reading reports the past as the
  present, which is the original problem wearing different clothes. Both were
  found by running the dashboards, not by review.

## The audit

| Instrument | Adjacent to a write? | Derivable from state? | Done |
|---|---|---|---|
| `editor.connections.active` | no — observable over the live registry | already is | ✅ state |
| `editor.operations.received` | no — request volume before validation | no state to count; a refused batch leaves no row, by design | counter is correct |
| `editor.operations.applied` | **yes — the log append** | **yes, `count(*)` over `document_ops`** | ❌ **not done: a sequential scan on the largest table in the schema. Recorded, not silently omitted — see below** |
| `editor.operations.rejected` | no — refusals write nothing | nothing is stored when a batch is refused | counter is correct |
| `editor.resync_required` | no — a refusal | as above | counter is correct |
| `editor.replicas.retired` | **yes — the retirement write** | yes, `retired_at IS NOT NULL` | ✅ `editor.replicas.retired.stored` added; the counter is kept as per-instance work done |
| `editor.acknowledgements` | **yes — the frontier write** | yes, empty `acknowledged` on recently-active replicas | ✅ `editor.replicas.silent` added; **counter renamed** to `editor.acknowledgements.received` |
| `editor.propagation.latency` | no — a duration | nothing stored to derive it from | histogram is correct |
| `editor.outbound.queue_depth` | no — no server-side subject (§10 divergence) | none | unchanged |
| `editor.catchup.requests` | no — request volume | none | counter is correct |
| `editor.backpressure.drops` | **adjacent to an action, not a write** | **no — a dropped connection leaves no trace anywhere** | counter is the only evidence there is, which is §13.15's own example. Flagged: it has the decoupling property and no remedy |
| `editor.gc.elements_collected` | **yes — the collected snapshot write** | partially: the stored snapshot's element count against the log's, per document | ❌ not done; needs a per-document comparison rather than an aggregate |
| `editor.snapshots.written` | **yes — `SaveSnapshotAsync`** | yes, `count(*)` over `document_snapshots` | ✅ `editor.snapshots.stored` added; counter kept as per-instance work done |
| `editor.snapshot.age` | no — read from the sweep's own observation | is already a property of the rows | ✅ state |

## The two that are not done, and why

**`editor.operations.applied`.** This is the one that would answer *"were the
operations actually written"* — the same question for ingest that
`editor.replicas.silent` answers for the frontier, and the more important of the
two. `count(*)` over `document_ops` is a sequential scan on the largest table in
the schema, and a gauge costing a table scan every thirty seconds is a gauge
somebody turns off. The cheap approximations available — `pg_class.reltuples`,
or a per-document `max(server_seq)` — are respectively an estimate the planner
may not have refreshed and a sum over every document. **Left undone deliberately,
recorded here, and worth a register row**: the gap is real and the remedy needs a
design decision rather than a line of SQL.

**`editor.gc.elements_collected`.** Derivable only per document, by comparing the
stored snapshot's element count with a replay's. That is the assertion §5's
collection tests already make; as a continuous reading it would cost a replay per
document. Not attempted.

## `editor.backpressure.drops` is the honest counter-example

Not everything can be derived. A connection dropped for backpressure is closed
and gone: no row records it, and the count is the only evidence it happened —
which §13.15 names outright, because dropping a slow client and never dropping
one produce the same document. **It has exactly the decoupling property this
audit is about and there is no state to derive it from.** The remedy is not
available, so it is flagged rather than fixed, and a reader of that number should
know it is a claim about control flow.
