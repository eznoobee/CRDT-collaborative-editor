# Row 34 — the convergence assertions, audited for §13.42's shape

§13.42 was found in the one test guarding the only operation that destroys
data: the collected replica computed a right origin from its own view and the
control was handed that answer, so transparency was true by construction. The
setup that produced it — compose on A, deliver to B, compare — is how every
convergence test in this repository is naturally written, so row 34 asked the
question of all of them:

> **What property is under test, and could the checking party have obtained
> that property from the party being checked?**

## The scoping lead was wrong, and checking it first is what produced the rest

The register and the 7b breakdown both carried one lead:

> `ScaleOutTests`' rejoin case compares `kept.Normalised` against a `rejoined`
> replica that adopted a server snapshot wholesale, so what it proves is closer
> to "the snapshot round-trips" than "two replicas independently agree".

**It does not adopt a snapshot.** `rejoined` catches up with an empty version
vector, and an empty vector asks for everything — eleven operations, far under
`MaxDeltaOperations`, so the server answers with a *delta*. Probed directly:
`caught.Snapshot` is `null`. The rejoining replica is handed operations and
places every one of them itself, which is exactly the independent decision
§13.42 asks for. ("Delivering an operation is fine.")

So the file named as the audit's worst case is one of its better ones. That
mattered, because the lead came with an inference attached — *found in the first
spot-check, which is itself evidence about the base rate* — and the base rate it
implied was wrong.

## What the audit found instead, by measurement

Reading twenty files could not settle what each comparison rests on, so the
question was made mechanical. `scripts/placement-probe.sh` inverts the sibling
tie-break in both cores — one change that reorders every user's text whenever
two people type at the same position — and reports which suites notice.

| suite | tests | red | which |
|---|---|---|---|
| `Crdt.Core.Tests` | 77 | **3** | the two comparator tests, and one expected sibling order |
| `Conformance` (C#) | 14 | **2** | committed trace expectations; the committed manifest |
| `Editor.Api.Tests` | 393 | **1** | `ScaleOutTests`' hardcoded `"beforeafter"` |
| `client` (`npm test`) | 213 | **0** | — |

**Not one of the twenty-five two-party comparisons caught it.** Every detection
was a comparison against a value committed to this repository.

That is not twenty-five badly written tests. It is structural:

> **A convergence assertion is invariant under any consistent ordering rule.**
> Both replicas run the same comparator, so they agree on whatever it says. A
> convergence test therefore cannot detect a placement bug — not because it is
> weak, but because convergence and correct placement are different properties,
> and only the second needs an oracle neither party computed.

§13.47 carries the general form. It also answers row 34's question in a way a
per-file reading would not have: for a two-party comparison the checking party
*always* could have obtained placement from the party being checked, because
both compute it with the same code. What these assertions independently
establish is **delivery** — that the operations arrived, in an order each side
had to cope with, and that readiness buffering released them correctly.

## The audit, per file

Grouped by what each comparison actually rests on.

### Type A — two parties, same code, same operations

Establishes: delivery, causal readiness, idempotency, and that no path drops or
duplicates. Blind to placement by construction.

| file | comparisons | the independent decision it rests on |
|---|---|---|
| `ScaleOutTests` | 6 | that the Redis backplane carried the operation to another instance at all; the rejoin case adds that catch-up reconstructs from a delta |
| `CatchUpTests` | 5 | that the version vector, not `server_seq`, decides what is missing — the joiner names what it holds and the server computes the rest |
| `CausalDeliveryTests` | 4 | that an out-of-order arrival is buffered and released on its dependency, rather than dropped or applied early |
| `converge.interop.test.ts` | 4 | that the *C# server* produced the bytes the TypeScript core decoded — a genuine second implementation |
| `offline.interop.test.ts` | 1 | that work authored with no socket survived a real disconnection and drained in order |
| `paste.interop.test.ts` | 1 | that a split paste's batches stayed causally intact across the split |
| `causalReadiness.test.ts` (TS) | — | as `CausalDeliveryTests`, in the core |
| `CausalReadinessTests` (C#) | — | as above |

### Type B — two parties, *different* code paths

Stronger: the two sides are not running the same code, so the comparison can
fail. Still blind to placement, because both paths share the comparator.

| file | comparison | what genuinely differs |
|---|---|---|
| `TombstoneCollectionTests` | collected replica vs a replay that never collected | §13.42's origin, repaired in 7.3: both sides now compose concurrently and exchange |
| `PeriodicSnapshotTests` | load-through-snapshot vs replay-from-zero | two different reconstruction paths over two tables |
| `LogTruncationTests` | document before and after truncation | that the rows removed were not load-bearing |
| `SnapshotTests`, `CrashDuringWriteTests` | in-memory source vs reloaded | the encode/decode round trip and the write path |
| `TraceReplay` | `viaJson` vs `viaBinary` | §9's two encodings of the same state |
| `ConformanceTests`, `FuzzTests` | C# vs TypeScript | two implementations — the strongest pairing here |

### Type C — one party against a committed value

The only kind that guards placement, and the entire detection budget.

| file | what the oracle is |
|---|---|
| `ElementIdTests` (C#), `elementId.test.ts` (TS, added by this audit) | §5's ordering rule, written out |
| `TreePositionTests` | expected sibling orders from the paper's figures |
| `ConformanceTests` trace expectations | outcomes committed with each trace |
| `GeneratedCorpusTests` manifest | a committed manifest of what the seed produces |
| `ScaleOutTests`' `"beforeafter"` | a literal — and the only placement oracle in 393 API tests |

## The repair this produced

**The default client suite had no placement oracle at all.** `npm test` — the
command `AGENTS.md` tells a contributor to run — passed 213 of 213 with the
comparator inverted. The only TypeScript check that caught it is
`conformance.test.ts`, which is *excluded* from the default run because it needs
the C# runner to materialise the corpus first.

And the comparator itself had no direct test on the TypeScript side, while
`AGENTS.md` singles it out as load-bearing **precisely because** TypeScript has
no `Guid` to agree with. `client/src/crdt/elementId.test.ts` now mirrors
`ElementIdTests`, with expectations taken from §5's sentence rather than from
the implementation, plus two cases C# does not need: sequence numbers past 2^53,
and the big-endian ordering across the `hi`/`lo` seam. With it, the same
sabotage turns three tests red instead of none.

## What was deliberately not changed

The Type A comparisons stay as they are. They are not vacuous — each rests on a
real independent decision, named above — and rewriting them to guard placement
would duplicate the conformance corpus at every layer while making each test
harder to read. What was missing was not assertions but a *statement* of which
property each one guards, which is what this document is, and one oracle in the
one suite that had none.
