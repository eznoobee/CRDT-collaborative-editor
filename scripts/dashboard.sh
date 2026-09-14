#!/usr/bin/env bash
# The operational views an operator reads (PROJECT_SPEC.md §10, register row 8).
#
#   scripts/dashboard.sh <admin-url> [<admin-url> ...]
#
# §13.22 rewrote "dashboards exist" into "the dashboards alone say which target
# broke and on which instance". These are those dashboards: one view per §8
# target plus the subsystems a violation of one is attributable to, rendered per
# instance side by side, because "which instance" is half of what row 8 asks.
#
# Deliberately dumb. It fetches and formats; it draws no conclusions and prints
# no verdicts. A dashboard that told you what was wrong would be the diagnosis,
# and the diagnosis is the thing under test.
set -uo pipefail

if [ "$#" -lt 1 ]; then
    echo "usage: scripts/dashboard.sh <admin-url> [<admin-url> ...]" >&2
    echo "  e.g. scripts/dashboard.sh http://127.0.0.1:9101 http://127.0.0.1:9102" >&2
    exit 2
fi

snapshots=$(mktemp -d)
trap 'rm -rf -- "$snapshots"' EXIT

index=0
for url in "$@"; do
    if ! curl -fsS --max-time 10 "${url%/}/metrics" > "$snapshots/$index.json" 2>"$snapshots/$index.err"; then
        # Unreachable is a reading, not an error. An instance that does not
        # answer is the first thing a dashboard should say, and exiting here
        # would hide the other instances that do.
        echo "{\"instance\":\"(unreachable: $url)\",\"counters\":{},\"gauges\":{},\"histograms\":{}}" \
            > "$snapshots/$index.json"
    fi
    index=$((index + 1))
done

python3 - "$snapshots" "$index" <<'PY'
import json, pathlib, sys

directory, count = pathlib.Path(sys.argv[1]), int(sys.argv[2])
snaps = [json.loads((directory / f"{i}.json").read_text()) for i in range(count)]
names = [s.get("instance", "?") for s in snaps]
width = max(24, max((len(n) for n in names), default=24) + 2)


def row(label, values):
    print(f"  {label:<38}" + "".join(f"{str(v):>{width}}" for v in values))


def counter(snap, name):
    return snap.get("counters", {}).get(name, 0)


def gauge(snap, name):
    return snap.get("gauges", {}).get(name, 0)


def tagged(snap, prefix):
    """Every series of one instrument, by its tag suffix."""
    out = {}
    for key, value in snap.get("counters", {}).items():
        if key.startswith(prefix + "{"):
            out[key[len(prefix) + 1:-1]] = value
    return out


def histogram(snap, name):
    return snap.get("histograms", {}).get(name, {})


def view(title, subtitle=""):
    print()
    print(f"── {title} " + "─" * max(0, 76 - len(title)))
    if subtitle:
        print(f"   {subtitle}")
    print()


print()
print("=" * 100)
print("  §10 OPERATIONAL VIEWS".ljust(60) + f"{len(snaps)} instance(s)")
print("=" * 100)

view("0 · WHO IS ANSWERING", "Read first. An instance missing here is the answer to most questions.")
row("instance", names)
row("uptime (s)", [round(s.get("uptimeSeconds", 0)) for s in snaps])
row("connections held now", [gauge(s, "editor.connections.active") for s in snaps])

view("1 · §8 TARGET — receive → broadcast enqueue", "Target: p99 < 25 ms. The bucket AT 25 is the target itself.")
for name, series in (("propagation", "editor.propagation.latency"),):
    hs = [histogram(s, series) for s in snaps]
    row(f"{name}: observations", [h.get("count", 0) for h in hs])
    row(f"{name}: at or below 25 ms", [h.get("atOrBelow", {}).get("25", 0) for h in hs])
    row(f"{name}: over 25 ms (TARGET)", [
        h.get("count", 0) - h.get("atOrBelow", {}).get("25", 0) for h in hs])
    row(f"{name}: over 100 ms", [
        h.get("count", 0) - h.get("atOrBelow", {}).get("100", 0) for h in hs])
    row(f"{name}: max ms", [round(h.get("max", 0), 1) for h in hs])

view("2 · INGEST", "What arrived, what was written, what was refused and why.")
row("operations received", [counter(s, "editor.operations.received") for s in snaps])
row("operations applied", [counter(s, "editor.operations.applied") for s in snaps])
codes = sorted({c for s in snaps for c in tagged(s, "editor.operations.rejected")})
if not codes:
    row("rejected", ["none"] * len(snaps))
for code in codes:
    row(f"rejected · {code}", [tagged(s, "editor.operations.rejected").get(code, 0) for s in snaps])
row("resync_required (DATA LOSS)", [counter(s, "editor.resync_required") for s in snaps])

view("3 · FAN-OUT", "§8's backpressure. A drop is a client told to reconnect.")
row("backpressure drops", [counter(s, "editor.backpressure.drops") for s in snaps])
row("outbound queue depth", [counter(s, "editor.outbound.queue_depth") for s in snaps])

view("4 · §5 STABILITY FRONTIER", "The three report paths. 'timer' at zero with traffic is the known blind spot.")
vias = sorted({v for s in snaps for v in tagged(s, "editor.acknowledgements")})
if not vias:
    row("acknowledgements", ["none"] * len(snaps))
for via in vias:
    row(f"acknowledgements · {via}", [tagged(s, "editor.acknowledgements").get(via, 0) for s in snaps])
row("replicas retired", [counter(s, "editor.replicas.retired") for s in snaps])
row("elements collected", [counter(s, "editor.gc.elements_collected") for s in snaps])

view("5 · §6 SNAPSHOTS", "Target: document load < 500 ms, which this is what defends.")
row("snapshots written", [counter(s, "editor.snapshots.written") for s in snaps])
row("oldest snapshot age (s)", [round(gauge(s, "editor.snapshot.age")) for s in snaps])
sources = sorted({c for s in snaps for c in tagged(s, "editor.catchup.requests")})
for source in sources:
    row(f"catch-up · {source}", [tagged(s, "editor.catchup.requests").get(source, 0) for s in snaps])

print()
PY
