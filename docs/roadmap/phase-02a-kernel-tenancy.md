# Phase 02a: Platform Kernel, Multi-Tenancy, Organization, and Foundation Sockets

> **Status (2026-08-09).** Phase 02a in progress. Packets 0–3 shipped; the 2026-08-08
> restructure re-scoped packets 4–10 and added packet 3b. Each packet is independently
> reviewable in its own commit, matching the
> [Phase 01 cadence](phase-01-repository-tooling.md). The order is dependency-driven: a
> later packet may consume any earlier packet's deliverables, never the reverse.
>
> | Packet | Title | State |
> |---|---|---|
> | 0 | Kickoff | ✅ [record](#delivery-record-packets-03) |
> | 1 | Foundation decisions | ✅ [record](#delivery-record-packets-03) |
> | 2 | Shared Kernel core | ✅ [record](#delivery-record-packets-03) |
> | 3 | Cross-cutting foundation | ✅ [record](#delivery-record-packets-03) |
> | 3b | Decision repair | ⏳ [scope](#packet-sequence) |
> | 4 | API conventions | ⏳ [scope](#packet-sequence) |
> | 5 | Foundation ports and default implementations | ⏳ [scope](#packet-sequence) |
> | 6 | Tenancy schema and the corrected RLS template | ⏳ [scope](#packet-sequence) |
> | 7 | Tenant and organization resolution, isolation, two tenants | ⏳ [scope](#packet-sequence) |
> | 8 | Tenant Customization foundation | ⏳ [scope](#packet-sequence) |
> | 9 | Audit infrastructure and the entitlement socket | ⏳ [scope](#packet-sequence) |
> | 10 | Architecture tests green and phase exit | ⏳ [scope](#packet-sequence) |
>
> **[`## Packet Sequence`](#packet-sequence) says which packet lands which part, in what
> order, and what gates it. [`## Scope`](#scope) says the same work grouped by subsystem —
> read it when you want the shape of a subsystem, read the sequence when you want the
> order. The shipped Packet 0–3 records are at the end of this document under
> [`## Delivery Record (Packets 0–3)`](#delivery-record-packets-03) and are not
> rewritten.**

## Goal

Build the runtime foundation everything else stands on — and **only** that.

Shared kernel conventions, cross-cutting concerns, API conventions, tenant +
organization resolution, tenant + organization isolation defense-in-depth, the
customization runtime read paths, durable audit, and the foundation **ports** with
their default implementations. Two seed tenants in unrelated domains, so that every
later phase is tested against the genericity claim rather than assuming it.

What this phase deliberately does **not** build: the Dapr, Kafka, APISIX and Vault
adapters, the Hub integration, signed licence keys, custom-domain TLS automation, and
`audit_log` partitioning. Each of those sits behind a port that ships here, has a
working default implementation that ships here, and has an owning phase and a written
trigger condition in
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md). The discriminator is the
one-way-door test: isolation, schema ownership and typed identifiers get more expensive
to add every week; a Dapr adapter does not.

[Phase 02d](phase-02d-walking-skeleton.md) follows immediately and renders both seed
tenants in a browser. [Phase 02b](phase-02b-events-auth.md) then adds identity and the
durable event path; [Phase 02c](phase-02c-hub-foundation.md) hangs off the spine rather
than sitting in it.

The decisions made in this phase are the ones that are most painful to reverse later.
They are codified in:

- [ADR-0003 Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md)
  (Amendment 1: Organization scope; **Amendment 3: corrected RLS template + database
  role model**)
- [ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md)
- [ADR-0018 Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md)
  (Amendment: genericity boundary)
- [ADR-0021 Feature-Based Entitlement](../decisions/0021-feature-based-entitlement.md)
- [ADR-0032 Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md)
- [ADR-0033 Audit Durability Model](../decisions/0033-audit-durability-model.md)
  (supersedes ADR-0016)
- [ADR-0035 Demand-Gated Infrastructure](../decisions/0035-demand-gated-infrastructure.md)

[ADR-0014 (Dapr)](../decisions/0014-adopt-dapr.md) and
[ADR-0015 (APISIX)](../decisions/0015-api-gateway-apisix.md) remain accepted decisions
about **what** LearnStack uses; ADR-0035 decides **when** each arrives, and the answer
is not this phase.

## Packet Sequence

The forward plan for packets 3b–10. Each entry states what the packet lands, what it
depends on, and what gates it. The subsystem view of the same work is [`## Scope`](#scope);
where the two overlap, Scope is the authority on shape and this section on order.

**Packet 3b — Decision repair ⏳**
A repair slice. Packets 0–3 are shipped and their records stand — see
[`## Delivery Record`](#delivery-record-packets-03) at the end of this document; what
they left behind lands here rather than being edited into their history. This
packet exists so that no migration, no module and no adapter is written on top
of a defect that is currently one constant or one attribute wide.

Suffix lettering follows the precedent already in this roadmap (`02a`/`02b`/`02c`,
`08a`/`08b`/`08c`) and keeps every existing cross-reference intact — including
the Hub repository's "blocked on P02a-5/6/7/9" table, which renumbering would
orphan.

**Shared Kernel repairs** (carried out of Packet 2):

- `Results.Unit` collides with `MediatR.Unit`. Any handler file that imports
  both namespaces gets an ambiguous reference, and every handler file will
  import both. Renamed to **`None`** before the first handler exists —
  [Phase 02d](phase-02d-walking-skeleton.md) writes it. `None` was chosen
  because it collides with nothing in the BCL, MediatR, EF Core, Vogen,
  FluentValidation or Polly, and needs no keyword suppression. The rename
  sweeps `Result.cs`'s XML doc and its `ArgumentNullException` message,
  `ResultTests`, [Standards 09](../standards/09-error-handling.md),
  [Standards 02](../standards/02-backend-coding.md) and the
  [glossary](../glossary.md) — the type file alone is not "done".

  This is recorded for the Hub's own agents rather than acted on: `LearnStack-Hub`
  mirrors this kernel and holds **73** `Result<Unit>` / `Unit.Value` sites across
  11 files, against 1 here. It is **not** carrying an unaddressed defect — it hit
  the same collision and mitigated it centrally, with a global
  `<Using Include="…Results.Unit" Alias="Unit" />` in
  `backend/src/Modules/Directory.Build.props` covering all 23 module projects. That
  works, and at 23 projects it is one line rather than one per handler.

  So the divergence is a genuine choice, not an oversight, and it is the Hub's to
  make: rename to `None` and drop the alias, or keep `Unit` behind the alias and
  accept that the two kernels no longer share a type name. The reconciliation is
  already booked in that repository at `p02c-1-hub-domain-core.md`; this note exists
  so whoever picks it up knows which name LearnStack chose and why, instead of
  discovering the split from a merge conflict.
- `Result<T>` carries no `[MemberNotNullWhen]` annotations, so the compiler
  cannot prove `Value` is non-null after an `IsSuccess` check. Without them
  every consumer writes `!` or a justification comment, in every module, for
  the lifetime of the codebase.
- `Entity<TId>` overrides `Equals(object?)` but implements neither
  `IEquatable<Entity<TId>>` nor `operator ==`. Every equality comparison boxes,
  and the EF change tracker compares constantly.

**Corpus repairs:**

- [ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md)'s
  `IProviderResilience<TPort>` example shows
  `services.Decorate<TPort, ResilientProviderAdapter<TPort>>()`, which does not
  compile — C# forbids using a type parameter as a base type. The **shipped**
  registration is correct (`AddSingleton<IProviderResilience<TPort>>` with an
  injected collaborator); only the ADR text is wrong. Documentation fix, not a
  design change.
- **Nine shipped source comments** schedule the Dapr-backed `ISecretProvider` to
  "Packet 5", across four files: `CrossCuttingFoundationExtensions.cs` (five sites,
  including the `TODO(2026-05-21, @platform)`), `ErrorTrackingRegistration.cs` (two),
  `LearnStack.SharedKernel.csproj` (one) and `ISecretProvider.cs` (one).
  `ConfigurationSecretProvider.cs` carried a tenth and is **already corrected** — the
  restructure fixed it while sweeping the provider's name, so it is not part of this
  packet's work. [ADR-0035](../decisions/0035-demand-gated-infrastructure.md)
  moved every Dapr adapter to [Phase 11](phase-11-production-hardening.md)
  against a written trigger, so those comments now point at a packet that will
  not ship them. The seam they describe is correct and unchanged — only the
  phase pointer is stale. Comment-only edit; no behaviour change.
- The Packet 3 `TenantContextBehavior` TODO names a `DbConnectionInterceptor`
  as the mechanism for setting the Row Level Security session variables.
  Interceptors fire when the connection opens, not when the transaction starts,
  and `set_config(..., true)` is transaction-local — so the value would be gone
  before the query it protects. The TODO is corrected to point at
  [Security Standards § Tenant Context](../standards/11-security.md) and at
  Packet 7, which implements it.

**Development-loop and CI repairs.** [Phase 01](phase-01-repository-tooling.md)
is complete and its record stands; the gaps it shipped with are remediated
here:

- `make seed`'s health gate requires every compose service to report healthy,
  but `coturn`, `dapr-placement` and `dapr-sidecar-api` declare no healthcheck —
  so the gate times out and the script exits non-zero on **every** run. This is
  step three of the quickstart.
- `infra/compose/e2e.yml` resets PostgreSQL, SeaweedFS, Meilisearch and Kafka to
  `tmpfs` but leaves Valkey on its named volume, so cache and rate-limit state
  leaks between end-to-end runs. Its `volumes: !reset []` additionally discards
  the PostgreSQL init script and the SeaweedFS S3 identity file — which breaks
  the moment [Phase 06](phase-06-renderer-admin-studio.md) starts running
  browser tests against it.
- `frontend/apps/web` runs `vitest run --passWithNoTests` against zero test
  files, so the frontend CI check is green without asserting anything. The
  tolerance is removed **together with the first test that satisfies it** — one
  render test over the existing `(public)/page.tsx` placeholder is enough — so the
  required `frontend` check never sits red across a packet boundary. The
  substantive frontend suite still arrives with
  [Phase 02d](phase-02d-walking-skeleton.md).
- Branch protection requires four checks but zero approvals and does not enforce
  for administrators, which contradicts
  [Git Workflow Standards](../standards/14-git-workflow.md). Either the setting
  or the standard changes — a security rule that differs from the live platform
  setting makes a green build look stronger than it is.
- `infra/dapr/components/secretstore-vault.yaml` sets `vaultKVPrefix: secret`,
  resolving reads to `secret/data/secret/<key>` instead of the documented
  `secret/learnstack/<area>` layout.
- Every published port in `infra/compose/dev.yml` binds `0.0.0.0` with committed
  development credentials, and `MEILI_MASTER_KEY` is hardcoded rather than read
  from the environment. Bind to loopback; read from `.env`.

**Packet 4 — API conventions ⏳**
REST + URL versioning (`/api/v1/...` per
[ADR-0024](../decisions/0024-api-versioning-policy.md)), Problem Details
(RFC 7807) on every error, cursor pagination, idempotency keys for write
endpoints with external side effects, ETag concurrency, correlation IDs in
headers and logs, OpenAPI generated from code, tenant + organization header
binding (`X-Tenant-Id`, `X-Organization-Id`).

Carries one correctness fix: `CursorPagination`'s constructor validation
currently surfaces as an unhandled `ArgumentOutOfRangeException` during model
binding, producing a 500 where a malformed cursor is a client error. Binding
failures return **400** with Problem Details.

SDK generation ships as a wired-but-empty scaffold — there are no endpoints to
generate from until [Phase 02d](phase-02d-walking-skeleton.md).

**Packet 5 — Foundation ports and default implementations ⏳**
`IEventBus` / `ICacheService` / `ISecretProvider` in
`LearnStack.SharedKernel`, with `InProcessEventBus` / `InMemoryCacheService` in
`LearnStack.Infrastructure` — `ISecretProvider` and `ConfigurationSecretProvider`
already shipped in Packet 3 — as the **only
registered implementations**. Composition-root branching on `DeploymentMode`
is present and exercised, with `Development` and `SaaS` wired; the remaining
three values resolve to the same defaults until
[Phase 11](phase-11-production-hardening.md) builds their adapters and
integration suites, per
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md).

`InProcessEventBus` is a **first-class transport, not a stub**: same
`IIntegrationEventHandler<T>` interface, same `IInboxGuard`, same
tenant-context restoration as the durable path. A development path that skips
those is a development path that never exercises the isolation code, and every
consumer would end up with two implementations.

`ICacheService.RemoveByPrefixAsync` is **removed or redesigned** before this
packet ships. The published implementation iterates an instance-local key set,
so keys written by another instance are never evicted — the contract cannot be
honoured by any candidate backend. Either the method leaves the interface, or
it is replaced by a generation-key pattern whose guarantee is achievable.

The Dapr sidecar, Kafka, APISIX and Vault adapters are **not** in this packet.
They are demand-gated with written triggers in
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md); the local compose
stack moves them behind a non-default profile so the daily loop runs the
services the backend can actually call.

**Packet 6 — Tenancy schema and the corrected RLS template ⏳**
Migrations and EF configurations for `tenants`, `organizations` (per
[ADR-0017](../decisions/0017-tenant-organization-hierarchy.md)),
`tenant_domains`, `tenant_locales` (per
[ADR-0008](../decisions/0008-localization-schema.md)), `tenant_settings` (with
nullable `organization_id` for org-scoped settings), `tenant_feature_flags`
(tenant-flag level only — plan-level features arrive through the entitlement
projection), `platform_entitlement_cache`, `platform_host_to_tenant`, and
`outbox_messages`. Default-organization seeding at tenant creation.

The `Organization` aggregate is declared in `LearnStack.Modules.Tenancy.Domain`, with its
EF configuration and its migration on `TenancyDbContext`, per
[ADR-0017 Amendment 2 (2026-08-10)](../decisions/0017-tenant-organization-hierarchy.md). Identity holds
`OrganizationId` by value from `LearnStack.SharedKernel` and reads organization data
through an application contract; it declares no `Organization` type of its own.

The `outbox_messages` table ships here even though nothing dispatches from it
until [Phase 02b](phase-02b-events-auth.md): the table's schema and its
ownership by LearnStack are a one-way door, and that ownership is precisely
what makes the dispatch transport swappable later.

**The first migration is written against the corrected template**, not the one
that was published in four documents before
[ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md):
one policy per table with an `AND`-ed predicate, `ENABLE` **and** `FORCE ROW
LEVEL SECURITY`, an explicit `WITH CHECK`, and the four-role model
(`learnstack_migration` owns, `learnstack_app` connects with `NOBYPASSRLS`,
`learnstack_platform` and `learnstack_outbox_admin` hold audited bypasses). The
canonical SQL lives in exactly one place:
[Database Standards § Tenant-Owned and Organization-Scoped Tables](../standards/05-database.md).

The migration also declares each table's **class** — tenant-owned, tenant-owned
self-keyed (`tenants`), or platform-scoped (`platform_host_to_tenant`) — because
two of the nine cannot take the template verbatim: `tenants` has no `tenant_id`
column, and `platform_host_to_tenant` is read in order to determine the tenant, so
a tenant-keyed predicate would make host resolution return zero rows forever. See
[Database Standards § Table classes](../standards/05-database.md). Every table's
`GRANT`s are written in this migration too; there are no `ALTER DEFAULT PRIVILEGES`
grants, so a table nobody granted fails loudly rather than inheriting DML — and can
never silently widen a `BYPASSRLS` role. Row security is enabled and forced on all
nine, so the structural scan needs no exception list.

Introduces the `TenantId` / `OrganizationId` Vogen value objects in
`LearnStack.SharedKernel` alongside the schema — the kernel-level identifiers
Packet 7 threads through `ITenantContext` and `CapturedContext`. The Packet 3
`TransactionBehavior` shell lights up here once the per-module `DbContext`
exists: unit-of-work begin, commit on success-`Result`, rollback on failure,
preserving the `ExceptionDispatchInfo` rethrow `AuditLogBehavior` owns one
frame out.

**Packet 7 — Tenant and organization resolution, isolation, two tenants ⏳**
`IHostToTenantResolver` backed by `platform_host_to_tenant` and **nothing
else** — never the Hub, per
[ADR-0034](../decisions/0034-hub-contract-surface-invariant.md); an anonymous
page load must not depend on a control plane being reachable.
`TenantResolverMiddleware`, request-scoped `ITenantContext` (`TenantId`,
`OrganizationId?`, `UserId?`), singleton `ITenantContextAccessor`
(`AsyncLocal<ITenantContext?>`-backed) populated at scope start by
`TenantResolverMiddleware` (HTTP), `HubCorrelationMiddleware`
(`/api/internal/*`), the Hangfire `JobActivator` (background jobs), and the
outbox / inbox handler scope (integration events). `[TenantOwned]` and
`[OrganizationScoped]` marker attributes. EF global query filters on every
entity implementing `ITenantOwned` / `IOrganizationScoped`.

**The RLS session variables are set with `SET LOCAL` inside the ambient
transaction**, after it opens. `set_config(..., true)` is transaction-local: set
from a MediatR behavior that runs before `TransactionBehavior`, or from a
connection interceptor that fires at connection open, it is discarded before the
query it is meant to protect ever runs. The corpus previously described all
three placements; [Security Standards § Tenant Context](../standards/11-security.md)
is now the single authority, and the Packet 3 `TenantContextBehavior` TODO —
which names the connection-interceptor option — is corrected here.

Explicit, scoped, audited `EnterPlatformAdminScope(reason)` for the narrow
cross-tenant access path. It reaches `learnstack_platform` through a **second,
separately-credentialed connection** (`ConnectionStrings:PlatformAdmin`), never
through `SET ROLE`: `learnstack_app` is not a member of `learnstack_platform`,
because membership would make `BYPASSRLS` a standing capability of the application
role, a plain `SET ROLE` survives `COMMIT` and would persist on a PgBouncer
transaction-pooled connection into the next tenant's request, and per-role settings
such as `statement_timeout` are applied at login and do not follow a role switch.
The composition root registers the platform data source as a keyed singleton that
only `PlatformAdminScope` may resolve
(`Platform_DataSource_Resolved_Only_By_PlatformAdminScope`).

The scope's **audit obligation is declared here and satisfied in Packet 9**, which is
where `audit_log` and `IAuditStore` land. Until then `EnterPlatformAdminScope(reason)`
records the entry through `ILogger` at `Warning` with the `reason`, the caller and the
sentinel platform tenant id, and Packet 9 replaces that with a `SecurityEvent` audit row
written as `learnstack_platform` **before** the operation runs — so an operation that
later fails is still recorded. Packet 7 must not claim a durable audit trail it has no
table for; a log line that is honestly a log line is better than an audit row that does
not exist.

`IHostToTenantResolver` sets `SET LOCAL app.resolving_host` inside its own short
read-only transaction before the lookup, because `SET LOCAL` outside a transaction
block has no effect and a session-level setting would leak across a pooled
connection. `app.resolving_host` is the fourth and last canonical session variable.

**Two seed tenants in unrelated domains**, each with two organizations: an
English school and a **yoga studio**. This is the artefact that tests the
genericity claim, and it moves here from
[Phase 10](phase-10-english-learning-mvp.md) because Packet 7 already needs two
tenants for isolation testing — the marginal cost is the second tenant's
customization data, and the marginal benefit is that every phase from
[Phase 02d](phase-02d-walking-skeleton.md) onward is tested against two shapes
instead of one. Picks up the application-level seed drop-in deferred from
[Phase 01 Packet 8](phase-01-repository-tooling.md), wired through the Tenancy
module `DbContext` rather than the placeholder `scripts/seed.sh`.

Cross-tenant and cross-organization isolation integration tests **run as
`learnstack_app`**. A test that connects as the table owner or as a `BYPASSRLS`
role passes even when every policy is inert, and therefore proves nothing. The
suite includes at minimum:

- `Tenant_A_cannot_read_Tenant_B_data`
- `Org_X_cannot_read_Org_Y_within_TenantA`
- `TenantWide_Row_Of_TenantB_Is_Invisible_To_TenantA` — the exact case the
  superseded template leaked
- `Unsetting_tenant_context_returns_zero_rows_through_RLS`
- `Write_With_Foreign_TenantId_Is_Rejected_By_WithCheck`

Carries two Packet 3 follow-ups: converting `ITenantContext` **and**
`CapturedContext` from raw `Guid` / `Guid?` to the strongly-typed `TenantId` /
`OrganizationId` value objects created in Packet 6, in a single pass to avoid a
half-typed intermediate; and replacing the `TenantContextBehavior.AllowsUnresolvedContext`
stub with a real `[AllowsUnresolvedTenantContext]` marker attribute for
tenant-provisioning and platform-admin commands that legitimately run before a
tenant is resolved, backed by an architecture test that the attribute appears
only on that narrow command set.

**Packet 8 — Tenant Customization foundation ⏳**
`LearnStack.Modules.Customization` with **two** aggregates:
`TenantContentType` (JSON Schema declaring a content shape) and
`TenantLevelTaxonomy` (the tenant's level or difficulty vocabulary). These are
the two the runtime needs before [Phase 02d](phase-02d-walking-skeleton.md) can
render two different tenants; the rest have no consumer for several phases.

Both tables ship the versioned key shape —
`UNIQUE (tenant_id, key, schema_version)` plus the partial index
`UNIQUE (tenant_id, key) WHERE status = 'active'`. Not `UNIQUE (tenant_id, key)`:
that rejects the second revision of any key. See
[`## Scope` § Tenant Customization Foundation](#tenant-customization-foundation).

This packet also **fixes the storage shape** for scoring and completion rule bodies
without creating their tables: **opaque `text` with a `dialect` discriminator**. The
aggregates and their migrations land with their first consumer in
[Phase 05](phase-05-education-learning-content.md); the column type is settled here
because it is the part that cannot be changed later without a migration. The
evaluation engine is not chosen yet — ADR-0025
decides between CEL, a restricted Lua, and a custom AST in
[Phase 05](phase-05-education-learning-content.md), and the three candidates do
not share a column type. Storing the body opaquely lets tenants author rules
before the engine exists without committing the schema to a choice not yet
made.

The remaining aggregates land with their consumers:

| Aggregate | Owning phase |
|---|---|
| `TenantPageBlock` | [Phase 04](phase-04-cms-media-pages.md) |
| `TenantCustomFieldDef` | [Phase 03](phase-03-identity-admin.md) |
| `TenantLessonItemType` | [Phase 05](phase-05-education-learning-content.md) |
| `TenantScoringRule` / `TenantCompletionRule` — aggregates, tables and evaluation | [Phase 05](phase-05-education-learning-content.md) |
| `TenantTemplateLibrary` | [Phase 08a](phase-08a-assessment-notifications.md) |

A small built-in seed — one `default-card` composite renderer and a stock
`Plain` level taxonomy — lets early phases exercise the customization runtime
before a tenant data set exists. Admin Studio editors land with their consuming
phases — the `TenantContentType` editor in [Phase 04](phase-04-cms-media-pages.md), the
`TenantLevelTaxonomy` editor in [Phase 05](phase-05-education-learning-content.md);
[Phase 06](phase-06-renderer-admin-studio.md) consolidates them into one editing idiom,
and its Studio screen ownership table is the single ownership record.

**Packet 9 — Audit infrastructure and the entitlement socket ⏳**
`LearnStack.Infrastructure.Audit` with `AuditChangeTrackerInterceptor` (an EF
`SaveChanges` interceptor), `IAuditStateCapture` (before / after / changes JSON
capture), and the Packet 3 `AuditLogBehavior` shell lit up per
[ADR-0033](../decisions/0033-audit-durability-model.md): **MUST-class audit
rows are written on the same transaction as the business write** —
`AuditLogBehavior` classifies and parks the intent, `TransactionBehavior` writes
it immediately before `COMMIT` — so they commit with it or not at all, and so
they execute while `app.tenant_id` is set and Row Level Security accepts them. SHOULD/MAY-class audit stays best-effort,
with its accepted loss written down. `AuditConfig` may narrow SHOULD/MAY
coverage but never removes baseline MUST coverage. Exactly two failures reject the
operation — an unclassified operation, and a MUST-class row that cannot be written
durably; a tenant-override read failure falls back to the in-process catalogue instead
([ADR-0033 § Fail-closed, stated precisely](../decisions/0033-audit-durability-model.md)).

`LearnStack.Modules.Audit` with the `AuditEntry` aggregate — inheriting
`Entity<TId>`, **not** `AuditableEntity<T>`, guarded by
`AuditEntry_Inherits_Entity_Not_AuditableEntity` — and `AuditConfig`.
`audit_log` ships as a **single correct table** with the composite primary key
`(id, timestamp)`; the DDL in the superseded ADR-0016 declares a primary key
twice and PostgreSQL rejects it. Monthly partitioning, the retention job and
the lifecycle policy from
[ADR-0028](../decisions/0028-audit-log-partition-management.md) move to
[Phase 11](phase-11-production-hardening.md) per
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md) — audit
correctness cannot be added later, audit scale can.

`IEntitlementProvider` is declared with `NullEntitlementProvider` (all features
enabled, no limits) as the **only** implementation. The Hub-backed and
signed-licence implementations land in
[Phase 02c](phase-02c-hub-foundation.md) and
[Phase 11](phase-11-production-hardening.md) respectively, against the triggers
in [ADR-0035](../decisions/0035-demand-gated-infrastructure.md).

**Packet 10 — Architecture tests green and phase exit ⏳**
Every Phase 02a rule in
[Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md)
green in CI, under reconciled canonical names — the catalogue currently carries
six spellings of the tenant-isolation rule and five of the organization-scope
rule, drift it exists specifically to prevent. Reconciling now is a
find-and-replace in Markdown; reconciling after the tests exist is a refactor
across a dozen files.

Implements `Core_Modules_HaveNo_DomainSpecific_Names` — the mechanical
guarantee behind the platform's entire premise, and currently unimplemented
while its far weaker sibling `No_Source_Folder_Named_Verticals` is green.

One rule is restated rather than renamed. The catalogue's
`Every_TenantOwned_Table_HasRls_With_AppTenantId` asserts that a policy
**exists** and mentions `app.tenant_id`. The superseded template satisfied that
assertion perfectly while leaking every tenant-wide row across tenants — a
structure-shaped test that passes against a broken policy is worse than no test,
because it converts an open question into a false answer. Structural assertions
stay, but the binding proof is the Packet 7 integration suite running as
`learnstack_app`: isolation is a runtime property and only a runtime test can
observe it.

Tests grouped by introducing packet:

- From the cross-cutting [ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md)
  batch (introduced by Packet 3, already green):
  `IExceptionHandler_Registered_AtStartup`,
  `MediatR_Pipeline_Order_Matches_Canonical_Sequence`,
  `ValidationBehavior_DoesNotThrow_ValidationException`,
  `Adapters_Wrap_Provider_Exceptions`,
  `Modules_Do_Not_Reference_Sentry_SDK_Directly`,
  `Logging_Goes_Through_Microsoft_Extensions_Logging`,
  `OTel_Pipeline_Includes_TenantContextSpanProcessor`,
  `TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing`,
  `IErrorTrackingProvider_Is_Singleton`, `Handlers_Return_Result`.
  `Domain_Methods_Do_Not_Throw_For_Expected_Cases` — the report-walking test —
  lands here, because it needs module domain code to walk and none existed
  until Packet 6. The underlying rule is enforced from Packet 3 by the `LS0001`
  analyzer.
- From the module-dependency arm (introduced by Packet 2, closed here): the
  Application + Infrastructure matrix extending
  [`ModuleDependencyTests`](../../backend/tests/LearnStack.Tests.Architecture/ModuleDependencyTests.cs),
  `LearnStack_Modules_DoNotReference_Hub`,
  `Modules_Do_Not_Inject_Valkey_Directly`,
  `Modules_Do_Not_Read_Entitlement_Cache_Directly`,
  `Modules_Do_Not_Write_AuditLog_Directly`,
  `Modules_Do_Not_Reference_DeploymentMode`,
  **`Core_Modules_HaveNo_DomainSpecific_Names`**,
  `No_Source_Folder_Named_Verticals` (green since Phase 01).
- From the tenancy and isolation arm (introduced by Packet 7):
  `Every_TenantOwned_Entity_HasFilterAndRlsPolicy`,
  `Every_OrgScoped_Entity_HasOrgIdAndFilter`,
  `No_IgnoreQueryFilters_Outside_PlatformAdminScope`,
  `AllowsUnresolvedTenantContext_Only_On_Provisioning_Commands`.
- From the audit arm (introduced by Packet 9):
  `AuditEntry_Inherits_Entity_Not_AuditableEntity`,
  `Every_TenantOwned_Command_HasAuditCoverage`,
  `MustClass_Audit_Writes_Share_The_Business_Transaction`,
  `Every_Module_Has_An_AuditCoverage_Matrix`.

Every standard in [the standards corpus](../standards/README.md) is
re-stated against reality: a standard with no implementing code moves from
`Active` to `Adopted`. All twenty-two currently claim `Active`, which makes the
three-state model decorative.

The deferred `backend-integration` CI job
([Phase 01 Packet 8](phase-01-repository-tooling.md)) activates once Packet 7's first
isolation test is green — `vars.ENABLE_BACKEND_INTEGRATION` set, the placeholder step
replaced, and the job renamed and re-required per
[`.github/CONTRIBUTING.md`](../../.github/CONTRIBUTING.md). Closes the architecture-test arm of the
[Phase Exit Decision](#phase-exit-decision); the remaining gates close as their
owning packets ship.


## Scope

### Shared Kernel

- Base entity and aggregate concepts.
- Strongly typed identifiers (UUIDv7-backed, with EF value converters).
- `Entity<TId>` (append-only / audit aggregate base) and `AuditableEntity<T>` (mutable
  aggregate base with `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`, `DeletedAt`,
  `DeletedBy`, `Version`).
- Soft delete strategy + EF global query filter.
- Optimistic concurrency strategy.
- Domain event model (in-process MediatR-style) — wired but consumed only inside
  modules; the cross-module outbox dispatcher lives in Phase 02b.
- Result and error model (`Result<T>` with `LocalizedMessage`'s `lockey_` prefix
  invariant).
- Pagination model (cursor-first).
- `IClock`, `IRandom`, `IGuidFactory` for deterministic tests.
- **`IEventBus`, `ICacheService`, `ISecretProvider`, `IEntitlementProvider`,
  `IHostToTenantResolver`** interfaces declared in `LearnStack.SharedKernel`. Their
  default implementations, and the reason the vendor-backed ones are not here, are in
  [§ Foundation Ports and Default Implementations](#foundation-ports-and-default-implementations)
  below.

### Foundation Ports and Default Implementations

Per [ADR-0035](../decisions/0035-demand-gated-infrastructure.md), the **ports** ship
here with **working default implementations**; the vendor adapters ship on a trigger.

- `IEventBus` with `InProcessEventBus` — a first-class transport, not a stub. Same
  `IIntegrationEventHandler<T>` interface, same `IInboxGuard`, same tenant-context
  restoration as the durable path, so development exercises the isolation code and no
  consumer needs two implementations.
- `ICacheService` with `InMemoryCacheService`. `RemoveByPrefixAsync` is removed from
  the interface or redesigned to a generation-key pattern before Packet 5 ships — the
  published contract iterates an instance-local key set and cannot be honoured across
  instances by any candidate backend.
- `ISecretProvider` with `ConfigurationSecretProvider`.
- `IEntitlementProvider` with `NullEntitlementProvider` (all features enabled, no
  limits).
- `IHostToTenantResolver` with a PostgreSQL-backed implementation reading
  `platform_host_to_tenant` — and **nothing else**. Host resolution never calls the
  Hub; an anonymous page load must not depend on a control plane being reachable
  ([ADR-0034](../decisions/0034-hub-contract-surface-invariant.md)).

Composition-root branching on `DeploymentMode` is present and exercised, with
`Development` and `SaaS` wired end to end. `Dedicated`, `SelfHostedOnline` and
`SelfHostedAirGapped` resolve to the same defaults and are **prepared seams, not
supported deployments**, until [Phase 11](phase-11-production-hardening.md) builds
their adapters and integration suites. Modules never read `DeploymentMode`
(`Modules_Do_Not_Reference_DeploymentMode`).

**Not in this phase**, per ADR-0035's trigger table: the Dapr sidecar and its pub/sub,
state and secret components; Kafka; APISIX; Vault. [ADR-0014](../decisions/0014-adopt-dapr.md)
and [ADR-0015](../decisions/0015-api-gateway-apisix.md) stand as decisions about what
LearnStack uses when it needs a cross-process event bus and an edge gateway; neither is
needed by a single-process platform with no integration events and no non-development
deployment. The local compose stack moves them behind a non-default profile so the
daily loop runs the services the backend can actually call.

### Tenancy Schema Foundations

The following Tenancy-owned tables are created in this phase so later modules don't
have to retrofit:

- `tenants` — tenant root.
- `organizations` — sub-unit within a tenant per
  [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md). Every tenant has at
  least one default organization seeded at creation.
- `tenant_domains` — the tenant's **own** domain lifecycle and verification state
  (requested / verifying / verified / failed), read and written under tenant context
  (lifecycle + verification UI lands in Phase 04 / Phase 06; the **Hub-owned**
  custom-domain admin lands in 02c). It is **not** the resolution index — that is
  `platform_host_to_tenant` below, which is read before any tenant context exists. The
  two differ in *when* they are read, which is exactly why they cannot share one Row
  Level Security rule.
- `tenant_locales` — default + enabled locales (see
  [ADR-0008](../decisions/0008-localization-schema.md)). Required before any
  tenant-owned content table ships.
- `tenant_settings` — non-translated tenant configuration. Org-scoped settings ride
  on a nullable `organization_id` column.
- `tenant_feature_flags` — typed feature flag overrides (see
  [21-feature-flags.md](../architecture/21-feature-flags.md) — *tenant-flag-level
  only*; plan-level features live in the entitlement projection).
- `platform_entitlement_cache` — Hub-projected entitlement cache (Hub-side population
  lands in 02c; the **table and `IEntitlementProvider` read path ship now** so the
  rest of the system can be coded against the projection from Day 1).
- `platform_host_to_tenant` — host → `(tenant_id, organization_id?)` mapping
  (Hub-populated for SaaS / Dedicated, config-populated for SelfHosted; the table
  ships now).
- `outbox_messages` — the outbox table. Nothing dispatches from it until
  [Phase 02b](phase-02b-events-auth.md), but its schema and LearnStack's ownership of
  it are a one-way door — and that ownership is exactly what makes the dispatch
  transport swappable later
  ([ADR-0006](../decisions/0006-events-and-outbox.md)).

All nine tables ship with `ENABLE` **and** `FORCE ROW LEVEL SECURITY` and an explicit
`WITH CHECK`. They do **not** all take the same policy, and saying they do produces a
migration that cannot run: the corrected template's predicate names a `tenant_id`
column, which `tenants` does not have, and it would deadlock host resolution, which
reads `platform_host_to_tenant` in order to *determine* the tenant. Three classes, per
[ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md) and
[Database Standards § Table classes](../standards/05-database.md):

| Class | Tables | Policy |
|---|---|---|
| Tenant-owned | `organizations`, `tenant_domains`, `tenant_locales`, `tenant_settings` (the one org-scoped table — it also takes the two `AS RESTRICTIVE` write guards), `tenant_feature_flags`, `platform_entitlement_cache`, `outbox_messages` | the corrected template verbatim |
| Tenant-owned, self-keyed | `tenants` | the corrected template with the tenant term keyed on `id`, because the primary key *is* the tenant id |
| Platform-scoped | `platform_host_to_tenant` | `ENABLE` + `FORCE` with role-qualified per-command policies: reads keyed on the declared `app.resolving_host` (pre-context, single row) or on `app.tenant_id` (a tenant listing its own hosts); writes keyed on `app.tenant_id` only |

Packet 6 also ships `infra/compose/postgres-init/02-create-roles.sql` and splits the
development connection strings — `learnstack_migration` for `dotnet ef`, `learnstack_app`
for the API, plus the two bypass roles' own credentials. Until it lands,
`infra/compose/dev.yml` runs everything as one `POSTGRES_USER` superuser, which owns
every table and therefore bypasses every policy: the isolation layer is inert in local
development and every isolation test would pass against it. The script also grants
`CREATE ON SCHEMA public` to `learnstack_migration` — since PostgreSQL 15 the public
schema no longer grants it to `PUBLIC`, so without it the first migration fails with
`permission denied for schema public`, and the tempting fix (make the role a superuser)
recreates exactly the ownership arrangement `FORCE ROW LEVEL SECURITY` exists to defeat.

`platform_entitlement_cache` is tenant-owned despite its name — every read resolves the
tenant from `ITenantContext` first and every write arrives on
`PUT /api/internal/tenants/{id}/entitlements`, so nothing about it is pre-context and
the application role never gets a table-wide read of every tenant's plan. Row security
is never *disabled* on any of the nine; the grant matrix that goes with the policies is
in [Database Standards § Database roles](../standards/05-database.md). The canonical SQL
lives in exactly one place because the template that preceded it was copied into four
documents and drifted in all of them.

### Audit Infrastructure

Per [ADR-0033](../decisions/0033-audit-durability-model.md), which supersedes ADR-0016:

- `LearnStack.Infrastructure.Audit` ships with `AuditChangeTrackerInterceptor` (an EF
  `SaveChanges` interceptor), `IAuditStateCapture` (before / after / changes JSON
  capture), and the Packet 3 `AuditLogBehavior` shell lit up.
- **MUST-class audit rows are written on the same transaction as the business
  write** — parked by `AuditLogBehavior`, written by `TransactionBehavior` immediately
  before `COMMIT`. They commit with the state change or not at all, and they execute while the
  transaction-local `app.tenant_id` is set — which is what stops the corrected Row
  Level Security policy from rejecting every audit insert and the documented
  catch-and-log posture from swallowing the rejection.
- SHOULD/MAY-class audit stays best-effort, with its accepted loss written down rather
  than assumed.
- `AuditConfig` may narrow SHOULD/MAY coverage but never removes baseline MUST
  coverage. Exactly two failures reject the operation — an unclassified operation and a
  MUST-class row that cannot be written durably; a tenant-override read failure falls
  back to the in-process catalogue, which carries the same MUST floor.
- `LearnStack.Modules.Audit` ships with the `AuditEntry` aggregate (inheriting
  `Entity<TId>`, **not** `AuditableEntity<T>`) and `AuditConfig`.
- `audit_log` ships as a **single correct table** with the composite primary key
  `(id, timestamp)`. Monthly partitioning, the retention job, and the lifecycle policy
  from [ADR-0028](../decisions/0028-audit-log-partition-management.md) land in
  [Phase 11](phase-11-production-hardening.md) per
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md). Audit correctness
  cannot be retrofitted; audit scale can.
- MUST-class coverage is enabled for every command and security event the modules
  declare; modules added in later phases extend the catalogue, not the infrastructure.

### Cross-cutting Concerns (Day 1)

Per [ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md):

- **L1 exception handler.** `LearnStackExceptionHandler : IExceptionHandler`
  ships in `LearnStack.Api`; registered with
  `services.AddExceptionHandler<LearnStackExceptionHandler>() +
  services.AddProblemDetails()`.
- **MediatR pipeline behaviors (eight-step canonical order).**
  `ValidationBehavior` (returns `Result.Fail(validation_failed)` — never
  throws), `LoggingBehavior` (opens the 8-field `ILogger` scope + manual
  `Activity` + latency histogram), `AuditLogBehavior` (per
  [ADR-0033](../decisions/0033-audit-durability-model.md), which supersedes ADR-0016 —
  keeps its shipped pipeline position and its try/catch + audit-fail entry +
  `ExceptionDispatchInfo` rethrow; MUST-class rows are written on the ambient
  transaction by `TransactionBehavior` immediately before `COMMIT`, so they commit
  with the state change or not at all), `TenantContextBehavior` (asserts resolved; does **not** set the RLS
  GUCs — see [Security Standards § Tenant Context](../standards/11-security.md)),
  `AuthorizationBehavior` (returns `Result.Fail(forbidden)` on deny),
  `TransactionBehavior` (UoW), `OutboxFlushBehavior` (enrols outbox writes
  in current tx).
- **`Result<T>.ToActionResult()` extension** lives in
  `LearnStack.Api.Common`; every controller endpoint uses it explicitly. No
  action filter, no `ResultUnwrapBehavior`.
- **Roslyn analyzer `LearnStackException-DomainExceptionThrow`** under
  `backend/analyzers/` flags every `throw new DomainException(...)` outside
  aggregate invariant guards. Warning in Phase 02a, escalates to Error after
  Phase 03 exit.
- **`IProviderResilience<TPort>` decorator** with Polly v8
  `ResiliencePipeline` (retry + circuit breaker + timeout + bulkhead) lives
  in `LearnStack.Infrastructure.Resilience`. Configuration shape:
  `appsettings.Resilience:<port-name>:`. Every adapter is wired through the
  `AddProviderResilience<TPort, TImpl>(string portName)` composition-root
  extension. The
  [add-provider-adapter](../../.claude/skills/add-provider-adapter/SKILL.md)
  skill walks the canonical wiring.
- **Serilog primary logger + OTLP sink.** Hosts wire
  `builder.Host.UseSerilog(...)` with `WriteTo.Console(...)` +
  `WriteTo.OpenTelemetry(...)`. The OTel `LoggerProvider`
  (`AddOpenTelemetry().WithLogging()`) is **not** registered alongside.
  Modules log through `ILogger<T>` only.
- **OpenTelemetry SDK** wired with `AddAspNetCoreInstrumentation` +
  `AddHttpClientInstrumentation` + `AddEntityFrameworkCoreInstrumentation` +
  `AddProcessor<TenantContextSpanProcessor>` + `AddOtlpExporter`. Manual
  `ActivitySource` named per module (`learnstack.<module>`) for use-case
  spans.
- **`ITenantContextAccessor`** (singleton, `AsyncLocal<ITenantContext?>`-backed)
  lives in `LearnStack.SharedKernel` alongside the request-scoped
  `ITenantContext`. The scoped interface is what handlers and services
  inject; the singleton accessor is what cross-cutting infrastructure
  (OTel processor, Serilog enricher, Sentry enricher) reads. The accessor
  is populated at scope start by `TenantResolverMiddleware` (HTTP),
  `HubCorrelationMiddleware` (`/api/internal/*`), Hangfire `JobActivator`
  (background jobs), and the outbox / inbox handler scope (integration
  events). Modules never write to the accessor.
- **`TenantContextSpanProcessor : BaseProcessor<Activity>`** lives in
  `LearnStack.Infrastructure.Observability`; reads from
  `ITenantContextAccessor.Current` in its `OnStart` hook and enriches every
  span with `tenant.id`, `organization.id`, `user.id`, `module`,
  `correlation.id`.
- **`IErrorTrackingProvider` socket.** Three implementations land:
  `NoOpErrorTracker`, `SentryErrorTracker`, `LocalFileErrorTracker`.
  Composition root branches on `DeploymentMode`. DSN comes from
  `ISecretProvider`. Modules never reference `Sentry.SentrySdk`.

### Tenant Customization Foundation

Per [ADR-0018](../decisions/0018-tenant-driven-customization-model.md):

- `LearnStack.Modules.Customization` ships with **two** aggregates —
  `TenantContentType` (a JSON Schema declaring a content shape) and
  `TenantLevelTaxonomy` (the tenant's level or difficulty vocabulary) — plus their
  schema tables and runtime read paths.
- These are the two the runtime needs before
  [Phase 02d](phase-02d-walking-skeleton.md) can render two tenants that genuinely
  differ. The remaining aggregates ship with their consumers:
  `TenantCustomFieldDef` in [Phase 03](phase-03-identity-admin.md),
  `TenantPageBlock` in [Phase 04](phase-04-cms-media-pages.md),
  `TenantLessonItemType` and the `TenantScoringRule` / `TenantCompletionRule` runtime
  in [Phase 05](phase-05-education-learning-content.md), and
  `TenantTemplateLibrary` in [Phase 08a](phase-08a-assessment-notifications.md).
- Both aggregates carry the versioned key shape from their first migration:
  `UNIQUE (tenant_id, key, schema_version)` for the revision, plus the partial index
  `UNIQUE (tenant_id, key) WHERE status = 'active'` for the live definition.
  [Phase 04](phase-04-cms-media-pages.md) § Customization Key Shape and Immutable Schema
  Versions is the authority; the constraint ships here because this is the table's first
  migration and ADR-0013's version history cannot be retrofitted onto
  `UNIQUE (tenant_id, key)`.
- The **storage shape** — not the tables — for scoring and completion rule bodies is
  fixed here; the aggregates and their migrations land in
  [Phase 05](phase-05-education-learning-content.md) with their first consumer. Rule
  bodies are stored as **opaque `text` with a `dialect` discriminator**. The evaluation engine is not chosen yet — ADR-0025 decides
  between CEL, a restricted Lua and a custom AST in
  [Phase 05](phase-05-education-learning-content.md), and the three candidates do not
  share a column type. Storing the body opaquely lets rules be authored before the
  engine exists without committing the schema to a choice not yet made.
- A small built-in seed (a `default-card` composite renderer, a stock `Plain` level
  taxonomy) lets early phases exercise the customization runtime without depending on a
  real tenant data set. Admin Studio editors land with their consuming phases — the
  `TenantContentType` editor in [Phase 04](phase-04-cms-media-pages.md), the
  `TenantLevelTaxonomy` editor in [Phase 05](phase-05-education-learning-content.md);
  [Phase 06](phase-06-renderer-admin-studio.md) consolidates them into one editing
  idiom, and its Studio screen ownership table is the single ownership record.

### Tenant + Organization Resolution

Context is resolvable from:

- Custom domain (via `platform_host_to_tenant`).
- Subdomain on the platform domain (still via `platform_host_to_tenant`).
- Org-scoped subdomain (`branch-istanbul.example.edu` → tenant + organization).
- Explicit tenant + organization selector headers for admin/studio usage.
- API request headers (`X-Tenant-Id`, `X-Organization-Id`).
- Background job parameter.
- Integration event envelope (envelope contract defined in Phase 02b; the resolver
  respects it from the start).

Implementation:

- `IHostToTenantResolver` (Postgres-backed default reading `platform_host_to_tenant`).
- `TenantResolverMiddleware`.
- `ITenantContext` (request-scoped) exposing `TenantId`, `OrganizationId?`, `UserId?`.
- Tenant- and org-aware query conventions.
- Tenant + org context propagation seams for Hangfire jobs and outbox dispatcher
  handlers (wired in 02b).

### Tenant + Organization Isolation — Defense in Depth

Implemented **from day one**, not deferred to hardening. Three enforcement layers, all
required, applied to both dimensions where the entity is org-scoped:

1. **EF Core global query filters** on every entity implementing `ITenantOwned` and
   `IOrganizationScoped`.
2. **PostgreSQL Row Level Security** on every tenant-owned table, built from the
   canonical template in [Database Standards](../standards/05-database.md): one policy
   with an `AND`-ed predicate, `ENABLE` **and** `FORCE ROW LEVEL SECURITY`, and an
   explicit `WITH CHECK`. The `app.tenant_id` and `app.organization_id` session
   variables are set with `SET LOCAL` **inside the ambient transaction**, after it
   opens. `set_config(..., true)` is transaction-local: set from a pipeline behavior
   that runs before the transaction, or from a connection interceptor that fires at
   connection open, the value is discarded before the query it protects executes.
   [Security Standards § Tenant Context](../standards/11-security.md) is the single
   authority for this placement.
3. **A non-owning application role.** The runtime connects as `learnstack_app`
   (`NOBYPASSRLS`, not the table owner); migrations run as `learnstack_migration`,
   which owns the tables and is denied any bypass by `FORCE ROW LEVEL SECURITY`. See
   [ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md).

Platform-admin cross-tenant access is explicit, scoped, audited
(`EnterPlatformAdminScope(reason)`), and uses the separate `learnstack_platform` role.
See [Tenant Isolation](../architecture/09-tenant-isolation.md).

**Isolation tests connect as `learnstack_app`.** A test that connects as the owner or
as a `BYPASSRLS` role passes even when every policy is inert. This is not a
hypothetical: the template that preceded ADR-0003 Amendment 3 satisfied every
structural assertion in the architecture-test catalogue while leaking every tenant-wide
row across tenants. Isolation is a runtime property; only a runtime test observes it.

### Database Conventions

- Naming, indexing, migration, JSONB, soft-delete, audit, concurrency rules per
  [Database Standards](../standards/05-database.md).
- Required columns and RLS policy on every tenant-owned table; verified by
  architecture tests.
- Required `OrganizationId` column + RLS policy on every `[OrganizationScoped]`
  entity.
- Migrations append-only after merge; destructive changes go through a deprecation
  window.

### API Conventions

Per [API Standards](../standards/04-api-design.md):

- REST + URL versioning (`/api/v1/...`).
- Problem Details (RFC 7807) for errors.
- Cursor pagination.
- Idempotency keys for write endpoints with external side effects.
- Optimistic concurrency via ETag / `version`.
- Correlation IDs in headers and logs.
- OpenAPI generated from code; SDK generated from spec.
- Tenant + organization headers (`X-Tenant-Id`, `X-Organization-Id`) bound on every
  request.

### Configuration

- Strongly typed options bound from `ISecretProvider` + environment variables +
  `appsettings.*.json`, in that precedence order. The default `ISecretProvider` reads
  the environment; the Vault-backed implementation is demand-gated per
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md).
- Environment-based configuration (dev / staging / prod) and
  **`DeploymentMode`-based composition** (Development / SaaS / Dedicated /
  SelfHostedOnline / SelfHostedAirGapped) per
  [ADR-0020](../decisions/0020-triple-deployment-hybrid-license.md). All five values
  exist and the composition root branches on all five; `Development` and `SaaS` are
  wired end to end, and the remaining three are prepared seams until
  [Phase 11](phase-11-production-hardening.md).
- Secret handling — never in source.
- Tenant-level + organization-level settings model with a typed accessor.

### Architecture Tests

The architecture test project goes fully green during this phase, under the canonical
identifiers registered in
[Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md). Phase
02a covers:

- Module dependency direction.
- No cross-module Domain/Infrastructure references.
- Every `[TenantOwned]` entity has filter and RLS policy — **structurally**. The
  binding proof that isolation actually holds is the Packet 7 integration suite running
  as `learnstack_app`; a structural assertion passes against a policy that leaks.
- Every `[OrganizationScoped]` entity has org filter + RLS
  (`Every_OrgScoped_Entity_HasOrgIdAndFilter`).
- No `IgnoreQueryFilters()` outside platform-admin module.
- Audit-coverage matrix file exists per module.
- `AuditEntry_Inherits_Entity_Not_AuditableEntity`.
- `MustClass_Audit_Writes_Share_The_Business_Transaction` — per
  [ADR-0033](../decisions/0033-audit-durability-model.md).
- `LearnStack_Modules_DoNotReference_Hub`.
- `Modules_Do_Not_Inject_Valkey_Directly`, `Modules_Do_Not_Read_Entitlement_Cache_Directly`,
  `Modules_Do_Not_Write_AuditLog_Directly`.
- `Modules_Do_Not_Reference_DeploymentMode` — modules never read `DeploymentMode`
  directly; the composition root selects provider implementations once. See
  [20-infrastructure-stack.md § Composition Root and Deployment Mode](../standards/20-infrastructure-stack.md).
- `Core_Modules_HaveNo_DomainSpecific_Names`,
  `No_Source_Folder_Named_Verticals`.
- `IExceptionHandler_Registered_AtStartup` — every host registers
  `LearnStackExceptionHandler`.
- `MediatR_Pipeline_Order_Matches_Canonical_Sequence` — DI registration produces the eight-step
  pipeline in the canonical order.
- `ValidationBehavior_DoesNotThrow_ValidationException` — runtime assertion
  via an integration test that triggers a validation failure.
- `Domain_Methods_Do_Not_Throw_For_Expected_Cases` — uses the
  `LearnStackException-DomainExceptionThrow` Roslyn analyzer report.
- `Adapters_Wrap_Provider_Exceptions` — provider SDK exception types do not
  leave `LearnStack.Infrastructure.<Adapter>` namespaces.
- `Modules_Do_Not_Reference_Sentry_SDK_Directly`.
- `Logging_Goes_Through_Microsoft_Extensions_Logging` — modules import
  `Microsoft.Extensions.Logging.ILogger<T>`, not `Serilog.ILogger`.
- `OTel_Pipeline_Includes_TenantContextSpanProcessor`.
- `TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing` — unit
  test guard.

Every identifier in the cross-cutting list above is described — assertion,
type, source ADR / standard — in
[21-architecture-tests-catalogue.md § Cross-cutting: error handling, logging, observability](../standards/21-architecture-tests-catalogue.md).
The catalogue is the canonical reference; rename or relocation lands there
first.

The event/outbox-specific tests (serialisable records, job payloads with `TenantId`)
land in Phase 02b.

## Deliverables

- Shared kernel package with `IEventBus` / `ICacheService` / `ISecretProvider` /
  `IEntitlementProvider` / `IHostToTenantResolver` interfaces and their default
  implementations — `InProcessEventBus`, `InMemoryCacheService`,
  `ConfigurationSecretProvider`, `NullEntitlementProvider`, and a PostgreSQL-backed host
  resolver — registered as the only implementations.
- Tenant-aware + organization-aware API foundation with EF filters, the corrected
  PostgreSQL Row Level Security template, and the four-role database model active for
  both dimensions.
- **Two seed tenants in unrelated domains** (an English school and a yoga studio), each
  with two organizations, seeded through the Tenancy module's `DbContext`.
- `LearnStack.Modules.Customization` with `TenantContentType` and
  `TenantLevelTaxonomy` plus their runtime read paths.
- `LearnStack.Modules.Audit` aggregates + `LearnStack.Infrastructure.Audit` pipeline
  writing MUST-class rows inside the business transaction, on a single correct
  `audit_log` table.
- `platform_entitlement_cache`, `platform_host_to_tenant` and `outbox_messages` tables
  + read paths.
- Cross-cutting foundation (per
  [ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md)):
  `LearnStackExceptionHandler`, 8-step MediatR pipeline, `Result.ToActionResult`
  extension, `IProviderResilience<TPort>` decorator, Serilog + OTLP sink,
  `TenantContextSpanProcessor`, `IErrorTrackingProvider` with three
  implementations, Roslyn analyzer for `DomainException`. The
  [wire-cross-cutting-foundation](../../.claude/skills/wire-cross-cutting-foundation/SKILL.md)
  skill walks the canonical wiring.
- Database conventions implemented and enforced.
- API conventions wired (versioning, Problem Details, cursor pagination, idempotency,
  ETag).
- Architecture test project fully green on the Phase-02a rules, including
  `Core_Modules_HaveNo_DomainSpecific_Names`.
- The `backend-integration` CI job active, running the isolation suite against
  Testcontainers.
- A local development loop that boots, seeds and tests on a clean checkout without
  manual intervention.

## Completion Criteria

- A request reliably resolves its tenant **and** organization.
- Unknown hosts return 404 (no platform disclosure).
- Tenant-owned queries cannot leak across tenants — verified by an integration suite
  **running as `learnstack_app`**, including
  `Tenant_A_cannot_read_Tenant_B_data`,
  `TenantWide_Row_Of_TenantB_Is_Invisible_To_TenantA` and
  `Unsetting_tenant_context_returns_zero_rows_through_RLS`.
- Org-scoped queries cannot leak across organizations within the same tenant —
  verified by `Org_X_cannot_read_Org_Y_within_TenantA`.
- A write carrying a foreign `tenant_id` is rejected by `WITH CHECK`, verified by
  `Write_With_Foreign_TenantId_Is_Rejected_By_WithCheck`.
- API errors use Problem Details consistently, and a malformed cursor returns 400
  rather than 500.
- A MUST-class audit row commits in the same transaction as the state change it
  records; forcing the audit write to fail leaves **zero** business rows.
- A MUST-audit operation written through any seed module produces an entry with
  `before` and `after` snapshots.
- `IFeatureFlags.IsEnabledAsync(FeatureKey)` reads the `NullEntitlementProvider`'s
  default of "all enabled". Swapping the registered `IEntitlementProvider`
  implementation changes the answer without touching module code.
- `IHostToTenantResolver` resolves a seed custom-domain row from
  `platform_host_to_tenant`, with no network call, **as `learnstack_app` with
  `app.tenant_id` unset** — the row that determines the tenant is readable before any
  tenant context exists, and only that row is
  (`Host_Resolves_With_No_Tenant_Context_Under_Rls`).
- The same connection with neither `app.resolving_host` nor `app.tenant_id` set reads
  **zero** rows from `platform_host_to_tenant` and **zero** rows from `tenants`: the
  application role can neither enumerate the host map nor enumerate the customer list
  (`App_Role_Cannot_Enumerate_Host_Map`, `App_Role_Cannot_Enumerate_Tenants`).
- Tenant A cannot repoint tenant B's host even though the resolver policy can see it —
  the `UPDATE` affects zero rows and an `INSERT` naming tenant B is rejected by
  `WITH CHECK` (`Tenant_A_Cannot_Repoint_Tenant_B_Host`).
- All nine tenancy tables report `relrowsecurity` **and** `relforcerowsecurity` true in
  `pg_class`, with no exception list.
- `make dev`, `make seed` and `make test` succeed on a clean checkout — `make seed`
  currently exits non-zero on every run, and that is a completion blocker, not a
  nuisance.
- Architecture tests for tenant + org ownership, RLS structure, module-boundary
  direction, domain-neutral module naming, and the audit pipeline are not skippable.

## Risks

- **A structural test standing in for a behavioural one.** The superseded RLS template
  satisfied every "does a policy exist" assertion in the catalogue while leaking every
  tenant-wide row across tenants. A test that converts an open question into a false
  answer is worse than no test. Mitigated by making the Packet 7 integration suite —
  running as `learnstack_app` — the binding proof, and by keeping the structural
  assertions as a cheap complement rather than the guarantee.
- **Isolation tests that connect as the wrong role.** Running them as the table owner
  or a `BYPASSRLS` role produces a green suite against inert policies. Mitigated by
  pinning the test connection string to `learnstack_app` in the integration fixture.
- Leaving tenant or organization enforcement to developer discipline; mitigated by RLS
  + architecture tests + EF global filters, all three.
- Treating RLS as optional "later" hardening — explicitly rejected by ADR-0003.
- **The deferrals being read as cancellations.** Dapr, Kafka, APISIX, Vault, the Hub
  and licence keys are demand-gated with written triggers in
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md), not dropped. Mitigated
  by the trigger table being normative and by every port shipping in this phase.
- **The phase growing back.** Eleven packets with no user-visible output was the
  original shape, and every removed item has a plausible argument for return.
  Mitigated by [Phase 02d](phase-02d-walking-skeleton.md) sitting immediately after
  this phase: anything that does not move the walking skeleton closer belongs to a
  later phase.
- Audit pipeline overhead on hot paths — mitigated by `AuditConfig` narrowing
  SHOULD/MAY coverage per tenant; MUST-class overhead is not negotiable, and the
  budget is reviewed in [Phase 11](phase-11-production-hardening.md) when partitioning
  lands.

## Phase Exit Decision

[Phase 02d](phase-02d-walking-skeleton.md) can begin when a reviewer, on a clean
checkout, can run `make test` and see: the architecture-test assembly green with zero
skips; the Packet 7 isolation suite green **connected as `learnstack_app`**, including a
case that reads with `app.tenant_id` reset rather than merely unset; a MUST-class command
whose audit store is unavailable rejected rather than committed; and two seed tenants in
unrelated domains, two organizations each, resolvable by host and returning
tenant-specific customization data through the runtime read paths.

[Phase 02c](phase-02c-hub-foundation.md) is not gated on this phase's exit in the
ordinary sense: it hangs off the spine and starts when its trigger fires (a tenant must
be billed or plan-gated), consuming the `IEntitlementProvider` and
`IHostToTenantResolver` sockets this phase ships.

### ADR commitments that must land in this phase

Three ADRs targeted Phase 02a as exit blockers; all three are now Accepted
(Packet 1):

| # | Topic | Status | Decision |
|---|---|---|---|
| [ADR-0023](../decisions/0023-strongly-typed-id-source-generator.md) | Strongly-typed ID source generator | **Accepted** (2026-05-20) | Vogen as the emitter for both IDs and value objects |
| [ADR-0024](../decisions/0024-api-versioning-policy.md) | API versioning policy | **Accepted** (2026-05-20) | URL `/v{N}/`, 6-month deprecation window, RFC 8594 `Sunset` + `Deprecation` headers, OpenAPI `x-sunset` extensions |
| [ADR-0028](../decisions/0028-audit-log-partition-management.md) | `audit_log` monthly partition management | **Accepted** (2026-05-20) | Daily Hangfire recurring job (`learnstack:audit:partition-management`); no `pg_partman` runtime dependency. Its *implementation* moves to [Phase 11](phase-11-production-hardening.md) per [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) — the decision stands, the schedule changed |

Four further decisions were taken during the phase and are Accepted:

| # | Topic | Status | Decision |
|---|---|---|---|
| [ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md) | RLS policy template + database role model | **Accepted** (2026-08-08) | One `AND`-ed policy, `FORCE ROW LEVEL SECURITY`, explicit `WITH CHECK`, four-role model; canonical template in Standards 05 only |
| [ADR-0033](../decisions/0033-audit-durability-model.md) | Audit durability | **Accepted** (2026-08-08) | Supersedes ADR-0016. MUST-class audit is a durable intent inside the business transaction and fails closed; SHOULD/MAY stays best-effort |
| [ADR-0034](../decisions/0034-hub-contract-surface-invariant.md) | Hub contract surface | **Accepted** (2026-08-08) | Two invariants replace the endpoint count; host resolution never calls the Hub |
| [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) | Demand-gated infrastructure | **Accepted** (2026-08-08) | The one-way-door test; ports ship now, adapters ship on a named trigger |

The remaining exit gates (tenant + organization resolution, isolation tests running as
`learnstack_app`, the durable audit pipeline, customization runtime read paths, API
conventions, two seed tenants, architecture-test catalogue green) close as Packets
3b–10 ship.



## Delivery Record (Packets 0–3)

Shipped history, kept verbatim. Packets 0–3 and the 2026-08-08 restructure annotation
are frozen: read them, do not edit them. They were written when they sat at the top of
this file, so where they say "below" and "above" they mean the original layout — the
forward plan they point at is [`## Packet Sequence`](#packet-sequence), now above them.
Nothing in them was reworded when they moved.

> **Packet 0 — Kickoff ✅ (this commit)**
> Phase 02a packet breakdown captured in this Status block. Glossary
> entries for "Phase", "Packet", and "Kickoff Packet" added under a new
> *Roadmap & Delivery* group so the terms are defined in exactly one
> place. No code, no ADR state changes — Packet 0 is a planning slice
> that unblocks the rest of the phase by fixing the order.
>
> **Packet 1 — Foundation decisions ✅**
> The three Phase-02a-blocking ADRs are now Accepted:
> [ADR-0023](../decisions/0023-strongly-typed-id-source-generator.md)
> picks **Vogen** as the source generator for both IDs and value objects;
> [ADR-0024](../decisions/0024-api-versioning-policy.md) codifies
> URL-based versioning with a **6-month deprecation window** + RFC 8594
> `Sunset` / `Deprecation` headers + OpenAPI `x-sunset` extensions;
> [ADR-0028](../decisions/0028-audit-log-partition-management.md) picks
> a **Hangfire recurring job** (`learnstack:audit:partition-management`)
> over `pg_partman` to keep the SelfHostedAirGapped story extension-free.
> Decision-only; no code. Standards 02 § Strongly-Typed Identifiers and
> Standards 04 § Versioning cross-link to the new ADRs.
>
> **Packet 2 — Shared Kernel core ✅**
> `IClock` + `SystemClock` / `FixedClock`, `IRandom` + `SystemRandom` /
> `FixedRandom`, `IGuidFactory` + `SystemGuidFactory` / `FixedGuidFactory`
> (deterministic-test abstractions per Standards 02 § Time);
> `LocalizedMessage` carrying the `lockey_` prefix invariant at the
> constructor (used by every `Result.Fail` and success-message payload);
> `Error` refactored to wrap `LocalizedMessage` with a `Code` projection
> over `Message.Key`; `Result<T>` extended with `IResultBase`, success-message
> overload, and the static `Result.FailFor<T>(error)` factory ADR-0032's
> `ValidationBehavior` consumes; `Entity<TId>` (append-only / audit
> aggregate base) + `AuditableEntity<TId>` (mutable aggregate base with
> `CreatedAt` / `CreatedBy` / `UpdatedAt` / `UpdatedBy` / `DeletedAt` /
> `DeletedBy` / `Version` plus the `IsDeleted` projection);
> `ISoftDelete` + `IOptimisticConcurrency` marker interfaces;
> `IDomainEvent : INotification` + `DomainEvent` base + `IHasDomainEvents`
> aggregate-side collector; cursor-first pagination
> (`CursorPagination` / `Page<T>` / `PageInfo` matching Standards 04
> § Pagination). Vogen 7.0.0 wired per ADR-0023 with
> `LearnStackVogenDefaults.IdMask` carrying the canonical
> `EfCoreValueConverter | SystemTextJson | TypeConverter` mask;
> `IAggregateRoot<TId>` / `IHasId<TId>` interfaces require `TId :
> IStronglyTypedId<Guid>` so future module aggregates inherit the
> constraint. Unit / architecture / contract suites all green in CI; the
> `VogenIdEmissionTests` smoke test asserts the emitter pipeline
> (Vogen `[ValueObject<Guid>]` → `IStronglyTypedId.Value` → JSON
> round-trip → `TypeConverter` round-trip) end-to-end via a synthetic
> `TestId` in the test project.
> Review pass folded in (commit `7c9133a`): `Entity<TId>` equality carries
> transient + cross-runtime-type guards; `Result.FailFor<TResponse>` returns
> the concrete `TResponse` via reflection (not `Result<TResponse>`);
> `Error.Code` is the unprefixed stable identifier projected from
> `Message.Key`; `Error.Details` flows `LocalizedMessage` lists so the prefix
> invariant covers field-level errors; `Result<T>.Ok` rejects null;
> `UserId` is a SharedKernel-level Vogen value object used by
> `AuditableEntity<TId>` instead of raw `Guid`; `DomainEvent.EventId` /
> `OccurredAt` are `required init`; `MarkCreated` throws on second call;
> `SoftDelete` bumps `UpdatedAt` for monotonic last-touched;
> `CursorPagination` validates `Limit > 0` at the ctor. Standards 01
> § Dependency Direction grows a "Build-time-only exceptions" sub-section
> for the EF Core + MediatR references SharedKernel requires. Unit /
> architecture / contract suites all green in CI.
>
> **Packet 3 — Cross-cutting foundation ✅**
> The [ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md)
> surface is wired end to end via the
> [wire-cross-cutting-foundation](../../.claude/skills/wire-cross-cutting-foundation/SKILL.md)
> skill. Shipped:
>
> - L1 `LearnStackExceptionHandler : IExceptionHandler` registered through
>   `services.AddExceptionHandler<T>()` + `app.UseExceptionHandler()`;
>   `ShouldCapture(ex)` switch drives the Sentry-vs-OTel boundary per
>   Standards 09 (`OperationCanceledException` + client-error
>   `ProviderException` skip capture).
> - Eight-step MediatR pipeline in `LearnStack.Application/Pipeline/`:
>   `Validation` + `Logging` are full implementations; `AuditLog` ships the
>   try / `ExceptionDispatchInfo` rethrow shell (audit-write lights up in
>   Packet 9 when `IAuditStore` lands); `TenantContext` shell short-circuits
>   with `Result.Fail(tenant_mismatch)` until the resolver middleware arrives
>   in Packet 7; `Authorization` / `Transaction` / `OutboxFlush` pass-through
>   shells whose registration order is the binding part. Order encoded in
>   `MediatRPipelineRegistration.CanonicalBehaviorOrder` and asserted by the
>   `MediatR_Pipeline_Order_Matches_Canonical_Sequence` architecture test.
> - `Result<T>.ToActionResult()` extension in `LearnStack.Api.Common`
>   alongside `ProblemDetailsFactory` + `HttpStatusMap`; explicit at every
>   future controller endpoint per Standards 09.
> - `LearnStackException` hierarchy in `LearnStack.SharedKernel/Errors/`:
>   `DomainException`, `InfrastructureException`, `ProviderException` (with
>   `IsClientError` flag), `TenantContextMissingException`.
>   `LearnStackException-DomainExceptionThrow` Roslyn analyzer under
>   `backend/analyzers/LearnStack.Analyzers/` flags `throw new DomainException`
>   in `Domain` + `Application` (warning class in Phase 02a, escalates to
>   error after Phase 03 exit per ADR-0032 § Sub-decision 4).
> - `IProviderResilience<TPort>` socket in
>   `LearnStack.SharedKernel/Resilience/` + Polly v8
>   `ResiliencePipeline` builder in
>   `LearnStack.Infrastructure.Resilience/`. Pipeline = retry (only
>   `InfrastructureException` + non-client `ProviderException`) → circuit
>   breaker → timeout → bulkhead. Configuration shape:
>   `appsettings.Resilience:<portName>:` (sample lit up under
>   `liveclass`, `payment`, `storage`, `search`). Per-adapter decoration is
>   the adapter's responsibility — the socket is what adapters consume in
>   Phase 02b+ via the [add-provider-adapter](../../.claude/skills/add-provider-adapter/SKILL.md)
>   skill. Hub HTTP clients excluded per Sub-decision 5.
> - Serilog primary logger wired in `LearnStack.Api/Composition/CrossCuttingFoundationExtensions.cs`
>   with `WriteTo.Console(RenderedCompactJsonFormatter)` +
>   `WriteTo.OpenTelemetry(OTLP gRPC)`. The OTel `LoggerProvider` is
>   intentionally **not** registered alongside per ADR-0032 § Sub-decision 8.
> - OpenTelemetry SDK with `AspNetCore` + `HttpClient` +
>   `EntityFrameworkCore` instrumentations + the OTLP exporter, plus the
>   singleton `TenantContextSpanProcessor` from
>   `LearnStack.Infrastructure.Observability/` that enriches every span
>   with `tenant.id` / `organization.id` / `user.id` / `correlation.id` /
>   `module` from the singleton
>   `ITenantContextAccessor` (`AsyncLocal<ITenantContext?>`-backed).
> - `IErrorTrackingProvider` socket in `LearnStack.SharedKernel/Observability/`
>   with three implementations in `LearnStack.Infrastructure.ErrorTracking/`:
>   `NoOpErrorTracker` (Development), `SentryErrorTracker` (SaaS / Dedicated /
>   SelfHostedOnline-with-DSN), `LocalFileErrorTracker`
>   (SelfHostedAirGapped, writes JSON envelopes to a configured directory).
>   Composition-root branching by `DeploymentMode` per Standards 20 table.
>   Sentry SDK is referenced only by the ErrorTracking project — enforced
>   by the `Modules_Do_Not_Reference_Sentry_SDK_Directly` architecture test.
> - Architecture tests green (`backend/tests/LearnStack.Tests.Architecture/CrossCuttingFoundationTests.cs`):
>   `MediatR_Pipeline_Order_Matches_Canonical_Sequence`,
>   `IExceptionHandler_Registered_AtStartup`,
>   `OTel_Pipeline_Includes_TenantContextSpanProcessor`,
>   `Logging_Goes_Through_Microsoft_Extensions_Logging`,
>   `Modules_Do_Not_Reference_Sentry_SDK_Directly`,
>   `Adapters_Wrap_Provider_Exceptions`,
>   `IErrorTrackingProvider_Is_Singleton`. Unit tests green for
>   `ValidationBehavior` (returns `Result.Fail(validation_failed)`,
>   never throws `ValidationException`), `AuditLogBehavior` (preserves
>   stack via `ExceptionDispatchInfo`), `TenantContextBehavior`
>   (short-circuits unresolved context), `TenantContextSpanProcessor`
>   (`OnStart` is null-safe and enriches resolved spans),
>   `ProviderResilience<TPort>` (retries non-client failures, skips
>   client-error retries), `HttpStatusMap` (mirrors Standards 09 table),
>   `ResultExtensions.ToActionResult()` (Problem Details shape),
>   `LocalFileErrorTracker` (writes the JSON envelope).
> - Initial validation (pre-review): `LearnStack.Tests.Unit` 111/111,
>   `LearnStack.Tests.Architecture` 25/25, `LearnStack.Tests.Contract` 1/1,
>   `LearnStack.Tests.Integration` 5/5 green under `CI=true`. Superseded by
>   the post-review-3/4 counts below.
>
> Review fixes (commit `<follow-up>`):
>
> - **B1** — Sentry DSN now reads via the new `ISecretProvider` socket
>   (`LearnStack.SharedKernel/Secrets/`) with `ConfigurationSecretProvider`
>   as the Phase 02a default; Packet 5 swaps in `DaprSecretProvider` for
>   Vault. ADR-0032 § Sub-decision 9 contract honoured.
> - **B2** — Serilog pipeline gains `CorrelationContextEnricher`
>   (copies tenant / org / user / correlation / module from
>   `ITenantContextAccessor` onto every log event) +
>   `RedactSensitiveFieldsEnricher` (scrubs password / token / secret /
>   DSN / JWT / authorization / SSN / TCKN / card-number tokens before
>   the formatter touches them). `LocalFileErrorTracker` redacts the
>   same token set on `CapturedContext.AdditionalTags`.
> - **M1** — `ProblemDetailsFactory.For(Exception)` routes status through
>   `HttpStatusMap.For(Exception)` so `ProviderException(IsClientError:true)`
>   returns 400 instead of falling through to 503 via the carried Error's
>   default code.
> - **M2** — L1 handler now skips `Activity.AddException` for
>   `ProviderException(IsClientError:true)` per Standards 09 § Sentry vs
>   OpenTelemetry table — `SetStatus(Error)` only, no exception event.
> - **M3** — All 14 module Domain + Application csproj files now reference
>   `LearnStack.Analyzers` via `OutputItemType="Analyzer"`. Future
>   `throw new DomainException(...)` in any module fails the analyzer.
> - **M4** — Polly `IProviderResilience<TPort>` pipeline now consumes
>   `BulkheadOptions` via `Polly.RateLimiting`'s
>   `AddRateLimiter(ConcurrencyLimiterOptions)`; the silent-dead config
>   gap is closed.
> - **N1** — `ProblemDetailsFactory` projects nested
>   FluentValidation property paths (`Address.Street`) and acronyms
>   (`URLValue`) to the right camelCase shape via
>   `JsonNamingPolicy.CamelCase`.
> - **N2** — `ToActionResult()` returns `ProblemDetailsActionResult`
>   which builds the body inside `ExecuteResultAsync(ActionContext)`, so
>   the sanctioned `(await Send(...)).ToActionResult()` shape populates
>   `Instance` + `correlationId` without the caller threading
>   `HttpContext`.
> - **N3 / A6** — `LocalFileErrorTracker` file names suffix a Guid for
>   guaranteed uniqueness in same-millisecond bursts; `stackalloc` is
>   capped at 128 chars so a multi-KB inbound `traceparent` cannot blow
>   the stack.
> - **A5** — `AuditLogBehavior` catch filter excludes
>   `OperationCanceledException` so client disconnects no longer churn
>   warning logs / future audit rows.
> - **A7** — `MediatRPipelineRegistration.CanonicalBehaviorOrder`
>   documentation explicitly notes "7 behaviors + the handler at the
>   innermost position = the 8 canonical steps".
> - **A8** — `ProblemDetailsFactory` strips the `_failed` suffix from
>   the Problem `type` URL (matches the Standards 09 § API Surface
>   example: `/validation`, not `/validation_failed`).
> - **A11** — New HTTP-level integration tests in
>   `LearnStack.Tests.Integration/CrossCuttingFoundationHttpTests` exercise
>   the L1 handler + ValidationBehavior end-to-end via
>   `WebApplicationFactory<Program>` and a synthetic test controller. The
>   Standards 21 catalogue row for
>   `ValidationBehavior_DoesNotThrow_ValidationException` is updated to
>   "unit + integration".
> - **A12** — `LearnStackExceptionHandler` is `internal sealed` — only
>   the framework's `AddExceptionHandler<T>()` instantiates it; tests
>   reach the type via `InternalsVisibleTo`.
> - **A13** — `TenantContextSpanProcessor` stringifies Guid tags
>   (`tenant.id` / `organization.id` / `user.id`) so the wire format is
>   stable across exporters.
> - **S1** — `MediatR_Pipeline_Order_Matches_Canonical_Sequence` test
>   asserts a hardcoded behavior-type sequence, not the production
>   `CanonicalBehaviorOrder` list, so an accidental list reorder cannot
>   sneak past.
> - **SU1** — L1 handler skips the body write on
>   `OperationCanceledException` / cancelled `CancellationToken`; the
>   client has already disconnected.
> - **SU4** — `TenantContextBehavior.AllowsUnresolvedContext` predicate
>   carries a TODO documenting the Packet 7 marker-attribute seam
>   (`[AllowsUnresolvedTenantContext]`).
> - New architecture test `Modules_Do_Not_Reference_DeploymentMode` lit
>   up — catalogue entry existed since ADR-0020 but had no implementation
>   until now.
>
> Review-2 fixes:
>
> - **N4** — `CrossCuttingFoundationExtensions.SelectSecretProvider`
>   becomes the single composition-root site that picks the
>   `ISecretProvider` implementation per `DeploymentMode`. Both the DI
>   registration and the local `AddLearnStackErrorTracking` argument
>   read the same instance. Packet 5's `DaprSecretProvider` swap now
>   touches one line, not two.
> - **SU5** — `SensitiveTokenCatalog` in
>   `LearnStack.SharedKernel/Secrets/` is the single source of truth for
>   the sensitive-property-name token list. Both
>   `RedactSensitiveFieldsEnricher` and
>   `LocalFileErrorTracker.RedactSensitiveTags` consume
>   `SensitiveTokenCatalog.IsSensitive(...)` so the two redaction
>   surfaces cannot drift. The catalogue now includes `vkn` (Vergi
>   Kimlik Numarası — Turkish corporate tax number, common for
>   instructor-owned sole proprietorships) alongside `tckn`.
> - **SU6** — `RedactSensitiveFieldsEnricher` remarks carry a dated
>   TODO naming the Packet 7+ Roslyn analyzer that should extend
>   `LearnStack.Analyzers` to flag string-interpolated
>   `throw new ...Exception($"...{token}...")` patterns in `Domain` +
>   `Application` projects. Runtime redaction covers logs / OTLP / Sentry
>   tags; the analyzer closes the last gap (secrets in exception
>   messages) at compile time.
> - **SU7** — `HttpStatusMap.For(Exception)` carries a rationale comment
>   for the non-IETF `499` "client closed request" status: matches
>   Nginx / IIS / Envoy / APISIX behaviour, keeps client disconnects
>   off the error-budget axis, and points at the L1 handler's skip-body
>   contract. If a future ADR pins a different code, the one comment
>   block is the seam to change.
>
> Review-3/4 fixes:
>
> - **H1 (blocker)** — the `DomainExceptionThrow` analyzer used a hyphenated
>   Roslyn diagnostic id, which Roslyn rejects (`AD0001` crash at report
>   time → CI build break under `TreatWarningsAsErrors` the first time a
>   `DomainException` is thrown). Fixed: diagnostic id is now `LS0001`
>   (valid identifier); `LearnStackException-DomainExceptionThrow` is
>   retained as the human-readable rule name. `LS0001` is listed in
>   `WarningsNotAsErrors` until the Phase 03 escalation so a legitimate
>   aggregate-invariant throw does not break CI. New
>   `DomainExceptionThrowAnalyzerTests` (`LearnStack.Tests.Unit`) run the
>   analyzer over synthetic compilations and assert `LS0001` is emitted
>   (no `AD0001`). Recorded as [ADR-0032 Amendment 1](../decisions/0032-exception-handling-logging-and-observability.md);
>   Standards 21 naming convention + analyzer entry updated.
> - **Provider error body/status consistency** — `HttpStatusMap.For(Exception)`
>   now derives the HTTP status from the carried `Error.Code` for every
>   `LearnStackException`, instead of special-casing
>   `ProviderException.IsClientError → 400`. `IsClientError` is purely an
>   observability concern (it gates Sentry capture), so a bare provider
>   failure is `dependency_unavailable` → 503 and an adapter surfacing a
>   provider 4xx passes an explicit `Error` (e.g. `validation_failed` → 400);
>   either way body code and status agree. Tests updated to assert both.
> - **Redaction over-match + nesting** — `SensitiveTokenCatalog.IsSensitive`
>   now matches on word-segment boundaries (camelCase / `_ . -`) instead of
>   raw substrings, so `ClassName` / `BusinessName` are no longer redacted
>   by the `ssn` token while `Password` / `ApiKey` / `SSNToken` still are.
>   `RedactSensitiveFieldsEnricher` recurses into destructured objects,
>   dictionaries, and sequences so a sensitive field nested in a
>   non-sensitive top-level property (`User.Password`) is scrubbed; lazy
>   reconstruction keeps clean events allocation-free. New
>   `SensitiveTokenCatalogTests` + `RedactSensitiveFieldsEnricherTests`.
> - **OTel naming + air-gapped** — the manual `AddSource` / `AddMeter`
>   filters use the documented lowercase `learnstack.*` convention (matching
>   the `learnstack.mediatr` ActivitySource without relying on
>   case-insensitive wildcard matching). `WireSerilog` / `WireOpenTelemetry`
>   now take `DeploymentMode`; `SelfHostedAirGapped` never wires the network
>   OTLP exporters (no-egress contract), with a dated TODO for the
>   `/var/learnstack/otel/` file target deferred to Phase 11 ops.
> - **M1** — new `Handlers_Return_Result` architecture test asserts every
>   `IRequestHandler<,TResponse>` has `TResponse : IResultBase`, so a
>   raw-DTO handler can't silently bypass the pipeline (validation / audit /
>   tenant-context + RLS). Vacuous today, active when handlers land.
> - **L4** — the Serilog enrichers are resolved from DI (the singletons
>   registered in `AddLearnStackObservabilityServices`) instead of being
>   `new()`'d in the pipeline, so there are no dead registrations.
> - **Docs** — `Domain_Methods_Do_Not_Throw_For_Expected_Cases` marked
>   **deferred** in Standards 21 (the `LS0001` analyzer already enforces the
>   rule at build time; the report-walking architecture test lands with
>   module domain code in Packet 6+). LoggingBehavior activity-name doc,
>   `correlationId`-as-full-traceparent in Standards 09/10, and the
>   `IMeterFactory.Create("learnstack.<module>")` example in architecture 33
>   reconciled with the code.
> - **Skipped (with reason)** — the resilience pipeline order
>   (retry → breaker → timeout → bulkhead) is left as-is: it is faithful to
>   ADR-0032 § Sub-decision 5's stated order. Whether the concurrency
>   limiter should sit outermost (to cap total in-flight including retries,
>   per the `Microsoft.Extensions.Resilience` standard handler) is an
>   ADR-level question for a future amendment, not a code defect in this
>   packet.
>
> Validation after review-3/4: `dotnet build LearnStack.slnx` (CI=true)
> clean; `LearnStack.Tests.Unit` 154/154, `LearnStack.Tests.Architecture`
> 26/26, `LearnStack.Tests.Integration` 5/5, `LearnStack.Tests.Contract`
> 1/1 green. (CI's `dotnet format --verify-no-changes` step also gates the
> backend job — run `dotnet format LearnStack.slnx --verify-no-changes`
> locally before pushing.)
>
> **Deferred follow-ups carried out of Packet 3** (each is recorded in its
> owning packet/phase below so it does not slip; no separate issue tracker
> needed — the roadmap is the backlog):
>
> - **Strongly-typed IDs for `ITenantContext` + `CapturedContext`** →
>   **Packet 7** (after the `TenantId` / `OrganizationId` Vogen value
>   objects land in Packet 6). Both contracts use raw `Guid` in Packet 3
>   because those VOs don't exist yet (only `UserId` does); they convert
>   together in one pass to avoid a half-typed intermediate.
> - **`[AllowsUnresolvedTenantContext]` marker attribute** for the
>   `TenantContextBehavior` opt-out (tenant-provisioning / platform-admin
>   commands) → **Packet 7**.
> - **`TransactionBehavior` / `OutboxFlushBehavior` shells light up** →
>   **Packet 6** (UoW transaction, per-module `DbContext`) and **Phase 02b**
>   (outbox enrolment) respectively.
> - **`AuthorizationBehavior` shell lights up** + **`LS0001` analyzer
>   severity escalates Warning → Error** (and is removed from
>   `WarningsNotAsErrors`) → **Phase 03 exit**.
> - **`Domain_Methods_Do_Not_Throw_For_Expected_Cases`** report-walking
>   architecture test → **Packet 10** (needs module domain code to walk;
>   until then the `LS0001` analyzer only does partial detection — it
>   reports every direct `throw new DomainException(...)` in `Domain` /
>   `Application` as a build-time Warning, non-blocking under
>   `WarningsNotAsErrors` until the Phase 03 exit escalation above, and does
>   not replace the broader report-walking test).
> - **Air-gapped OTLP file target** (`/var/learnstack/otel/`) → **Phase 11**
>   ops (the no-egress branch already prevents network export in
>   `SelfHostedAirGapped`; the file sink needs an exporter-package decision,
>   the operational controls in
>   [phase-11-production-hardening.md](phase-11-production-hardening.md#observability),
>   and a test asserting no network telemetry exporter is ever wired under
>   `SelfHostedAirGapped`).
> - **"No secrets in exception messages" Roslyn analyzer** (compile-time
>   complement to the runtime redactor) → **Phase 02b or later** (code TODO
>   in `RedactSensitiveFieldsEnricher`).
> - **Resilience pipeline order** (should the concurrency limiter sit
>   outermost to cap total in-flight incl. retries?) → **future ADR-0032
>   amendment**; the current order is faithful to ADR-0032 § Sub-decision 5.
>
> ---
>
> **Restructure (2026-08-08).** Packets 4–10 below were re-scoped after a
> four-report audit of the corpus. Packets 0–3 are shipped and their records
> above are unchanged.
>
> **Two things in the Packet 2 and Packet 3 records above are now known to be
> wrong. Do not act on them; Packet 3b corrects both:**
>
> - The Packet 3 `TenantContextBehavior` TODO names a `DbConnectionInterceptor`
>   as the mechanism for setting the Row Level Security session variables. That
>   is the wrong option. Interceptors fire when the connection opens, not when
>   the transaction starts, and `set_config(..., true)` is transaction-local —
>   so the value would be discarded before the query it protects ever runs. The
>   GUCs are set with `SET LOCAL` **inside the ambient transaction**, per
>   [Security Standards § Tenant Context](../standards/11-security.md), and
>   Packet 7 implements it.
> - The Packet 2 Shared Kernel shipped three defects that get more expensive
>   with every consumer: `Results.Unit` collides with `MediatR.Unit`,
>   `Result<T>` carries no `[MemberNotNullWhen]`, and `Entity<TId>` implements
>   neither `IEquatable<>` nor `operator ==` so every comparison boxes. All
>   three are repaired in Packet 3b, before the first handler exists.
>
> Separately, [ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md)'s
> `IProviderResilience<TPort>` registration **example** does not compile, but
> the code Packet 3 actually shipped is correct — read the code, not the ADR
> snippet. ADR-0032 Amendment 2 records this.
>
> Three things moved:
>
> - **Correctness moved earlier.** The Row Level Security template published in
>   [ADR-0003](../decisions/0003-tenant-isolation-defense-in-depth.md) Amendment 1 and
>   copied into three further documents created two *permissive* policies, which
>   PostgreSQL combines with `OR` — making every tenant-wide row visible across
>   tenants. [ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md)
>   corrects it, and Packet 6 is now the packet that must not be written against
>   the old template. Audit durability moved with it:
>   [ADR-0033](../decisions/0033-audit-durability-model.md) makes MUST-class
>   audit a durable intent inside the business transaction, which is also what
>   stops the corrected RLS policy from silently rejecting every audit insert.
> - **Additive infrastructure moved later.** Per
>   [ADR-0035](../decisions/0035-demand-gated-infrastructure.md), Packet 5 now
>   ships the foundation **ports and their default implementations**; the Dapr,
>   Kafka, APISIX and Vault adapters land in
>   [Phase 11](phase-11-production-hardening.md) against written trigger
>   conditions. Packet 8 drops from eight customization aggregates to two, and
>   Packet 9's `audit_log` partitioning moves to Phase 11.
> - **Proof moved earlier.** The second tenant — the artefact that tests the
>   genericity claim — moves from [Phase 10](phase-10-english-learning-mvp.md)
>   into Packet 7, where two seed tenants already exist for isolation testing.
>   [Phase 02d](phase-02d-walking-skeleton.md) then renders both of them in a
>   browser.
>
