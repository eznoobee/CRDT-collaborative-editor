# Row 40: what made the offline-window suite intermittent

Row 40 was opened with a closing condition written before the answer was known:

> Closes when that instrumentation has named the failing request and the cause
> is either fixed or explained — not when a run happens to be green.

It has named it, twice. **The second naming corrected the first explanation and
the repair built on it**, which is why this document has two findings in it and
why the row is not closed.

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

## Why the row is still open

The closing condition allows "explained". It is explained as far as the evidence
goes, and the suite now tolerates it.

**Closing it here would still be wrong.** The explanation on record twenty
minutes ago was falsified by the next CI run, and a row closed on a reading with
that track record, in a phase whose whole complaint about this suite was
green-red-green, would be the thing the row exists to prevent. It closes when
the rescoped suite has survived CI — or it names a third site and is written up
honestly a third time.
