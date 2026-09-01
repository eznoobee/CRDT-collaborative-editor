# Working agreement

`PROJECT_SPEC.md` is the contract. This file is how to work on it day to day.
Where the two disagree, the spec wins — and the disagreement is a bug in one of
them, so fix it rather than routing around it.

## The rules that are not negotiable

**Do not add a CRDT or OT library.** Not Yjs, Automerge, ShareDB, Loro, Collabs,
and not an existing Fugue or FugueMax implementation. The algorithm is written
from the paper. Reference implementations may be read to resolve an ambiguity;
they may not be vendored, copied, or depended on. (§1)

**Ask before adding any dependency.** Say what it does and why the BCL or the
standard library is insufficient. For anything CRDT-shaped the answer is no. (§12)

**Do not weaken a test to make it green.** If you think a test is wrong, argue
for changing the spec first. A failing invariant gets reported with its
minimised trace, not deleted. (§12)

**No stubs presented as done.** Unimplemented means it throws
`NotImplementedException` and appears in the phase report. Never a silent no-op,
never a hardcoded return that makes a test pass. `/health/ready` is the worked
example: it is absent, and a test asserts its absence, because a readiness probe
that checks nothing is worse than no probe. (§12)

**Plan before code.** Each phase starts with a task breakdown and waits for
approval. Do not implement straight from the spec. (§12)

**Say when you are unsure.** Distributed systems bugs live where the implementer
felt "this probably works." Flag those explicitly rather than hoping. (§12)

## Phase discipline

Do not start a phase before the previous one is committed, green in CI, and
reviewed. Stop and report at the end of each phase. The phase table is §11.

Phase 1 builds the C# *and* TypeScript cores together against a shared trace
corpus. Do not defer the TypeScript one to Phase 4 — §9 makes byte-identical
behaviour a build-breaking requirement, and a divergence found after a UI is
built on top is far more expensive.

## The eight invariants

`Crdt.Core` is accepted against these, and they are written as failing tests
before the implementation exists (§5):

1. Convergence · 2. Idempotency · 3. Commutativity · 4. Causal readiness ·
5. Intention preservation · 6. No resurrection · 7. GC safety ·
8. No interleaving

Invariant 8 is **maximal non-interleaving**, not "runs never interleave" — that
stronger property is impossible for any algorithm (arXiv Theorem 5). Forward
runs never interleave, unconditionally. Backward runs interleave only where the
Lemma 5 exception applies, which needs three concurrent replicas. Read §5's
scope note before writing the test; the generator has to respect that boundary
rather than demanding a property the algorithm is not permitted to satisfy.

Test it **from its definition**. Never assert a specific tree shape, and never
derive the expectation from the implementation: the conformance harness compares
two implementations written by the same author from the same paper, so a
misreading would agree with itself.

## Things that are easy to get wrong

- **`ElementId` ordering is load-bearing.** It breaks sibling ties, so a
  C#/TypeScript disagreement reorders user text. Compare the pair
  `(ReplicaId, Seq)`: the replica's 16 bytes in RFC 4122 big-endian order, then
  `Seq`. Never `Guid.CompareTo`, never string forms, never Postgres `uuid`
  collation. Comparing `ReplicaId` alone happens to give the same answer — the
  authors' reference implementation does that — but §5 follows the paper. (§5)
- **There is no Lamport clock, on purpose.** Nothing in the comparator is a
  causal clock: the tie-break compares identities, which is why a dense
  per-replica counter suffices where RGA needed a Lamport timestamp. `Seq`
  starts at **0**. Adding an ordering timestamp would add a field nothing reads —
  see the decision log, §13.2.
- **The root sentinel has a `null` id**, not `(ReplicaId.Empty, 0)` — the latter
  is a legal element id and would collide. (§5)
- **64-bit values are decimal strings on the wire.** JSON numbers are doubles and
  do not round-trip above 2^53. TypeScript parses them as `BigInt`. (§6)
- **Inserts have two causal dependencies**, not one: `Parent`, and `RightOrigin`
  when the node is a right child. Deletes depend on their target. (§5)
- **Backward runs are not right-to-left scripts.** Arabic and Hebrew append in
  logical order and produce forward runs. Backward runs come from caret-left
  editing, paste, and some IME paths. (§5)
- **Code points, not UTF-16 units, and no normalization.** Normalizing would
  break element identity. (§7)

## Layering

```
Editor.Api → Editor.Infrastructure, Editor.Domain, Crdt.Core
Editor.Infrastructure → Editor.Domain, Crdt.Core
Editor.Domain → Crdt.Core
Crdt.Core → BCL only
```

Enforced twice, because neither alone is enough: reflection over compiled
assemblies catches a forbidden package that is actually used, and parsing the
project files catches a declared reference the compiler elided because nothing
used it. Both live in `tests/Crdt.Core.Tests/ArchitectureTests.cs`.

## What Phase 1 learned the hard way

Four things were wrong and were only found by running something, not by reading:

- **Definition 4 is over the full element order, tombstones included.** Checking
  it against the visible order is systematically wrong the moment anything is
  deleted, because a tombstoned origin simply vanishes from the analysis.
- **Left origin comes from the visible order; right origin from the order
  including tombstones.** That asymmetry is the paper's (Algorithm 1 lines 23-24,
  arXiv §5.1). Taking both from one list silently redefines the property.
- **Causal stability does not license collecting a tombstone.** It means everyone
  saw the delete, not that nobody will reference the element — right origins can
  name tombstones. See §5's four collection conditions.
- **Backward run contiguity has no replica-count threshold.** §13.6 records the
  measurement that disproved the one this project used to assert.

A near-100% pass rate on an observed property usually means the generator is not
reaching the interesting shape. That is how the false boundary above survived
its first measurement.

## Running things

Requires the .NET 10 SDK, Node 22, and Docker.

```bash
# .NET: build and test everything
./scripts/run-tests.sh

# Client
cd client && npm ci && npm run lint && npm run typecheck && npm test

# Full stack
cp .env.example .env      # then set POSTGRES_PASSWORD
docker compose up --build
curl http://localhost:8080/health/live
```

### Toolchain quirks worth knowing

- **Use `./scripts/run-tests.sh`, not `dotnet test`.** On .NET 10 the SDK routes
  `dotnet test` through Microsoft.Testing.Platform, and its bridge does not
  discover tests in xunit.v3 4.0.0 projects: every project reports
  "Zero tests ran" (exit code 5) while the same assemblies run correctly when
  executed directly. The script uses `dotnet run`, which invokes xunit's
  in-process runner and propagates exit codes. Recheck when either package
  updates.
- **Testcontainers tests skip without Docker locally and fail in CI.** That is
  deliberate: local runs stay usable, but Phase 0 done-when (d) cannot be voided
  by a runner that quietly lost its daemon. The switch is the `CI` environment
  variable.
- **`dotnet format --verify-no-changes` runs in CI.** Run it before pushing.
- **`Crdt.Core.Tests` carries a VSTest adapter as well as xunit.v3's own
  runner.** Stryker drives VSTest directly and does not support
  Microsoft.Testing.Platform (stryker-net#3094), so without the adapter it
  discovers zero tests and exits 0 — a green gate measuring nothing. Do not
  remove those two package references thinking they are leftovers.
- **`CRDT_PROPERTY_CASES` lowers the per-property case count.** It exists for
  mutation runs, which execute the suite once per mutant. The full 10,000-case
  gate runs unconditionally in CI, so the override cannot weaken what the build
  enforces.
- **TypeScript is pinned to 5.x** even though 7.x exists (§3). Do not bump a
  pinned dependency without asking.

## Commits

Conventional commits, one logical change each, and every commit leaves the build
green. Say *why* in the body, not just what — the diff already shows what. When
a change uses a new C# 14 feature, say why it made the code clearer; new language
features are not used for their own sake. (§12)
