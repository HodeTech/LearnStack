# Phase 02c: Hub Integration — LearnStack side

> **Status (2026-05-21).** **P02c-0 — Repository bootstrap ✅**
> The sibling `learnstack-hub` git repository exists
> (GitHub: https://github.com/HodeTech/LearnStack-Hub). Backend solution
> (`LearnStack.Hub.slnx`, 7 core projects + 4 test projects) builds green;
> frontend pnpm monorepo (`apps/operator-portal` + `packages/{config,sdk,ui}`)
> typechecks, lints and builds; Hub CI (backend + frontend + meta + secret-scan)
> mirrors LearnStack's shape; documentation skeleton in place, including the
> `HUB-NNNN` Hub-internal ADR series. No Hub domain code.
>
> **Status (2026-08-08).** The Hub plan moved to the Hub repository. `P02c-1`
> through `P02c-7` — Hub domain core, Hub-side internal API, operator portal,
> custom-domain lifecycle, licence keys and the Hub exit gate — are now planned,
> tracked and shipped in `../LearnStack-Hub/docs/roadmap/`, one document per packet.
> The `P02c-N` identifiers are unchanged; they are load-bearing across both
> repositories. This document is no longer a status mirror. It carries **only the
> LearnStack-side work**, which is the half that lands in this repository.
>
> **`P02c-1` on the Hub side shipped on 2026-08-09**, after a review against the
> restructured corpus found that its domain code conflicts with none of
> ADR-0033/0034/0035 — its entitlement wire shape already carries `grace_until`
> and `generation`, it hosts no endpoint, and its audit behavior is a shell.
>
> **The Hub track is frozen from `P02c-2` onward**, on this phase's trigger — see
> [Goal](#goal). That is where the contract surface ADR-0034 redrew actually gets
> built, so it is the packet the freeze protects; holding P02c-1's merged artefact
> would have bought nothing and cost a SharedKernel reconciliation that grows with
> every packet on top. The Hub repository's `p02c-1-hub-domain-core.md` records
> the same state.

## Goal

Build LearnStack's side of the LearnStack ↔ Hub boundary: the adapters that read
entitlement from a control plane, the internal-API handlers that a control plane calls,
and the projection tables both write into.

**This phase hangs off the spine; it never sits in it.** LearnStack runs on
`NullEntitlementProvider` — every feature enabled, no limits — from
[Phase 02a Packet 9](phase-02a-kernel-tenancy.md) onward, and every phase from
[Phase 02d](phase-02d-walking-skeleton.md) through [Phase 11](phase-11-production-hardening.md)
works without a Hub. Nothing in the product spine waits on this document.

**The trigger is written down.**
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md) gates the Hub-backed
`IEntitlementProvider` on a single observable condition:

> **A tenant must be billed or plan-gated.**

Until that is true, a control plane adds a second repository, a second deployment, an
mTLS certificate chain, and a network dependency in front of a feature-flag read — in
exchange for gating features that nobody is paying differently for. When it becomes true,
this phase starts, and the Hub-side `P02c-1` branch unfreezes with it.

Everything Hub-side — `Plan`, `HubSubscription`, `Entitlement`, `LicenseKey`,
`CustomDomain`, `CompliancePolicy`, the `learnstack-hub` Keycloak realm, the operator
portal, the DNS/ACME challenge runner, and Hub's own observability — is planned and built
in the Hub repository under `../LearnStack-Hub/docs/roadmap/`. Do not restate it here; a
second copy of a plan is a plan that will be wrong.

Decisions this phase implements:

- [ADR-0034 Hub Contract Surface Invariant](../decisions/0034-hub-contract-surface-invariant.md)
  — the two invariants, the real endpoint set, the normative entitlement read path, and
  the rule that host resolution never calls the Hub. **This ADR carries the endpoint
  table; it is not repeated below.**
- [ADR-0035 Demand-Gated Infrastructure](../decisions/0035-demand-gated-infrastructure.md)
  — the trigger above, and the reason APISIX and Dapr are not prerequisites for it.
- [ADR-0019 LearnStack Hub](../decisions/0019-learnstack-hub.md) — separate repository,
  mTLS-guarded internal API. Its "closed at four endpoints" rule is replaced by ADR-0034.
- [ADR-0021 Feature-Based Entitlement](../decisions/0021-feature-based-entitlement.md) —
  the projection contract and the grace window.
- [ADR-0020 Triple Deployment + Hybrid License](../decisions/0020-triple-deployment-hybrid-license.md)
  — the three `IEntitlementProvider` implementations.
- [ADR-0022 Custom Domain + TLS](../decisions/0022-custom-domain-tls.md), as amended by
  ADR-0034: certificate material leaves the entitlement payload.

## Scope

Everything below lands in **this** repository. Each item has a Hub-side counterpart
tracked in `../LearnStack-Hub/docs/roadmap/`.

### `HubEntitlementProvider`

The Hub-backed implementation of `IEntitlementProvider`, in
`LearnStack.Infrastructure.Hub`. Its read path is **normative** and fixed by
[ADR-0034](../decisions/0034-hub-contract-surface-invariant.md):

```text
L1 in-process cache
  → L2 distributed cache (ICacheService)
    → platform_entitlement_cache   (durable; carries valid_until and grace_until)
      → Hub  (POST /api/v1/internal/license/verify)
```

The ordering is the design, not an optimisation:

- **`platform_entitlement_cache` sits between the caches and the network.** It is durable
  and it carries `grace_until`, so a Hub outage on a cold cache degrades to the last known
  projection within its grace window instead of reaching the network on every feature
  check. Collapsing the grace window into a cache TTL — which is what the corpus described
  before ADR-0034 — means a process restart during a Hub outage loses the grace period
  entirely.
- **Cold start has a defined answer.** A brand-new instance with empty L1 and L2 and no
  cached row for the tenant does not throw out of a feature-flag check and does not block
  the request. It resolves against the per-key-class policy below and records the
  degradation.
- **Each feature-key class declares fail-open or fail-closed explicitly**, in the key
  registry, not at the call site. Presentation and convenience keys fail open — a
  temporarily visible tab is cheaper than a broken page. Keys that gate paid capacity,
  data retention, or compliance behaviour fail closed. A key with no declared class does
  not compile.
- **Writes go through `IEntitlementProvider.RefreshAsync` only.** No module reads or
  writes `platform_entitlement_cache` directly
  (`Modules_Do_Not_Read_Entitlement_Cache_Directly`).

`NullEntitlementProvider` stops being registrable outside `Development` once this phase
lands (`NullEntitlementProvider_NotRegistered_OutsideDevelopment`).

### `entitlement-v1.schema.json`

The projection's wire shape is pinned by a **checked-in JSON Schema** in this repository,
with an identical copy in the Hub repository, and a **snapshot test in each**. Two
services that agree on a payload only in prose disagree on it within one release; the
schema is the artefact that makes a breaking change fail a build instead of a production
tenant.

- The schema covers the feature set, the limit set, the compliance caps, `valid_until`
  and `grace_until`.
- The LearnStack-side snapshot test asserts that the serialized shape the handler accepts
  still matches the schema, and that every declared feature key resolves to a registered
  `FeatureKey` / `LimitKey`.
- Versioning is in the filename. `entitlement-v2.schema.json` is a new file and a new
  ADR-gated contract change, not an edit.

### `IUsageReporter`

The outbound adapter for `POST /api/v1/usage/report`. Usage reporting is idempotent and
best-effort: a dropped usage report is a billing-accuracy problem the Hub reconciles,
never a reason to fail a tenant's request.

- Reports are enqueued through the outbox path built in
  [Phase 02b](phase-02b-events-auth.md) rather than sent inline, so a Hub outage produces
  a dispatch backlog instead of latency on the operation being metered.
- The adapter is the **only** type holding a usage-reporting Hub client
  (`Hub_Client_Referenced_Only_By_Named_Adapters`).

### Internal-API handlers LearnStack hosts

LearnStack implements the **Hub → LearnStack** half of the endpoint set enumerated in
[ADR-0034 § The endpoint set](../decisions/0034-hub-contract-surface-invariant.md) —
tenant create, entitlement push, status change, termination, usage pull, and host-mapping
push. The table lives in the ADR and is not duplicated here; both repositories read the
same list.

Each handler:

- Runs on the **internal listener only** and is unreachable from the public surface
  (`Internal_API_Endpoints_AreNot_Public`).
- Enforces the full auth chain unchanged from
  [ADR-0019](../decisions/0019-learnstack-hub.md): mTLS with a LearnStack-internal CA,
  an RS256 JWT with `aud=learnstack-internal` and a five-minute expiry, an HMAC-SHA256
  body signature, and replay protection on `jti`. Failing any one layer is a rejection.
- Rejects `learnstack` realm tokens outright. The tenant-facing surface likewise rejects
  `learnstack-hub` realm tokens. The two realms never cross.
- Runs under `HubCorrelationMiddleware`, which populates `ITenantContextAccessor` for the
  request so that audit, tracing and Row Level Security behave exactly as they do on the
  tenant-facing path.
- Writes a MUST-class audit entry through
  [ADR-0033](../decisions/0033-audit-durability-model.md)'s durable path. An operator
  action against a tenant is precisely the class of event that must not be lost.

### The internal route, and why APISIX is not a prerequisite

[ADR-0015](../decisions/0015-api-gateway-apisix.md) stands: when LearnStack needs an edge
gateway it uses APISIX. [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) gates
the adapter on a non-development deployment needing edge rate limiting, host routing, or
JWT pre-validation — and lands it in [Phase 11](phase-11-production-hardening.md).

Until then the internal API is exposed as a **separate ASP.NET Core listener binding** —
its own port, its own certificate requirement, its own endpoint filter — rather than an
APISIX route. The security properties are the same and they are enforced in code, which
is where `Internal_API_Endpoints_AreNot_Public` can see them. When APISIX arrives, the
route configuration is added in front of a listener that already refuses anything the
route would have refused.

Two corrections land with the route configuration when it is written, both recorded in
[API Gateway](../architecture/30-api-gateway.md): the internal route carries no
`client_secret_ref`, and the route-priority ordering must not let a public route fall
through to the authenticated catch-all.

### Entitlement invalidation

The Hub publishes `learnstack.hub.entitlement` when a tenant's projection changes, so a
plan flip reaches `IFeatureFlags.IsEnabledAsync` in seconds rather than at the next TTL
expiry.

- The **consumer** ships here, as an `IIntegrationEventHandler<T>` using the same inbox
  guard and tenant-context restoration as every other consumer
  ([Phase 02b](phase-02b-events-auth.md)).
- The **transport** is in-process until [Phase 11](phase-11-production-hardening.md), per
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md). Cross-process delivery
  through Dapr and Kafka arrives with the same trigger as everything else on that path —
  a second process needing to consume an integration event, which a Hub in a separate
  deployment is.
- Until the cross-process transport exists, an entitlement push arrives through the HTTP
  handler (`PUT /api/internal/tenants/{id}/entitlements`), which calls `RefreshAsync`
  directly. The eager-invalidation event is the optimisation; the HTTP push is the
  contract.
- The TTLs are the safety net, not the refresh mechanism: L1 60s, L2 15 minutes as an
  upper bound.

### `platform_host_to_tenant` mirroring

Host mappings arrive on their own endpoint, `PUT /api/internal/tenants/{id}/host-mappings`,
and are mirrored into `platform_host_to_tenant`.

One row is **not** pushed: the tenant's own platform subdomain.
`POST /api/internal/tenants` seeds `{slug}.{platform-domain}` into
`platform_host_to_tenant` in the same transaction as the tenant row and the default
organization. It needs no DNS verification and rides the platform wildcard
certificate, so making it wait on the custom-domain path would leave every
Hub-provisioned tenant unreachable at the URL the Hub redirects it to — and
[Standards 20](../standards/20-infrastructure-stack.md) makes an unknown host a 404,
not a Hub lookup.

Two rules from [ADR-0034](../decisions/0034-hub-contract-surface-invariant.md) are
load-bearing here:

- **TLS certificate material never travels in this payload, or in the entitlement
  payload.** The entitlement projection is cached, logged, audited and mirrored; a private
  key in it is a private key in all four. Certificates move between the Hub-owned and
  LearnStack-owned secret stores by secret-store replication, and the host-mapping payload
  references them **by path, not by value**.
- **Host resolution never calls the Hub.** `IHostToTenantResolver` reads
  `platform_host_to_tenant` and nothing else; `IHubClient.LookupHostAsync` does not exist.
  An anonymous page load on a tenant's marketing site must not depend on a control plane
  being reachable, and the resolver shipped in
  [Phase 02a Packet 7](phase-02a-kernel-tenancy.md) is already written this way.

### Architecture tests

- `LearnStack_Modules_DoNotReference_Hub` — no module assembly holds a Hub client or URL.
- `Hub_Client_Referenced_Only_By_Named_Adapters` — only `IEntitlementProvider`,
  `IUsageReporter` and `IHubTenantSync` implementations may.
- `Internal_API_Endpoints_AreNot_Public` — `/api/internal/*` is not reachable from the
  tenant-facing surface.
- `IEntitlementProvider_Implementations_Are_Three` — Null, Hub-backed, signed licence key;
  no fourth appears without an ADR.
- `NullEntitlementProvider_NotRegistered_OutsideDevelopment` — becomes enforceable the
  moment the Hub-backed implementation exists.
- `Modules_Do_Not_Read_Entitlement_Cache_Directly` — `platform_entitlement_cache` is
  read-only to modules, through `IFeatureFlags`.

`Hub_NeverStores_TenantData` is the Hub-side invariant and is asserted in the Hub
repository, against the Hub schema. Its LearnStack-side counterpart is this list.

### Explicitly not in this phase

| Capability | Owner |
|---|---|
| `Plan`, `HubSubscription`, `Entitlement`, `LicenseKey`, `CustomDomain`, `CompliancePolicy` aggregates | Hub repository, `P02c-1` |
| Hub-side internal API and outbound `LearnStackApiClient` | Hub repository, `P02c-2` |
| `learnstack-hub` Keycloak realm, operator portal, operator MFA | Hub repository, `P02c-4` |
| DNS-01 / HTTP-01 challenge runner, ACME provider adapter | Hub repository, `P02c-5` |
| Licence-key issuance and the `.lic` file format | Hub repository, `P02c-6` |
| `SignedLicenseKeyEntitlementProvider` (LearnStack side) | Skeleton: Hub repository `P02c-6`, as a coordinated pull request into this repository. Operational hardening: [Phase 11](phase-11-production-hardening.md), on a signed Self-Hosted contract ([ADR-0035](../decisions/0035-demand-gated-infrastructure.md)) |
| APISIX internal route configuration | [Phase 11](phase-11-production-hardening.md) |
| Dapr / Kafka transport for `learnstack.hub.entitlement` | [Phase 11](phase-11-production-hardening.md) |
| Custom-domain TLS automation end to end | [Phase 11](phase-11-production-hardening.md) |
| Hub billing, invoicing, dunning | [Phase 09b](phase-09b-hub-billing.md) → Hub repository |
| Hub marketplace | [Phase 12](phase-12-hub-marketplace.md) → Hub repository |

## Deliverables

- `HubEntitlementProvider` implementing the ADR-0034 read path, with cold-start fallback
  and per-key-class fail-open / fail-closed policy resolved from the key registry.
- `IUsageReporter` adapter dispatching through the outbox.
- The Hub → LearnStack internal-API handlers, on an internal-only listener, behind the
  full mTLS + RS256 JWT + HMAC + replay-protection chain.
- `entitlement-v1.schema.json` checked in, with a snapshot test asserting the accepted
  payload shape and that every key resolves in the registry.
- An `IIntegrationEventHandler<T>` for `learnstack.hub.entitlement`, in-process until
  Phase 11.
- `IHubTenantSync` — the `PUT /api/internal/tenants/{id}/host-mappings` handler and its
  `platform_host_to_tenant` mirroring, with certificate material referenced by
  secret-store path rather than carried by value.
- Audit coverage for every internal-API handler, MUST-class and durable.
- The six architecture tests above, green in CI.
- A `Development`-mode integration suite that runs the whole path against a Hub test
  double, so the LearnStack side is testable without a Hub deployment.

## Completion Criteria

- A tenant created through `POST /api/internal/tenants` exists in LearnStack with its
  default organization, resolves at `{slug}.{platform-domain}` through
  `platform_host_to_tenant`, and its entitlement projection is populated.
- An entitlement push through `PUT /api/internal/tenants/{id}/entitlements` changes what
  `IFeatureFlags.IsEnabledAsync` returns for that tenant within seconds.
- With the Hub unreachable and L1 and L2 cold, a feature check resolves from
  `platform_entitlement_cache` inside its `grace_until` window, returns a value, and does
  not throw.
- Past `grace_until` with the Hub still unreachable, fail-open keys stay enabled and
  fail-closed keys are refused — each according to its declared class, and each recorded.
- A request missing any one of mTLS, the signed JWT, or the HMAC body signature is
  rejected, and a replayed `jti` is rejected.
- An `/api/internal/*` request bearing a `learnstack` realm token is rejected; a
  tenant-facing request bearing a `learnstack-hub` realm token is rejected.
- A host-mapping push resolves the new host to the right tenant through
  `platform_host_to_tenant`, with the Hub then taken offline — host resolution is
  unaffected.
- No TLS private key appears in `platform_entitlement_cache`, in any log line, or in any
  audit row. The schema makes this checkable rather than a matter of trust.
- The `entitlement-v1.schema.json` snapshot tests are green in **both** repositories at
  the same commit pair.
- Every architecture test listed above is green.

## Risks

- **Two-repository contract drift.** Two teams, two release cadences, one payload.
  Mitigated by the checked-in schema and the paired snapshot tests, by ADR-0034 as the
  single endpoint authority for both repositories, and by the coordination protocol below.
- **The Hub creeps onto the hot path.** The failure this phase most needs to prevent is a
  Hub outage taking anonymous tenant pages down. Mitigated structurally: host resolution
  never calls the Hub, and the entitlement read path reaches the network only after a
  durable local row has been consulted.
- **Grace collapses into a TTL.** A cache TTL and a grace window look interchangeable
  until a process restarts during an outage. `platform_entitlement_cache` is durable and
  carries `grace_until` for exactly that case; a review that lets grace live only in L2 has
  reintroduced the defect ADR-0034 removed.
- **Fail-open by default.** A key whose class was never declared behaves as whatever the
  first implementation happened to do. The registry rejects an undeclared class at compile
  time; reviewers check that paid capacity and compliance keys are on the closed side.
- **~~The frozen Hub branch rots.~~ Discharged 2026-08-09 by merging it.**
  `feat/phase-02c-packet-1-hub-domain-core` was 221 files against a moving base. The
  review that preceded the merge found the rot was the only certain cost: the code
  conflicts with none of ADR-0033/0034/0035, so holding it bought no safety. What it did
  leave is a SharedKernel reconciliation against
  [Packet 3b](phase-02a-kernel-tenancy.md), tracked on the Hub side, which does grow with
  every packet built on top.
- **mTLS certificate and HMAC secret rotation.** Mitigated by a dual-key window during
  rotation and a documented cadence; both are Phase 11 operational work and are named
  there.
- **Operator abuse.** Mitigated on the Hub side by mandatory MFA, and on this side by
  auditing every internal-API mutation with the operator as actor.

### Cross-repository PR coordination

A contract change is two pull requests in two repositories, and the order is not
negotiable:

1. **The Hub PR merges first**, carrying the Hub-side handler or client and the updated
   `entitlement-v1.schema.json`.
2. **The LearnStack PR references that Hub commit SHA** in its description, carries the
   identical schema file, and merges after it.
3. **Both snapshot tests are green at the resulting commit pair** before either side is
   deployed. A red snapshot on either side blocks the deployment of both.

A contract change that reaches only one repository is an outage waiting for the next
deploy. The Hub repository's `p02c-3-learnstack-integration.md` states the same protocol
from the other side; the two documents are deliberately symmetric.

## Phase Exit Decision

Phase 02c is complete when the SaaS deployment mode is exercisable end to end — an
operator creates a tenant on the Hub, the tenant appears in LearnStack with its default
organization and entitlement projection, a plan change reaches the tenant's feature flags,
a host mapping resolves, and a Hub outage degrades billing and provisioning without
touching public pages.

This phase gates nothing else. [Phase 03](phase-03-identity-admin.md) and everything after
it proceed on `NullEntitlementProvider` regardless of when Phase 02c starts or finishes.
The Self-Hosted mode's exit — signed `.lic` issuance and
`SignedLicenseKeyEntitlementProvider` — belongs to
[Phase 11](phase-11-production-hardening.md) and its own trigger, a signed Self-Hosted
contract.
