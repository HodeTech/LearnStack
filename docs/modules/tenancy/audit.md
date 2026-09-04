# Tenancy — Audit Coverage Matrix

Per [Audit Coverage](../../standards/18-audit-coverage.md), which names this
file. Part of the [module spec](README.md).

Four of the operations below now exist. `Tenant` create and `Organization`
create, written together by `ProvisionTenantCommand`
([ADR-0042](../../decisions/0042-tenant-provisioning-cross-aggregate-transaction.md))
; a second `Organization` create, written alone by `CreateOrganizationCommand`;
and `platform_host_to_tenant` write, by `MapHostToTenantCommand`. The rest are
still classification ahead of code.

All four are **MUST**. The two provisioning writes share one transaction, so
[ADR-0033](../../decisions/0033-audit-durability-model.md)'s guarantee for them is
the ordinary one — the rows commit with the aggregates or nothing does. The other
two are each their own transaction, and the guarantee is the same shape for each.

**All four are unaudited today**, and the host mapping is the one that matters
most: it is described in the matrix below as "the row that decides whose data an
anonymous request sees", and nothing records who pointed a hostname where.
`AuditLogBehavior` lights up in
[Packet 9](../../roadmap/phase-02a-kernel-tenancy.md), and `TransactionBehavior`
carries the `TODO(2026-08-28, @platform, phase-02a-packet-9)` marking the line the
MUST-class write goes on, immediately before the commit.
This matrix is not the floor — [Audit Coverage § Baseline Coverage](../../standards/18-audit-coverage.md)
is, and a module matrix "cannot remove anything in this list". This file adds rows
beneath that baseline and classifies what the baseline leaves open; a tenant
`AuditConfig` may then narrow SHOULD/MAY at runtime. Neither touches a baseline
MUST.

| Resource | Operation | Class | Why |
|---|---|---|---|
| `Tenant` | create | **MUST** | The root of a customer's data; its existence is a contractual fact |
| `Tenant` | status change | **MUST** | Suspension withdraws access; an operator must be able to say who and when |
| `Tenant` | rename | SHOULD | Presentational |
| `Organization` | create / archive | **MUST** | Changes the isolation surface of every org-scoped row |
| `Organization` | rename | SHOULD | Presentational |
| `TenantDomain` | claim / verify / fail | **MUST** | A host change redirects traffic; a wrongly verified domain serves one tenant's content at another's address |
| `TenantDomain` | release / delete | **MUST** | `ux_tenant_domains_host` is partial on `deleted_at IS NULL`, so a release frees a globally unique host for another tenant to claim |
| `TenantSetting` | write / delete | **MUST** | [Audit Coverage](../../standards/18-audit-coverage.md) puts "tenant setting changed" on the Tenancy baseline row; a tenant `AuditConfig` cannot narrow it |
| `TenantLocale` | write | SHOULD | Configuration |
| `TenantFeatureFlag` | write | **MUST** | "Feature flag toggled" is on the same baseline row, and [Feature Flags § Audit](../../architecture/21-feature-flags.md) classes both flag surfaces as security events |
| `TenantFeatureFlag` | killswitch toggle (`tenancy.killswitch.toggle`) | **MUST** | A platform-admin flip stored under the sentinel platform tenant that disables a capability for every tenant at once ([Feature Flags § Killswitch Pattern](../../architecture/21-feature-flags.md)) |
| `platform_entitlement_cache` | refresh | **MUST** | Changes what the tenant may do; written only by `IEntitlementProvider.RefreshAsync` |
| `platform_host_to_tenant` | write / delete | **MUST** | The resolution index — the row that decides whose data an anonymous request sees |
| any | read under `EnterPlatformAdminScope` | **MUST** (`security-event`) | Cross-tenant access is the one read worth a row; [Audit Coverage](../../standards/18-audit-coverage.md) puts every platform-bypass invocation on `security-event`, and that is what [Packet 9](../../roadmap/phase-02a-kernel-tenancy.md) writes when it replaces the scope's log line |

A feature flag that gates a **billed** capability is not a `tenant_feature_flags`
row at all. It is plan-level, it is written only by
`IEntitlementProvider.RefreshAsync`, and it is audited on the
`platform_entitlement_cache` refresh row above (`tenancy.entitlement.refresh`).
The distinction routes the change to the right row; it does not make either row
optional.

The classification is inert until [Packet 9](../../roadmap/phase-02a-kernel-tenancy.md)
lights up `AuditLogBehavior`, and Packet 9 transcribes its in-process catalogue
from this file.
