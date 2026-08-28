# Tenancy — Audit Coverage Matrix

Per [Audit Coverage](../../standards/18-audit-coverage.md), which names this
file. Part of the [module spec](README.md).

Per [Audit Coverage](../../standards/18-audit-coverage.md). The operations do not
exist yet; the classification does, and it is the floor a later packet may narrow
for SHOULD/MAY but never for MUST.

| Resource | Operation | Class | Why |
|---|---|---|---|
| `Tenant` | create | **MUST** | The root of a customer's data; its existence is a contractual fact |
| `Tenant` | status change | **MUST** | Suspension withdraws access; an operator must be able to say who and when |
| `Tenant` | rename | SHOULD | Presentational |
| `Organization` | create / archive | **MUST** | Changes the isolation surface of every org-scoped row |
| `Organization` | rename | SHOULD | Presentational |
| `TenantDomain` | claim / verify / fail | **MUST** | A host change redirects traffic; a wrongly verified domain serves one tenant's content at another's address |
| `TenantSetting` | write / delete | SHOULD | Configuration, per key; a tenant `AuditConfig` may narrow this |
| `TenantLocale` | write | SHOULD | Configuration |
| `TenantFeatureFlag` | write | SHOULD | Configuration, but see the note |
| `platform_entitlement_cache` | refresh | **MUST** | Changes what the tenant may do; written only by `IEntitlementProvider.RefreshAsync` |
| `platform_host_to_tenant` | write / delete | **MUST** | The resolution index — the row that decides whose data an anonymous request sees |
| any | read under `EnterPlatformAdminScope` | **MUST** (`read-sensitive`) | Cross-tenant access is the one read worth a row |

A feature flag that gates a **billed** capability is plan-level and belongs in the
entitlement projection, not here — so a SHOULD on this table never covers a
change that should have been MUST elsewhere.
