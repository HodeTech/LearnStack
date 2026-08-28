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

- Generating coverage reports for release. CI does that.
- Writing new tests — different skills cover authoring.
- Production data migrations or seed scripts.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Suite | Yes | `unit` / `integration` / `architecture` / `e2e`. |
| Filter | No | `--filter <expr>` to run a subset. |
| Docker available? | Integration / E2E | Required for Testcontainers + LiveKit. |

## Workflow

### Step 1: One-time setup

```bash
# Required toolchain
dotnet --version    # 10.0.x
node --version      # 20+ for the frontend
pnpm --version

# Restore (locked)
dotnet restore --locked-mode
pnpm install --frozen-lockfile

# Docker must be running for integration / E2E
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

frontend/apps/web/                 # Vitest + axe-core + Playwright (E2E).
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

Common failure messages and fixes:

| Message | Fix |
|---------|-----|
| `Every_TenantOwned_Entity_Has_TenantId` | Missing `TenantId` property; see [add-tenant-owned-entity](../add-tenant-owned-entity/SKILL.md). |
| `Every_TenantOwned_Table_HasRls_With_AppTenantId` | Migration missing `ENABLE ROW LEVEL SECURITY` + the policy. |
| `Integration_Event_Handlers_Use_InboxGuard` | Handler skipped `IsAlreadyProcessedAsync`; see [add-integration-event](../add-integration-event/SKILL.md). |
| `Dapr_PubSub_TopicNames_FollowConvention` | Topic isn't `learnstack.{module}.{aggregate}`. |
| `Modules_Do_Not_Inject_Valkey_Directly` | Use `ICacheService` not `IConnectionMultiplexer`. |
| `LearnStack_Modules_DoNotReference_Hub` | Hub URL or namespace referenced outside the dedicated adapter. |
| `No_Source_Folder_Named_Verticals` | A `Verticals/` folder exists; ADR-0018 forbids. |
| `Core_Modules_HaveNo_DomainSpecific_Names` | A `Cefr`, `English`, `Asana`, etc. name appears in core. |

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
| Docker container fails to start | Port collision (5432, 6379) — stop local Postgres / Valkey. |
| Test hangs | A handler awaiting Dapr in the dev fallback path; check `IEventBus` registration. |

### Step 6: Run frontend tests

```bash
cd frontend/apps/web
pnpm test                # vitest
pnpm test:a11y           # axe-core
pnpm test:e2e            # playwright (requires backend running)
```

For E2E:

```bash
# Terminal 1
make dev                 # brings up backend + frontend via docker-compose

# Terminal 2
cd frontend/apps/web
pnpm test:e2e
```

### Step 7: Single-test focus

`dotnet test --filter`:

```bash
# Class
dotnet test --filter "FullyQualifiedName~EnrollmentCreateTests"

# Method
dotnet test --filter "FullyQualifiedName~EnrollmentCreateTests.Create_succeeds"

# Trait (xUnit)
dotnet test --filter "Trait=tenant-isolation"
```

`vitest`:

```bash
pnpm test usage-meter             # path filter
pnpm test -t "shows danger tone"  # name filter
```

`playwright`:

```bash
pnpm exec playwright test e2e/enrollment.spec.ts --headed --debug
```

### Step 8: Coverage (optional)

```bash
dotnet test --collect:"XPlat Code Coverage"
reportgenerator -reports:"**/coverage.cobertura.xml" \
                -targetdir:"coverage-html" \
                -reporttypes:Html
```

Targets per
[06-testing.md](../../../docs/standards/06-testing.md): Domain ≥ 90%,
Application ≥ 80%, Infrastructure ≥ 50%. CI fails on regression.

### Step 9: Reproduce a CI failure

```bash
# Pull the exact branch CI ran
git checkout <branch>

# Use the same locked deps
dotnet restore --locked-mode
pnpm install --frozen-lockfile

# Run the same command CI ran (see .github/workflows/*.yml)
dotnet test backend/tests/LearnStack.Tests.Integration \
  --no-restore \
  --logger "trx;LogFileName=results.trx"
```

For flaky tests, run with `--blame-hang` and `--blame-hang-timeout`:

```bash
dotnet test --blame-hang --blame-hang-timeout 5min
```

## Validation

- The relevant suite passes locally with the same `dotnet --version` and
  `pnpm --version` CI uses.
- For integration suites, Docker is running; no port collisions on 5432 / 6379 /
  9092 / 8200.
- A failing test message points at the specific rule / scenario it violates.
- For frontend changes, `pnpm test:a11y` is clean — `axe-core` violations fail
  the test by design.

## Common pitfalls

- **No Docker.** Integration / E2E require it. Start Docker Desktop first.
- **Stale lockfile.** `pnpm install --frozen-lockfile` after a `package.json`
  change — the lockfile must match.
- **Skipped architecture test.** Forbidden. If a test is marked `[Skip]`, treat
  it as a bug.
- **Port collisions.** Local Postgres / Valkey on default ports collides with
  Testcontainers. Stop them.
- **`--no-build` after a source change.** Drop the flag — the test would run
  against stale binaries.
- **Ignoring `axe-core` failures.** Accessibility violations are blocking per
  [16-accessibility.md](../../../docs/standards/16-accessibility.md).
- **CI-only failures.** Usually a race or timing assumption. Use `--blame-hang`
  + `--blame-crash` locally.
