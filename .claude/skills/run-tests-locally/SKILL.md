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
| Suite | Yes | `unit` / `integration` / `architecture` / `contract` / `frontend`. (`e2e` arrives in Phase 02d.) |
| Filter | No | `--filter <expr>` to run a subset. |
| Docker available? | Integration (`Requires=Docker`) | A running daemon. Postgres only — no Valkey, no Kafka. |

## Workflow

### Step 1: One-time setup

```bash
# Required toolchain
dotnet --version    # 10.0.x
node --version      # 20+ for the frontend
pnpm --version

# Restore — from the directories that hold the solution and the workspace.
# There is no solution and no package.json at the repository root, so both
# commands fail there. `make install` is these two lines, after its `.env` and
# `hooks` prerequisites (which copy `.env.example` and point core.hooksPath at
# `.githooks/`).
(cd backend && dotnet restore LearnStack.slnx)
(cd frontend && pnpm install --frozen-lockfile)

# Docker must be running for the integration suite's Database/ cases
docker info >/dev/null && echo "docker OK"
```

### Step 2: Test-suite map

```
backend/tests/
  LearnStack.Tests.Unit/           # No DB, no Docker. Pure unit tests.
  LearnStack.Tests.Architecture/   # Reflection + Roslyn + migration-scan rules.
  LearnStack.Tests.Integration/    # WebApplicationFactory HTTP tests (no Docker) AND the
                                   # Testcontainers Postgres suite under Database/ (as
                                   # learnstack_app), split by [Trait("Requires","Docker")].
  LearnStack.Tests.Contract/       # OpenAPI / SDK contract assertions.

frontend/apps/web/                 # Vitest. The axe-core and Playwright suites
                                   # arrive with the first content-bearing pages
                                   # in Phase 02d, which is also when CI's
                                   # `lighthouse budget` job stops being deferred.
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
| `Modules_Do_Not_Inject_IEventBus_Directly` | The only sanctioned publisher is the outbox processor; enqueue through `IOutbox`. |
| `Modules_Do_Not_Reference_Sentry_SDK_Directly` | Capture through `IErrorTrackingProvider`. |
| `Handlers_Return_Result` | A handler threw where it should return `Result.Fail(...)`. |
| `MediatR_Pipeline_Order_Matches_Canonical_Sequence` | A behavior moved; the eight-step order is fixed by [ADR-0032](../../../docs/decisions/0032-exception-handling-logging-and-observability.md). |
| `Integration_Event_TopicNames_FollowConvention` | Topic isn't `learnstack.{module}.{aggregate}`. |
| `No_Source_Folder_Named_Verticals` | A `Verticals/` folder exists; ADR-0018 forbids it. |
| `Every_Database_Test_Carries_The_Docker_Trait` | A `Database/` test class is missing `[Trait(RequiresDocker.Key, RequiresDocker.Value)]` and would run in the wrong CI job. |
| `Migrate_Target_Covers_Every_Migration_Chain` | A new chain exists that `make migrate` does not apply. |

### Step 5: Run integration tests

The assembly holds two kinds. The `WebApplicationFactory` HTTP tests need no
Docker; everything under `Database/` does, and **a running Docker daemon is
required** for it — Postgres only, no Valkey and no Kafka, because nothing the
backend runs calls either
([ADR-0035](../../../docs/decisions/0035-demand-gated-infrastructure.md)). The two
are split by `[Trait("Requires","Docker")]`, and CI runs them as two jobs; locally
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
| Docker container fails to start | Port collision on 5432 — stop a local Postgres. Testcontainers maps a random host port, so this only bites when something else already holds the container port. |
| `password authentication failed` | The four roles are provisioned by the fixture from `infra/compose/postgres-init/02-create-roles.sql`; a failure there fails the fixture, not one test. |

### Step 6: Run frontend tests

```bash
cd frontend/apps/web
pnpm test                # vitest
pnpm typecheck           # tsc --noEmit
pnpm lint                # next lint — what `pnpm -r lint` runs in CI
```

> **`pnpm test:a11y` and `pnpm test:e2e` do not exist yet.** `package.json`
> defines `dev`, `build`, `start`, `lint`, `typecheck` and `test`, and neither
> `axe-core` nor `@playwright/test` is a dependency. Both arrive in **Phase 02d**
> with the first content-bearing public pages — the same phase that activates
> CI's deferred `lighthouse budget` job. Until then there is no accessibility or
> end-to-end gate to run.

### Step 7: Single-test focus

`dotnet test --filter`:

```bash
# Class
dotnet test --filter "FullyQualifiedName~EnrollmentCreateTests"

# Method
dotnet test --filter "FullyQualifiedName~EnrollmentCreateTests.Create_succeeds"

# Trait — the only one this repository sets, and the one CI routes on
dotnet test --filter "Requires=Docker"
```

`vitest`:

```bash
pnpm test usage-meter             # path filter
pnpm test -t "shows danger tone"  # name filter
```

### Step 8: Coverage (optional)

```bash
dotnet test --collect:"XPlat Code Coverage"
reportgenerator -reports:"**/coverage.cobertura.xml" \
                -targetdir:"coverage-html" \
                -reporttypes:Html
```

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

# The `backend-integration` job — the exact complement, so every test runs once
(cd backend && dotnet test LearnStack.slnx \
  --no-restore --no-build --configuration Release \
  --filter "Requires=Docker" --logger trx)
```

`--logger trx` carries no `LogFileName` on purpose: a fixed name makes all four
projects write the same path in the same results directory, and three assemblies'
outcomes are silently overwritten.

For flaky tests, run with `--blame-hang` and `--blame-hang-timeout`:

```bash
dotnet test --blame-hang --blame-hang-timeout 5min
```

## Validation

- The relevant suite passes locally with the same `dotnet --version` and
  `pnpm --version` CI uses.
- For integration suites, Docker is running and nothing else holds 5432.
- A failing test message points at the specific rule / scenario it violates.
- For frontend changes, `pnpm test`, `pnpm lint` and `pnpm typecheck` are clean.
  The accessibility gate joins this list in Phase 02d, with the suite that
  enforces it.

## Common pitfalls

- **No Docker.** The `Requires=Docker` integration cases need it. Start Docker
  Desktop first, or run `--filter "Requires!=Docker"`.
- **Stale lockfile.** `pnpm install --frozen-lockfile` after a `package.json`
  change — the lockfile must match.
- **Skipped architecture test.** Forbidden. If a test is marked `[Skip]`, treat
  it as a bug.
- **Port collisions.** A local Postgres on 5432 collides with the fixture's
  container. Stop it, or let Testcontainers pick the host port (it does) and stop
  publishing 5432 from `make dev`.
- **`--no-build` after a source change.** Drop the flag — the test would run
  against stale binaries.
- **Assuming an accessibility gate exists.**
  [16-accessibility.md](../../../docs/standards/16-accessibility.md) makes WCAG
  2.2 AA binding, and Phase 02d is what makes a suite enforce it. Reading the
  standard is the gate until then.
- **CI-only failures.** Usually a race or timing assumption. Use `--blame-hang`
  + `--blame-crash` locally.
