---
name: run-tests-locally
description: >
  Run the LearnStack test suites locally — unit, integration (Testcontainers),
  architecture, end-to-end — and interpret common failure modes. USE FOR: before
  pushing a commit, debugging a CI failure on a fresh checkout, isolating a single
  failing test. DO NOT USE FOR: writing new tests (use the per-suite skills:
  `add-integration-test`, `add-architecture-test`), running production migrations,
  or load testing.
---

# Running tests locally

## Purpose

Walk every test suite locally, with the correct prereqs, the right `dotnet test`
flags, and a triage map for the most common failure shapes.

## When to use

- Before pushing a commit / opening a PR.
- A CI run failed; you want to reproduce locally.
- You want to isolate one failing test before iterating.

## When not to use

- Generating coverage reports for release — no CI job collects coverage; this
  step is local-only and optional.
- Writing new tests — different skills cover authoring.
- Production data migrations or seed scripts.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Suite | Yes | `unit` / `integration` / `architecture` / `contract` / `frontend` / `public-demo` (P02d-7). Phase 06 owns the broader Playwright Test/axe suite. |
| Filter | No | `--filter <expr>` to run a subset. |
| Docker available? | Integration (`Requires=Docker`) | A running daemon for PostgreSQL and the pinned Keycloak/PostgreSQL provider fixture. Other adapters retain their named triggers. |

## Workflow

### Step 1: One-time setup

```bash
# Required toolchain
dotnet --version    # 10.0.x
node --version      # 22.23.1 (frontend/.nvmrc)
pnpm --version

# Restore — from the directories that hold the solution and the workspace.
# There is no solution and no package.json at the repository root, so both
# commands fail there. `make install` is these two lines, after its `.env` and
# `hooks` prerequisites (which copy `.env.example` and point core.hooksPath at
# `.githooks/`).
(cd backend && dotnet restore LearnStack.slnx)
(cd frontend && pnpm install --frozen-lockfile)

# Docker must be running for Database/ and TenancyIdentity/ provider cases
docker info >/dev/null && echo "docker OK"
```

### Step 2: Test-suite map

```
backend/tests/
  LearnStack.Tests.Unit/           # No DB, no Docker. Pure unit tests.
  LearnStack.Tests.Architecture/   # Reflection + Roslyn + migration-scan rules.
  LearnStack.Tests.Integration/    # WebApplicationFactory HTTP tests (no Docker) AND the
                                   # Postgres (learnstack_app) and TenancyIdentity/
                                   # Keycloak/Postgres, split by [Trait("Requires","Docker")].
  LearnStack.Tests.Contract/       # OpenAPI / SDK contract assertions.

frontend/apps/web/                 # Guarded Vitest unit/component/control suites.
scripts/demo.mjs                   # Shared local/CI production lifecycle.
frontend/apps/web/scripts/
  verify-public-demo.mjs           # P02d-7 Playwright library + Lighthouse proof.
                                   # Phase 06 owns full Playwright Test/axe flows.
```

### Step 3: Run unit tests

Fastest; no Docker required.

```bash
dotnet test backend/tests/LearnStack.Tests.Unit \
  --no-restore \
  --logger "console;verbosity=normal"
```

Typical runtime: < 30 s. Failures here are almost always domain-logic bugs.

### Step 4: Run architecture tests

Also fast; no Docker. Failures here mean **structural drift** — the code violates
a documented rule.

```bash
dotnet test backend/tests/LearnStack.Tests.Architecture \
  --no-restore
```

Common failure messages and fixes, from the rules that are **implemented today**.
A rule you expected and do not see here is probably **Registered** against a later
phase — check its Status line in
[21-architecture-tests-catalogue.md](../../../docs/standards/21-architecture-tests-catalogue.md)
before concluding a net is under you.

| Message | Fix |
|---------|-----|
| `ModuleDomain_DoesNotDependOn_OtherModuleDomain` | Depend on the other module's `Application.Contracts`, never its `Domain`. |
| `ModuleDomain_DoesNotDependOn_AnyApplicationOrInfrastructure` | Dependency direction is inverted; see [add-backend-module](../add-backend-module/SKILL.md). |
| `Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork` | A context opened its own connection — register it with `AddModuleDbContext<T>`, per [ADR-0040](../../../docs/decisions/0040-ambient-unit-of-work.md). |
| `Aggregates_With_Optimistic_Concurrency_Map_RowVersion` | The `row_version` mapping is missing a save behaviour; the token stays 0 and every ETag comparison is meaningless ([ADR-0039](../../../docs/decisions/0039-optimistic-concurrency-token.md)). |
| `Modules_Do_Not_Reference_DeploymentMode` | The composition root branches on the mode; modules never. |
| `Modules_Do_Not_Inject_IEventBus_Directly` | The only sanctioned publisher is the outbox processor; a module enqueues in the outbox — through `IOutbox`, which Phase 02b ships. |
| `Modules_Do_Not_Reference_Sentry_SDK_Directly` | Capture through `IErrorTrackingProvider`. |
| `Handlers_Return_Result` | A handler's response type is not a `Result` — return `Result<T>` or `Result<None>`, or the pipeline's `IResultBase`-constrained behaviors skip it. |
| `MediatR_Pipeline_Order_Matches_Canonical_Sequence` | A behavior moved; the eight-step order is fixed by [ADR-0032](../../../docs/decisions/0032-exception-handling-logging-and-observability.md). |
| `Integration_Event_TopicNames_FollowConvention` | Topic isn't `learnstack.{module}.{aggregate}`. |
| `No_Source_Folder_Named_Verticals` | A `Verticals/` folder exists; ADR-0018 forbids it. |
| `Every_Database_Test_Carries_The_Docker_Trait` | A test class under `Database/`, or one anywhere referencing a database/Keycloak fixture or collection, is missing `[Trait(RequiresDocker.Key, RequiresDocker.Value)]` and would run in the wrong CI job. |
| `Migrate_Target_Covers_Every_Migration_Chain` | A new chain exists that `make migrate` does not apply. |

### Step 5: Run integration tests

The assembly holds Docker-free HTTP/offline tests and Docker-backed proofs.
Everything under `Database/` requires PostgreSQL; `TenancyIdentity/` also includes
the pinned Keycloak fixture with its separate PostgreSQL database. **A running
Docker daemon is required** for both provider populations. Neither needs Valkey
or Kafka, because nothing the backend runs calls either
([ADR-0035](../../../docs/decisions/0035-demand-gated-infrastructure.md)). The two
are split by `[Trait("Requires","Docker")]`; CI further partitions Docker tests
into native/ordinary execution jobs behind the required integration aggregate. Locally
`--filter "Requires!=Docker"` gives you the Docker-free half.

```bash
dotnet test backend/tests/LearnStack.Tests.Integration \
  --no-restore \
  --logger "console;verbosity=normal" \
  --filter "FullyQualifiedName~Enrollment"     # subset
```

Typical runtime: 30 s – 3 min for the full suite, depending on Docker host.

Common failure shapes:

| Symptom | Likely cause |
|---------|--------------|
| Test passes locally, fails in CI | Race condition; check `await` chains. |
| Empty result where rows should exist | No `app.tenant_id` on this transaction, or the wrong one — RLS working as designed. Set it with `SchemaQueries.SetTenantAsync` as the transaction's first statement. |
| `relation "<table>" does not exist` | Migration didn't apply; check the module's `Persistence/Migrations`. |
| Docker container fails to start | Check Docker connectivity, image availability and the fixture's startup error. Testcontainers maps PostgreSQL to a random host port; a separate local 5432 listener is not a collision. |
| `password authentication failed` | The four roles are provisioned by the fixture from `infra/compose/postgres-init/02-create-roles.sql`; a failure there fails the fixture, not one test. |

### Step 6: Run frontend tests

```bash
cd frontend
pnpm test                # all discovered test packages; nonempty, zero skips/todos
pnpm typecheck           # workspace tsc --noEmit
pnpm lint                # workspace lint, including next lint
```


The root frontend runner reads actual Vitest reports, compares discovered files,
and refuses missing scripts, unreadable/empty reports, failures, skipped or todo
cases. Use `pnpm --filter @learnstack/web test <path>` only for focused development;
packet/PR validation uses the guarded workspace command. Backend Docker integration
also runs P5's disposable production Next/TLS fixture against the real API and
PostgreSQL; Node, pnpm, installed frontend dependencies and OpenSSL are required.
It owns ports 3000/3011 while running and refuses occupied ports without stopping
an existing user process. P6 adds separate production product modes for foundation,
pagination, presentation, concurrent isolation and freshness/failure states. Those
HTTP/HTML/RSC proofs do not claim browser or screen-reader execution; P6's manual
accessibility closeout is recorded with its tested scope in the
[packet closeout](../../../docs/roadmap/phase-02d-walking-skeleton.md#p02d-6-packet-closeout-2026-10-10).
New UI changes require their own applicable manual checks; P7 supplies the narrow
seeded browser demo described below.

ADR-0055's separate admission fixture adds a narrow stock-Next browser navigation
probe and development HMR admission controls. Its Docker integration run requires
an installed stock Chromium browser: Chrome or Brave at the supported macOS
application paths, or Chrome/Chromium at the supported Linux executable paths.
Missing browser installation fails the proof; it never skips. The required CI
integration job checks `/usr/bin/google-chrome` and its version before running.
The fixture launches headless with its own disposable profile and trust limited
to its certificate's public-key fingerprint; it does not use a personal profile.
Browser observations cover a refused Flight followed by stock Next's fresh document
navigation and separately counted API calls. HMR controls cover source recompilation
and a fresh live site snapshot in the same development process. The
[Step 3 record](../../../docs/roadmap/phase-02d-walking-skeleton.md#adr-0055-step-3--production-admission-proof-and-closeout)
owns actual execution, completed reviews and verified fixes. This probe remains
separate from P7's browser demo/Lighthouse and Phase 06's full Playwright Test/axe.

> **`pnpm test:a11y` and `pnpm test:e2e` do not exist.** P7 directly pins the
> development tools `playwright@1.64.0` and `lighthouse@13.5.0` in the frontend
> workspace. Lighthouse brings transitive development `axe-core@4.14.0`; this is
> not an application axe dependency or a standalone axe suite. There is no
> `@playwright/test` project. Phase 06 owns that broader harness, per
> [Testing Standards § End-to-End Tests](../../../docs/standards/06-testing.md).
> P6's blocking `jsx-a11y` and product-semantic controls and recorded manual smoke
> remain separate evidence from P7's automated Lighthouse accessibility score.

### Public demo: managed browser and Lighthouse

From a fresh checkout without root `.env`, web `.env.local` or
`artifacts/public-demo`, install the pinned managed Chromium and run the shared
lifecycle:

```bash
(cd frontend && pnpm install --frozen-lockfile)
(cd frontend && pnpm exec playwright install --with-deps chromium)
make demo DEMO_MODE=ci
```

Docker, .NET, Node, pnpm, OpenSSL and make must be available; the fixed application
and Compose ports must be free. CI mode creates private disposable configuration,
uses a unique tmpfs-backed Compose project, migrates and seeds through the normal
pipeline, stops its Keycloak, and builds the production API/native HTTPS renderer.
It runs nine linked product pages and 27 cold Lighthouse audits over one unchanged
API/build/database, with real admission pacing and certificate-specific trust.
Missing browser installation fails the proof rather than skipping it.

The hosted workflow bounds APT mirror acquisition with one retry and 15-second
HTTP/HTTPS timeouts in an exclusively created temporary configuration fragment.
It checks the effective settings as root without evaluation, then removes only
that fragment on shell exit. Sources, package verification, the complete
Playwright dependency set and the five-minute installation limit stay intact.
This is hosted setup handling, not a workstation prerequisite or a guarantee of
total setup duration. The actual workflow-shell controls live in
`frontend/apps/web/src/test/public-demo-ci.test.ts`.

The workflow's outer setup/proof allowance ends at minute 17 from its first
step, reserving three minutes within the same 20-minute job ceiling. The native
proof and cleanup retain their separate 12-minute and 30-second caps. Sandbox
cleanup and workflow-receipt commands receive TERM after 15 seconds and KILL
after five more; artifact upload retains its one-minute step limit. The nominal
35 + 20 + 20 + 60-second tail leaves 45 seconds for runner/post overhead, not a
worst-case scheduling guarantee. A forced stop or unconfirmed cleanup fails.
This job restores the existing NuGet cache without a save post-hook; cache
misses still use ordinary dependency restoration.

On GitHub-hosted Linux runners, run `node scripts/ci-public-chromium.mjs prepare`
after installation and before the lifecycle, then run
`node scripts/ci-public-chromium.mjs cleanup` in an always-running cleanup step.
The helper discovers Playwright's exact executable and performs a sandbox-enabled
20-second launch smoke with a disposable browser profile. If Ubuntu's AppArmor
user-namespace restriction is enabled, it loads one invocation-owned profile
allowing `userns` only for that exact executable; the source and ownership marker
stay under `RUNNER_TEMP`, and cleanup unloads only that profile. It preserves the
kernel restriction and foreign policies/files. This follows
[Chromium's AppArmor guidance](https://chromium.googlesource.com/chromium/src/+/main/docs/security/apparmor-userns-restrictions.md)
with a literal attachment path. The helper refuses local/self-hosted execution;
it does not change workstation policy. Run its isolated controls with
`node --test scripts/ci-public-chromium.test.mjs`; these use fake commands and
an executable fixture without mutating host AppArmor. A passing Linux job remains
required evidence of the actual runner prerequisite and complete demo/audit flow.

Review `artifacts/public-demo/receipt.json`, the 27 JSON/HTML report pairs and browser
screenshots. Proof and owned cleanup must both pass. Archive only this sanitized
artifact directory; private configuration, certificates, keys, profiles and raw
process logs are excluded. Existing artifacts must be moved aside before another
run so stale evidence cannot pass. The
[P7 delivery record](../../../docs/roadmap/phase-02d-walking-skeleton.md#p02d-7-step-3--browser-and-audit-proof-2026-10-11)
owns local proof/review evidence; the packet's Step 4 record and
[branch protection](../../../.github/CONTRIBUTING.md#branch-protection-settings-on-main)
own real CI receipts and required-check activation. Local success alone does not
establish activation.

For a human foreground demo, use `make demo` after the root README's manual
hosts/CA/leaf setup. Local mode retains Compose data and joins its owned API/web
processes on Ctrl+C; it does not run the CI browser/audit proof.

### Step 7: Single-test focus

`dotnet test --filter`:

```bash
# Class
dotnet test --filter "FullyQualifiedName~EnrollmentCreateTests"

# Method
dotnet test --filter "FullyQualifiedName~EnrollmentCreateTests.Create_succeeds"

# Docker trait — native CI additionally uses NativeCrashProof=true and its complement
dotnet test --filter "Requires=Docker"
```

`vitest`:

```bash
# From frontend/; the guarded workspace runner accepts no filters.
pnpm --filter @learnstack/web test usage-meter             # path filter
pnpm --filter @learnstack/web test -t "shows danger tone"  # name filter
```

### Step 8: Coverage (optional)

```bash
# From the repository root; collect behavioral suites separately.
dotnet test backend/tests/LearnStack.Tests.Unit \
  --collect:"XPlat Code Coverage" --results-directory coverage/unit
dotnet test backend/tests/LearnStack.Tests.Integration \
  --collect:"XPlat Code Coverage" --results-directory coverage/integration
dotnet test backend/tests/LearnStack.Tests.Architecture
reportgenerator -reports:"coverage/**/coverage.cobertura.xml" \
                -targetdir:"coverage/html" \
                -reporttypes:Html
```

Keep the architecture suite uninstrumented. In P02d-1, a solution-wide Coverlet
run injected a tracker type that NetArchTest's IL/reflection sweep could not resolve,
raising `TypeLoadException`; unit and integration coverage plus a normal architecture
run passed. Do not skip or weaken the structural test to make coverage succeed.

Targets per
[06-testing.md § Coverage Targets](../../../docs/standards/06-testing.md): Domain
≥ 90% line and ≥ 80% branch, Application ≥ 80% line, Infrastructure adapters
≥ 70% line. **Coverage gates nothing** — no CI job collects it and the standard
does not make it a blocker. The architecture, isolation and contract suites are
the hard gates.

### Step 9: Reproduce a CI failure

CI runs the **whole solution** with a trait filter, not one project — and it builds
with `CI=true`, which is what turns `TreatWarningsAsErrors` on
(`backend/Directory.Build.props`). A local build without it is green on exactly the
warnings the required check rejects; that shipped once, in Packet 4, and `CI=true`
is now the only way this repository is built.

```bash
# Pull the exact branch CI ran
git checkout <branch>

# The same restore CI does
make install

# The `backend` job — build with warnings as errors, then the Docker-free half
(cd backend && CI=true dotnet build LearnStack.slnx --no-restore --configuration Release)
(cd backend && dotnet test LearnStack.slnx \
  --no-restore --no-build --configuration Release \
  --filter "Requires!=Docker" --logger trx)

# The complete Docker complement; CI requires both native/ordinary partitions
(cd backend && dotnet test LearnStack.slnx \
  --no-restore --no-build --configuration Release \
  --filter "Requires=Docker" --logger trx)
```

`--logger trx` carries no `LogFileName` on purpose: a fixed name makes all four
projects write the same path in the same results directory, and three assemblies'
outcomes are silently overwritten.

ADR-0062's `scripts/ci-native-tests.py` is a closed hosted-CI owner, not a local
launcher. Its separate native VM may verify/set the fixed collector through the
named provisioning owner; no local/shared/self-hosted/production authority is
implied. Both jobs require exact discovery/result case IDs, zero skips and owned
completion; the unchanged required aggregate verifies both complete artifacts.
Run the offline `scripts/test-ci-native-provisioning.py` and
`scripts/test-ci-native-tests.py` controls locally without changing host policy.
Their source/fixture checks do not qualify actual hosted cgroups, native exits or
twenty-minute whole-job fit; the owning P02b-4 CI record carries those results.

For flaky tests, run with `--blame-hang` and `--blame-hang-timeout`:

```bash
dotnet test --blame-hang --blame-hang-timeout 5min
```

The `public demo + lighthouse` job uses the same `make demo DEMO_MODE=ci` lifecycle.
Its real receipt and required-check status are recorded in the packet's Step 4
record and CONTRIBUTING; do not infer them from a local audit.

## Validation

- The relevant suite passes locally with the same `dotnet --version` and
  `pnpm --version` CI uses.
- For integration suites, Docker is running; PostgreSQL uses random host ports.
  The P5 native renderer fixtures require free 3000/3011 and fail on collisions.
- A failing test message points at the specific rule / scenario it violates.
- For frontend changes, `pnpm test`, `pnpm lint` and `pnpm typecheck` are clean.
  Public-route changes also run the applicable P7 browser/Lighthouse proof. The
  full axe suite joins this list in Phase 06. Accepted P02d-6/G43 requires failing
  applicable `jsx-a11y` rules and localized page-semantics checks, plus manual
  keyboard, focus, reflow, contrast and screen-reader evidence. Acceptance does
  not claim those controls pass; implementation records their actual results in
  [Phase 02d](../../../docs/roadmap/phase-02d-walking-skeleton.md#p02d-6-accepted-answers).

## Common pitfalls

- **No Docker.** The `Requires=Docker` integration cases need it. Start Docker
  Desktop first, or run `--filter "Requires!=Docker"`.
- **Stale lockfile.** `pnpm install --frozen-lockfile` after a `package.json`
  change — the lockfile must match.
- **Skipped architecture test.** Forbidden. If a test is marked `[Skip]`, treat
  it as a bug.
- **Port collisions.** Testcontainers PostgreSQL uses a random host port and can
  coexist with `make dev` on 5432. Stop your own web process before P5's native
  renderer fixtures: they require free 3000/3011 and never kill a foreign listener.
- **`--no-build` after a source change.** Drop the flag — the test would run
  against stale binaries.
- **Assuming an accessibility gate exists.**
  [16-accessibility.md](../../../docs/standards/16-accessibility.md) makes WCAG
  2.2 AA binding, and Phase 06 brings the axe suite that enforces it. Reading the
  standard does not prove implementation. Accepted P02d-6/G43 requires earlier
  failing lint/semantics controls and actual manual assistive-technology evidence;
  DOM assertions cannot substitute for a screen reader. Delivery is recorded in
  [Phase 02d](../../../docs/roadmap/phase-02d-walking-skeleton.md#p02d-6-accepted-answers).
- **CI-only failures.** Usually a race or timing assumption. Use `--blame-hang`
  + `--blame-crash` locally.
