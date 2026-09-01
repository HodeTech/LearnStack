# Tenancy — Permission Matrix

Per [Permission Standards](../../standards/19-permissions.md), which names this
file. Part of the [module spec](README.md).

**No permission keys yet** — Packet 6 ships no command or query handler, so there
is nothing to authorize. The matrix below is a forward declaration in the
`{module}.{resource}.{action}` form with the closed action set of
[Permission Standards](../../standards/19-permissions.md), not a Packet 7
deliverable. Registration runs through
`IModule.RegisterPermissions(IPermissionRegistry)`, and neither type exists in
`backend/src` yet; the catalogue lands with the Identity module in
[Phase 03](../../roadmap/phase-03-identity-admin.md), together with `Role`,
`Permission` and the lighting-up of the `AuthorizationBehavior` shell. Packet 7
ships the aggregates and the seed, not the keys.

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
