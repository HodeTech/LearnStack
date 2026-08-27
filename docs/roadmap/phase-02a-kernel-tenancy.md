# Phase 02a: Platform Kernel, Multi-Tenancy, Organization, and Foundation Sockets

> **Status (2026-08-20).** Phase 02a in progress. Packets 0–3, 3b and 4 shipped; the
> 2026-08-08 restructure re-scoped packets 4–10 and added packet 3b. Each packet
> is independently reviewable in its own commit, matching the
> [Phase 01 cadence](phase-01-repository-tooling.md). The order is dependency-driven: a
> later packet may consume any earlier packet's deliverables, never the reverse.
>
> | Packet | Title | State |
> |---|---|---|
> | 0 | Kickoff | ✅ [record](#delivery-record-packets-03) |
> | 1 | Foundation decisions | ✅ [record](#delivery-record-packets-03) |
> | 2 | Shared Kernel core | ✅ [record](#delivery-record-packets-03) |
> | 3 | Cross-cutting foundation | ✅ [record](#delivery-record-packets-03) |
> | 3b | Decision repair | ✅ [record](#delivery-record-packet-3b) |
> | 4 | API conventions | ✅ [record](#delivery-record-packet-4) |
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
> rewritten. Packet 3b has its own record in
> [`## Delivery Record (Packet 3b)`](#delivery-record-packet-3b), and Packet 4 in
> [`## Delivery Record (Packet 4)`](#delivery-record-packet-4), and Packet 5 in
> [`## Delivery Record (Packet 5)`](#delivery-record-packet-5) — each kept separate
> because the frozen one is scoped to packets 0–3.**

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
- [ADR-0036 Trusted Inputs for Tenant and Organization Resolution](../decisions/0036-tenant-resolution-trusted-inputs.md)

[ADR-0014 (Dapr)](../decisions/0014-adopt-dapr.md) and
[ADR-0015 (APISIX)](../decisions/0015-api-gateway-apisix.md) remain accepted decisions
about **what** LearnStack uses; ADR-0035 decides **when** each arrives, and the answer
is not this phase.

## Packet Sequence

The forward plan for packets 3b–10. Each entry states what the packet lands, what it
depends on, and what gates it. The subsystem view of the same work is [`## Scope`](#scope);
where the two overlap, Scope is the authority on shape and this section on order.

**Packet 3b — Decision repair ✅** ([delivery record](#delivery-record-packet-3b))
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
  import both. Renamed to **`None`** before the first module handler exists —
  [Phase 02d](phase-02d-walking-skeleton.md) writes it. `None` was chosen
  because it collides with nothing in the BCL, MediatR, EF Core, Vogen,
  FluentValidation or Polly, and needs no keyword suppression. The rename
  sweeps `Result.cs`'s XML doc and its `ArgumentNullException` message,
  `ResultTests`, [Standards 09](../standards/09-error-handling.md),
  [Standards 02](../standards/02-backend-coding.md) and the
  [glossary](../glossary.md) — the type file alone is not "done".

  `LearnStack-Hub` mirrors this kernel and hit the same collision, which it
  mitigated differently rather than missing. Its current state and its
  reconciliation are tracked in that repository, not restated here — a second copy
  of a plan is a plan that will be wrong ([Phase 02c](phase-02c-hub-foundation.md)).
  LearnStack's side of the boundary is the name: **`None`**.
- `Result<T>` carries no `[MemberNotNullWhen]` annotations, so the compiler
  cannot prove `Value` is non-null after an `IsSuccess` check. Without them
  every consumer writes `!` or a justification comment, in every module, for
  the lifetime of the codebase. Annotated on **both** `Result<T>` and
  `IResultBase` — the attributes do not flow from an interface to its
  implementations, so a caller typed to `Result<T>` gains nothing from the
  interface's copy alone. Proved by a test that dereferences `Value` after an
  `IsSuccess` check with no `!` and no `#pragma`: remove the annotations and the
  test stops compiling, which is the only way to assert a compile-time contract
  from inside a test suite.
- `Entity<TId>` overrides `Equals(object?)` but implements neither
  `IEquatable<Entity<TId>>` nor `operator ==`. Two consequences, and the widely
  assumed one is not among them: **EF Core's change tracker does not call
  `Entity<TId>.Equals`** — its identity map keys on the primary-key value through
  a `ValueComparer` and tracks instances by reference. What actually breaks is
  (a) without `==`, two aggregates compared with `==` fall back to reference
  equality, silently skipping the transient and cross-type guards `Equals`
  enforces — so `a == b` and `a.Equals(b)` disagree; and (b) `Id.Equals(other.Id)`
  binds to `ValueType.Equals(object)` and boxes the struct id on **every**
  comparison. Measured per call on the shipped kernel, Release:

  | | `Equals` / `==` / `Equals(object)` | `GetHashCode` |
  |---|---|---|
  | As Packet 3 shipped it | 120 B | 40 B |
  | Dead `default(TId)` guard removed | 40 B | 0 B |
  | `IEquatable<TId>` added to the constraint | **0 B** | **0 B** |

  Two causes, not one, and it is worth keeping them apart: the constraint accounts
  for a single boxed call, while the guard that turned out to be dead accounted for
  two more. All three equality entry points now delegate to one typed body, and
  `Equals(object?)` / `GetHashCode()` are `sealed override` so no aggregate can
  redefine them — with both sealed, a derived `operator ==` can no longer silence
  CS0660 / CS0661 and fails the build.

  Two things this packet found only by measuring, both now fixed and both
  previously stated wrong in this document:

  - The transient guard was **dead code**. It asked `Id.Equals(default(TId))`, but
    a Vogen `[ValueObject]` returns `false` from `Equals` when either side is
    uninitialized — so the question answered `false` for a transient id and the
    guard never ran. `GetHashCode` therefore took the `HashCode.Combine` branch and
    **threw** `ValueObjectValidationException` for any unsaved aggregate: a
    `HashSet` of two new aggregates was an exception, not a set. `IStronglyTypedId`
    gains `IsInitialized()` — which every Vogen id already emits — and both guards
    ask that instead.
  - `EqualityComparer<T>.Default` still picks `ObjectEqualityComparer` for a
    concrete aggregate, because `Course` is `IEquatable<Entity<CourseId>>` and never
    `IEquatable<Course>`. Fixing that needs a CRTP base (`Entity<TSelf, TId>`) and
    is **not** worth it: with the id constraint in place, routing through
    `Equals(object?)` allocates nothing.

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
  `ConfigurationSecretProvider.cs` carries a tenth. The restructure fixed its
  *packet pointer* but left ADR-0035's explicitly rejected trigger clause — "or a
  non-development deployment" — in place, so it is **ten** sites, not nine. Two carry
  a second error beyond the phase: `ISecretProvider.cs` says Packet 5 adds the
  `DeploymentMode` branching, when Packet 3 shipped the single selection site and only
  the branch is deferred; and the `SelectSecretProvider` TODO invents a
  `FileSecretProvider` type that appears in no document. The matrix in
  [Standards 20](../standards/20-infrastructure-stack.md) reads
  "`DaprSecretProvider` → Vault **or file**" for air-gapped, so a file *store* is
  allowed and a separate *provider type* is not — the earlier wording overstated
  it in the other direction. The deferral now carries the four elements CLAUDE.md
  requires — port, default, owning phase, trigger — stated once in full at
  `SelectSecretProvider`'s TODO; the four neighbouring mentions are
  cross-references to it, not four copies, because the rule is single-source and
  four copies is how the RLS template shipped broken in four files at once. [ADR-0035](../decisions/0035-demand-gated-infrastructure.md)
  moved every Dapr adapter to [Phase 11](phase-11-production-hardening.md)
  against a written trigger, so those comments now point at a packet that will
  not ship them. The seam they describe is correct and unchanged — only the
  phase pointer is stale. Comment-only edit; no behaviour change.
- The Packet 3 `TenantContextBehavior` TODO names a `DbConnectionInterceptor`
  as the mechanism for setting the Row Level Security session variables.
  Interceptors fire when the connection opens, not when the transaction starts,
  and `set_config(..., true)` is transaction-local — so the value would be gone
  before the query it protects. **Three** sites carry the wrong mechanism, not one —
  and the third says "RLS interceptor" rather than the type name, so a sweep that
  greps the type finds two and reports itself finished. All three are corrected to
  name `TransactionBehavior`'s `SET LOCAL` at step 6, per
  [Security Standards § Tenant Context](../standards/11-security.md) — the single
  authority for the placement, which assigns the implementation to **Packet 7**.
  Packet 6 opens the transaction; Packet 7 issues the `SET LOCAL` inside it, together
  with the resolver that gives it a tenant to write. Correcting the TODO is 3b's;
  implementing the mechanism is Packet 7's.

**Development-loop and CI repairs.** [Phase 01](phase-01-repository-tooling.md)
is complete and its record stands; the gaps it shipped with are remediated
here:

- `make seed`'s health gate requires every compose service to report healthy, but
  three declare no healthcheck — so the gate times out and the script exits non-zero
  on **every** run. This is step three of the quickstart. Only two of the three are
  genuinely exempt: `daprio/placement` and `daprio/daprd` are single-binary images
  with no shell and no probe tool. `coturn` is not — it ships
  `turnutils_stunclient` and gains a real STUN probe. The gate skips the exempt set,
  derived per service from the compose file rather than from an empty Health value,
  because a crash-looping service reports an empty Health for the instant after each
  restart attempt and a value-based skip passes it.
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

  Standing the harness up is more than deleting a flag: it needs
  `vitest.config.ts`, a jsdom environment, `@vitejs/plugin-react` (pinned to
  `^4` — `@vitejs/plugin-react@6` requires Vite 8 while vitest 2.1.x resolves
  Vite 5), `@testing-library/react` + `jest-dom`, and a setup file.

  **`jsdom` is capped at `^26` and `jest-dom` at `^6.9` on purpose.** CI pins
  Node 20.11.0; `jsdom@30` requires `^22.22.2 || ^24.15.0 || >=26.0.0` and pulls
  `html-encoding-sniffer@6`, so on CI's Node the suite dies with an
  `ERR_REQUIRE_ESM` naming a transitive package, with nothing in the output
  mentioning Node or jsdom. jsdom 27, 28 and 29 do not help — same transitive.
  Revisit when the Node pin rises, not before.

  The config lives in `apps/web` rather than `@learnstack/config` — but not
  because a shared export could not work: that package already exports
  `./eslint`, `./tsconfig/*` and `./tailwind`, validated the same way, by
  `apps/web` consuming them. There is no second consumer **by design**.
  ADR-0009 keeps one Next.js app in this repository, the operator portal lives
  in `LearnStack-Hub`, and the *Implemented* architecture test
  `Frontend_Has_Only_The_Web_App` fails the build if a second app appears here.

  One limit worth stating rather than discovering later: `pnpm -r test` covers
  `apps/web` alone. `packages/config`, `packages/sdk` and `packages/ui` declare
  no `test` script, so they stay silently green — the required `frontend` check
  proves something about the app and nothing about the packages.
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

**Packet 4 — API conventions ✅**
REST + URL versioning (`/api/v1/...` per
[ADR-0024](../decisions/0024-api-versioning-policy.md)), Problem Details
(RFC 7807) on every error, cursor pagination, idempotency keys for write
endpoints with external side effects, ETag concurrency, correlation IDs in
headers and logs, OpenAPI generated from code, tenant + organization header
binding (`X-Tenant-Id`, `X-Organization-Id`).

Carries one correctness fix, whose shape turned out to differ from the one
scoped here. `CursorPagination`'s `Limit` guard throws during model binding, and
that was expected to surface as an unhandled `ArgumentOutOfRangeException` — a
500 where a client error belongs. Measured when the packet reached it, the
status was already **400**: the `InvalidModelStateResponseFactory` wired one
step earlier catches the binder's exception. What remained was worse than the
status suggested — the `errors` map named `$` and `pagination`, the binder's own
keys, so a client got a 400 with no way to learn that `limit` was the problem.
Binding failures return **400** with Problem Details **naming the parameter the
client sent**.

Also lands the Packet 4 half of
[ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md): the in-process rate
limiter [architecture/30](../architecture/30-api-gateway.md) has promised since Phase 01,
`EffectiveHostAccessor` and the total host normalizer, the trusted-hop predicate, the
`X-Tenant-Id` / `X-Organization-Id` assertion comparison behind `ITenantAssertionRecorder`
(logging implementation only — Packet 9 swaps in the auditing one), and the
`Deployment:Mode` fail-fast that corrects the key shipping as `Development` in the
`appsettings.json` that goes to every environment. Packet 4 resolves no tenant and claims
no audit trail; the ADR's staging table says what each packet owes.

SDK generation ships as a wired-but-empty scaffold — there are no endpoints to
generate from until [Phase 02d](phase-02d-walking-skeleton.md).

**Packet 5 — Foundation ports and default implementations ✅** ([delivery record](#delivery-record-packet-5))
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

`ICacheService.RemoveByPrefixAsync` is **removed**
([ADR-0014 Amendment 2](../decisions/0014-adopt-dapr.md)). The published
implementation iterates an instance-local key set, so keys written by another
instance are never evicted — the contract cannot be honoured by any candidate
backend, and the corpus contains no call site for it.

"Removed **or** redesigned to a generation-key pattern" was not a fork at the
port: that pattern puts its counter in durable domain state — a column bumped
inside the business transaction and embedded in the key template
([architecture/32 § 8.2](../architecture/32-tenant-customization-model.md)) — so
it adds no member to the interface. It stays a caller-side convention, owned by
the consumers that specify it.

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
projection), `platform_entitlement_cache`, `platform_host_to_tenant`, `idempotency_keys`
(the durable `IIdempotencyStore` per
[ADR-0037](../decisions/0037-idempotency-key-contract.md); Packet 4 shipped the
port and an in-memory default that is correct for one instance and wrong for
two), and `outbox_messages`. Default-organization seeding at tenant creation.

**Seed the system actor.** `UserId.SystemActor` — the fixed id
`00000000-0000-7000-8000-000000000001` in
`LearnStack.SharedKernel.Identifiers` — is what an integration-event consumer, a
background job, or any other non-request execution writes state as, per
[Audit Coverage](../standards/18-audit-coverage.md)'s actor-of-type-`system` rule.
`AuditableEntity.MarkCreated` refuses `default(UserId)` and `Guid.Empty` alike, so
without it no consumer can create an aggregate at all. It is a foreign key: this
packet's migration seeds the matching `users` row so `created_by` resolves.

The `Organization` aggregate is declared in `LearnStack.Modules.Tenancy.Domain`, with
its EF configuration and its migration on `TenancyDbContext`, per [ADR-0017 Amendment 2
(2026-08-10)](../decisions/0017-tenant-organization-hierarchy.md). Identity holds
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
two of the ten cannot take the template verbatim: `tenants` has no `tenant_id`
column, and `platform_host_to_tenant` is read in order to determine the tenant, so
a tenant-keyed predicate would make host resolution return zero rows forever. See
[Database Standards § Table classes](../standards/05-database.md). Every table's
`GRANT`s are written in this migration too; there are no `ALTER DEFAULT PRIVILEGES`
grants, so a table nobody granted fails loudly rather than inheriting DML — and can
never silently widen a `BYPASSRLS` role. Row security is enabled and forced on all
ten, so the structural scan needs no exception list.

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
is now the single authority. The Packet 3 `TenantContextBehavior` TODO — which
named the connection-interceptor option — was corrected in Packet 3b; this packet
implements the mechanism it now points at.

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
- `ICacheService` with `InMemoryCacheService`. `RemoveByPrefixAsync` is **removed**
  ([ADR-0014 Amendment 2](../decisions/0014-adopt-dapr.md)) — the published contract
  iterated an instance-local key set and could not be honoured across instances by any
  candidate backend. "Removed **or** redesigned" was never a fork at the port: the
  generation-key pattern puts its counter in durable domain state, so it adds no member
  to the interface and stays a caller-side convention.
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
- `idempotency_keys` — the durable `IIdempotencyStore`
  ([ADR-0037](../decisions/0037-idempotency-key-contract.md)). Packet 4 ships the port
  and an in-memory default that is correct for one instance and wrong for two; this is
  the table that makes it survive a restart and a second instance. Each store call opens
  its own short transaction and sets `app.tenant_id` as its first statement, because a
  claim is taken **before** the MediatR `TransactionBehavior` that would otherwise do it.
- `outbox_messages` — the outbox table. Nothing dispatches from it until
  [Phase 02b](phase-02b-events-auth.md), but its schema and LearnStack's ownership of
  it are a one-way door — and that ownership is exactly what makes the dispatch
  transport swappable later
  ([ADR-0006](../decisions/0006-events-and-outbox.md)).

All ten tables ship with `ENABLE` **and** `FORCE ROW LEVEL SECURITY` and an explicit
`WITH CHECK`. They do **not** all take the same policy, and saying they do produces a
migration that cannot run: the corrected template's predicate names a `tenant_id`
column, which `tenants` does not have, and it would deadlock host resolution, which
reads `platform_host_to_tenant` in order to *determine* the tenant. Three classes, per
[ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md) and
[Database Standards § Table classes](../standards/05-database.md):

| Class | Tables | Policy |
|---|---|---|
| Tenant-owned | `organizations`, `tenant_domains`, `tenant_locales`, `tenant_settings` (the one org-scoped table — it also takes the two `AS RESTRICTIVE` write guards), `tenant_feature_flags`, `platform_entitlement_cache`, `idempotency_keys`, `outbox_messages` | the corrected template verbatim |
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
is never *disabled* on any of the ten; the grant matrix that goes with the policies is
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
- **`IProviderResilience<TPort>` collaborator** with Polly v8
  `ResiliencePipeline` (retry + circuit breaker + timeout + bulkhead) lives
  in `LearnStack.Infrastructure.Resilience`. Configuration shape:
  `appsettings.Resilience:<port-name>:`. Every adapter is wired through the
  `AddProviderResilience<TPort>(IConfiguration, string portName)`
  composition-root extension and takes the pipeline as a constructor
  collaborator — there is no decorator, because C# forbids a type parameter
  as a base type ([ADR-0032 Amendment 2](../decisions/0032-exception-handling-logging-and-observability.md)). The
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
- Studio / Portal tenant selection, which travels as a **re-issued JWT claim**, never as
  a selector header ([ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md)).
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
- Tenant + organization headers (`X-Tenant-Id`, `X-Organization-Id`) bound and compared
  on every request — **assertions, never a resolution source**. The trusted-hop
  `X-LearnStack-Host` names a host and LearnStack still resolves it itself. Full model,
  reconciliation matrix and packet staging in
  [ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md).

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
  extension, `IProviderResilience<TPort>` collaborator, Serilog + OTLP sink,
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
- All ten tenancy tables report `relrowsecurity` **and** `relforcerowsecurity` true in
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

Six further decisions were taken during the phase and are Accepted:

| # | Topic | Status | Decision |
|---|---|---|---|
| [ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md) | RLS policy template + database role model | **Accepted** (2026-08-08) | One `AND`-ed policy, `FORCE ROW LEVEL SECURITY`, explicit `WITH CHECK`, four-role model; canonical template in Standards 05 only |
| [ADR-0033](../decisions/0033-audit-durability-model.md) | Audit durability | **Accepted** (2026-08-08) | Supersedes ADR-0016. MUST-class audit is a durable intent inside the business transaction and fails closed; SHOULD/MAY stays best-effort |
| [ADR-0034](../decisions/0034-hub-contract-surface-invariant.md) | Hub contract surface | **Accepted** (2026-08-08) | Two invariants replace the endpoint count; host resolution never calls the Hub |
| [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) | Demand-gated infrastructure | **Accepted** (2026-08-08) | The one-way-door test; ports ship now, adapters ship on a named trigger |
| [ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md) | Trusted inputs for tenant + organization resolution | **Accepted** (2026-08-18, Amendment 1 2026-08-20) | Resolution by **agreement, not priority**; no request header names a tenant, one header names a **host** over an authenticated hop; Amendment 1 corrects the normalization order |
| [ADR-0037](../decisions/0037-idempotency-key-contract.md) | What an idempotency key identifies, owns and replays | **Accepted** (2026-08-20) | A key is a **nonce inside a tenant's key space**, not an identity; a fingerprint decides whether a replay answers the question asked; a fencing token owns the claim; capacity is admission, not eviction |

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

## Delivery Record (Packet 3b)

Kept separate from the Packets 0–3 record above, which is frozen and scoped to those
packets. This one records what Packet 3b actually shipped, including where its own
plan turned out wrong — a repair packet that hides its misses teaches nothing.

> **Packet 3b — Decision repair ✅**
>
> **Shared Kernel.** `Results.Unit` → **`None`**, chosen by compiling the candidate
> against MediatR, FluentValidation, EF Core and Vogen rather than by argument.
> `[MemberNotNullWhen]` on `Result<T>` **and** `IResultBase` — the annotations do not
> flow from an interface to its implementations. `Entity<TId>` gained
> `IEquatable<Entity<TId>>`, `==` / `!=`, and one typed body every entry point
> delegates to; `Equals(object?)` and `GetHashCode()` are `sealed override`.
>
> Three things the packet's own plan had wrong, all found by measuring:
>
> - **The transient guard was dead code.** `Id.Equals(default(TId))` cannot detect an
>   unset Vogen id — Vogen returns `false` when either side is uninitialized — so
>   `GetHashCode()` threw `ValueObjectValidationException` for any unsaved aggregate.
>   A `HashSet` of two new aggregates was an exception, not a set.
>   `IStronglyTypedId` gained `IsInitialized()`; ADR-0023 Amendment 3 records it.
> - **The boxing was never where the plan said.** `Entity<TId>` is a class, so
>   `Equals(object?)` never boxed anything. Measured: 120 B/call as Packet 3 shipped
>   it, 40 B once the dead guard went, 0 B once the constraint gained
>   `IEquatable<TId>`. Two causes, not one.
> - **`AuditableEntity`'s audit-actor guard had the identical defect**, one file from
>   the one being fixed.
>
> **Corpus.** ADR-0035's explicitly rejected Vault trigger was quoted in four places;
> ADR-0032's `Decorate<TPort, …>` example did not compile, and neither did its copy in
> `wire-cross-cutting-foundation/SKILL.md` — the executable one. Three skills taught
> `LocalizedMessage` keys without the `lockey_` prefix, which the constructor rejects
> by throwing: code that compiles and fails on first use; a fourth,
> `standards-check`, taught the wrong `Result.Fail` signature instead.
> `add-mediatr-handler` taught an `ICommand`/`ICommandHandler` layer that exists
> nowhere. Ten secret-provider
> comments were repointed to Phase 11 with the four elements CLAUDE.md requires, and
> **fourteen of nineteen** `<see href>` targets in C# XML docs were broken — the
> majority spelling used three `../` and resolved to a `backend/docs/` that has never
> existed, because CI's link audit reads Markdown only.
>
> **Dev loop.** `make seed` exits 0. It had failed on **every** run since Phase 01:
> the gate flagged any service with no healthcheck, and two images cannot carry one
> (`daprio/placement` and `daprio/daprd` have no shell). `coturn` was not exempt —
> it ships `turnutils_stunclient`. The e2e overlay had **never booted**: `cp-kafka`
> runs as uid 1000 and Docker gives a tmpfs target the parent's mode but not its
> ownership. Its `volumes: !reset []` was also discarding `postgres-init` and the S3
> identity file — under that overlay an isolation suite would have run as the owning
> superuser, where `FORCE ROW LEVEL SECURITY` is inert. Every published port binds
> `127.0.0.1`; Kafka's 9092 and placement's 50005 were removed rather than rebound.
> The 14 `container_name:` literals are gone so `-p` means something, but that alone
> does **not** make two projects concurrently runnable: the host ports are fixed
> literals, so a second project now fails on a port bind instead of a name clash.
> `make dev` and `make e2e-up` are still mutually exclusive.
>
> **Frontend.** The Vitest harness replaces `--passWithNoTests`, verified by deleting
> the test rather than by running it. `jsdom` is capped at `^26` and `jest-dom` at
> `^6.9` because CI pins Node 20.11.0 — see the Packet 3b scope entry for the
> mechanism and the trigger to revisit.
>
> **Governance.** ADR-0017 Amendment 2 settles the `Organization` aggregate in
> `LearnStack.Modules.Tenancy.Domain`; ADR-0023 Amendments 2 and 3 cover the three
> cross-cutting identifiers and `IsInitialized()`. No new ADR number was taken —
> 0036 remains unclaimed. Two branch-protection settings are deferred by maintainer
> decision (2026-08-10) with a named trigger, recorded in
> [CONTRIBUTING § Branch protection](../../.github/CONTRIBUTING.md).
>
> **Method.** Every step ran two adversarial review rounds. The recurring finding was
> one shape: *a claim verified against something other than what ships* — an awk
> tested on synthetic `docker compose ps` rows, a fence compiled inside a wrapper
> written for the test, a diff whose normaliser filtered out exactly the lines that
> differed, a harness verified on Node 22 when CI pins 20.11.0. Two of those classes
> are now mechanically checkable and were run against this branch: every `<see href>`
> target in the backend resolves, and every prose line the branch adds respects the
> 88-column rule in
> [Documentation Standards](../standards/13-documentation.md). Both checks live in
> the author's local scratchpad, which is gitignored — a reader of this repository
> cannot run them, so treat the claim as a measurement taken, not as shipped tooling.
> Promoting them to CI is [Phase 02b](phase-02b-events-auth.md)'s to do, on the
> trigger that a second contributor gains write access and the checks stop being
> one person's habit.

## Delivery Record (Packet 4)

Kept separate from the two records above for the same reason they are separate from
each other: each is scoped to its own packets and is not rewritten. This one records
what Packet 4 shipped, and what its own plan had wrong — six of the entries below are
defects the packet introduced and then found in its own review rounds, which is the
only reason they are in a record rather than in production.

> **Packet 4 — API conventions ✅**
>
> **Versioned routing.** `VersionedRouteConvention` prefixes every controller with
> `api/v{N}`, and four **startup guards** refuse the escapes a route rule cannot see
> at runtime: a missing `[ApiController]`, a null template, an absolute template on
> either the controller or the action, a major outside `LiveMajors`, and a
> hand-written prefix disagreeing with the attribute. One OpenAPI document per live
> major, at `/openapi/v{N}.json`, with Scalar over it.
>
> The rule that enforces this is **not** in the architecture assembly, and that is
> the packet's first lesson. It was first written there as a reflection scan over
> `Assembly.GetReferencedAssemblies()` — which returns the emitted AssemblyRef table,
> not the project's references, so the compiler had elided every module. The scan
> reached four assemblies and no module while a module controller served
> `/legacy/courses` with the suite green. It now runs against a production host's real
> `EndpointDataSource`, and the CI filter that had been excluding that assembly was
> removed: mutation-testing the convention turned nine of fourteen integration tests
> red and left all twenty-nine architecture tests green.
>
> **One error shape.** Every 4xx and 5xx carries RFC 7807 with `code`, `messageKey`
> and `correlationId` — including the three that used to escape it. 404 and 405 come
> from routing before MVC, so `UseStatusCodePages` catches them; 415 comes from MVC,
> which had already converted it to ASP.NET's own `ProblemDetails` — the right idea
> in the wrong shape — so `IClientErrorFactory` replaces that conversion rather than
> layering over it. `ProblemDetailsNormalizationFilter` rewrites any 4xx/5xx
> `ObjectResult` that reaches the wire without a `code`.
>
> **Cursor pagination and the sort grammar.** Binding failures return 400 naming the
> parameter the client sent, not the binder's `$` and `pagination`. `SortSpecification`
> decides the edges once — at most four terms, canonicalised to the allow-list
> spelling, and an unparsed spec **throws** rather than silently falling back, because
> a fallback would answer a sorted query with an unsorted page.
>
> **The ADR-0036 edge.** `EffectiveHost.Normalize` as a total function;
> `EffectiveHostAccessor` with the trusted-hop predicate; `X-Tenant-Id` /
> `X-Organization-Id` compared and never resolved from; the in-process anonymous rate
> limiter architecture/30 had promised since Phase 01; and `Deployment:Mode` made
> required, which corrected a key that shipped as `Development` in the
> `appsettings.json` that goes to every environment.
>
> **Idempotency and ETag.** `IIdempotencyStore` with a fencing token and a request
> fingerprint, an in-memory default that is correct for one instance and says so, and
> `EntityTag` with strong comparison and an `If-Match` reader a command can carry.
>
> **Limits, the SDK, and the identifier contract.** The request body is bounded at
> 1 MiB by middleware — `TestServer` implements neither request-body-size feature, so
> a Kestrel-only limit is one no test can assert — with Kestrel set to the same number
> behind it. The SDK generation pipeline runs for the first time. Strongly-typed
> identifiers publish as the primitive they actually send, a choice
> [ADR-0023](../decisions/0023-strongly-typed-id-source-generator.md) assigned to this
> packet and that nothing had made.
>
> **Two decision records.**
> [ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md) — resolution by
> agreement, not priority — and
> [ADR-0037](../decisions/0037-idempotency-key-contract.md) — what an idempotency key
> identifies, owns and replays. ADR-0036 gained Amendment 1 when the implementation
> measured its normalization order and found a hole in it.
>
> ### What the packet got wrong, and how it found out
>
> Every item here was introduced by this packet and caught by its own review rounds.
> None reached `main`.
>
> - **A sweep deleted a claim someone had just won.** The idempotency store's expiry
>   pass observed an entry and then removed it *by key*, so a live claim installed in
>   between was destroyed and the next caller was told to run the operation — two
>   callers, one key, both answered 2xx. On the surface Standards 04 reserves for
>   payments that is a double charge with nothing anywhere reporting it. Reproduced on
>   a frozen clock, fixed with a value-comparing removal, and the stress test that
>   proves it kills the old line 5/5 and passes 10/10 clean.
> - **Capacity cancelled the guarantee it protects.** The entry ceiling evicted
>   completed records to make room — a record that has not expired is a promise for
>   the rest of its window, so evicting one let the operation run again, and a tenant
>   could trigger it on itself. Capacity is admission now; expiry is the only reason
>   an entry leaves.
> - **A response over the replay cap released its key**, so the retry re-ran the
>   operation and both attempts answered 2xx. It records a tombstone now. The test
>   that covered the old behaviour was asserting the bug.
> - **A partial body was delivered when the action's result threw.** MVC returns
>   normally from `next()` and rethrows after the filter unwinds, so the buffer can
>   hold a half-written body; copying it out handed the client a truncated 2xx *and*
>   took the exception away from `UseExceptionHandler`, whose 500 cannot be written
>   once the response has started.
> - **The correlation header echoed the client's value**, which meant an anonymous
>   caller could put bytes Kestrel accepts in a request header and refuses in a
>   response header into every reply — 500 and an error-tracker capture per request.
>   Four captures for four requests, measured; zero after.
> - **A "normalised" host could contain `/`, `@` and `%`.** `IdnMapping.GetAscii`
>   performs a compatibility mapping, so the fullwidth forms arrive as the real
>   characters *after* the input scan has run. The function ends with a whitelist over
>   its own output now, and [ADR-0036 Amendment 1](../decisions/0036-tenant-resolution-trusted-inputs.md)
>   records both that and the port-before-IPv4 ordering it also got wrong.
>
> Two process notes, because they cost real time. The solution was built without
> `CI=true` for most of the packet — `TreatWarningsAsErrors` is conditioned on it — so
> a commit shipped that failed the required check; `CI=true` is now the only way this
> repository is built. And a review agent left an artefact in the tracked tree,
> including a deliberately-throwing diagnostic test; the file was reverted and the
> legitimate finding redone by hand. Later review prompts forbid touching tracked
> files by name.
>
> ### What Packet 4 did not deliver
>
> Nothing from its scope paragraph, and — after the Step 6 review round found
> them still marked `Registered` — none of the architecture tests ADR-0036
> assigns to it either: the four tenancy-edge rules are in
> `TenancyConventionTests`, as source scans, because the resolver that could
> misuse those symbols does not land until Packet 7 and a scan holds the line
> from the day a symbol exists rather than the day it acquires a caller.
>
> The deprecation headers ADR-0024 describes are
> **not** in it and were never meant to be: they attach to a deprecated endpoint, and
> `Every_Deprecated_Endpoint_Has_Sunset_And_Successor` is Registered against the
> packet that adds the first `/api/v2`. The authenticated and write-endpoint rate
> limits need a token to key on and wait for [Phase 02b](phase-02b-events-auth.md);
> the multipart and file-upload rows need an endpoint and wait for
> [Phase 04](phase-04-cms-media-pages.md). Each is written down where the limit is
> published, with the phase that owns it.

## Delivery Record (Packet 5)

Kept separate from the records above for the reason they are separate from each
other: each is scoped to its own packets and is not rewritten. This one records
what Packet 5 shipped, and — like Packet 4's — what its own plan had wrong. Most
of the entries below are defects the packet introduced and then found in its own
review rounds, which is the only reason they are in a record rather than in
production. Several of them are defects introduced by the *fix* for an earlier
one.

> **Packet 5 — Foundation ports and default implementations ✅**
>
> **The ports.** `ICacheService` with `InMemoryCacheService`, `IEventBus` with
> `InProcessEventBus`, and — from Packet 3 — `ISecretProvider` with
> `ConfigurationSecretProvider`, as the only registered implementations. Each is
> selected at a single composition-root site so Phase 11's adapter is one line
> rather than a search. `IHostToTenantResolver` and `IEntitlementProvider` are
> **not** here: they need tenancy schema, and belong to Packets 7 and 9. Two
> sections of this document disagreed about that, because one is phase scope and
> one is packet scope.
>
> **The cache key is the isolation boundary, and that is not a figure of speech.**
> There is no query filter and no RLS policy in front of a dictionary, so
> `CacheKey` composes and `EnsureValid` guards: the tenant segment first and
> mandatory, `platform` for a platform-wide value, `ForOrganization` for a scope
> ADR-0017 makes real, and every segment that parses as an identifier required to
> be the canonical rendering of a non-empty one.
>
> The guard shipped **validating arity rather than tenancy** —
> `hub:entitlement:{id}` has three non-empty segments and puts the module first,
> so it passed a check whose own error message says the tenant segment is
> mandatory. A guard that admits the shape it exists to reject is worse than
> none, because it makes the rule look enforced. Standards 20's cheat sheet
> listed five key families and every one of them led with the module,
> contradicting the rule stated a few lines above it; two of the five could not
> be built by any factory at all, so the two the standard singles out — including
> the host lookup, on the anonymous page-load path — would have been hand-built
> past the only place `Guid.Empty`, non-canonical rendering and separator
> injection are checked.
>
> **The bound was not a bound, and then it crashed the writers it protects.**
> Trimming lived inside the sweep, the sweep is throttled by clock time, and a
> burst does not advance the clock: measured, 60,000 entries against a ceiling of
> 10,000. Moving it to every write that adds a key fixed the count and introduced
> something worse — `OrderBy` over a live `ConcurrentDictionary` buffers it
> through `CopyTo` after reading `Count`, and those two steps are not atomic. Two
> concurrent writers failed 4.1% of ordinary writes; four failed 15.5%. A
> component whose contract is that it may no-op at any time was instead failing
> the caller's request, and in `GetOrSetAsync` the throw lands after the factory
> has already run. An atomic snapshot plus a low-water mark fixed both, and took
> the steady-state cost from 0.26 ms and 281 KB per write to 0.0072 ms and 1.2 KB.
>
> **The single-flight cleanup was bound to the wrong event twice.** Unregistering
> when a *caller* exits meant a joiner that cancelled removed the shared
> registration while the factory still ran, so the next arrival started a second
> concurrent run — the stampede the method exists to prevent, reintroduced by its
> own cleanup. Unregistering on the *factory's* completion instead meant the
> flight was gone by the time the caller stored, so nothing could mark it
> superseded. It retires when its last caller is done. A per-key version counter
> written along the way lived in a dictionary nothing swept: 50,000 entries
> against the cache's own ceiling of 10,000, an unbounded structure behind a
> bounded one.
>
> **The event bus carries four obligations, and each has a test that fails when
> the code implementing it is removed.** The same `IIntegrationEventHandler<T>`
> contract, the same `IInboxGuard` seam, the same tenant-context restoration, the
> same per-partition ordering. `PublishAsync` is not generic and handlers resolve
> by runtime type, because the outbox publishes through the base interface and a
> generic parameter would resolve `IIntegrationEventHandler<IIntegrationEvent>`,
> which nothing implements — the publish would reach zero handlers and report
> success.
>
> **The reentrancy fix broke the guarantee it protected.** A handler publishing
> about its own aggregate deadlocked and wedged the partition permanently. Running
> the reentrant call inline was worse: an `AsyncLocal` flows into every task
> started inside a unit, so a fire-and-forget spawn inherited the marker and ran
> *concurrently* with the unit it should have queued behind. The detection is the
> same either way; only the action differs, and that asymmetry is the point — a
> false positive that throws is diagnosable, one that runs inline is a silent
> concurrency violation. Comparing against the innermost key alone then still
> missed `A → B → A`, the same cycle one hop longer, five times out of five.
>
> **The envelope, decided before the first call site.** The outbox row requires
> `topic` and `correlation_id` as `NOT NULL` and carries organization, causation
> and actor; none of them belong on the event, and the two-parameter signature had
> nowhere to put them, so correlation was read from whatever context was ambient
> at dispatch — `null` inside the background service the processor is. The
> partition key had two sources and the transport read the one the event did not
> declare, while every test published an event whose declared key disagreed with
> the one passed. And no consumer could write state at all:
> `AuditableEntity.MarkCreated` refuses `default(UserId)`, and the consumer
> context supplied neither an actor nor an organization — under the canonical RLS
> policy an absent organization *hides* every organization-scoped row rather than
> widening to all of them, which is the opposite of what the code claimed. See
> [ADR-0014 Amendment 3](../decisions/0014-adopt-dapr.md).
>
> **A trap the non-generic port creates, closed with it.** With `IIntegrationEvent`
> as the declared type at every dispatch boundary,
> `JsonSerializer.Serialize(@event)` emits four members and silently drops
> everything the concrete event added — valid JSON, no exception, committed inside
> the transaction that reported success, and failing to deserialize on every retry
> until it dead-letters. `ToPayloadJson()` serialises by runtime type.
>
> **Seven services left the daily loop.** Kafka, Valkey, Vault, APISIX and the two
> Dapr containers sit behind a compose profile per ADR-0035; `make dev` starts 7
> instead of 14. Two failure modes decided the shape and both were measured: a
> profile-less `down` silently leaves profiled containers running, and
> `--remove-orphans` does not help; and a default service depending on a gated one
> is not a warning but a whole-project error, so `config`, `up`, `down` and `ps`
> all refuse. Nothing was checking the second — CI did not validate the compose
> files at all. It does now, across both profile projections and both overlays.
>
> **`DeploymentMode` branching is booted, not described.** Existing coverage
> stopped at reading the mode. The first version of the new test passed while
> proving nothing: it set `Deployment:Mode` through `ConfigureAppConfiguration`,
> which under minimal hosting runs *after* the composition root has read
> `builder.Configuration`, so `appsettings.Development.json` won and the SaaS case
> silently exercised the Development branch.
>
> **Three tests were found agreeing with the code instead of constraining it,**
> and that is the packet's most repeated lesson. A bound test that advanced the
> clock one second per write — the one schedule under which the broken bound held.
> A stampede test built with `Select(...).ToArray()`, which LINQ evaluates
> sequentially, so eight "concurrent" callers never raced and
> `LazyThreadSafetyMode.None` survived it. A cross-key rendezvous sharing one
> semaphore, where each side consumed its own release and waited for nothing, so
> collapsing every partition onto a single chain passed. A fourth kind appeared in
> the mutation harness itself: a mutant that failed to compile looked like a
> passing suite, because the check grepped only for test failures.
>
> **What is enforced, and where.** `Integration_Event_TopicNames_FollowConvention`
> is implemented — which required making `Topic` a property of the event type
> rather than a producer-supplied string, since the rule reads the declarations.
> `Modules_Do_Not_Inject_IEventBus_Directly` closes the door on a fifth
> cross-module mechanism. `Assertion_Budget_Does_Not_Depend_On_ICacheService`
> became the dependency check the catalogue promised once the type existed. Both
> new rules sweep `.Application.Contracts` as well, because that is where
> integration events are declared and the existing sweep omitted it — which would
> have made them vacuous permanently rather than until the first module ships one.
>
> **Outside the packet's own scope, found by working in it.** The pre-commit hook
> never applied `.leakwatchignore`: leakwatch resolves it relative to the scan
> target, and the hook scans file by file, so seven paths were unscannable locally
> while CI was green — and the hook's own remediation text told the developer to
> extend a file that could not have helped. The first fix layered the ignore file
> onto the repository's own stack, which broke it in both directions: a
> `.gitignore` negation outranks `core.excludesFile`, so two of the fourteen paths
> were still blocked, and patterns from `.gitignore` and a developer's
> `.git/info/exclude` were honoured as leakwatch's. It is evaluated in isolation
> now.
