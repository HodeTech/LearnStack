# Tenancy — Audit Coverage Matrix

Per [Audit Coverage](../../standards/18-audit-coverage.md), which names this
file. Part of the [module spec](README.md).

Four writes below exist today, between them raising three of the slugs. `Tenant`
create and `Organization` create, written together by `ProvisionTenantCommand`
([ADR-0042](../../decisions/0042-tenant-provisioning-cross-aggregate-transaction.md))
; a second `Organization` create, written alone by `CreateOrganizationCommand`;
and `platform_host_to_tenant` write, by `MapHostToTenantCommand`. Fifteen more rows
are classification ahead of code and carry `(planned)`; five name operations no
MediatR request raises and carry `(off-path)`.

All four are **MUST**. The two provisioning writes share one transaction, so
[ADR-0033](../../decisions/0033-audit-durability-model.md)'s guarantee for them is
the ordinary one — the rows commit with the aggregates or nothing does. The other
two are each their own transaction, and the guarantee is the same shape for each.

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

**All four are unaudited today**, and the host mapping is the one that matters
most: it is described in the matrix below as "the row that decides whose data an
anonymous request sees", and nothing records who pointed a hostname where.
`AuditLogBehavior` lights up in
[Packet 9](../../roadmap/phase-02a-kernel-tenancy.md), and `TransactionBehavior`
carries the `TODO(2026-08-28, @platform, phase-02a-packet-9)` marking the line the
MUST-class write goes on, immediately before the commit.

**The snapshots come with them.** `PlatformHostMapping`, `TenantLocale`,
`TenantFeatureFlag` and `PlatformEntitlement` are plain classes rather than
`AuditableEntity<>` descendants, and `AuditChangeTrackerInterceptor` captures every
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
that applies** — a row can carry both, and one does
([ADR-0044 Amendment 3](../../decisions/0044-audit-write-path.md)). `(planned)` is a row
classified ahead of the command that will raise it — the classification
[Audit Coverage](../../standards/18-audit-coverage.md) asks for *before* the command
ships, which is what makes MUST/SHOULD/MAY a functional distinction rather than a
documentation one. `(off-path)` is an operation no MediatR request raises, so its
catalogue entry is registered by slug and there is no request type to key it on. An
unmarked row is one a shipped command already raises.

| Resource | Operation | Class | Why |
|---|---|---|---|
| `Tenant` | `tenancy.tenant.create` | **MUST** | The root of a customer's data; its existence is a contractual fact |
| `Tenant` | `tenancy.tenant.change_status` `(planned)` | **MUST** | Suspension withdraws access; an operator must be able to say who and when |
| `Tenant` | `tenancy.tenant.rename` `(planned)` | SHOULD | Presentational |
| `Organization` | `tenancy.organization.create` | **MUST** | Adds an isolation surface; every org-scoped row written afterwards is written under it |
| `Organization` | `tenancy.organization.archive` `(planned)` | **MUST** | Withdraws that surface while every row it isolated stays behind, and the rows themselves say nothing about who archived the organization or when |
| `Organization` | `tenancy.organization.rename` `(planned)` | SHOULD | Presentational |
| `TenantDomain` | `tenancy.domain.claim` `(planned)` | **MUST** | `ux_tenant_domains_host` is unique on the host alone, so a claim takes a host no other tenant can claim while it stands |
| `TenantDomain` | `tenancy.domain.verify` `(planned)` | **MUST** | Verification is the gate the custom-domain lifecycle opens before a mapping row exists; a wrongly verified domain serves one tenant's content at another's address |
| `TenantDomain` | `tenancy.domain.fail` `(planned)` | **MUST** | The negative half of the same lifecycle — the attempt count and the recorded reason are what a support thread reconstructs, and repeated failures against one host are worth seeing |
| `TenantDomain` | `tenancy.domain.soft_delete` `(planned)` | **MUST** | `ux_tenant_domains_host` is partial on `deleted_at IS NULL`, so retiring the row releases that globally unique host for another tenant to claim |
| `TenantSetting` | `tenancy.setting.write` `(planned)` | **MUST** | [Audit Coverage](../../standards/18-audit-coverage.md) puts "tenant setting changed" on the Tenancy baseline row; a tenant `AuditConfig` cannot narrow it |
| `TenantSetting` | `tenancy.setting.soft_delete` `(planned)` | **MUST** | Removing a setting is a change to it, so the same baseline row covers it — and `ux_tenant_settings_tenant_id_organization_id_key` is partial on `deleted_at IS NULL`, so the key becomes free for a successor |
| `TenantLocale` | `tenancy.locale.write` `(planned)` | SHOULD | Configuration |
| `TenantFeatureFlag` | `tenancy.feature_flag.write` `(planned)` | **MUST** | "Feature flag toggled" is on the same baseline row, and [Feature Flags § Audit](../../architecture/21-feature-flags.md) classes both flag surfaces as security events |
| `platform_killswitches` | `tenancy.killswitch.toggle` `(off-path)` `(planned)` | **MUST** | A platform-wide flip that disables a capability for every tenant at once. It is not a `tenant_feature_flags` row — that table's foreign key to `tenants` is one the platform sentinel cannot satisfy, so killswitches ship as their own platform-scoped table ([ADR-0045 § 5](../../decisions/0045-entitlement-and-feature-flag-socket.md)). Every write goes through `EnterPlatformAdminScope(reason)`, which is what gives the row a real actor ([Feature Flags § Killswitch Pattern](../../architecture/21-feature-flags.md)); the scope is entered by a service method and not by a MediatR request, so there is no request type to key a catalogue entry on. [Packet 9](../../roadmap/phase-02a-kernel-tenancy.md) ships the table, its policies and the read path and **no writer**: nothing can enter that scope while `DenyAllPlatformAdminGate` is the registered gate, so [Phase 03](../../roadmap/phase-03-identity-admin.md) owns the toggle, its permission and its runbook ([ADR-0045 Amendment 1](../../decisions/0045-entitlement-and-feature-flag-socket.md)) |
| `platform_entitlement_cache` | `tenancy.entitlement.refresh` `(planned)` `(off-path)` | **MUST** | Changes what the tenant may do; written only by `IEntitlementProvider.RefreshAsync`, a port method driven by the Hub over `/api/internal/*` ([ADR-0045 § 1](../../decisions/0045-entitlement-and-feature-flag-socket.md)) and never by a MediatR request, so there is no request type to key a catalogue entry on. **Both markers**, for the reason `tenancy.killswitch.toggle` carries both: Packet 9 ships `NullEntitlementProvider`, which answers from constants and touches no table, and the `HubEntitlementProvider` that performs a real refresh lands in [Phase 02c](../../roadmap/phase-02c-hub-foundation.md) ([ADR-0045 § 6](../../decisions/0045-entitlement-and-feature-flag-socket.md)) — so there is no writer here yet either |
| `platform_host_to_tenant` | `tenancy.hostmapping.write` | **MUST** | The resolution index — the row that decides whose data an anonymous request sees |
| `platform_host_to_tenant` | `tenancy.hostmapping.delete` `(planned)` | **MUST** | The same row removed. The table carries no `deleted_at`, so the delete is a row gone and a host that resolves to nobody |
| `TenantAssertion` | `tenancy.tenant_assertion.reject` `(off-path)` | **MUST** (`security-event`) | A header or claim naming a tenant other than the one the API resolved is the probe that precedes a cross-tenant read. Written for **every occurrence** that carries a validated principal, with no coalescing and no per-tenant ceiling, and always under the **resolved** tenant — never a sentinel and never the asserted value, which would hand an anonymous client a write into a tenant of its choosing ([ADR-0036 Amendment 7](../../decisions/0036-tenant-resolution-trusted-inputs.md)). [Packet 9](../../roadmap/phase-02a-kernel-tenancy.md) swaps `LoggingTenantAssertionRecorder` for the auditing implementation; the recorder is a non-MediatR caller reaching `IAuditStore.WriteStandaloneAsync` ([ADR-0033 Amendment 2](../../decisions/0033-audit-durability-model.md)), so there is no request type to key a catalogue entry on |
| `TenantAssertion` | `tenancy.tenant_assertion.anonymous_burst` `(off-path)` | **MUST** (`security-event`) | The anonymous half of the same detector, written once per `(resolved tenant, dimension, window)` when the mismatch counter crosses its threshold. An anonymous mismatch is not itself an audited operation; the burst is — the precedent the baseline's "login failure burst" already sets ([ADR-0036 Amendment 7](../../decisions/0036-tenant-resolution-trusted-inputs.md)). Same writer, and `(off-path)` for the same reason |
| any | `platform.admin_scope.enter` `(off-path)` | **MUST** (`security-event`) | Cross-tenant access is the one read worth a row; [Audit Coverage](../../standards/18-audit-coverage.md) puts every platform-bypass invocation on `security-event`. [Packet 9](../../roadmap/phase-02a-kernel-tenancy.md) replaces the scope's log line with a row written by `IAuditStore.WritePlatformScopeAsync` on the scope's own platform-role connection, **before** the operation runs, carrying `TenantId.PlatformSentinel` ([ADR-0044 § 1, § 10](../../decisions/0044-audit-write-path.md)). `EnterAsync` is a service method and not a MediatR request, and the catalogue is keyed by request type — so its catalogue entry is registered by slug rather than by request type. Its module segment is `platform` for the same reason: the scope belongs to no module's request path |

A feature flag that gates a **billed** capability is not a `tenant_feature_flags`
row at all. It is plan-level, it is written only by
`IEntitlementProvider.RefreshAsync`, and it is audited on the
`platform_entitlement_cache` refresh row above (`tenancy.entitlement.refresh`).
The distinction routes the change to the right row; it does not make either row
optional. `IFeatureFlags` reads the plan half through the provider and the tenant
half from `tenant_feature_flags`, and no module reads the cache table directly
([ADR-0045 § 2](../../decisions/0045-entitlement-and-feature-flag-socket.md)).

The classification is inert until [Packet 9](../../roadmap/phase-02a-kernel-tenancy.md)
lights up `AuditLogBehavior`. Packet 9 does not parse this file: it declares the same
operations in code through `IAuditCatalogSource.Describe(IAuditCatalogBuilder)`, this
matrix stays the human-readable artifact, and
`Every_TenantOwned_Command_HasAuditCoverage` asserts the two agree
([ADR-0044 § 6](../../decisions/0044-audit-write-path.md)).

**That join runs in two directions and they have different domains**
([ADR-0044 Amendment 3](../../decisions/0044-audit-write-path.md)). Catalogue → matrix
is total: every entry this module's `IAuditCatalogSource` registers has a row here
carrying the same slug, and one that does not fails. Matrix → catalogue binds only to a
slug whose request type **exists**, which is why the fifteen `(planned)` rows are
classification and not drift — and a `(planned)` row whose command has since shipped
fails, so the marker is a claim the rule re-checks on every run rather than an
exemption.

Five rows sit outside that join in both directions, each marked `(off-path)` for the
reason its own cell gives: `platform.admin_scope.enter`, written on the fourth write path
([ADR-0044 § 10](../../decisions/0044-audit-write-path.md));
`tenancy.killswitch.toggle`, written inside `EnterPlatformAdminScope(reason)`;
`tenancy.entitlement.refresh`, whose writer is `IEntitlementProvider.RefreshAsync` and
whose refreshing implementation lands in Phase 02c; and the two
`tenancy.tenant_assertion.*` keys, written by a non-MediatR caller reaching
`IAuditStore.WriteStandaloneAsync`
([ADR-0033 Amendment 2](../../decisions/0033-audit-durability-model.md)). Their catalogue
entries are registered by slug rather than by request type. Every other slug above is
joined in both directions the day its command lands.
