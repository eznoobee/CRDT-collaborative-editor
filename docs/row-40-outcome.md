# Row 40: what made the offline-window suite intermittent

Row 40 was opened with a closing condition written before the answer was known:

> Closes when that instrumentation has named the failing request and the cause
> is either fixed or explained — not when a run happens to be green.

It has named it, twice — and then a green CI run exposed a third problem in the
closing condition itself. **Three findings, in the order they arrived**: the
first explanation was falsified by the second sighting, the first repair was
scoped to the last stack trace rather than to a boundary (§13.65), and the first
two green runs proved the fault absent rather than the repair working (§13.66).

The row is closed at the end of this document, on the repair being exercised —
not on a run that happened to be green.

## Finding 1 — `bdbf784`: `GET /me`

Commit `bdbf784` produced four CI runs (two workflows × the `push` and
`pull_request` events). Thirty-one of thirty-two jobs passed. The one failure
was `§9's offline-window discard, in a browser`, job `106496292372` in run
`35648963079` — and **the same job in run `35648968632`, on the same commit,
passed**. That pair is the intermittency, isolated: identical tree, identical
workflow, different outcome.

The instrumentation added in 9.6's sixth iteration reported this:

```
Error: waiting for the signed-in home page to offer a Create button timed out after 60000 ms.
--- requests the browser could not make ---
GET https://10.1.0.246:8444/me — net::ERR_NETWORK_CHANGED
--- what the page showed ---
Collaborative Editor

Failed to fetch
```

`net::ERR_NETWORK_CHANGED` is Chromium's error for the host's network
configuration changing underneath an in-flight request — not a timeout, not a
refusal, not a TLS failure. The connection was live and the ground moved.

**The reading taken at the time:** the suite brings up its own Compose stack on
a GitHub runner while the runner's Docker creates a bridge network for it, and a
request in flight across that moment dies here. A repair followed from that
reading, scoped to the sign-in prologue, on the grounds that the prologue is
where the failure was.

## Finding 2 — `7be8c7a`: `POST /documents`, and the first reading was wrong

The very next CI run failed again. The prologue passed this time; the retry was
never needed:

```
proxy-1 | "GET /"           200
proxy-1 | "GET /assets/…js" 200
proxy-1 | "GET /callback?…" 200
proxy-1 | "GET /me"         200
proxy-1 | "GET /documents"  200
proxy-1 | "POST /documents" 499
```

```
Error: waiting for the application to navigate to the document it created timed out after 60000 ms.
--- requests the browser could not make ---
POST https://10.1.0.138:8444/documents — net::ERR_NETWORK_CHANGED
```

**Two things fall out of that, and both matter more than the first finding.**

**The explanation was too tidy.** "Docker's bridge network coming up while the
stack starts" makes the fault belong to startup, and the stack had been serving
successfully for a second — five requests, all 200 — before this one died.
nginx logged `499`, which is its code for the client going away mid-request: the
server was there, waiting, and the browser left.

**The repair was scoped to the evidence rather than to a boundary.** "The
sign-in prologue" was not a line in this test's design; it was the place the one
failure on record had happened. That is §13.65, and it is the more useful of the
two findings: a scope chosen from the last stack trace looks principled, cites
§13.29 correctly, and covers exactly one sample.

## What actually survives both

The API answered every request on either side of each failure. Both failures
landed **within the first two seconds of the browser's first navigation** —
`bdbf784` on request four, `7be8c7a` on request six, roughly one second apart in
wall-clock terms. That is the observable pattern, and it is as far as the
evidence reaches.

**Why the runner's network changes at that moment is not visible from this
repository.** The honest record stops there rather than keeping the tidier
sentence, because the tidier sentence has already been falsified once.

**What would falsify what is left:** a failure carrying a different `errorText`,
or one landing well after the browser has settled, is a different fault and
belongs in a new row.

## What was done

`arrange` in `client/src/offline/offlineWindow.e2e.test.ts` now rebuilds the
**whole arrangement** — a fresh context, sign in, create the document, reach
`live`, type and watch the outbox drain — at most three times, and only when the
browser recorded exactly `net::ERR_NETWORK_CHANGED`, with the recorded failures
cleared per attempt so a stale one cannot authorise a retry.

**The boundary is `setOffline`, and it can be defended without reference to
which request failed last.** Everything before it is arrangement, and a failure
there means the property was never exercised. Everything after it — the offline
transition, the wait past `T_retire`, the reconnection, §9's sentence — is the
property, and **nothing there retries for any reason**. That is the difference
between a span and a memory (§13.65).

The error is still named, still required to have been reported by the browser,
still bounded at three attempts. Widening *the span* is not widening *the class*.

## What was not done, and is row 41

**This tolerates the fault; it does not fix it, and the product does not
tolerate it at all.**

Both logs show the page settling on "Failed to fetch" and staying there.
`bootstrap` (`client/src/app/bootstrap.ts`) wraps the whole sequence in one
`try` and returns `{ kind: 'failed', message }`; nothing retries, and the
composed app offers no way back except a manual reload. A user whose connection
blips during the two seconds after sign-in sees a dead end with the API healthy
behind it.

Nobody was looking for that. It fell out of a log line collected to explain a
flaky test.

It is **not** fixed here. A new recovery path in the bootstrap sequence is
product work with its own failure modes — a retry that loops on a genuine 401, a
reload that re-enters the PKCE exchange. Row 41 records it with the evidence
attached.

## Finding 3 — `01db35b` was green, and that proved nothing

The rescoped rebuild went out as `01db35b`. All four CI runs passed;
thirty-two jobs, nothing red.

**The rebuild never fired.** The offline job took its usual eighty seconds and
its log carries no `arrangement attempt` warning — `net::ERR_NETWORK_CHANGED`
did not happen that time, which is what green means on an intermittent fault
most of the time.

> Two runs of evidence that the fault was **absent** were about to be written up
> as evidence that the repair **works**. The closing condition this row carried
> — "closes when the rescoped suite has survived CI" — could not tell those
> apart, and was satisfied by a run that demonstrated neither.

That is §13.66, and it has a precedent on file in this project: 7.5 found
`SyncController` emptying the outbox and reporting nothing, because the branch
that reported correctly had been unreachable since Phase 4 while every suite
around it passed. An unfired retry is the same shape.

## What finally closed it

The decision moved to `client/src/offline/networkChange.ts`, where the
**default** client suite can create the conditions on demand. Nine cases, and
the two that carry the weight are not the happy path:

- **a different error is not absorbed** — without this, every other assertion is
  satisfied by a function that retries unconditionally, which would also absorb
  a real regression in §9's discard, the one thing this suite exists to catch;
- **the attempt cap is real** — "it recovered" alone is satisfied by
  `while (true)`;
- and recorded failures are forgotten between attempts, so one early network
  change cannot authorise a rebuild for every later failure of any kind.

Three sabotages confirmed each guard fails on its own: retrying
unconditionally breaks two cases, dropping the reset breaks one, removing the
cap breaks the third.

**The directory exclusion came with it.** `src/offline/**` was excluded from the
default run because the suite there brings up a stack of its own; these tests
need nothing. Leaving them excluded alongside it would have been §13.52 exactly
— the committed conformance traces, lost for nine phases because they shared a
directory with the generated ones. The pattern now names the e2e file.

The e2e suite composes the same module rather than holding a second copy.

## Row 40 is closed

Named, explained as far as the evidence reaches, repaired, and **the repair
itself exercised by a suite that can make it fire**. Not closed on a green run;
the green run is what exposed the gap.

Three things were wrong along the way and each was found by running something
rather than by reading it: the explanation (falsified by the second sighting),
the scope (§13.65), and the closing condition (§13.66).

**Row 41 remains open**, and is the finding worth more than the flake.
