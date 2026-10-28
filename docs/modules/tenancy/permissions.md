# Tenancy — Permission Matrix

Per [Permission Standards](../../standards/19-permissions.md), which names this
file. Part of the [module spec](README.md).

**P02b-5 account-site subject.** `GetAccountSiteQuery` is non-public and
explicitly audit Off. It adds no permission key or grant, and
returns only the context's own actor/scope with the existing public site
projection. Its independent `HostAndClaim`/host-scope guard precedes all readers;
membership alone cannot admit it. [The module contract](README.md#p02b-5-authenticated-account-site-read)
owns the boundary. P02b-5 Step 4 exposes only its authenticated GET route;
Accepted [ADR-0076](../../decisions/0076-live-scoped-authorization.md) adds live
membership/lifecycle checks to its protected admission while preserving its
independent host ceiling and Off success classification. The
[Phase 03 Status block](../../roadmap/phase-03-identity-admin.md) owns execution
and qualification; acceptance is not activation.

**P02b-1 Step 3 producer implemented — 2026-10-12.** The completed-provisioning
domain translator runs only during owner completion, adds no endpoint or
permission, and inserts a pending outbox row under the provisioning announcement.
Existing command reachability is unchanged. The
[integration-event catalogue](README.md#integration-event-catalogue) owns the flow.

**ADR-0046 P02b-2 complete and merged — 2026-10-13.** All four steps
completed both independent review rounds. The [merge closeout](../../roadmap/phase-02b-delivery-log.md#p02b-2-merge-and-closeout-2026-10-13)
owns CI/merge status.
The API-owned dispatcher and terminal audit add no
permission key or HTTP endpoint. P02b-3's
targeted replay is internal; Phase 03 separately decides operator permission and
routes. Neither grants a tenant administrator bypass-role access.

**P02d-4 Step 2 implemented — 2026-10-03.** Both review rounds
and focused fix verification pass. Site bootstrap
is a marked GET/HEAD request, audit Off and without a permission key or grant. It
uses matching host scope and READ ONLY; normal reads add no audit row. Public
configuration/settings ports remain internal and unrouted. Independent rejected-
assertion auditing remains unchanged. [The public contract](README.md#p02d-4-public-configuration-contract)
owns allowlisting and lifecycle/locale checks.

Accepted ADR-0076 classifies that existing bootstrap through virtual Tenant
`tenancy.public_site.read`. This is Portal Public policy metadata, unavailable
as a stored Role grant; it adds no endpoint, Membership, template or visibility.
Credential-free public reads perform no identity/grant lookup. The
[Permission standard](../../standards/19-permissions.md) owns the virtual class.

**P02d-3 Step 1 implemented — 2026-10-02; both review rounds passed.**
`ITenantSettingsAccessor` is internal and unrouted, uses trusted ambient
scope and adds no HTTP endpoint or permission key. Public admission belongs to
P02d-4. [The module contract](README.md#p02d-3-accepted-typed-settings-contract) owns its scope.

**P02d-2 Step 2 writers — 2026-10-02.** The
[locale and branding commands](README.md#p02d-2-accepted-locale-and-branding-contract)
are implemented unrouted tenant-wide operations, with no assignable permission or
organization override. Their finite Seed admission is described below. Human-facing
permissions require their owning Phase 03 decision; no HTTP or Hub-internal route
is admitted here.

**No assignable Tenancy permission keys yet.** The matrix below is a forward declaration in the
`{module}.{resource}.{action}` form with the closed action set of
[Permission Standards](../../standards/19-permissions.md). Its adopted module-source
contract registers scoped definitions separately from finite grants; no `IModule`
API is implied. Identity's foundation definitions/Role templates are implemented.
Tenancy's assignable keys and their human/operator consumers require their owning
Phase 03 decision; these examples register or grant nothing. The virtual public
definition above is separate from this assignable resource matrix.

**Six write handlers and four verification queries have finite Seed admission.**
`TenancyOperationPolicySource` binds each request to its declared Seed source under
the [live contract](../../standards/19-permissions.md#live-permission-enforcement-contract).
The verification queries remain audit Off. These bindings add no HTTP or
Hub-internal route and no assignable permission key.

`ProvisionTenantCommand` runs with an **unresolved** tenant context so it can
announce the tenant it creates. Provisioning precedes Membership and attributes
the write to `UserId.SystemActor`; that value supplies no exemption from the
finite source/request gate.

`CreateOrganizationCommand`, `MapHostToTenantCommand` and the three P02d-2 writers
run resolved and derive their tenant from context, never request input. Finite
source admission and database isolation independently constrain these writes.
The Seeder remains their production caller.
[Phase 02c](../../roadmap/phase-02c-hub-foundation.md) owns the planned Hub-internal
routes: tenant creation invokes `ProvisionTenantCommand`, which also creates the
default organization, and host mapping invokes `MapHostToTenantCommand`. It adds
no direct `CreateOrganizationCommand` endpoint. That future surface accepts only
`learnstack-hub` realm tokens and needs its own admitted operation policy.

**A future host-mapping route requires its own admission decision.**
`MapHostToTenantCommand` controls whose data an anonymous request sees. The proposed
`tenancy.hostmapping.admin` key therefore describes an admin-scope act, outside
ordinary tenant-admin grants; `HostMapping` has no `write` action. The proposed
`tenancy.tenant.admin` and `tenancy.organization.write` keys describe provisioning
and organization creation. Each requires its owning surface decision and approved
finite registration; the matrix alone registers or grants none of them.

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
