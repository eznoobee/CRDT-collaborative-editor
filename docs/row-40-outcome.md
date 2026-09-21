# Row 40: what made the offline-window suite intermittent

Row 40 was opened with a closing condition written before the answer was known:

> Closes when that instrumentation has named the failing request and the cause
> is either fixed or explained — not when a run happens to be green.

It has named it. This is the explanation, and the part of it that is *not*
fixed.

## The evidence

Commit `bdbf784` produced four CI runs (two workflows × the `push` and
`pull_request` events). Thirty-one of thirty-two jobs passed. The one failure
was `§9's offline-window discard, in a browser`, job `106496292372` in run
`35648963079` — and **the same job in run `35648968632`, on the same commit,
passed**. That pair is the intermittency, isolated: identical tree, identical
workflow, different outcome.

The instrumentation added in 9.6's sixth iteration reported this:

```
Error: waiting for the signed-in home page to offer a Create button timed out after 60000 ms.
--- url ---
https://10.1.0.246:8444/
--- requests the browser could not make ---
GET https://10.1.0.246:8444/me — net::ERR_NETWORK_CHANGED
console: Failed to load resource: net::ERR_NETWORK_CHANGED
--- what the page showed ---
Collaborative Editor

Failed to fetch
```

Everything before that line had been a guess. "Failed to fetch" is what a page
says when a request did not complete; it names neither the request nor the
reason, and two previous cycles were spent on the hypothesis that the stack was
not up.

## What it says

**Three facts, all in that block.**

1. **The request that failed is `GET /me`** — the identity call `bootstrap`
   makes after the token is in hand. Not the document, not the hub, not the
   issuer.
2. **The reason is `net::ERR_NETWORK_CHANGED`.** That is Chromium's error for
   the host's network configuration changing underneath an in-flight request —
   an interface appearing or disappearing, a route table rewritten. It is not a
   timeout, not a refusal, and not a TLS failure; the connection was live and
   the ground moved.
3. **The stack was serving.** The same failure block carries the API's own log,
   and its retirement sweep is running throughout — one `UPDATE
   document_replicas` per tick, for the whole window. And the page shell itself
   had loaded from that same origin: the browser rendered "Collaborative
   Editor" before the XHR died. A stack that is down does not serve the HTML
   and then fail one request.

**The reading.** This suite brings up its own Compose stack on a GitHub runner,
with its own overlay and its own port, while the runner's Docker is creating the
bridge network for it. A request in flight across that moment is exactly what
`ERR_NETWORK_CHANGED` describes. It is a property of the environment the suite
starts in, not of §9's discard path — which is the part of the suite that has
never once failed, on any run.

**What would falsify it.** A recurrence carrying a different `errorText`, or one
whose failed request is not on the prologue, is a different fault and belongs in
a new row. The instrumentation now says which, without costing a cycle; that is
what it was for.

## What was done

`signIn` in `client/src/offline/offlineWindow.e2e.test.ts` retries the sign-in
prologue, under three conditions, all required:

- the failure is in the prologue — `goto`, the account chooser, and the wait for
  a Create button — which asserts nothing about §9;
- the browser recorded exactly `net::ERR_NETWORK_CHANGED`, with the recorded
  failures cleared per attempt so a stale one cannot authorise a retry; and
- fewer than three attempts have been made.

Anything else fails as before, and so does the same error anywhere after the
prologue. §13.29: name the specific thing you are trusting, never widen the
class. A bare retry of the test — or of `until` — would have covered this
failure and also covered a real discard regression, which is the trade this
project does not make.

## What was not done, and is now row 41

**This tolerates the fault; it does not fix it, and the product does not
tolerate it at all.**

The log shows the page settling on "Failed to fetch" and staying there.
`bootstrap` (`client/src/app/bootstrap.ts`) wraps the whole sequence in one
`try` and returns `{ kind: 'failed', message }`; nothing retries, and the
composed app offers no way back except a manual reload. A user whose connection
blips during the two seconds after sign-in sees a dead end with the API healthy
behind it.

Nobody was looking for that. It fell out of a log line collected to explain a
flaky test, and it is the more interesting of the two findings.

It is **not** fixed here. Phase 9 is the close-out and a new recovery path in the
bootstrap sequence is product work with its own failure modes — a retry that
loops on a genuine 401, a reload that re-enters the PKCE exchange. Row 41
records it with the evidence attached.

The honest summary of the pair: **the suite now survives a transient network
fault; the product still does not, and the suite's retry is not evidence that it
does.** (§13.64.)
