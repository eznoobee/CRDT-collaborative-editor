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
- `OIDC_ISSUER` and `OIDC_METADATA_ADDRESS` — your identity provider. No
  provider to hand? See [Running it locally with no identity
  provider](#running-it-locally-with-no-identity-provider) below.
- `TLS_CERT_FILE` / `TLS_KEY_FILE` — `./scripts/dev-cert.sh deploy/tls` makes a
  self-signed pair for local use

```bash
docker compose up --build
curl -k https://localhost:8443/health/live
```

The API's own port is not published. Everything goes through the proxy over
TLS, because a plaintext route past the termination point is a control that
exists and does not apply (§4).

### On Windows: check your line endings first

`docker build` sends your **working tree**, not the index. Git for Windows
defaults to `core.autocrlf=true`, which rewrites text files to CRLF on checkout,
and `.editorconfig` requires LF — so a Windows clone can hand the compiler CRLF
sources and fail `IDE0055` under `TreatWarningsAsErrors` on a commit that builds
clean in CI. `.gitattributes` now pins LF for every clone, but an existing
checkout needs one re-normalisation to pick it up:

```bash
git rm --cached -r . >/dev/null   # drop the index, keep the files
git reset --hard                  # re-check-out under .gitattributes
git ls-files --eol src/Editor.Api/Logging/SecretRedaction.cs
```

That last line should print `i/lf  w/lf`. If it prints `w/crlf`, the
re-normalisation has not taken and the build will fail on formatting.

### Running it locally with no identity provider

`OIDC_ISSUER` and `OIDC_METADATA_ADDRESS` have no defaults on purpose, so with
no provider to point them at the stack will not start at all. There is an
overlay that runs one — the same issuer the test suites use — **for local
development only**.

> **This is not an identity provider you may expose.** It mints a valid token
> for any account it is asked for, with no password, because that is what makes
> it useful in a test. On a laptop that is convenient; anywhere reachable it is
> a complete authentication bypass. It lives in a separate file, and is
> deliberately **not** in `docker-compose.yml`, so that using it is always a
> choice someone typed.
>
> It changes nothing about the API. No check is disabled, no development bypass
> is added, `RequireHttpsMetadata` stays at its secure default, and issuer,
> audience, lifetime and signing key are all still validated — which is why the
> issuer serves real HTTPS and hands the API a CA bundle rather than serving
> plaintext. Nothing in `src/` knows it exists.

**One line in `/etc/hosts` first**, and it is not optional:

```bash
echo '127.0.0.1 editor-oidc' | sudo tee -a /etc/hosts
```

An OIDC issuer is an absolute URL, and the browser, the API and the token's
`iss` claim must agree on it exactly — the metadata document's `jwks_uri` is
derived from it too. `localhost` cannot be that URL, because inside the API's
container `localhost` is the API, so it would fetch signing keys from itself.
One name that resolves to the issuer from the host *and* from inside the compose
network is the only arrangement that works without weakening a check: on the
host that name is this hosts entry, and inside the network it is a service
alias.

Then:

```bash
# The certificate FIRST. TLS_CERT_FILE and TLS_KEY_FILE ship blank on purpose,
# so a .env copied unchanged fails at `up` with a message rather than mounting
# a path that does not exist (see below).
./scripts/dev-cert.sh deploy/tls         # covers localhost AND editor-oidc

cp .env.example .env

# then in .env:
#   POSTGRES_PASSWORD=...                (openssl rand -base64 24)
#   TLS_CERT_FILE=./deploy/tls/cert.pem  (dev-cert.sh prints both)
#   TLS_KEY_FILE=./deploy/tls/key.pem
#   OIDC_ISSUER=https://editor-oidc:9443
#   OIDC_METADATA_ADDRESS=https://editor-oidc:9443/.well-known/openid-configuration

docker compose -f docker-compose.yml -f deploy/docker-compose.dev-oidc.yml up --build
```

**If you already ran this and got `EISDIR`**, Docker created directories where
the certificate should be, and they have to go before a real one can be written:

```bash
docker compose -f docker-compose.yml -f deploy/docker-compose.dev-oidc.yml down
rm -rf deploy/tls/cert.pem deploy/tls/key.pem
./scripts/dev-cert.sh deploy/tls
```

Open <https://localhost:8443>. Both the application and the issuer are served
with the same self-signed certificate, so your browser will warn once per
origin; accept it for `https://localhost:8443` and for `https://editor-oidc:9443`
and sign-in will complete. Sign in as `alice` or `bob` — set `DEV_OIDC_ACCOUNTS`
to change them. Two accounts rather than one, so two browser profiles can
demonstrate two people editing the same document, which is the thing this
project is.

To go back to a real provider, drop the `-f` overlay and put your provider's
values in `.env`. Nothing else changes.

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
