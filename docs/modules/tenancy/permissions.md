# Tenancy — Permission Matrix

Per [Permission Standards](../../standards/19-permissions.md), which names this
file. Part of the [module spec](README.md).

**P02d-3 Step 1 implemented — 2026-10-02; review pending.**
`ITenantSettingsAccessor` is internal and unrouted, uses trusted ambient
scope and adds no HTTP endpoint or permission key. Public admission belongs to
P02d-4. [The module contract](README.md#p02d-3-accepted-typed-settings-contract) owns its scope.

**P02d-2 Step 2 writers — 2026-10-02.** The
[locale and branding commands](README.md#p02d-2-accepted-locale-and-branding-contract)
are implemented unrouted tenant-wide operations, with no registered permission or
organization override. Their eventual identity-backed permission admission remains
Phase 03. This note grants no HTTP or Hub-internal reachability for the new commands.

**No permission keys yet.** The matrix below is a forward declaration in the
`{module}.{resource}.{action}` form with the closed action set of
[Permission Standards](../../standards/19-permissions.md). Registration runs
through `IModule.RegisterPermissions(IPermissionRegistry)`, and neither type
exists in `backend/src` yet; the catalogue lands with the Identity module in
[Phase 03](../../roadmap/phase-03-identity-admin.md), together with `Role`,
`Permission` and the lighting-up of the `AuthorizationBehavior` shell.

**Six write handlers exist without identity-backed authorization**, for two
different reasons. Four contextual verification queries are explicitly audit Off.
P02d-2 adds no HTTP or Hub-internal route.

`ProvisionTenantCommand` has nothing for a permission check to read: it runs with
an **unresolved** tenant context by construction — that is what lets it announce
the tenant it is creating — and attributes the write to `UserId.SystemActor`,
because provisioning precedes any membership in the tenant being provisioned.

`CreateOrganizationCommand`, `MapHostToTenantCommand` and the three P02d-2 writers do
run resolved, so the first argument does not transfer to them. What stands in for
authorization is the same thing for these writers: reachability. None of the six
writers has an HTTP route today; the seeder is their trusted production caller.
[Phase 02c](../../roadmap/phase-02c-hub-foundation.md) owns the planned Hub-internal
routes: tenant creation invokes `ProvisionTenantCommand`, which also creates the default
organization, and host mapping invokes `MapHostToTenantCommand`. It adds no direct
`CreateOrganizationCommand` endpoint. That surface takes `learnstack-hub` realm tokens
and no others. Resolved writers take
their tenant from the context and never from the request, so a caller cannot name
another tenant even without a permission check; the database refuses the write.

**`MapHostToTenantCommand` is the one to gate first.** It writes the row that
decides whose data an anonymous request sees, which makes it the highest-value
write in the module — and the reason the matrix above gives `HostMapping` its own
resource with **no `write`**: pointing a hostname at a tenant is an admin-scope
act, so `tenancy.hostmapping.admin` is the key that will govern it rather than any
grant inside the everyday tenant-admin role. `tenancy.tenant.admin` governs
provisioning and `tenancy.organization.write` the second organization. All three
are registered with the rest in Phase 03.

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
