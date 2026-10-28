# Tenancy — Permission Matrix

Per [Permission Standards](../../standards/19-permissions.md), which names this
file. Part of the [module spec](README.md).

**P02b-5 account-site subject.** `GetAccountSiteQuery` is non-public and
explicitly audit Off. It adds no permission key or grant, and
returns only the context's own actor/scope with the existing public site
projection. Its implemented `HostAndClaim`/host-scope guard precedes all readers;
membership alone cannot admit it. [The module contract](README.md#p02b-5-authenticated-account-site-read)
owns the boundary. P02b-5 Step 4 exposes only its authenticated GET route;
Accepted [ADR-0076](../../decisions/0076-live-scoped-authorization.md) adds live
membership/lifecycle checks to its protected admission while preserving its
independent host ceiling and Off success classification. The
[Phase 03 Status block](../../roadmap/phase-03-identity-admin.md) owns execution
and qualification; acceptance is not activation.

ADR-0078 additionally accepts the separately validated native-session origin
under the same real-host ceiling. That consumer remains pending P03-3; it does
not admit HostOnly/ClaimAndMembership or turn AccountSite into an admin query.

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
permissions are selected by ADR-0079 below; their new consumers remain pending
P03-3. No additional Hub-internal route is admitted here.

**Accepted P03-3 assignable contract; registration/consumers pending implementation.**
[ADR-0079](../../decisions/0079-bounded-identity-administration.md) selects exactly
the three Tenant pairs below. The
[versioned manifest](../../standards/19-permissions.md#bounded-administration-and-version-2-adoption)
owns first adoption into Tenant Admin; declaration alone grants nothing. No
Organization Tenancy pair or generic settings power is selected. The virtual
Portal Public definition above remains nonassignable and separate.

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
`tenancy.tenant.admin` remains a future provisioning example, not a P03-3 grant.
The older unregistered `tenancy.organization.write` example is replaced for this
packet by Tenant `tenancy.organization.admin`, covering lifecycle/default acts.
P03-3 creates no human host-mapping, domain, feature-flag or platform-operator
authority. Their named Phase 02c/P03-9 surfaces retain separate admission decisions.

| Resource | read | write | delete | admin | Default role grants |
|----------|:----:|:-----:|:------:|:-----:|---------------------|
| `Organization` (`organization`) | – | – | – | Tenant | Exact v2 Tenant Admin delta; only admitted Organization/default operations |
| `TenantLocale` (`locale`) | – | Tenant | – | – | Same delta; only admitted locale operations through Tenant root |
| `TenantSetting` (`setting`) | – | Tenant | – | – | Same delta; only whole tenant-wide `branding.theme` |

`Tenant` has no `delete`: deprovisioning has no owning phase, and
[Database Standards § GRANT matrix](../../standards/05-database.md) records that
the widening it needs is an ADR's to make, not a migration's.

## Bounded human administration contract

**Accepted; new requests, policy/catalogue bindings and proof pending P03-3.**
All human writes require current Tenant capability, exact orgless context and
fresh target/delegation/version checks under the canonical fences. The Tenant
comes from resolved context; OrganizationId is a resource selector, not authority.
Org Admin receives none of these three pairs. Its own-organization settings and
Organization editor capabilities require a separate Phase 06 decision/consumer.

| Human request | Exact Tenant capability | Root |
|---|---|---|
| `CreateManagedOrganizationCommand` | `tenancy.organization.admin` | Organization |
| `RenameOrganizationCommand` | Same | Organization |
| `ArchiveOrganizationCommand` | Same | Organization |
| `SelectDefaultOrganizationCommand` | Same | Tenant; Active/nondeleted selected Organization must belong to it |
| `AddManagedTenantLocaleCommand` | `tenancy.locale.write` | Tenant |
| `SetManagedDefaultTenantLocaleCommand` | Same | Tenant |
| `SetTenantLocaleEnabledCommand` | Same | Tenant |
| `RemoveTenantLocaleCommand` | Same | Tenant |
| `SetManagedTenantBrandingCommand` | `tenancy.setting.write` | TenantSetting |

These requests share module business logic with the existing Seed acts without
dispatching them or obtaining Seed provenance. `TenancyOperationPolicySource`
classifies each human request separately; [audit](audit.md#bounded-administration-bindings)
owns the exact event/class bindings. Organization reads require the same Tenant
administration capability and MUST configuration-read audit; reads do not imply
an orgless mutation. Default selection writes only Tenant's reference/version,
not the target Organization. Locale writes capture Tenant's contained navigation.

The new `AdoptSeedAdministrationOriginCommand` is instead a closed Seed-only act,
one Tenant per invocation. It cannot be sent by a human wrapper or obtained from
the v2 grant. Its fixed mapping projection and Tenant marker share the announced
unit of work and two MUST intents. No general host-mapping route or permission
is introduced. The [module contract](README.md#warm-seed-and-administration-origins)
and [Standard 05](../../standards/05-database.md#p03-3-storage-and-replay-contract)
own exact witness/adoption/fence rules and write-free repeats.
