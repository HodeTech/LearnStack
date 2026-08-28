# Tenancy — Permission Matrix

Per [Permission Standards](../../standards/19-permissions.md), which names this
file. Part of the [module spec](README.md).

**No permission keys yet** — Packet 6 ships no command or query handler, so there
is nothing to authorize. The matrix below is what Packet 7 registers, in the
`{module}.{resource}.{action}` form with the closed action set of
[Permission Standards](../../standards/19-permissions.md).

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
