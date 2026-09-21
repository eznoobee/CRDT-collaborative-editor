# A collaborative text editor, with the CRDT written from the paper

Several people edit one document at the same time, from different browsers,
over a bad network, and end up with the same text. The part that makes that
true is a **FugueMax CRDT implemented twice from the paper** — once in C#, once
in TypeScript — with a conformance harness that fails the build if the two ever
disagree about where a character goes.

No CRDT library is used. [`PROJECT_SPEC.md`](PROJECT_SPEC.md) §1 forbids it, and
that constraint is the point of the project rather than an obstacle to it.

## What is here

| | |
|---|---|
| `src/Crdt.Core` | The algorithm. Depends on the BCL and nothing else |
| `src/Editor.Domain` | Documents, members, replicas |
| `src/Editor.Infrastructure` | Postgres, the operation log, snapshots, collection |
| `src/Editor.Api` | The HTTP and SignalR surface, and the background sweeps |
| `client/src/crdt` | The same algorithm in TypeScript |
| `client/src` | The React editor over it, with an outbox that survives being offline |
| `tests/` | Property tests for §5's eight invariants, the conformance corpus, the API suite |
| `deploy/`, `docker-compose.yml` | Postgres, Redis, a migrator, the API, and a TLS-terminating proxy |

**What it does.** Real-time collaborative editing over SignalR with a Redis
backplane; offline editing with a local outbox and a reconnect that reconciles
by version vector; OIDC sign-in with PKCE; per-document roles; tombstone
collection and log truncation once operations are causally stable.

**What it does not.** Remote cursors and presence are deliberately out of scope
— see §2, and register row 14. Two of §8's four performance targets are missed,
recorded as misses with their causes rather than retuned.

## Running it

Requires Docker. The whole stack, including the schema migration:

```bash
cp .env.example .env
```

Then fill in `.env`. Three things have no defaults on purpose (§7: a deployment
that falls back to a convenient value is one that authenticates against the
wrong thing):

- `POSTGRES_PASSWORD` — `openssl rand -base64 24`
- `OIDC_ISSUER` and `OIDC_METADATA_ADDRESS` — your identity provider
- `TLS_CERT_FILE` / `TLS_KEY_FILE` — `./scripts/dev-cert.sh deploy/tls` makes a
  self-signed pair for local use

```bash
docker compose up --build
curl -k https://localhost:8443/health/live
```

The API's own port is not published. Everything goes through the proxy over
TLS, because a plaintext route past the termination point is a control that
exists and does not apply (§4).

## Working on it

```bash
./scripts/run-tests.sh                              # the .NET suites
cd client && npm ci && npm run lint && npm test     # the client
./scripts/conformance.sh                            # the two cores against each other
./scripts/push.sh                                   # push, and confirm CI started
```

[`AGENTS.md`](AGENTS.md) is the working agreement: the rules that are not
negotiable, the things that are easy to get wrong, and the toolchain quirks
worth knowing before you lose an afternoon to one.

## Reading it

**[`PROJECT_SPEC.md`](PROJECT_SPEC.md) is the contract.** §5 is the algorithm
and its eight invariants, §6 the storage and wire formats, §7 security, §8
scalability with its four measured targets, §9 the client contract, §11 the
phase table.

**§13 is the decision log, and it is the most useful part of the repository.**
Sixty entries, each one a thing that was believed and turned out to be wrong,
with what was measured. A sample:

- §13.42 — a test whose expectation came from the implementation it is testing
- §13.47 — a convergence assertion is invariant under any consistent ordering
  rule, so none of them can detect a placement bug
- §13.53 — an audit that counts failures counts the failures it did not cause
- §13.54 — a test that supplies the configuration proves the mechanism, not the
  product
- §13.57 — a gate written from one failure checks that failure, not its class

The **deferred register** in §13 tracks every known debt, open or settled, with
the task that settled it.

### The phase reports

- [Phase 7](docs/phase-7-report.md) — garbage collection and the offline window
- [Phase 7b](docs/phase-7b-report.md) — the register, worked
- [Phase 9](docs/phase-9-report.md) — the close-out

### The measurements

- [§8's four targets](docs/phase-8-measurements.md), with what was broken to
  make each number fail
- [The convergence audit](docs/convergence-audit.md) — which tests actually
  guard element placement, established by inverting the comparator
- [GC reclamation](docs/gc-reclamation.md) and
  [limit headroom](docs/limit-headroom.md)
