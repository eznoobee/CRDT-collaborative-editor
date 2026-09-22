# Limit headroom — how far each number can fall before a real use breaks

Register row 27, §13.37's standing technique applied to the thirteen limits
Phase 7 left. The tests are
`tests/Editor.Api.Tests/Limits/LargestLegitimateUseTests.cs` and
`client/src/limits/largestLegitimateUse.test.ts`; the measurement is
`scripts/limit-headroom.sh`, run against the defaults on 2026-09-18.

## The question, and why halving is the wrong form of it

§13.37 states the check as: *if the test would still pass after halving the
configured number, it is testing the wrong thing.* That is sound for a limit
set **at** the largest legitimate use, and wrong for one set at a stated
multiple of it — and most of these are the second kind. "Survived halving"
then reads as a failure when it is a margin, and the two are only
distinguishable by **how far the number can actually fall**.

So the script divides until a use test goes red, and the factor is the answer:

- **2** — the number sits on top of the use. Any reduction breaks somebody.
- **4** — a deliberate margin of three to four times. A decision, once recorded.
- **64 and up** — nothing in this suite is watching the number, and the
  "largest legitimate use" column §13.37 wrote for it is not what sets it.

## The measurements

| Limit | Default | Largest legitimate use | Breaks at | Factor |
|---|---|---|---|---|
| `Ingest.MaxOperationsPerBatch` | 256 | a client batch: one paste chunk | 128 | **2** |
| `Ingest.MaxRunCodePoints` | 256 | a client batch: one paste chunk | 128 | **2** |
| `Ingest.MaxReplicasPerDocument` | 50 | a workshop of 40 | 25 | **2** |
| `ConnectionLimits.MaxPerUser` | 32 | 12 documents × a wake-from-sleep reconnect = 24 | 16 | **2** |
| offline window (`RETIRE_AFTER_MS`) | 7 days | a laptop shut over a 4-day weekend | 3.5 days | **2** |
| `RateLimits.CodePointsPerConnection` | 10,000 / 10 s | three pages pasted = 3,000 | 2,500 | 4 |
| `RateLimits.CodePointsPerUser` | 30,000 / 10 s | three pages in three tabs = 9,000 | 7,500 | 4 |
| `DocumentApiRateLimits.WritesPerUser` | 120 / min | a document shared with a team of 30 = 31 | 30 | 4 |
| `ConnectTicket.Lifetime` | 60 s | a cold first load ≈ 20 s | 15 s | 4 |
| `ReplicaClaims.Lifetime` | 2 min | a reload across a slow network ≈ 45 s | *refused* | — |
| `Ingest.MaxMessageBytes` | 64 KB | the largest batch a client can build | ~1–2 KB | **~48** |
| `Ingest.MaxDocumentBytes` | 5 MB | a book chapter ≈ 25 KB | ~25 KB | **~210** |
| pending-set bound | *never set* | a burst across a partition | — | — |

## What the outliers mean

### `MaxMessageBytes` and `MaxDocumentBytes`: §13.37's table has the wrong column

Both survive division by tens. The reflex is "the test is too weak", and for
both it is wrong. **Neither number is set by use, and neither can be.**

`MaxMessageBytes` is checked *before* the decode, because the decode is what
allocates. It is an allocation guard against a hostile frame, not a capacity
promise to a client — and it cannot bind a legitimate client at all, because
`MaxOperationsPerBatch` caps a batch at 256 operations and 256 operations
encode to one or two kilobytes. Whatever the byte cap is set to above about
2 KB, the operation cap refuses first, every time. Its stated largest
legitimate use — "the largest batch the client can build" — describes a
quantity that is 3% of it.

Two further things follow. Its comment still reasons about SignalR's JSON
protocol base64-inflating a `byte[]` by a third; the product has framed with
MessagePack since 3b.1, so the sizing argument is a fossil. And a guard sized
48× above anything legitimate is a guard that would not be noticed if it were
misconfigured by an order of magnitude in either direction.

`MaxDocumentBytes` is the same shape at 210×. Five megabytes of live text is
roughly 800,000 words in one collaboratively edited document. There is no user
to write a test for at that size; the number is answerable only from the
storage-and-load side, which is §8's question and not §13.37's.

**The correction this earns.** §13.37's table asserts a "largest legitimate
use" for every tuned value. For at least these two that column was written by
reflex: a **resource guard** takes its number from the resource, and a use test
against one proves only that the guard is not in the way. The halving check is
uninformative for them by construction, and saying so is the honest result —
inventing a user who needs five megabytes would not be.

### `ReplicaClaims.Lifetime`: floored by validation, above the use

It cannot be halved at all. `[Range("00:01:30", "00:10:00")]` refuses 60 s, and
the floor exists for a stated reason: the claim is taken at `negotiate` and the
connection that would refresh it does not exist yet, so it must comfortably
exceed §7's 60-second ticket lifetime.

That is a **stronger** guarantee than any use test. The largest legitimate use
is a 45-second reload; the lowest value the system will accept is 90 seconds;
so no configuration can put this number where a reload breaks. Recorded as a
pass of a different kind, not as an untested limit.

### The pending-set bound is not configured by the product at all

§5 requires a bounded pending set *per connection*. Both cores default it to
unbounded — `Replica.MaxPending = int.MaxValue`,
`replica.maxPending = Number.MAX_SAFE_INTEGER` — deliberately and with a
written reason: a replica is not a connection, and a replica replaying a stored
trace legitimately buffers as much as the trace demands. The comment in both
files says *whoever attaches a replica to a network connection sets this.*

**Nobody does.** The server has no pending set by design — `IngestValidator`
rejects a non-ready operation rather than buffering it, which removes the vector
instead of bounding it. That leaves exactly one layer where a replica is fed by
a remote peer: the browser client, and `DocumentSession` constructs
`new Replica(id)` and never assigns the bound. The only assignments anywhere are
in `causalReadiness.test.ts` and `CausalReadinessTests.cs`.

So there is no number to halve, and the use test below it — a burst of 2,000
out-of-order operations is not refused — passes at any bound including
unbounded, which is the vacuity §13.37 is about. It is kept as the use half of
a pair whose other half does not exist yet, and the gap is register row 36
rather than a silent pass.

### Two limits this suite cannot tell apart

`MaxOperationsPerBatch` and `MaxRunCodePoints` turn **exactly the same five
tests** red at exactly the same factor. That is §13.31's shape: two mechanisms,
one observation. These tests assert that a legitimate use is *accepted*, so
they cannot discriminate on the refusal code, and nothing here would notice if
one of the two stopped being enforced while the other still was. Their own
suites in `IngestValidationTests` do distinguish them — by rejection code —
which is why this is recorded rather than fixed here.

## What a reader should do with this

The factor column is the maintenance surface. A change that moves a limit with
factor **2** breaks a real user on the next deploy, and the test will say so. A
change that moves one with factor **4** is spending a margin that was chosen on
purpose; the margin is stated here so that spending it is a decision. A change
to either of the two resource guards will not be caught by anything in this
suite, and should be argued from §8's measurements instead.
