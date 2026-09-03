# Tenancy — Permission Matrix

Per [Permission Standards](../../standards/19-permissions.md), which names this
file. Part of the [module spec](README.md).

**No permission keys yet.** The matrix below is a forward declaration in the
`{module}.{resource}.{action}` form with the closed action set of
[Permission Standards](../../standards/19-permissions.md). Registration runs
through `IModule.RegisterPermissions(IPermissionRegistry)`, and neither type
exists in `backend/src` yet; the catalogue lands with the Identity module in
[Phase 03](../../roadmap/phase-03-identity-admin.md), together with `Role`,
`Permission` and the lighting-up of the `AuthorizationBehavior` shell.

**One handler exists and is deliberately unauthorized.** Packet 7 ships
`ProvisionTenantCommand`, and there is nothing for a permission check to read: it
runs with an **unresolved** tenant context by construction — that is what lets it
announce the tenant it is creating — and it attributes the write to
`UserId.SystemActor`, because provisioning precedes any membership in the tenant
being provisioned. What stands in for authorization today is reachability: the
command has no HTTP endpoint, so its only callers are the seeder and, from
[Phase 02c](../../roadmap/phase-02c-hub-foundation.md), the Hub over
`/api/internal/*` — a surface that takes `learnstack-hub` realm tokens and no
others. `tenancy.tenant.admin` is the key that will govern it, registered with
the rest in Phase 03.

| Resource | read | write | delete | admin | Default role grants |
|----------|:----:|:-----:|:------:|:-----:|---------------------|
| `Tenant` | ✓ | ✓ | – | ✓ | tenant-admin: read+write; platform operator: admin |
| `Organization` | ✓ | ✓ | ✓ | ✓ | tenant-admin: all; org-admin: read+write (own) |
| `TenantDomain` | ✓ | ✓ | ✓ | – | tenant-admin: all |
| `TenantSetting` | ✓ | ✓ | ✓ | – | tenant-admin: all; org-admin: own organization only |
| `TenantLocale` | ✓ | ✓ | ✓ | – | tenant-admin: all |
| `TenantFeatureFlag` | ✓ | ✓ | ✓ | – | tenant-admin: all |

`Tenant` has no `delete`: deprovisioning has no owning phase, and
[Database Standards § GRANT matrix](../../standards/05-database.md) records that
the widening it needs is an ADR's to make, not a migration's.
