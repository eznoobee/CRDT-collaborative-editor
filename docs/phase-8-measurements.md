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

**What was broken, and what the measurement said.** §8: *a measurement is not
done until something has been deliberately broken and the measurement said which
thing.* The pair for this target is a 40 ms delay placed inside the segment and
the same delay placed outside it:

| | overall p50 | `editor.broadcast` p50 |
|---|---|---|
| unbroken | 66.6 ms | 2.21 ms |
| +40 ms **inside** the segment | **105.0 ms** | **43.6 ms** |
| +40 ms **outside** it, after the latency record | 64.0 ms | 1.65 ms |

The first shows the number moves and the trace says which stage. **The second is
the stronger one:** it is what distinguishes this harness from one that happens
to be timing the whole hub call, which is 3b.1's near-miss — a length measured on
the payload rather than the frame — in the place it is easiest to repeat.

---

## Target 1, re-measured after 9.4's adaptive flushing · **still missed, and now for a different reason**

`Release · a62bc23 · .NET 10.0.11 · Ubuntu 24.04.4 LTS · Xeon 2.10GHz × 4 · 10 GiB`

The window is gone from the segment. §8's own scenario, twenty editors at
eight batches a second on one document, n = 1,200:

| | p50 | p95 | p99 | throughput |
|---|---|---|---|---|
| 50 ms window (7b.4's arrangement) | 66.6 ms | — | **234 ms** | 161/s |
| adaptive (9.4) | **10.1 ms** | **13.6 ms** | 54.7 ms | 162/s |

**The p50 improves by a factor of six and is now well inside a target it used
to be more than twice outside.** 7b.4's conclusion — *the p50 alone is more than
twice the p99 target, so this is not a tail problem and no amount of tail work
reaches it* — no longer holds. It is a tail problem now.

### The curve, which is what 9.4 was asked for

Six rates under both policies, one build, one run, alternating.

| rate, per editor | adaptive p50 | adaptive p95 | adaptive p99 | window p50 | window p99 | adaptive thru | window thru |
|---|---|---|---|---|---|---|---|
| 1/s | 11.4 | 20.3 | 117.1 | 61.3 | 65.2 | 20/s | 20/s |
| 2/s | 11.7 | 16.1 | 42.4 | 60.8 | 65.8 | 41/s | 41/s |
| 4/s | 11.4 | 15.0 | 29.7 | 61.1 | 69.8 | 81/s | 81/s |
| **8/s (§8's scenario)** | **10.9** | **14.4** | 71.4 | 61.2 | 143.3 | 162/s | 161/s |
| 16/s | 10.3 | 14.2 | 30.2 | 60.1 | 115.2 | 324/s | 320/s |
| 32/s | 9.9 | 13.1 | 38.5 | 60.3 | 67.6 | **630/s** | **326/s** |
| saturating | 8.6 | 12.5 | 67.9 | — | — | **1,884/s** | 309/s (7b.4) |

**No cliff.** That was the thing to look for, and the reason there is none is
structural rather than lucky: there is no threshold in the code to cross. The
adaptive p50 falls monotonically from 11.4 to 9.9 as offered load rises by a
factor of 32, which is the batch size tuning itself — more company per write at
higher load, and the write amortised over more of it.

**The window's cost is a constant, not a proportion.** Its p50 sits at 60-61 ms
at every rate, and the floor is the window itself: `min` was 51.3 ms at 1/s.
This corrects a prediction written before the run, which expected the gap to
shrink as load rose on the grounds that a busy server would fill its batches
before the timer expired. It does not, because the timer is what the loop waits
on and a full batch at 100 operations is far above what twenty typists produce.

**Where the window does become proportional is throughput**, and only past 16/s:
at 32 batches a second per editor, adaptive sustains 630/s against the window's
326/s, and saturated it reaches 1,884/s. The window is a rate ceiling of roughly
one flush per 50 ms per document.

### The honest statement about the p99, which is that this data cannot settle it

The two runs of this curve disagree about the p99 by more than the distance to
the target. At 8/s adaptive: **36.6 ms** in the first run, **71.4 ms** in the
second, same code, same machine, minutes apart. At 1/s it was 114.6 and 117.1 —
stable, and attributable: `editor.validate` p99 is 99.4 ms there against a p50
of 2.5 ms, in the **first measurement the process runs**. That is JIT, not load;
the harness warms one submission per client, which does not warm the validation
path.

§8's own third rule is the one that applies: *a p99 over 50 requests is the worst
of 50*. n = 1,200 puts twelve observations above the p99, and twelve
observations on a four-core box shared with the generator is not enough for a
25 ms decision.

**The p95 is stable.** Across both runs and all six rates it sits between 12.5
and 20.3 ms, always inside 25 ms, and it moves smoothly with load. If §8's
target 1 were stated at p95 it would now pass, and the statement would be one
this harness can actually support.

> **That is a decision about §8 rather than about the code, and it is the same
> decision target 4 needs.** It is not taken here.

### What was decided, and what was rejected

**Decided: §8's batching rule becomes adaptive.** Forced by the measurement —
the fixed window is worse on p50 at every rate by a constant 50 ms, worse or
equal on p99 at every rate, and caps throughput at roughly half past 16/s. There
is no load shape in this data where it wins. Keeping it would also leave the
code and §8 disagreeing, which §11's Phase 9 forbids.

Rejected, with reasons:

| Option | Why not |
|---|---|
| Keep the 50 ms window and move the target to ~250 ms | This was the live option after 7b.4, and it was live only because the window made 25 ms unreachable. It is not any more. |
| Keep the window as the default and offer adaptive | Backwards: the measurement says adaptive is better everywhere. The window survives as `BatchingPolicy.FixedWindow` because the comparison needs it and a deployment dominated by lock contention rather than round trips could still want it. |
| Declare target 1 met | It is not. 54.7 ms against 25 ms at §8's scenario, and the p99 is too unstable here to claim either way. |
| Chase the remaining tail in `editor.validate` | Out of scope for a close-out, and premature: the p99 is not measured well enough to know there is a tail to chase. It needs a bigger sample and a generator off the box first. |

---

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

## Targets 2 and 4, re-measured for 9.5's decisions

### Target 2 · **still missed, and 7b.4's explanation was wrong**

`Release · f2c79cd · node v22.22.2 · Xeon 2.10GHz × 4 · 15.7 GiB`

| | keystrokes reaching the server | matched p50 | p95 | p99 | host busy |
|---|---|---|---|---|---|
| 7b.4, 50 ms window | 457 / 1000 | 94 ms | 9,540 ms | 31,801 ms | not measured |
| 9.4's adaptive flushing | **275 / 1000** | **24 ms** | 2,581 ms | 51,164 ms | 71 % of 4 cores |
| 9.5 discriminator, 4 editors | **1000 / 1000** | 16 ms | 26 ms | **30 ms** | 23 % of 4 cores |

**The prediction was that a faster round trip would raise the send rate. It
fell.** The server got faster on every axis and fewer keystrokes reached it —
275 against 457 — while the latency of those that did improved sharply. That
pair only makes sense if the constraint is not where 7b.4 put it.

**7b.4's recorded explanation:** *at 20 concurrent editors, one client cannot
submit as fast as a person types, because `SyncController.drain` submits one
batch at a time and awaits each.* It proposed coalescing queued batches as the
remedy.

**The discriminator disproves it.** Hold the writer, the typing rate, the
keystroke count and the serial `drain` fixed, and remove only the background
load: **1,000 of 1,000 arrive, p99 30 ms, five times inside a target that was
missed by two orders of magnitude.** The same one-batch-per-round-trip loop is
not a cap at all. What changed is how much work the writer's own browser was
doing for everyone else.

**Where it actually goes.** The writer's browser applies every peer's
operations — nineteen editors at eight a second is about 150 a second — and
`Replica.text` walks the whole document (`inOrder()`) on every change, which the
editor then re-renders. The document grows past 20,000 elements during the run,
so per-operation cost grows with it. The main thread that would run `drain`'s
continuations is the one doing that work.

Two things follow that are worth stating separately.

**The p50 of 24 ms is conditioned on arrival and is not evidence of health.**
Only keystrokes that reached the reader have a latency at all; the 725 that
never left the outbox contribute nothing to the percentile. The distribution
improves as the failure worsens, which is the shape §13.22 warns about in a
different setting.

**The host figure exists now because §8 required it and this harness never had
it.** 71 % of four cores is not saturation, so it does not by itself indict the
box — and it cannot see a single browser main thread pegged at one core, which
is what the discriminator says is happening. It is reported because §8's second
rule says a result without it is not one, not because it settles this.

#### Decided, and rejected

**Rejected: coalescing queued batches in `drain`.** It was the remedy 7b.4
proposed and the measurement says it addresses nothing — the serial loop
delivers 1,000 of 1,000 when the browser has capacity. It would also complicate
§9's byte-identical retry rule for a refused batch, which is a real constraint,
in exchange for no measured gain.

**Rejected: raising the target, or narrowing its scenario.** Nothing here shows
150 ms to be unreachable; the discriminator shows 30 ms at four editors.

**Recorded as missed, with the cause corrected**, and the remaining work named:
the client's per-operation cost is a function of document size, and the fix is
incremental rendering rather than a change to the sync loop. That is a project,
not a close-out task — §8 allows a recorded miss with a decision, and this is
one.

**Implemented and flagged for decision: the unsent-work line.** §8's own report
carried the sentence *the UI says `live`, with no problem, the whole time*. A
person typing would see their own text and nothing to say that most of it was
going nowhere. `backlogMessage` now names the backlog once it passes eight
queued batches — past anything ordinary typing produces, because `drain` keeps
one or two queued at a time, and not zero because an indicator that flickers
constantly is one people stop reading. **The threshold and the wording are the
part to review.**

---

### Target 4 · **the distribution, and what it argues**

`Release · f2c79cd · .NET 10.0.11 · Xeon 2.10GHz × 4 · 10 GiB`, 600,000
elements (100,000 live, 500,000 tombstones), snapshot plus 480 operations of
tail.

```
load ms    n=200 min=176.43 p50=308.84 p95=557.86 p99=745.21 max=763.08
deciles ms p0=176 p10=209 p20=240 p30=267 p40=286 p50=310 p60=326
           p70=350 p80=384 p90=490 p100=763
gc pause   n=200 min=0.00 p50=101.36 p95=261.55 p99=432.99 max=518.21
```

**n = 200, against 7b.4's 20**, because the decision asked for is which
statistic to read and a p95 over twenty samples has one observation above it.
Two hundred puts ten there, the same standard target 1's runs already use.

**The numbers improved by raising the count**, which is worth naming: p50 432 →
309, p95 979 → 558, p99 1,917 → 745. Nothing about the code changed. Twenty cold
loads were substantially measuring JIT and pool warm-up and amortising it over
twenty samples; two hundred amortises it over two hundred. §8's third rule is
usually read as "a small sample makes the tail meaningless"; this is the other
half of it — a small sample can make the whole distribution wrong.

**The shape argues for p95, which is where the prior already was.** The deciles
rise smoothly from 176 to 384 through the first eighty per cent and then turn:
490 at p90, 763 at the worst. That is one mode with a right tail, not two
populations, so there is no second cluster for a max to be picking out and no
hidden bimodality for a p50 to be hiding. The case for reading it elsewhere
would have to come from the shape and the shape does not make it.

- **p50 (309 ms)** passes with room and describes a load most users get.
- **p90 (490 ms)** passes by ten milliseconds.
- **p95 (558 ms)** **misses by 12 %**.
- **max (763 ms)** is the worst of two hundred and is not a stable number: it
  moves with whatever else the box is doing.

**So adopting the prior turns 7b.4's "qualified pass" into a recorded miss**,
which is the honest direction for a stricter statistic to move a result. About
nine loads in ten are inside 500 ms; one in twenty is beyond 558 ms.

GC pause is measured rather than guessed and remains about a third of a typical
load at p50. It does not explain the tail: the worst pause is 518 ms against a
worst load of 763 ms, and the p90 knee sits where no pause figure does.

---

## What this leaves

| Target | Result | The decision it needs |
|---|---|---|
| 1 · receive → broadcast p99 | 234 ms → **54.7 ms** after 9.4's adaptive flushing; still missed against 25 ms, p50 now 10.1 ms | **taken:** §8's batching becomes adaptive. **open:** whether the target reads at p95, where it passes and where this harness can support the claim |
| 2 · keystroke → render p99 | missed; 72% never sent, and 7b.4's explanation of why was wrong | **taken:** coalescing rejected — the discriminator shows the send loop is not the cap. **open:** the unsent-work line's threshold and wording, implemented and flagged |
| 3 · 1,000 connections | passed, 285 MiB | none |
| 4 · document load | at n=200: p50 309 ms in, **p95 558 ms out by 12 %** | **recommended:** read it at p95, which the prior held and the distribution's single mode supports — making this a recorded miss rather than 7b.4's qualified pass |

**That paragraph was written after 7b.4 and 9.5 disproved half of it.** It is
kept rather than edited, with the correction beneath, because a superseded
conclusion that quietly vanishes leaves no record that it was ever believed —
and this one was acted on.

> **The correction.** The window did sit inside the measured segment, and
> removing it moved target 1 from 234 ms to 54.7 ms. It did *not* cap the
> client's send loop: with the window gone, target 2 got worse, and a
> discriminator holding everything fixed but the background load delivered
> 1,000 of 1,000 keystrokes through the same serial loop. Target 2's constraint
> is the writer's own browser applying everyone else's operations at a cost that
> grows with the document. The two targets were not one finding.

Targets 1 and 2 are one architectural finding: **the batching window sits inside
both the measured segment and the client's serial send loop.** Neither is a bug
in the sense of code doing what it was not meant to; both are consequences of
decisions §8 records, meeting a target §8 set before anything measured it.
