# Row 31 — what happened, against what was predicted

Five CI iterations of a budgeted ten. `docs/row-31-predictions.md` was written
before the first push and is unedited.

## The predictions, scored

| # | Predicted | Outcome |
|---|---|---|
| 1 | The overlay applies and the API starts | **Right.** Three timer settings, bound like any other option; `Heartbeat < Retire` held. |
| 2 | The stack comes up | **Right**, eventually — see 3. |
| 3 | Sign-in and creation work, copied from walk step 7 | **Wrong, twice.** Both failed, at two different points. |
| 4 | `setOffline` will not drop an established WebSocket — my largest doubt | **Wrong, and it was never the problem.** The socket dropped, the client reported `offline`, and the fallback plan was never needed. |
| 5 | The client may reconnect inside the window and never be retired | **Wrong.** Fifteen seconds of `T_retire` against forty-five offline was ample. |
| 6 | The backlog assertion may read an empty string | **Right, for the wrong reason** — see below. |

**Four of six wrong, and the one I was least sure of was fine.** The thing that
actually cost three iterations was prediction 3, the part copied from a green
suite and therefore not examined.

## What the iterations bought

**1 → 2: nothing, and that was the finding.** A timeout waiting for a URL, with
a log that said only that sixty seconds had passed. Every wait in the suite now
reports the page's text, its URL and the stack's logs (§13.23). That is the
change that made every later iteration productive.

**2 → 3: "Collaborative Editor / Failed to fetch".** The page had loaded and its
first API call had not. The harness waits for `/health/live` — which §7 is
explicit is a probe that checks nothing — and then this suite opened a browser
immediately, where the walk's first steps are fetches with their own retries. It
now also waits for `/config`, which is what the client itself loads. No new
endpoint; the thing waited for is the thing that has to work.

**4 → 5: the suite passed its own claim and failed its own guard.** §9's
sentence was on screen with a non-zero count — the row's actual claim, verified —
and the guard establishing "there was work to lose" read the unsent-work line,
which `backlogMessage` hides while offline, deliberately, by a rule written in
the same phase. §13.59. The evidence moved to the count in §9's own message.

## What the row asked for, and what it got

A person typed, lost their link, kept typing, came back after the window had
closed, and **was told what could not be recovered** — in a browser, against
`docker compose up`, with the message and the count read off the screen.

7.5 had verified each step of that separately and none of them together, and
7.5 is also where the discard was found to be silent: the branch that reported
correctly was unreachable and had been since Phase 4. This is the test that
would have caught that, and it is the reason the row stayed open.
