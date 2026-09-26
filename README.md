# A collaborative text editor, with the CRDT written from the paper

Several people edit one document at the same time, from different browsers,
over a bad network, and end up with the same text. The part that makes that
true is a **FugueMax CRDT implemented twice from the paper** — once in C#, once
in TypeScript — with a conformance harness that fails the build if the two ever
disagree about where a character goes.

No CRDT library is used. [`PROJECT_SPEC.md`](PROJECT_SPEC.md) §1 forbids it, and
that constraint is the point of the project rather than an obstacle to it.

**If you are here to read rather than to run it**, the editor is not the
interesting part. Start with [`docs/findings.md`](docs/findings.md) — a dozen
ways verification turned out to be lying, each with the failure that exposed
it. Then [what is open](#what-is-open), which is not a short list and is not
meant to be.

| I want to… | Go to |
|---|---|
| Understand what was learned | [`docs/findings.md`](docs/findings.md) |
| Run it | [Running it](#running-it) |
| Read the contract | [`PROJECT_SPEC.md`](PROJECT_SPEC.md) — §13 is the decision log |
| See every known debt | the deferred register, at the end of §13 |
| Know what is unfinished | [What is open](#what-is-open) |
| Read the close-out | [`docs/phase-9-report.md`](docs/phase-9-report.md) |

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

**If an earlier attempt failed**, clear what it left behind. Two different
leftovers bite, and `down` alone removes neither:

```bash
# -v as well as down: the oidc-ca volume keeps the ownership it was created
# with, so a volume made before the image declared that directory stays
# root-owned and the issuer cannot write its CA bundle into it.
docker compose -f docker-compose.yml -f deploy/docker-compose.dev-oidc.yml down -v

# Directories Docker invented where the certificate should be (the EISDIR case).
rm -rf deploy/tls

./scripts/dev-cert.sh deploy/tls
```

**Accept the issuer's certificate before you sign in — this is a step, not
troubleshooting.** Open <https://editor-oidc:9443/.well-known/openid-configuration>
and accept the warning. You should see the discovery document.

The application and the issuer share one certificate but are **two origins**,
and a browser trusts them separately. Accepting it at `https://localhost:8443`
does not accept it at `https://editor-oidc:9443`, and there is no second prompt:
a `fetch` to an untrusted origin rejects silently. The page can only tell you
which origin it was reaching, which it now does — it cannot ask the browser to
trust it.

Then open <https://localhost:8443>, accept that origin's warning too, and sign
in as `alice` or `bob` — set `DEV_OIDC_ACCOUNTS` to change them. Two accounts rather than one, so two browser profiles can
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

**[`docs/findings.md`](docs/findings.md) is the place to start.** §13 of the
spec is a decision log with seventy-three entries, written in the order things
went wrong; `findings.md` is the dozen with the widest reach, grouped and with the
concrete failure that produced each. If you read one thing here, read that.

**§13 itself is the full log**, and it is the most useful part of the
repository: each entry is something that was believed, turned out to be wrong,
and what was measured. The through-line is that almost every entry is a *check
that could not fail* — present, passing, plausible, and still green if the thing
it named had been deleted.

**The deferred register** lives at the end of §13. Forty-four rows, each a known
debt with the task that settled it or the condition that would close it. It is
the honest inventory: nothing here was quietly dropped, and the open rows below
are taken from it.

### The phase reports

- [Phase 7](docs/phase-7-report.md) — garbage collection and the offline window
- [Phase 7b](docs/phase-7b-report.md) — the register, worked
- [Phase 9](docs/phase-9-report.md) — the close-out

### What is open

Taken from the register in §13; none of it is tidied up to look finished.

| | |
|---|---|
| **Row 41** | A transient failure during `bootstrap` leaves the user on a dead page with a healthy API behind it. The page now names the origin it could not reach; it still cannot recover |
| **Row 42** | Nothing required in CI builds the published image *on purpose* — the compose suites build it incidentally, as setup for asserting something else |
| **Row 43** | Whether the Windows checkout was ever CRLF is unresolved. The diagnostic shipped in the same commit as the fix and can no longer distinguish the two worlds |
| **Row 44** | No test does what a person on a fresh clone does. Five setup failures, none a product bug, all invisible to CI. Supersedes the setup half of 42 and 43 |
| **Row 38** | An interior placeholder is never collected and the fraction grows without bound. Measured, with a reversal condition; deliberately not fixed |
| **§8 target 1** | **Missed: 28.0 ms against 25 ms**, across five runs agreeing within a tolerance fixed beforehand. A recorded miss, not a retune |
| **§8 target 2** | Missed. The writer's own browser cannot keep up applying everyone else's operations — a different cause from the one recorded in Phase 7b |
| **`ERR_NETWORK_CHANGED`** | Unexplained. The browser suite absorbs it; nobody understands why a runner's network changes at that moment. Two explanations were offered and both were falsified |

Row 8's limit — that state-derived readings detect but do not localise — stands
as originally written and has not been upgraded by any later report.

### The measurements

- [§8's four targets](docs/phase-8-measurements.md), with what was broken to
  make each number fail
- [The convergence audit](docs/convergence-audit.md) — which tests actually
  guard element placement, established by inverting the comparator
- [GC reclamation](docs/gc-reclamation.md) and
  [limit headroom](docs/limit-headroom.md)
