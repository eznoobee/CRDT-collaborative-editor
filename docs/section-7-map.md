# The §7 requirement map

Every requirement in PROJECT_SPEC.md §7 has a row here naming the test that
proves it. `Section7MapTests` fails the build when a §7 bullet has no row, when
a row anchors to text §7 no longer contains, or when a row names a test that
does not exist.

**The list is derived from §7's text, not from the suite.** Deriving it the
other way makes the map a restatement of whatever happens to be written — a
document that can never disagree with the code, which is the shape §13.22 is
about. The `Anchor` column holds a phrase copied verbatim out of the §7 bullet
it stands for, so a requirement that is reworded past its anchor, deleted, or
added fails this map rather than silently leaving it stale.

**A row is not evidence on its own.** `Proven by` names test methods that must
exist; that they *reach* what they claim is what the sabotage practice (§12) is
for, and what the vacuity notes in each suite record. This map answers a
narrower question — is there anything at all standing behind this line of §7 —
and that question had no answer before.

| # | Anchor (verbatim from §7) | Enforced by | Proven by |
|---|---|---|---|
| 1 | `OIDC with JWT bearer tokens` | `OidcOptions`, `AddEditorAuthentication` | `TokenValidationTests`, `HostStartupTests` |
| 2 | `short-lived connect ticket` | `RedisConnectTicketStore`, `NegotiateEndpoint` | `ConnectTicketTests`, `NegotiateTests` |
| 3 | `must never be written to logs` | `SecretRedaction`, logging configuration | `LogRedactionSentinelTests`, `SecretRedactionTests` |
| 4 | `Authorization Code with PKCE, and no client secret` | `client/src/auth/pkce.ts` | `app.e2e.test.ts` |
| 5 | `The access token lives in memory only` | `InMemoryWebStorage` in `client/src/auth/pkce.ts` | `app.e2e.test.ts` |
| 6 | `The token must never reach the SignalR connection URL` | `signalRTransport.ts`, the ticket flow | `signalRTransport.test.ts`, `security.e2e.test.ts` |
| 7 | `Refresh is delegated to the provider's library` | `oidc-client-ts` `automaticSilentRenew` | `app.e2e.test.ts` |
| 8 | `A refresh that fails is a client state, not an exception` | `tokenSource.ts`, `SyncController` | `rejections.test.ts` |
| 9 | `The redirect URI is exact-match` | `client/src/auth/pkce.ts` | `app.e2e.test.ts` |
| 10 | `Signing out is three separate things` | `client/src/app/signOut.ts` | `signOut.test.ts`, `app.e2e.test.ts` |
| 11 | `A membership change reaches a live connection` | `MembershipSweep`, `DocumentRoleCache` | `MembershipRevocationTests`, `DocumentRoleCacheTests` |
| 12 | `Every hub method and every endpoint re-checks document membership` | `EditorHub`, `DocumentsEndpoints` | `EditorHubTests`, `DocumentCreationTests`, `MembershipGrantTests` |
| 13 | `Two checks, at different costs` | `EditorHub` tier-1 binding, tier-2 role | `EditorHubTests`, `CountingDocumentRoles` |
| 14 | `Viewers receive broadcasts but any write` | `EditorHub`, `Role` | `EditorHubTests`, `MembershipRevocationTests` |
| 15 | `Authorization failures return 404, not 403` | `NegotiateEndpoint`, `DocumentsEndpoints` | `NegotiateTests`, `DocumentListingTests`, `security.e2e.test.ts` |
| 16 | `Reject operations whose `ReplicaId` does not match` | `EditorHub` tier-1 | `EditorHubTests`, `IngestValidationTests` |
| 17 | `Reject operations whose `Seq` is not the next dense value` | `IngestValidator` | `IngestValidationTests` |
| 18 | `Caps: single element value = exactly 1 code point` | `IngestLimits`, `IngestValidator` | `IngestValidationTests` |
| 19 | `Validate UTF-8 and reject lone surrogates` | `IngestValidator`, `OperationBinary` | `IngestValidationTests`, `BinaryRejectionTests` |
| 20 | `Per-user and per-connection rate limits on operation submission` | `RedisOperationRateLimiter`, `EditorHub` | `RateLimitTests` |
| 21 | `Rate limits on the document API too` | `RedisDocumentApiRateLimiter`, the `/documents` group filter | `DocumentApiRateLimitTests` |
| 22 | `Connection limits per user` | `RedisUserConnections`, `NegotiateEndpoint` | `ConnectionLimitTests` |
| 23 | `A malformed or oversized message closes the connection` | `HubOptions.MaximumReceiveMessageSize`, `IngestValidator` | `IngestValidationTests` |
| 24 | `Content Security Policy, stated as a policy rather than as a prohibition` | `SecurityHeaders.ContentSecurityPolicy` | `SecurityHeaderTests`, `security.e2e.test.ts` |
| 25 | `HSTS, with a deliberate difference between what is shipped` | `SecurityHeaderOptions`, `docker-compose.yml` | `SecurityHeaderTests`, `security.e2e.test.ts` |
| 26 | `X-Content-Type-Options: nosniff` | `SecurityHeaders.UseSecurityHeaders` | `SecurityHeaderTests`, `security.e2e.test.ts` |
| 27 | `The editor renders text into a DOM text node` | `client/src/editor/Editor.tsx` | `Editor.test.tsx`, `app.e2e.test.ts` |
| 28 | `Through the published port` | `scripts/deployment.sh`, `docker-compose.yml` | `security.e2e.test.ts` |
| 29 | `From `docker-compose.yml` and a `.env`` | `scripts/deployment.sh`, the walk harness | `security.e2e.test.ts`, `walk.e2e.test.ts` |
| 30 | `Nothing reconfigured by the test` | `scripts/deployment.sh` | `security.e2e.test.ts` |
| 31 | `Nothing in `appsettings.json` but non-secret defaults` | `appsettings.json`, `docker-compose.yml` | `SecretRedactionTests` |

## The named test/production divergence

§7 requires HSTS and this project's Compose stack diverges from the production
value deliberately. It is a row of its own because a divergence recorded only in
a compose comment is the artefact that stops being read.

| Setting | Production (§7) | This stack | Why |
|---|---|---|---|
| `Strict-Transport-Security` `max-age` | 365 days | 60 seconds | A long `max-age` served once from a development host pins that browser profile for a year and nothing in the application can undo it. What is this project's to prove is that the header is present, enforced, and carries the configured value — a short value proves all three, and a long one would be testing the browser's implementation instead. |

The default in `SecurityHeaderOptions` is the **production** value, so a
deployment that configures nothing gets the setting that protects it; the
divergence is an override in `docker-compose.yml`, not a weakened default.

## What this map does not claim

- **It is not coverage.** A row means something exists that would fail if the
  requirement were violated in the way that test checks. §12's sabotage runs are
  what establish that the test reaches the mechanism.
- **Rows 28–30 are one suite.** §7's "verified against the application as
  Compose starts it" is three clauses about *how* the other rows are checked,
  and `security.e2e.test.ts` is where all three hold at once. They are listed
  separately because each can be lost independently — sharing the walk's compose
  bring-up would break 30 while leaving 28 and 29 intact.
- **`gitleaks` is not named as a row's proof**, though it runs in CI. It is
  third-party and narrow by construction (§13.36) — a check for secret-shaped
  strings, not a proof that no secret is committed — and naming it here would
  make row 31 look better supported than it is.

## What building this map found

Writing the rows from §7 and checking each name against the repository turned up
**four §7 requirements whose only coverage is the browser walk**: rows 4, 7 and
9 (PKCE with no client secret, refresh delegated to the provider's library, the
exact-match redirect URI) and row 5's storage rule. There is no unit test for
`client/src/auth/pkce.ts` or `client/src/auth/tokenSource.ts` at all — seven of
the thirty-one rows named tests that turned out not to exist, and these are what
was left after correcting the names.

`app.e2e.test.ts` does drive a real sign-in through the harness issuer, so the
flow is genuinely exercised; what is missing is anything that fails for a
*specific* reason. A PKCE flow that silently stopped sending a code challenge
would still sign in, and nothing would go red.

Recorded as register row 26 rather than fixed here. This phase was scoped before
the map existed, and quietly widening a phase because a new check found
something is how the register stops being a record of anything (§12).
