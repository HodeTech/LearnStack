# Tenancy — Audit Coverage Matrix

Per [Audit Coverage](../../standards/18-audit-coverage.md), which names this
file. Part of the [module spec](README.md).

The additional ADR-0078/0079 administration, native source and warm-adoption
contracts are Accepted; their new request/catalogue consumers and proofs remain
pending P03-3. Existing classifications below retain their implemented writers.
[The new bindings](#bounded-administration-bindings) are planned until the actual
request and catalogue registration land together.

**P02b-1 Step 3 producer implemented — 2026-10-12.** Completed provisioning
adds one pending outbox row before the existing two MUST rows and commit. Its
domain translator adds no independent intent or audit operation and writes no
aggregate. A failed MUST write rolls back tenant, organization and outbox together.
The [integration-event catalogue](README.md#integration-event-catalogue) owns the flow;
P02b-2 implements hosted producer dispatch and production-census enforcement.
All four steps completed both independent review rounds and local closeout.
The [merge closeout](../../roadmap/phase-02b-delivery-log.md#p02b-2-merge-and-closeout-2026-10-13)
owns CI/merge status. P02b-3 Steps 1–5 implement durable subscriber delivery and
both real defaults; its
[Step 6 record](../../roadmap/phase-02b-delivery-log.md#p02b-3-step-6--production-enforcement-and-pr-closeout-2026-10-13)
owns current enforcement and review status.

**ADR-0046 producer terminal writer implemented — 2026-10-12; both review rounds
complete.**
The infrastructure operations below belong to this catalogue/matrix, without a
synthetic aggregate. P02b-2 Step 2 registers the delivered producer terminal
operation; P02b-3 Step 4 adds the subscriber writer and registration, with
implementation validation and independent reviews recorded in its delivery record.
Both use the restricted fifth audit seam on the fenced state-transition transaction.
Routine confined dispatch has the explicit ADR-0046 exemption; platform-scope
entry auditing is unchanged.

**P02d-4 Step 2 implemented — 2026-10-03.** Both review rounds
and focused fix verification pass. Site bootstrap
is a marked GET/HEAD request, audit Off and without a permission key or grant. It
uses matching host scope and READ ONLY; normal reads add no audit row. Public
configuration/settings ports remain internal and unrouted. Independent rejected-
assertion auditing remains unchanged. [The public contract](README.md#p02d-4-public-configuration-contract)
owns allowlisting and lifecycle/locale checks.

**P02d-3 Step 1 implemented — 2026-10-02; both review rounds passed.**
`ITenantSettingsAccessor` is an internal application interface, not a
MediatR request or audited write. It creates no intent or business-state mutation.
Any production request introduced for it must be audit Off; test-only requests
stay test-only. [The module contract](README.md#p02d-3-accepted-typed-settings-contract) owns the read.

**P02d-2 Step 2 implemented; both review rounds passed — 2026-10-02.**
The [accepted writer
contract](README.md#p02d-2-accepted-locale-and-branding-contract) implements the locale
and setting rows below. Locale commands declare `tenancy.locale.write` over the owning
Tenant root and captured locale navigation; branding declares `tenancy.setting.write`
over TenantSetting. Generic setting values require whole-value `[PiiSensitive]`
redaction, verified at capture and durable audit. Public branding projection is a
separate allowlist. Verification request types are Off.

Packet 7 ships four audited root writes through three commands: Tenant and default
Organization provisioning, a separate Organization create, and host mapping creation.
P02d-2 adds two locale commands (SHOULD over Tenant) and one branding command (MUST over
TenantSetting). P02b-2 Step 2 adds the producer terminal MUST security-event writer;
P02b-3 Step 4 adds its independent subscriber terminal counterpart.
Rows without a writer remain `(planned)`; operations without a MediatR request
carry `(off-path)`. The claim-context assertion row below is registered off-path
through `TenancyAuditCatalogSource`.

All four original writes are **MUST**. Their records commit with the business state,
under [ADR-0033](../../decisions/0033-audit-durability-model.md). The branding writer
shares that guarantee; a failed MUST audit rolls its setting write back too.


**One command, two rows.** An intent is parked per audited `(resource, operation)`
and not per request ([ADR-0044 § 3](../../decisions/0044-audit-write-path.md)), so
`ProvisionTenantCommand` declares `tenancy.tenant.create` **and**
`tenancy.organization.create`, and the owning unit-of-work frame flushes both on the
one transaction. Under the singular reading ADR-0044 retires, the `Organization`
create row this matrix requires would never be written. Both rows carry the tenant
the transaction announced — for provisioning that is
`IProvisionsTenant.ProvisioningTenantId`, which is the only value the row's own
`WITH CHECK` accepts. The value travels on the intent — `AuditIntent` carries
`TenantId` and `OrganizationId?`, resolved by `AuditLogBehavior` at step 3, because the
store composes the row from the intent and never resolves a tenant of its own
([ADR-0044 Amendment 3](../../decisions/0044-audit-write-path.md)).

**All four write MUST rows**, and the host mapping is the one that matters most: it
is described in the matrix below as "the row that decides whose data an anonymous
request sees", and its row is what records who pointed a hostname where.
`TransactionBehavior` writes them through `IAuditStore.WritePendingAsync` immediately
before the commit, since [Packet 9](../../roadmap/phase-02a-kernel-tenancy.md).

**The snapshots come with them.** `PlatformHostMapping`, `TenantLocale`,
`TenantFeatureFlag`, `PlatformEntitlement` and `PlatformKillswitch` are plain classes
rather than `AuditableEntity<>` descendants — Tenancy's share of the plain classes
[Audit Coverage § Required Behaviours](../../standards/18-audit-coverage.md#required-behaviours)
lists — and `AuditChangeTrackerInterceptor` captures every
`ChangeTracker` entry in state `Added`, `Modified` or `Deleted` minus a named
exclusion list ([ADR-0044 § 7](../../decisions/0044-audit-write-path.md)) — so the
row this file calls the most important one gets a real `before` and `after` rather
than two empty objects.

This matrix is not the floor — [Audit Coverage § Baseline Coverage](../../standards/18-audit-coverage.md)
is, and a module matrix "cannot remove anything in this list". This file adds rows
beneath that baseline and classifies what the baseline leaves open; a tenant
`AuditConfig` may then narrow SHOULD/MAY at runtime. Neither touches a baseline
MUST.

The `Operation` column carries the catalogue key — `{module}.{resource}.{verb}`, the
shape of a permission key with a verb this matrix names rather than the closed action
set ([ADR-0044 § 6](../../decisions/0044-audit-write-path.md)). Its first two
segments match the permission key for the same resource, so the resource segment drops
the `Tenant` prefix the module segment already carries — `TenantFeatureFlag` is
`tenancy.feature_flag.*` in [Permission Standards](../../standards/19-permissions.md),
and `platform_host_to_tenant` keeps the `hostmapping` segment
[permissions.md](permissions.md) already gives it. `tenancy.tenant_assertion.*` is the
one pair that keeps the prefix: nothing in [permissions.md](permissions.md) gates a
detector the middleware runs on its own, so there is no key for the slug to agree with,
and both are fixed by
[ADR-0036 Amendment 7](../../decisions/0036-tenant-resolution-trusted-inputs.md).

**One slug per cell, one row per audited `(resource, operation)`.** The column is read
as a column of slugs and not parsed as prose
([Audit Coverage § Classification Matrix Template](../../standards/18-audit-coverage.md)),
so two acts that differ get two rows even where one sentence would explain both.
`hostmapping` is the one segment here that does not spell a two-word resource in
snake_case, and it is inherited rather than chosen: `tenancy.hostmapping.admin` is
already the permission key, and the first two segments of the two keys have to agree —
so the spelling changes in both files or in neither.

**A cell carries its slug and, where the row is not joined by request type, every marker
that applies** — a row can carry both
([ADR-0044 Amendment 3](../../decisions/0044-audit-write-path.md)). `(planned)` is a row
classified ahead of the command that will raise it — the classification
[Audit Coverage](../../standards/18-audit-coverage.md) asks for *before* the command
ships, which is what makes MUST/SHOULD/MAY a functional distinction rather than a
documentation one. `(off-path)` is an operation no MediatR request raises, so its
catalogue entry is registered by slug and there is no request type to key it on. An
unmarked row is one a shipped command already raises.

| Resource | Operation | Class | Why |
|---|---|---|---|
| `outbox_messages` | `platform.outbox.dead_letter` `(off-path)` | **MUST** (`security-event`) | P02b-2 Step 2's consequential BYPASSRLS terminal transition; state and row commit together through the row-bound fifth audit seam, with stored tenant/org/event identity and SystemActor ([ADR-0046](../../decisions/0046-durable-event-delivery-and-recovery.md)) |
| delivery ledger | `platform.delivery.dead_letter` `(off-path)` | **MUST** (`security-event`) | P02b-3 Step 4's subscriber terminal writer binds stored tenant/org/event/subscription, acquisition token, reason and attempts to the actual fenced transition; ledger and MUST audit commit together through the same restricted fifth seam. It receives no causal-envelope or payload SELECT grant |
| `background_jobs` | `platform.job.dead_letter` `(off-path)` | **MUST** (`security-event`) | P02b-4 Step 5 binds stored tenant/org/job/definition/attempt and the actual old acquisition token to the fenced terminal transition. The sixth audit seam writes on that same transaction; audit failure rolls back terminal state. Validation and both review dispositions belong to its delivery record ([ADR-0047](../../decisions/0047-transactional-background-job-runtime.md)) |
| `Tenant` | `tenancy.tenant.create` | **MUST** | The root of a customer's data; its existence is a contractual fact |
| `Tenant` | `tenancy.tenant.change_status` `(planned)` | **MUST** | Suspension withdraws access; an operator must be able to say who and when |
| `Tenant` | `tenancy.tenant.rename` `(planned)` | SHOULD | Presentational |
| `Tenant` | `tenancy.tenant.adopt_seed_origin` `(planned)` | **MUST** (`security-event`) | Closed `AdoptSeedAdministrationOriginCommand` advances only its fixed Tenant seed-origin marker; its separate hostmapping MUST intent shares the same transaction. |
| `Organization` | `tenancy.organization.create` | **MUST** | Adds an isolation surface; every org-scoped row written afterwards is written under it |
| `Organization` | `tenancy.organization.archive` `(planned)` | **MUST** | Withdraws that surface while every row it isolated stays behind, and the rows themselves say nothing about who archived the organization or when |
| `Organization` | `tenancy.organization.rename` `(planned)` | SHOULD | Presentational |
| `Tenant` default reference | `tenancy.organization.select_default` `(planned)` | **MUST** (`update`) | `SelectDefaultOrganizationCommand` changes only Tenant's default/version; the selected Active/nondeleted Organization must belong to that Tenant. Resource segment matches the organization-admin sub-operation. |
| Organization summaries | `tenancy.organization.list` `(planned)` | **MUST** (`read-sensitive`) | Fresh Tenant organization-admin admission before scope-owned configuration/default-version disclosure. |
| Organization detail | `tenancy.organization.read` `(planned)` | **MUST** (`read-sensitive`) | Fresh target read admission before Organization configuration/version disclosure. |
| Original Organization/Tenant default outcome | `tenancy.organization.read_receipt` `(planned)` | **MUST** (`read-sensitive`) | Current original-target receipt disclosure authority and fresh committed evidence before replay bytes; no mutation rerun. |
| `TenantDomain` | `tenancy.domain.claim` `(planned)` | **MUST** | `ux_tenant_domains_host` is unique on the host alone, so a claim takes a host no other tenant can claim while it stands |
| `TenantDomain` | `tenancy.domain.verify` `(planned)` | **MUST** | Verification is the gate the custom-domain lifecycle opens before a mapping row exists; a wrongly verified domain serves one tenant's content at another's address |
| `TenantDomain` | `tenancy.domain.fail` `(planned)` | **MUST** | The negative half of the same lifecycle — the attempt count and the recorded reason are what a support thread reconstructs, and repeated failures against one host are worth seeing |
| `TenantDomain` | `tenancy.domain.soft_delete` `(planned)` | **MUST** | `ux_tenant_domains_host` is partial on `deleted_at IS NULL`, so retiring the row releases that globally unique host for another tenant to claim |
| `TenantSetting` | `tenancy.setting.write` | **MUST** | [Audit Coverage](../../standards/18-audit-coverage.md) puts "tenant setting changed" on the Tenancy baseline row; a tenant `AuditConfig` cannot narrow it |
| `TenantSetting` | `tenancy.setting.soft_delete` `(planned)` | **MUST** | Removing a setting is a change to it, so the same baseline row covers it — and `ux_tenant_settings_tenant_id_organization_id_key` is partial on `deleted_at IS NULL`, so the key becomes free for a successor |
| `Tenant` | `tenancy.locale.write` | SHOULD | Configuration; contained TenantLocale changes are captured with their owning Tenant root |
| `TenantFeatureFlag` | `tenancy.feature_flag.write` `(planned)` | **MUST** | "Feature flag toggled" is on the same baseline row, and [Feature Flags § Audit](../../architecture/21-feature-flags.md) classes both flag surfaces as security events |
| `platform_killswitches` | `tenancy.killswitch.toggle` `(off-path)` `(planned)` | **MUST** | A platform-wide flip that disables a capability for every tenant at once. It is not a `tenant_feature_flags` row — that table's foreign key to `tenants` is one the platform sentinel cannot satisfy, so killswitches ship as their own platform-scoped table ([ADR-0045 § 5](../../decisions/0045-entitlement-and-feature-flag-socket.md)). Every write goes through `EnterPlatformAdminScope(reason)`, which is what gives the row a real actor ([Feature Flags § Killswitch Pattern](../../architecture/21-feature-flags.md)); the scope is entered by a service method and not by a MediatR request, so there is no request type to key a catalogue entry on. [Packet 9](../../roadmap/phase-02a-kernel-tenancy.md) ships the table, its policies and the read path and **no writer**: nothing can enter that scope while `DenyAllPlatformAdminGate` is the registered gate, so [Phase 03](../../roadmap/phase-03-identity-admin.md) owns the toggle, its permission and its runbook ([ADR-0045 Amendment 1](../../decisions/0045-entitlement-and-feature-flag-socket.md)) |
| `platform_entitlement_cache` | `tenancy.entitlement.refresh` `(planned)` `(off-path)` | **MUST** | Changes what the tenant may do; written only by `IEntitlementProvider.RefreshAsync`, a port method driven by the Hub over `/api/internal/*` ([ADR-0045 § 1](../../decisions/0045-entitlement-and-feature-flag-socket.md)) and never by a MediatR request, so there is no request type to key a catalogue entry on. **Both markers**, for the reason `tenancy.killswitch.toggle` carries both: Packet 9 ships `NullEntitlementProvider`, which answers from constants and touches no table, and the `HubEntitlementProvider` that performs a real refresh lands in [Phase 02c](../../roadmap/phase-02c-hub-foundation.md) ([ADR-0045 § 6](../../decisions/0045-entitlement-and-feature-flag-socket.md)) — so there is no writer here yet either |
| `platform_host_to_tenant` | `tenancy.hostmapping.write` | **MUST** | The resolution index — the row that decides whose data an anonymous request sees |
| `platform_host_to_tenant` | `tenancy.hostmapping.delete` `(planned)` | **MUST** | The same row removed. The table carries no `deleted_at`, so the delete is a row gone and a host that resolves to nobody |
| `TenantAssertion` | `tenancy.tenant_assertion.reject` `(off-path)` | **MUST** for admitted events (`security-event`) | A validated header/claim mismatch with an independently defended host tenant is recorded under that host tenant, never the asserted tenant or sentinel; row 14 is counted only. [ADR-0065](../../decisions/0065-bounded-tenant-assertion-diagnostics.md) admits 600 authenticated standalone write attempts per host tenant per API instance per first-request-anchored monotonic minute, shared across scoped recorders, actors, sources and dimensions. Failed writes consume permits; later occurrences make no audit-store call and retain their refusal, count and exactly one attributed Warning (4006 replaces normal 4001). An attacker can exhaust the allowance, so later actors lose durable evidence; aggregate log volume remains unbounded. Admitted events retain MUST failure/health behavior, tenant-wide organization scope, exact actor/correlation and trusted organization in metadata. No tenant `AuditConfig` override or independent tenant-registry cap exists. General mutation MUST durability is unchanged. Packet 9 owns the recorder/decorator path; P02b-5 Steps 3–4 implement JWT attribution and HTTP activation under ADR-0064. ADR-0065 acceptance precedes the dependent budget implementation; the [merge closeout](../../roadmap/phase-02b-delivery-log.md#p02b-5-merge-and-closeout-2026-10-20) owns its final delivery status |
| `TenantAssertion` | `tenancy.tenant_assertion.anonymous_burst` `(off-path)` | **MUST** (`security-event`) | The anonymous half of the same detector, written once per `(resolved tenant, dimension, window)` when the mismatch counter crosses its threshold. An anonymous mismatch is not itself an audited operation; the burst is — the precedent the baseline's "login failure burst" already sets ([ADR-0036 Amendment 7](../../decisions/0036-tenant-resolution-trusted-inputs.md)). Same writer, and `(off-path)` for the same reason. The window is **in-process** and never `ICacheService` — a cache outage must not decide whether a MUST-class security event is recorded — so the effective threshold is per instance, which errs toward more auditing. `Tenancy:AssertionBurst` sets it; the defaults are ten occurrences in five minutes |
| Confirmed claim-context assertion | `tenancy.assertion.reject_claim_context` `(off-path)` | **MUST** (`security-event`) | A header conflicting with actual successful ClaimAndMembership resolution uses the confirmed tenant/actor, fixed dimension/reason and membership-confirmed source. No asserted UUID or host allowance. The production claim-context recorder and central evidence owner implement this path; [Phase 03](../../roadmap/phase-03-identity-admin.md) owns qualification. |
| any | `platform.admin_scope.enter` `(off-path)` | **MUST** (`security-event`) | Cross-tenant access is the one read worth a row; [Audit Coverage](../../standards/18-audit-coverage.md) puts every platform-bypass invocation on `security-event`. [Packet 9](../../roadmap/phase-02a-kernel-tenancy.md) ships it: an admitted `PlatformAdminScope.EnterAsync` writes the row through `IAuditStore.WritePlatformScopeAsync` on the scope's own platform-role connection, in a transaction of its own that **commits before** the caller's begins — so work that later fails, throws or is abandoned cannot take the record with it — carrying `TenantId.PlatformSentinel` ([ADR-0044 § 1, § 10](../../decisions/0044-audit-write-path.md)). An entry the catalogue cannot classify, or whose row cannot be written, is **refused** — no transaction is handed out for it. The `Warning` log line stays beside the row, carrying its id, as the real-time signal the row is not. `EnterAsync` is a service method and not a MediatR request, and the catalogue is keyed by request type — so its catalogue entry is registered by slug rather than by request type. Its module segment is `platform` for the same reason: the scope belongs to no module's request path. The permission gate denies before opening a connection; those denied attempts write no entry row. Phase 03 must decide denied-entry evidence before its first privileged consumer |

A feature flag that gates a **billed** capability is not a `tenant_feature_flags`
row at all. It is plan-level, it is written only by
`IEntitlementProvider.RefreshAsync`, and it is audited on the
`platform_entitlement_cache` refresh row above (`tenancy.entitlement.refresh`).
The distinction routes the change to the right row; it does not make either row
optional. `IFeatureFlags` reads the plan half through the provider and the tenant
half from `tenant_feature_flags`, and no module reads the cache table directly
([ADR-0045 § 2](../../decisions/0045-entitlement-and-feature-flag-socket.md)).

`TenancyAuditCatalogSource` declares the same operations in code through
`IAuditCatalogSource.Describe(IAuditCatalogBuilder)` rather than parsing this file; the
matrix stays the human-readable artifact, and `AuditCoverageTests` asserts the two agree
in both directions ([ADR-0044 § 6](../../decisions/0044-audit-write-path.md)).

**That join runs in two directions and they have different domains**
([ADR-0044 Amendment 3](../../decisions/0044-audit-write-path.md)). Catalogue → matrix
is total: every entry this module's `IAuditCatalogSource` registers has a row here
carrying the same slug, and one that does not fails. Matrix → catalogue binds only to a
slug whose request type **exists**, which is why the remaining `(planned)` rows are
classification and not drift — and a `(planned)` row whose command has since shipped
fails, so the marker is a claim the rule re-checks on every run rather than an
exemption.

Off-path rows use slug registration when their writer ships. Delivered
registrations are platform entry, producer/subscriber/job terminal transitions,
the two `tenancy.tenant_assertion.*` operations and
`tenancy.assertion.reject_claim_context`. The entitlement refresh and killswitch
rows carry both `(off-path)` and `(planned)` until their named writers exist.
They have no synthetic request type and sit outside the request-type join in
both directions. Every ordinary request
slug joins in both directions when its command/query lands.

## P02d-2 contextual verification reads

`GetTenantSeedStateQuery`, `GetOrganizationSeedStateQuery`,
`GetHostMappingSeedStateQuery`, `GetSettingSeedStateQuery` are explicitly Off in
`TenancyAuditCatalogSource`. These unrouted queries verify announced
tenant state through the ambient transaction and declare no write operation.

## P02d-4 public site read

`GetPublicSiteQuery` is explicitly Off in `TenancyAuditCatalogSource`. Its GET/HEAD
endpoint returns only the public configuration allowlist through the marked
read-only pipeline. No operation slug or resource audit row is registered. Normal
successes and refusals add no intent; independent rejected-assertion auditing is
unchanged. `IPublicTenantConfigurationReader` and `ITenantSettingsAccessor` are
internal ports, not separately dispatched or directly routed endpoints.

## P02b-5 authenticated account-site read

`GetAccountSiteQuery` is explicitly Off through
`builder.Off<GetAccountSiteQuery>()` in `TenancyAuditCatalogSource`, as required by
[ADR-0064](../../decisions/0064-tenant-api-authentication-boundary.md#closed-authenticated-read).
The non-public query, exposed only by AccountSite GET, returns its own actor/scope
and the existing
public site projection. Normal success and refusal create no query resource intent
or slug; it is neither ReadSensitive nor MAY. Accepted ADR-0077 separately requires a central Identity
denial event for live membership denial attributable to a resolved tenant.
Independent rejected assertions retain their own path. The
[module contract](README.md#p02b-5-authenticated-account-site-read) owns admission.


## P02b-5 attributed assertion refusals

[ADR-0064](../../decisions/0064-tenant-api-authentication-boundary.md#rejection-recorder-and-diagnostic-contracts)
keeps the two existing operation slugs and extends their captured evidence:

- `TenantAssertionSource` maps only to `header` or `jwt-claim` in counters and
  `metadata.assertionSource`. The existing dimension, asserted UUID and authenticated
  fields remain; `metadata.trustedOrganizationId` carries the trusted UUID or null.
- The row takes `ActorUserId` and `CorrelationId` from the captured rejection, including
  when resolution refuses before any tenant context is installed. Its tenant is the
  defended host tenant, and `OrganizationId` remains null for tenant-wide visibility.
- Row 7's rejected organization produces one authenticated rejection row under the host
  tenant when admitted by ADR-0065's host-tenant allowance. Row 10 remains unaudited.
  Separate admitted occurrences remain separate rows. Suppressed occurrences write
  no row and retain their refusal, mismatch/suppression counters and one Warning.
- Row 14 calls synchronous `RecordNoTenantRefusal` with no tenant or asserted value.
  The decorator performs no store I/O. Logging increments the existing unresolved
  counter with only `source=jwt-claim` and `dimension=Tenant`, and emits the fixed
  Debug diagnostic `membership_unconfirmed`. Actor and correlation are structured log
  fields, never metric labels; no audit slug or row is added.
- Existing anonymous burst limits and failure behavior remain: a failed audit preserves
  the refusal while the audit store reports Critical, failure count and unhealthy state.

<a id="accepted-p03-2-claim-context-evidence--runtime-pending"></a>

## Claim-context assertion evidence

[Standard 18](../../standards/18-audit-coverage.md#trusted-authorization-evidence)
owns the exact evidence population, trusted provenance and durability rules.
The new assertion event requires confirmed ClaimAndMembership resolution from
the production reader. It cannot use a candidate/raw claim or impersonate a
host-backed rejection. Host-backed events keep their own allowance; row 7 stays
host-recorded, row 10 unaudited, and refused row 14 diagnostic-only. A successful
row 14 followed by conflicting tenant/org header is the distinct new MUST event,
without an asserted UUID or host-budget debit. Actual refusal survives audit
failure with the existing failure signals. The
[Phase 03 Status block](../../roadmap/phase-03-identity-admin.md) owns source-bound
qualification.

## Mapped-property personal-data declarations

P03-1 Step 2 declares every current Tenancy scalar in its
[EF mappings](../../../backend/src/Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/Persistence/Configurations.cs).
Institution routing, entitlement and definition fields are explicit None with
purpose; inherited person references and stamps use the
[common declaration contract](../../architecture/23-data-protection.md#identity-foundation-and-classification-ownership).
Setting JSON, free-text verification errors/operator explanations and actor
references are classified Identity. This conservative declaration reads no row
values and grants no public projection, log exposure or new retention policy.
TenantSetting.Value retains its independent whole-value snapshot redaction.
The model finalizer and complete census reject undeclared/new neutral fields;
provider/domain-verification and privileged toggle writers retain their named
phase authority and fixed-diagnostic requirements before activation.

## Bounded administration bindings

**Accepted under [ADR-0079](../../decisions/0079-bounded-identity-administration.md);
new request/catalogue bindings and actual proof pending P03-3.** Existing slugs
may bind more than one request type; the catalogue/matrix join remains total.
Human wrappers have separate Protected policies, never Seed provenance or nested
dispatch to Seed-only commands. The classification matrix above remains the sole
class/type declaration; this request-binding table maps types to those rows:

| New human request | Aggregate subject | Matrix operation |
|---|---|---|
| `CreateManagedOrganizationCommand` | Organization | `tenancy.organization.create` |
| `RenameOrganizationCommand` | Organization | `tenancy.organization.rename` |
| `ArchiveOrganizationCommand` | Organization | `tenancy.organization.archive` |
| `SelectDefaultOrganizationCommand` | Tenant | `tenancy.organization.select_default` |
| `AddManagedTenantLocaleCommand` | Tenant | `tenancy.locale.write` |
| `SetManagedDefaultTenantLocaleCommand` | Tenant | `tenancy.locale.write` |
| `SetTenantLocaleEnabledCommand` | Tenant | `tenancy.locale.write` |
| `RemoveTenantLocaleCommand` | Tenant | `tenancy.locale.write` |
| `SetManagedTenantBrandingCommand` | TenantSetting | `tenancy.setting.write` |

Locale writes are contained Tenant changes: their permission/audit resource is
`locale`, while snapshots and version belong to Tenant. Default selection is the
Tenant-root sub-operation of `tenancy.organization.admin`, not a general Tenant
settings grant. Its Tenant intent and returned version describe that root only.
Successful mutation audit shares business commit; existing MUST rollback remains.

`AdoptSeedAdministrationOriginCommand` is a distinct closed Seed request for one
Tenant. It declares both `tenancy.tenant.adopt_seed_origin` and existing
`tenancy.hostmapping.write` MUST intents on its common transaction. Tenant marker
and fixed projection commit together for qualified `0→2` / `1→2`; an exact existing
mapping is designated unchanged without an UPDATE. Under Accepted
[ADR-0081](../../decisions/0081-fixed-seed-completion-before-origin-adoption.md),
the runner's marker-2/exact-live-mapping branch skips mutation dispatch, witness
minting, the temporary hatch and MUST intents. The separate completion check is
internal to this act, adds no successful sensitive-read event or new Off request,
and retains failed/denied and genuine unknown-COMMIT evidence on refusal.
It grants no human host-mapping permission or v2 Role authority and introduces
no second aggregate/provisioning exception. The
[canonical completion contract](../../standards/05-database.md#fixed-seed-completion-read-authority)
owns the separate source, retained completion facts, restoration, marker/repeat
and recovery rules; qualification remains in the phase Status block.

Organization/default protected receipt reads are ordinary MUST sensitive reads
with current original-target disclosure admission before stored bytes. Failed
guard/audit retains the completed receipt, and original mutation evidence is not
replayed. Off global-self/current-context navigation obtains Tenancy labels only
through the separately confined own-discovery reader; the helper creates no
independent resource intent or general cross-tenant directory. AccountSite remains
Off under its actual-host ceiling, including the pending native-origin extension.
