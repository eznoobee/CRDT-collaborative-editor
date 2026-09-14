# §8's performance targets, measured (7b.4, register row 5)

Four targets, measured for the first time. **Two missed, one qualified, one
passed.** §8's own rule applies to everything below: *a missed target is a
recorded miss and a decision, not a retune.* Nothing here was tuned to make a
number better, and the two experiments that changed configuration are labelled
as diagnostics and were reverted.

Every figure carries the build that produced it, the sample count, and the
generator's own utilisation, because §8 requires all three and a number missing
any of them cannot be shown to have regressed — only replaced.

**Runner class.** All figures below: Intel Xeon @ 2.10GHz × 4, 15.7 GiB, Ubuntu
24.04.4, .NET 10.0.11, node v22.22.2, **Release**. This is not the
`ubuntu-latest` class (2 vCPU, 7 GB) §8 names for the browser figure, so these
are not comparable with §13.9's numbers.

---

## Target 1 — p99 receive → broadcast enqueue < 25 ms · **MISSED**

20 concurrent editors on one document.

| Run | p50 | p99 | throughput |
|---|---|---|---|
| paced, 8 batches/s each (§8's scenario) | 66.6 ms | **234 ms** | 161/s |
| paced, one document each (diagnostic) | 62.1 ms | 286 ms | 161/s |
| unthrottled (capacity, no target) | 62.2 ms | 154 ms | 309/s |

n = 1,200 in each. Generator 5–24% of 4 cores; not saturated.

**The harness instruments nothing.** It reads `editor.propagation.latency`,
which `EditorHub` records across exactly the segment §8 names. 7b.2's trace
stages then attribute it:

```
editor.validate    p50   2.33   p99 174.00
editor.persist     p50  58.64   p99  65.24    ← min 27.9, never below it
editor.broadcast   p50   2.21   p99   6.90
```

**The floor is §8's own batching window.** `editor.persist` has a minimum of
28–54 ms and a p50 of 57–59 ms in every run. Setting `BatchingPolicy`'s window
to zero drops the overall p50 to **18.6 ms** — inside the target — and triples
throughput to 786 batches/s.

> **§8's 25 ms target and §8's 50 ms batching window cannot both hold.** The
> wait for company happens inside the segment the target measures. This is a
> contradiction in the specification, not a defect in the implementation.

The **p50 alone is more than twice the p99 target**, so this is not a tail
problem and no amount of tail work reaches it.

**What is not attributed:** the tails. They move between stages across load
shapes, and the generator shares the server's process, so scheduling delay
cannot be excluded. Settling that needs the out-of-process harness target 3 now
has.

**A measurement-design error, found before reporting.** The first version drove
twenty clients in a tight loop at 786 batches/s and called it "20 concurrent
editors". An editor is a person; a fast typist sustains eight characters a
second. The paced run is §8's scenario; the saturating run is reported beside it
as capacity with no target attached.

---

## Target 2 — p99 keystroke → remote render < 150 ms · **MISSED, and not for a latency reason**

Two real browsers on one document, 18 protocol-level editors at 8/s alongside.
Both timestamps taken inside the pages, on one clock.

```
typed      1000 keystrokes over 127.2s
arrived    writer holds 1000 marks, reader holds 456
server     holds 457, read by a third client catching up from scratch
writer     state=live problem=(none)
reader     state=live problem=(none)
latency ms n=456 min=18 p50=94 p95=9540 p99=31801 max=163640
```

**The p50, 94 ms, is inside the target — for the 46% of keystrokes that
arrived.** The rest never reached the server.

**Three parties localise it.** Writer 1000, server 457, reader 456. The reader
has everything the server has; the server does not have what the writer holds.
The missing text is in **the writer's outbox** — not the network, not the
reader, and not lost.

> **At 20 concurrent editors, one client cannot submit as fast as a person
> types.** `SyncController.drain` submits one batch at a time and awaits each,
> deliberately: §5's density requires this replica's operations to arrive
> without gaps. That caps a client's send rate at one batch per round trip, and
> with target 1's 50 ms window inside every round trip, the cap falls below
> eight characters a second under this load. The backlog then grows without
> bound.
>
> **The UI says `live`, with no problem, the whole time.** A person typing would
> see their own text and no indication that half of it has not been sent.

The two findings are the same finding seen twice: the batching window is inside
the client's send loop as well as inside §8's measured segment.

An obvious remedy exists — coalescing queued batches in `drain`, which §5's
density rule permits since merging preserves order — but §8 says a missed target
is a decision, so it is recorded and not taken.

**Correlation was wrong first, and the guard caught it.** The first version
matched keystrokes by textarea length; the reader's length also grows from the
other eighteen editors, so 89 of 1,000 correlated and it reported a p50 of
2.7 seconds drawn from whichever few lined up — a number that would have
*improved* as the system got worse. Keystrokes are now correlated by a character
only the measured writer types, and the match rate is asserted.

---

## Target 3 — 1,000 connections per instance < 2 GB RSS · **PASSED**

```
build      Release · node v22.22.2 · Xeon 2.10GHz × 4 · 15.7 GiB
load       1000 idle connections over 100 documents, 10 each
live       1000 of 1000 still connected when RSS was read
server rss 107 MiB idle → 207 MiB provisioned → 285 MiB holding 1000
per conn   80 KiB
connect ms n=1000 min=29 p50=52 p95=108 p99=145 max=162
generator  4 % of 4 cores, 137 MiB rss
```

**285 MiB against a 2 GB target**, with a seven-fold margin.

Measured **out of process**: RSS names a process, and a harness holding a
thousand clients beside the server would add the generator's memory to the
server's. A thousand distinct users, because §7 caps connections per user and
raising that cap would measure a configuration that will not exist.

**What was broken, and what the measurement said.** Capping
`Ingest:MaxReplicasPerDocument` at 2 made the measurement **refuse to report** —
`negotiate as load-conn-0-1: 409 too_many_replicas` — rather than reporting the
small RSS of a server holding almost nothing. That is the failure mode this
target has: *it passes most easily when the server refuses the work.* The guard
is that all 1,000 connections are checked to be still `Connected` at the moment
RSS is read, each having caught up, against documents carrying text.

---

## Target 4 — server-side document load < 500 ms · **QUALIFIED: passes at p50, misses at the tail**

600,000 elements (100,000 live, 500,000 tombstones), snapshot plus 480
operations of tail, 20 cold loads.

```
load ms    n=20 min=238.29 p50=431.59 p95=979.44 p99=1916.94 max=1916.94
gc pause   n=20 min=0.00   p50=126.09 p95=270.19 p99=274.30  max=274.30
```

**p50 431 ms is inside §8's 500 ms, and not by much.** p95 979 ms and a worst
of 1917 ms are outside it.

**Two qualifications the number needs.** "Cold" is optimistic: each load gets
its own data source so no pooled connection carries over, but Postgres' page
cache stays warm. A real cold start on a cold server is slower.

The tail is only partly attributed. GC pause is measured, not guessed: p50
126 ms, about a third of a typical load. But the worst pause is 274 ms against a
worst load of 1917 ms, so **the slowest loads are not explained by it**.

§8 notes that 500k tombstones implies GC is not keeping up — this is a stress
target, not a steady state — so nothing collects first, deliberately.

**A near-miss the guard caught.** The first version stamped the snapshot at
600,000 (an element count, not a `server_seq`) against an empty log, so the tail
was assigned 1..480, landed *below* the snapshot, and was skipped. It reported a
fast load of a document missing its tail: §13.19 arriving as a performance
result, where the number is good because the work was not done. The element
count is asserted inside the loop, which is what caught it.

---

## What this leaves

| Target | Result | The decision it needs |
|---|---|---|
| 1 · receive → broadcast p99 | missed, 234 ms vs 25 ms | §8's target or §8's 50 ms batching window — they contradict |
| 2 · keystroke → render p99 | missed; 54% never sent | whether `drain` coalesces, and whether the UI shows a backlog |
| 3 · 1,000 connections | passed, 285 MiB | none |
| 4 · document load | p50 in, tail out | whether the target is a p50 or a max |

Targets 1 and 2 are one architectural finding: **the batching window sits inside
both the measured segment and the client's serial send loop.** Neither is a bug
in the sense of code doing what it was not meant to; both are consequences of
decisions §8 records, meeting a target §8 set before anything measured it.
