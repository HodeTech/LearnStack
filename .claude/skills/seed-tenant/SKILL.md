---
name: seed-tenant
description: >
  Provision and converge LearnStack's local demo tenants through the real request
  pipeline. USE FOR: first-run seed, reruns after migrations, request-level isolation
  fixtures and data-only showcases. P02d-2 includes enabled locales, built-in and
  tenant-specific definitions, complete branding and scoped Course/Lesson content.
  DO NOT USE FOR: production provisioning, production reseeding, Hub licensing,
  identity setup or domain-specific application branches.
---

# Seeding a tenant

## Purpose and current scope

The [P02d-2 accepted package](../../../docs/roadmap/phase-02d-walking-skeleton.md#p02d-2-decision-package-2026-10-02)
records the decisions. Step 4 implements the complete inventory and contextual
verification; implementation review is pending. Public API reads and browser
rendering remain P02d-4 and P02d-5–7. Do not claim a rendered demo from seed alone.

[SeedData](../../../backend/src/LearnStack.Tools.Seeder/SeedData.cs) owns all demo
identities, schemas, labels, palettes, bodies and computed inventory. The runner
has no English/Yoga branch. Adding a pure content shape changes data, not a module;
stateful entitlement and external capabilities remain the
[genericity boundary](../../../docs/architecture/01-platform-vision.md#genericity-boundary).

## When to use

- Local development first run or safe repeat.
- Request-level isolation tests using the shipped data declaration.
- A new data-only showcase whose required aggregates already exist.

Production provisioning is a Hub operator action; licensing belongs to the Hub.
This skill never resets or reseeds production, creates a platform administrator,
or supplies enrollment, learner identity, commerce or live-session data.

## Inputs

The data set is the input; there is no `--tenants` flag. The production entry point
uses `SeedData.All`. `SeedRunner.RunAsync` accepts alternate declarations for tests.
Connection configuration arrives in the environment, not command-line arguments.
`scripts/seed.sh` refuses a role other than `learnstack_app`; using an owner or
BYPASSRLS role would erase the evidence the seed is meant to provide.

## Workflow

### 1. Check the environment and migrations

```bash
make seed
```

This target depends on migration and invokes `scripts/seed.sh`, which checks compose
health and both Keycloak realms before running the .NET tool. The direct tool runs
from `backend/`, where `global.json` pins the SDK:

```bash
(cd backend && ConnectionStrings__Default="<learnstack_app connection string>" \
    dotnet run --project src/LearnStack.Tools.Seeder --nologo)
```

Do not put a real connection string on `argv` or print it. The script reads the
environment first and falls back to the local `.env`; the agent should not expose
that file. There is no `make seed-tenant`, `infra/seed/`, or seed-reset command.

### 2. Read the declaration

The existing fixed tenant, organization, host and built-in IDs are preserved.
English's host remains tenant-wide; Yoga's host maps to Studio One. The exact
[accepted inventory](../../../docs/roadmap/phase-02d-walking-skeleton.md#seed-inventory-and-ownership)
and `SeedData` own the choices; this skill does not keep a second literal list.

The inventory includes enabled/default locales, unchanged Active `card`/`plain`,
each tenant's own Active type/taxonomy, one tenant-wide `branding.theme`, and
courses/lessons with explicit exact pins, scope, policy, translations and state.
All body/schema/label data pass the ordinary validators. No remote media, fonts,
URLs or logo are seeded. Restricted publication grants no anonymous lesson access
([ADR-0050](../../../docs/decisions/0050-publication-and-course-content-access.md)).

### 3. Execute acts through the pipeline

[SeedRunner](../../../backend/src/LearnStack.Tools.Seeder/SeedRunner.cs) sends:

1. Provisioning, followed by default-organization verification, second organization
   and host mapping. The tenant starts Trial. Provisioning alone writes the
   sanctioned Tenant/Organization pair ([ADR-0042](../../../docs/decisions/0042-tenant-provisioning-cross-aggregate-transaction.md)).
2. Enabled locales, with the declared default first.
3. Built-in and tenant-specific definitions, each registered then published.
4. Complete branding, in tenant-wide scope.
5. Draft Course/Lesson creation and each translation, followed by each selected
   publication. Course and Lesson publication remain independent.

Every request gets a fresh composed trusted context. Provisioning writes unresolved;
verification reads resolved. Tenant-wide acts announce null organization. Scoped
roots, their translations and publication announce the exact root organization.
The runner never writes `ITenantContextAccessor.Current`, opens a private
transaction, calls a tenant setter, mutates EF state or executes seed SQL.

### 4. Converge or fail safely

[SeedVerification](../../../backend/src/LearnStack.Tools.Seeder/SeedVerification.cs)
checks exact identity, ownership, scope, pin, state and content before a skip.
Contextual module-owned `ISender` queries return immutable value DTOs and are audit
Off; they have no endpoint or unresolved/public marker. Internal ports retain typed
IDs; contract-local IDs cross the module boundary as Guid under ADR-0023.

- Missing acts write through the ordinary command.
- Completed acts skip before invoking a writer, so the second completed run changes
  no root, timestamp, version, generation or audit row.
- Creation accepts only the declared Draft intermediate or intended final state
  with matching immutable data. Definition creation accepts Draft/Active, with a
  separate publication act verifying Active.
- Existing translations are checked before draft-only insertion. JSON object
  property order is immaterial; descriptor arrays and authored strings remain exact.
- A typed uniqueness/concurrency/lifecycle race gets one fresh-scope completed
  postcondition check. Generic failures are never success and there is no retry loop.
- A mismatch stops nonzero; the runner does not overwrite, unpublish, rebind,
  reactivate, choose a newer revision or borrow another tenant's identity.
- If another run has not completed the exact act yet, this run fails safely. Re-run
  explicitly after the competitor finishes; a failed command is not convergence.

The process entry point catches an exception, logs failure and exits 1. Completed
acts remain durable; a later explicit run resumes the remaining acts.

### 5. Verify

[SeederTests](../../../backend/tests/LearnStack.Tests.Integration/Database/SeederTests.cs)
uses disposable migrated databases and writes as `learnstack_app`. It proves fresh
inventory, exact DTO/scope/state, unchanged rerun, interrupted recovery, a coordinated
provisioning race, semantic JSON equivalence and mismatches without overwrites.
`TenantIsolationHttpTests` checks both mapped host classes and filtered/raw RLS
customization reads; expected projections come from `SeedData`.

The [caller fence](../../../docs/standards/21-architecture-tests-catalogue.md#seeder_does_not_call_tenant_context_setters)
and planted companion catch calls, method groups, private transactions, accessor
assignment and announcing SQL. G20(a)'s literal reader observes the actual declaration
and fails on unreadable/empty data. The complete demo-literal production-branch guard
remains Registered for P02d-5/6 scope and P02d-7 exit.

### 6. Reach the hosts

The existing names are under `*.learnstack.local` and need hosts-file aliases for
local browsing. Development transport/host changes remain G32 in P02d-5. The web
middleware is still a scaffold; no browser render is supplied by this seed packet.
Do not add a host alias or change a deployment's reserved-host registry implicitly.

### 7. Reset only an explicitly disposable development environment

An exact rerun is the normal recovery. A reset destroys local volumes:

```bash
make clean
make dev
make seed
```

Never run the destructive reset without the user's authorization. Tests drop only
the database they created, preserving append-only audit controls in the shared stack.

## What later phases add

| Data / surface | Owner |
|---|---|
| Keycloak OIDC and realm reconciliation | Phase 02b |
| Users, memberships, roles, invitations and custom fields | Phase 03 |
| CMS pages/media, locale lifecycle and customization revision editors | Phases 03–04 |
| Versions, modules, items, scoring and completion rules | Phase 05 |
| Studio editors, branding override/merge and rich renderer coverage | Phase 06 |
| Enrollment, course access evaluation and progress | Phase 07 |
| Templates | Phase 08a |
| Availability, scheduling, classroom and reservations | Phases 08b–08c |
| Billing and commerce | Phase 09; proposed Course Marketplace Phase 09a |
| Hub entitlement projection | Phase 02c, demand-gated under ADR-0035 |

## Adding a showcase

Add its explicit records to `SeedData`, keep fixed identities unique, and supply
localized schemas/bodies/pins supported by shipped contracts. Derive inventory and
test expectations from the declaration. A new locale must be enabled before its
translation is written. Domain-specific application behavior requires the owning
phase's platform-feature decision; it cannot be hidden inside seed orchestration.
Keycloak users still come from compose realm imports, not this tool.
