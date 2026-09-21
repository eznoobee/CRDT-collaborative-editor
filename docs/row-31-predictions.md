# Row 31, predicted before the first push

§13.51: this cannot run in the development sandbox, which has no Docker daemon,
and that is a fact about the sandbox. CI has one. So the 5b arrangement applies —
write the predictions down first, because a remote loop punishes guessing much
harder than a local one, and the budget is ten CI iterations at roughly nine
minutes each.

**These were written before the first push and are not edited afterwards.** What
actually happened is recorded beneath each, in `docs/row-31-outcome.md`.

## What I expect to work first time

1. **The overlay applies.** `ReplicaRetirement__Retire`, `__Heartbeat` and
   `__Interval` are ordinary options bound from configuration, and Compose
   passes environment variables with `__` as section separators. The API
   validates `Heartbeat < Retire` at startup, and 5 s < 15 s, so it starts.
2. **The stack comes up.** It is the walk's stack with three environment
   variables added and a different published port; the walk's harness already
   brings this up in CI.
3. **Sign-in and document creation.** Copied from walk step 7, which is green.

## What I expect to be wrong, in order of confidence

4. **`context.setOffline(true)` will not drop the open WebSocket.** This is my
   largest doubt. Chromium's offline emulation is documented for the network
   stack; whether an *established* WebSocket is torn down rather than merely
   starved is the thing I am least sure of. If it is only starved, the client
   will sit in `live` with a socket that delivers nothing, the state assertion
   will time out, and nothing about §9 will have been tested.
   **If this is what happens**, the fallback is to stop the proxy container for
   the offline period — which is a network partition rather than a browser
   setting, is closer to the train tunnel §9 describes, and does not need the
   browser to cooperate.
5. **The timing.** T_retire 15 s, sweep every 3 s, offline for 45 s. The risk is
   in the other direction from the obvious one: not that 45 s is too short, but
   that **the client reconnects during it**. `setOffline` blocks the attempts,
   but if it does not, a successful reconnect inside the window resets
   `last_seen_at` and the replica is never retired. That failure looks identical
   to the discard not working.
6. **The backlog assertion may read an empty string.** It is captured after
   typing offline, and the unsent-work line needs eight queued batches.
   `DocumentSession.edit` produces one batch per change event, and Playwright's
   `keyboard.type` fires one per character, so twelve short words is far more
   than eight — but if the outbox is flushed by something I have not accounted
   for, the guard fires and says so, which is the outcome I want from it.

## What would make me stop rather than iterate

7. If the discard **does not report at all** and the mechanism is sound, that is
   a defect in the product found by this row, and it stops being a harness
   problem. 7.5 found exactly that defect once — the reporting branch was
   unreachable — so finding it again is the outcome this suite is for, not a
   reason to adjust the test until it passes.
8. If three iterations go on the offline mechanism, switch to prediction 4's
   fallback rather than spending the rest of the budget on browser emulation.
