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

**Three handlers exist and all are deliberately unauthorized**, for two different
reasons.

`ProvisionTenantCommand` has nothing for a permission check to read: it runs with
an **unresolved** tenant context by construction — that is what lets it announce
the tenant it is creating — and attributes the write to `UserId.SystemActor`,
because provisioning precedes any membership in the tenant being provisioned.

`CreateOrganizationCommand` and `MapHostToTenantCommand` do run resolved, so the
first argument does not transfer to them. What stands in for authorization is the
same thing for all three: reachability. None has an HTTP endpoint, so their only
callers are the seeder and, from
[Phase 02c](../../roadmap/phase-02c-hub-foundation.md), the Hub over
`/api/internal/*` — a surface that takes `learnstack-hub` realm tokens and no
others. Both take their tenant from the context and never from the request, so a
caller cannot name another tenant even without a permission check; the database
refuses the write.

**`MapHostToTenantCommand` is the one to gate first.** It writes the row that
decides whose data an anonymous request sees, which makes it the highest-value
write in the module and the reason `tenancy.tenant.admin` — not
`tenancy.tenant.write` — is the key that will govern it. The three keys are
registered with the rest in Phase 03.

| Resource | read | write | delete | admin | Default role grants |
|----------|:----:|:-----:|:------:|:-----:|---------------------|
| `Tenant` | ✓ | ✓ | – | ✓ | tenant-admin: read+write; platform operator: admin |
| `HostMapping` | ✓ | – | – | ✓ | platform operator: admin. **No `write`**: pointing a hostname at a tenant is an admin-scope act, and a `write` grant would put the resolution index inside the everyday tenant-admin role |
| `Organization` | ✓ | ✓ | ✓ | ✓ | tenant-admin: all; org-admin: read+write (own) |
| `TenantDomain` | ✓ | ✓ | ✓ | – | tenant-admin: all |
| `TenantSetting` | ✓ | ✓ | ✓ | – | tenant-admin: all; org-admin: own organization only |
| `TenantLocale` | ✓ | ✓ | ✓ | – | tenant-admin: all |
| `TenantFeatureFlag` | ✓ | ✓ | ✓ | – | tenant-admin: all |

`Tenant` has no `delete`: deprovisioning has no owning phase, and
[Database Standards § GRANT matrix](../../standards/05-database.md) records that
the widening it needs is an ADR's to make, not a migration's.
