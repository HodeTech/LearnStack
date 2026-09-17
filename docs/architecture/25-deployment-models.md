# Deployment Models

**Derives from:** [ADR-0020](../decisions/0020-triple-deployment-hybrid-license.md),
[ADR-0019](../decisions/0019-learnstack-hub.md),
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md).

LearnStack targets three deployment models from **one codebase, one Helm chart, one set
of container images**. The differentiator across modes is configuration + component
wiring + `IEntitlementProvider` implementation choice — never application code.

> **Support state (2026-09-17).** The `DeploymentMode` enum has five values and the
> composition root branches on all five. **`Development`** and **`SaaS`** have wired
> and tested foundation paths; this is not a production-readiness claim. Entitlements
> still use `NullEntitlementProvider` in every mode. `Dedicated`, `SelfHostedOnline`
> and `SelfHostedAirGapped` are **prepared seams, not supported deployments**, until
> [Phase 11](../roadmap/phase-11-production-hardening.md) builds their adapters and
> integration suites, per
> [ADR-0035](../decisions/0035-demand-gated-infrastructure.md). See
> [§ 5](#5-deploymentmode-configuration) for what "prepared seam" means concretely. The
> rest of this document describes target topologies. The production Helm chart, image
> release pipeline, licence adapters and migration runbooks below are not shipped
> artifacts; their examples must not be treated as operational instructions.

## 1. The three modes

The diagram and comparison table describe target topologies and provider choices.
The [current support matrix](#supported-today-versus-prepared-seam) distinguishes them
from shipped foundation wiring.

```mermaid
flowchart TB
    subgraph SaaS["SaaS — LearnStack hosted, many tenants"]
        SaaSLS["LearnStack.Host pods<br/>(shared, multi-tenant)"]
        SaaSHub["LearnStack Hub<br/>(LearnStack hosted)"]
        SaaSDB[("Shared Postgres,<br/>RLS-isolated rows<br/>per tenant")]
    end

    subgraph Dedicated["Dedicated — LearnStack hosted, single tenant"]
        DedLS["LearnStack.Host pods<br/>(dedicated to customer)"]
        DedHub["LearnStack Hub<br/>(LearnStack hosted)"]
        DedDB[("Dedicated Postgres<br/>per customer")]
    end

    subgraph Self["Self-Hosted — Customer hosted"]
        SelfLS["LearnStack.Host pods<br/>(customer infra)"]
        SelfHub["LearnStack Hub<br/>(optional: LearnStack hosted OR customer)"]
        SelfDB[("Customer Postgres")]
    end

    SaaSLS --> SaaSDB
    DedLS --> DedDB
    SelfLS --> SelfDB
    SaaSLS -. phone-home .-> SaaSHub
    DedLS -. phone-home .-> DedHub
    SelfLS -. phone-home or signed key .-> SelfHub
```

| Aspect | SaaS | Dedicated | Self-Hosted |
|--------|------|-----------|-------------|
| **Operator** | LearnStack | LearnStack | Customer |
| **Infrastructure** | LearnStack cloud | LearnStack cloud, customer-isolated | Customer's K8s / VMs |
| **Tenants per instance** | Many | One | One (or many, customer's choice) |
| **Database** | Shared Postgres, RLS-isolated | Dedicated Postgres per customer | Customer-owned Postgres |
| **Hub** | LearnStack-hosted central Hub | LearnStack-hosted central Hub | Customer-hosted OR LearnStack-hosted (with phone-home) OR signed license key (air-gapped) |
| **Entitlement** | `HubEntitlementProvider` (online) | `HubEntitlementProvider` (online) | `HubEntitlementProvider` (online) OR `SignedLicenseKeyEntitlementProvider` (air-gapped) |
| **Connectivity** | Always online | Always online | Online preferred; air-gapped supported |
| **Plan** | Starter / Growth / Scale | Enterprise (custom) | Enterprise (custom) |
| **Upgrade cadence** | LearnStack-controlled rolling | LearnStack-coordinated per customer | Customer-controlled (Helm upgrade window) |
| **Data residency** | Region per LearnStack offering | Customer-selected region | Customer-owned data sovereignty |
| **Compliance posture** | LearnStack-defined caps | Customer-negotiated caps | Customer-defined caps |

## 2. SaaS mode

**Topology:**

```
Internet
   │
   ▼
[ APISIX gateway pool (2+ replicas) ]
   │
   ▼ host: app.learnstack.dev | {tenant-custom-domain}
[ LearnStack.Host pods (auto-scaled 2-8) ]
   │
   ├──► PostgreSQL (managed, RLS policies enforced)
   ├──► Valkey (managed, multi-tenant via key prefix)
   ├──► SeaweedFS (multi-tenant via key prefix)
   ├──► Meilisearch (per-tenant filter + per-locale index)
   ├──► Keycloak (single realm with multi-tenant claims)
   ├──► LiveKit (shared SFU pool)
   └──► Dapr sidecars

   ▼ (separate gateway path) host: hub.learnstack.dev
[ LearnStack Hub pods ]
   │
   └──► Hub Postgres (separate from tenant DB)
```

- **Tenant onboarding**: Customer signs up at `hub.learnstack.dev`, picks plan, Stripe
  checkout, Hub provisions tenant in LearnStack via `POST /api/internal/tenants`. Total
  time: < 1 minute.
- **Tenant URL**: Starts on `{slug}.learnstack.app`; upgrades to custom domain on
  Growth+ tier.
- **Failure isolation**: two different problems, and only one of them is solved today.
  **Correctness** isolation — no tenant can read or write another tenant's rows — is
  covered by tenant context + EF query filters + Row Level Security + architecture tests
  ([ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md)).
  **Resource** isolation — no tenant can starve another of connections, CPU, or job
  workers — is **not** covered by any of those. Row Level Security is a visibility
  predicate: it filters rows, and a filtered query consumes exactly as much of the
  database as an unfiltered one. A single tenant running an unbounded report can hold a
  connection, saturate the pool, and degrade every other tenant on the instance while
  every isolation test stays green.

  What exists today: the API's own anonymous limiter, which runs before host
  classification and partitions every request on its socket peer
  ([API Standards § Request and Response Limits](../standards/04-api-design.md#request-and-response-limits)).
  APISIX's `limit-req`, keyed on `remote_addr`, fronts the API only once the gateway
  lands, which [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) gates to
  [Phase 11](../roadmap/phase-11-production-hardening.md). Each keys on the client, so
  each throttles a noisy client but not a noisy tenant — one tenant behind many IPs is
  unaffected, and many tenants behind one NAT are punished together. A server-side
  renderer is such a NAT: every visitor it renders for, of every tenant, reaches the API
  from one peer.

  > **Open in Phase 02d.** How the anonymous limiter treats a request arriving over the
  > trusted hop is G34 in
  > [Phase 02d's decision register](../roadmap/phase-02d-walking-skeleton.md#the-decision-register).
  > It is answered in the decision pass of the packet that ships the server-rendering
  > path, and that pass edits this paragraph with its answer.

  What is required, and where it lives: **resource fairness is
  [Phase 11](../roadmap/phase-11-production-hardening.md)** — `statement_timeout` per
  connection class, per-tenant connection-pool partitioning, query cost ceilings on
  report and export paths, Hangfire queue fairness so one tenant's bulk import cannot
  monopolise the workers, and per-tenant rate limiting keyed on the resolved tenant
  rather than on the client address. No earlier phase owns this work, and until Phase 11
  ships it, SaaS multi-tenancy is protected against **leakage** but not against
  **contention**.

## 3. Dedicated mode

**Topology** — same as SaaS but isolated per customer:

```
[ LearnStack-managed Kubernetes namespace per customer ]
   │
   ├──► Dedicated PostgreSQL instance (no shared rows; still RLS-protected for org scope)
   ├──► Dedicated Valkey instance
   ├──► Dedicated SeaweedFS instance
   ├──► Dedicated Meilisearch
   ├──► Dedicated Keycloak realm (could be in shared Keycloak cluster with realm-per-tenant)
   ├──► Shared LiveKit pool (with per-tenant resource caps) OR dedicated SFU
   └──► Dapr sidecars
```

- **Tenant**: Exactly one. Domain model still includes `Tenant` aggregate with a default
  organization; tenant_id is fixed.
- **Use case**: Mid-to-large customers with data residency requirements, regulatory
  isolation, or volume that justifies dedicated infra.
- **Hub**: Same central LearnStack Hub manages the dedicated tenant; phone-home as
  normal.

## 4. Self-Hosted mode

**Topology**:

```
[ Customer-owned Kubernetes cluster, customer admin ]
   │
   ├──► PostgreSQL (customer-managed)
   ├──► Valkey (customer-managed)
   ├──► SeaweedFS or S3-compatible (customer-managed)
   ├──► Meilisearch (customer-managed)
   ├──► Keycloak (customer-managed; LearnStack ships pre-configured realm export)
   ├──► LiveKit (customer-managed OR fall back to LiveKit Cloud)
   ├──► Dapr sidecars (deployed by LearnStack Helm chart)
   └──► Optional: customer-hosted LearnStack Hub (rare; usually they consume LearnStack-
        hosted Hub via phone-home, or use signed license key air-gapped)
```

### 4a. Self-Hosted Online

- Customer's deployment phones home to LearnStack's Hub daily.
- `HubEntitlementProvider` registered; `Entitlement` projection refreshed via
  `POST /api/v1/internal/license/refresh`.
- 30-day grace period on phone-home failure.

### 4b. Self-Hosted Air-Gapped

- Customer's deployment has no outbound network access.
- `SignedLicenseKeyEntitlementProvider` registered; reads RSA-signed license key from a
  filesystem location (`/var/learnstack/license/current.lic`).
- License key embeds the entitlement projection; the key itself is the entitlement
  source.
- License updates via signed-file delivery (USB stick, secure email, customer's SFTP).
- SIGHUP to the LearnStack process triggers immediate re-read of the license file.
- Revocation list also delivered out-of-band when revocations occur.

**Tenant provisioning without Hub.** Hub is the canonical issuer of `tenant_id`
([28-platform-tenant-organization.md § Hub ↔ LearnStack ownership matrix](28-platform-tenant-organization.md)).
In air-gapped Self-Hosted mode there is no Hub at the customer's site, so the
**bootstrap rule** is:

1. The signed `.lic` file carries `tenant_id` (and a default `organization_id`) as
   claims. The LearnStack-side CLI (`learnstack tenant init`) reads the `.lic`,
   verifies the signature, and creates the `tenants` + `organizations` rows with
   the IDs from the license claims.
2. Subsequent tenants (rare in air-gapped enterprise) require a new `.lic`
   issued by the LearnStack-hosted Hub and delivered out-of-band — the customer
   never mints `tenant_id` themselves.
3. In `SelfHostedOnline` mode the CLI exists too, but defers to Hub via
   `POST /api/internal/tenants` so Hub remains authoritative.

Customer responsibilities (air-gapped):
- Customer manages their own TLS certs (cert-manager + local CA OR
  customer-provided certs).
- Customer manages their own Vault (or alternative secret store).
- Customer manages their own backups, DR, monitoring.

Target LearnStack deliverables for the supported Phase 11 air-gapped release:
- Helm chart with air-gapped mode pre-configured.
- Pre-signed license keys per agreement.
- Documentation runbook (`docs/operations/hub-on-prem-setup.md`).
- Optional: bundled container registry mirror script for air-gapped image pulls.

## 5. `DeploymentMode` configuration

The LearnStack process reads `Deployment:Mode` from configuration at startup. Five
values exist; the provider comments below describe the target, not today's registration:

```csharp
public enum DeploymentMode
{
    Development,           // Local dev; NullEntitlementProvider
    SaaS,                  // LearnStack-hosted multi-tenant; HubEntitlementProvider
    Dedicated,             // LearnStack-hosted single-tenant; HubEntitlementProvider
    SelfHostedOnline,      // Customer-hosted, phone-home enabled; HubEntitlementProvider
    SelfHostedAirGapped    // Customer-hosted, no phone-home; SignedLicenseKeyEntitlementProvider
}
```

The composition root uses this value for mode-specific wiring. Entitlements currently
use `NullEntitlementProvider` in every mode; the provider choices shown above are
ADR-0020's target. Modules never read the enum —
`Modules_Do_Not_Reference_DeploymentMode` enforces that the composition root owns the
selection.

### Supported today versus prepared seam

| Value | State | What that means concretely |
|---|---|---|
| `Development` | **Supported foundation** | Foundation host-boot path covered by the integration suite |
| `SaaS` | **Supported foundation** | Foundation host-boot path covered by the integration suite; real Hub entitlement integration belongs to [Phase 02c](../roadmap/phase-02c-hub-foundation.md) and has not shipped |
| `Dedicated` | Prepared seam | Shares SaaS's Sentry error tracker and refuses startup without a Sentry DSN; uses the default entitlement provider; no dedicated-topology integration suite or operational runbook |
| `SelfHostedOnline` | Prepared seam | Uses Sentry when a DSN is supplied and NoOp error tracking otherwise; uses the default entitlement provider; real phone-home and its failure-mode tests are absent |
| `SelfHostedAirGapped` | Prepared seam | Local exception-file capture and suppression of network OTLP exporters exist; the signed-licence adapter, full telemetry file exporters and no-egress operational suite are not shipped |

A prepared seam accepts the value and supplies foundation wiring, including some
mode-specific behavior, but lacks the adapters and operational proofs needed for a
supported deployment. The entitlement provider remains the same default used in
`Development`. ADR-0035's "wired end to end" statement is read together with its
explicit default-provider and adapter-trigger table: it does not claim Hub entitlement
integration or full production readiness. It is honest to design for a seam; it is not
honest to sell it. Each seam becomes a supported mode in
[Phase 11](../roadmap/phase-11-production-hardening.md) when
its trigger fires — for `SelfHostedAirGapped`, that trigger is a signed Self-Hosted
contract ([ADR-0035](../decisions/0035-demand-gated-infrastructure.md)).

The same ADR adds a rule that this document must obey: **a deployment mode without a
signed contract cannot be the deciding factor in a technical choice.** It may break a tie
between otherwise-equal options. It may not, on its own, reject a dependency or justify
an abstraction.

### Other config settings switched by mode

- **Hub URL** — target: LearnStack-hosted for SaaS / Dedicated / SelfHostedOnline,
  customer-hosted only where separately arranged, or absent for SelfHostedAirGapped.
  The current default entitlement provider makes no Hub call.
- **Secret store** — `ConfigurationSecretProvider` today in every mode; the Vault-backed
  provider is demand-gated to Phase 11 behind `ISecretProvider`, triggered when a
  production secret must rotate without a redeploy, or more than one operator needs
  access to production secrets.
- **Telemetry sink** — non-air-gapped modes currently use the configured OTLP endpoint;
  `SelfHostedAirGapped` wires no network exporter. The target operator is LearnStack
  for SaaS / Dedicated and the customer for Self-Hosted. Full telemetry file exporters
  for the air-gapped mode belong to Phase 11; local exception-file capture exists today.
- **Event transport** — `InProcessEventBus` today in every mode; the Dapr pub/sub
  component and its Kafka backend land in Phase 11, triggered by a second process needing
  to consume an integration event. See
  [15-event-and-outbox.md](15-event-and-outbox.md).

## 6. Same Helm chart

The target LearnStack Helm chart (`deploy/helm/`) configures all modes through
`values.yaml`.
The chart is not implemented yet; these are configuration sketches for Phase 11:

```yaml
# values-saas.yaml
deployment:
  mode: SaaS
  hub:
    url: https://hub.learnstack.dev
    # mTLS client cert + HMAC secret + JWT signing key; no API key (ADR-0034)
    credentialsRef: { name: hub-internal-api, key: bundle }
  postgres:
    managed: true
    connectionRef: { name: postgres-creds, key: connection-string }
  dapr:
    kafka:
      brokers: kafka-managed.example.com:9092
      auth: scram-sha-512
```

```yaml
# values-dedicated-acme-corp.yaml
deployment:
  mode: Dedicated
  hub:
    url: https://hub.learnstack.dev
  postgres:
    managed: true
    connectionRef: { name: postgres-acme, key: connection-string }
  customDomain:
    primary: learn.acmecorp.com
  dataResidency: eu-west
```

```yaml
# values-self-hosted-airgapped.yaml
deployment:
  mode: SelfHostedAirGapped
  hub: {}                       # no Hub
  licenseKey:
    path: /var/learnstack/license/current.lic
    revocationListPath: /var/learnstack/license/revocations.json
  postgres:
    managed: false
    customerProvidedSecret: postgres-creds
  imageRegistry: registry.customer.internal/learnstack
  telemetry:
    otlpEndpoint: otel-collector.customer.internal:4317
```

Same chart. Same templates. Different values.

## 7. Deployment-mode-conditional behaviour

A small list of behaviours change across modes. This is the **target** table.
`NullEntitlementProvider` remains registered in every mode today; Hub and signed-licence
adapters follow the owners and triggers in ADR-0035. The table does not claim those
adapters or the production deployment automation are already running.

| Behaviour | SaaS | Dedicated | Self-Hosted Online | Self-Hosted Air-Gapped |
|-----------|------|-----------|--------------------|------------------------|
| `IEntitlementProvider` | Hub | Hub | Hub | SignedLicenseKey |
| `ISecretProvider` (target) | Vault | Vault | Vault | Vault OR file |
| `ISecretProvider` (today) | `ConfigurationSecretProvider` in every mode — the Vault adapter is demand-gated to Phase 11 | ← | ← | ← |
| Phone-home enabled | Yes | Yes | Yes | No |
| Outbound HTTP to Hub | Yes | Yes | Yes | No |
| OTel collector endpoint | LearnStack-hosted | LearnStack-hosted | Customer-hosted | Customer-hosted |
| Cert provisioning | LearnStack-managed | LearnStack-managed | Hub-driven (online) | Customer-managed |
| Helm chart values file | `values-saas.yaml` | `values-dedicated-{customer}.yaml` | `values-self-hosted.yaml` | `values-self-hosted-airgapped.yaml` |

## 8. Migration between modes

Planned customer-driven paths require Phase 11's migration runbooks and restore proofs:

- **SaaS → Dedicated**: Hub-orchestrated. New dedicated cluster provisioned; customer's
  tenant data exported through a tenant-scoped export procedure, restored into dedicated
  Postgres; DNS cutover; tenant_id remains the same.
- **Dedicated → Self-Hosted**: Customer takes over operation. LearnStack provides
  customer with Helm chart values + Postgres dump + Keycloak realm export.
- **Self-Hosted → SaaS**: Reverse import. Less common; one-time engagement.

Cross-mode migration is designed as engineering-assisted, not automated. A consistent
data model is a prerequisite, not a proven export procedure. The runbook must account
for tenant-scoped rows, shared identity references, media and outstanding operations.
PostgreSQL's `pg_dump` does not apply row security by default; its
[`--enable-row-security` option](https://www.postgresql.org/docs/18/app-pgdump.html)
also has role and restore-format constraints. A shared Keycloak realm export is not a
tenant-scoped identity export or a
[complete backup](https://www.keycloak.org/server/importExport).

## 9. Single container image

The target release model runs all LearnStack environments on the same container image
(`learnstack/learnstack-api:<git-sha>`). Dapr sidecar image
(`daprio/daprd:<version>`) is also identical across modes.

Phase 11 owns the production image pipeline; today's CI does not publish these images.
The target pipeline builds one image per merge to `main`, tags it with the git sha and
a SemVer release tag when a release is cut, and references it from Helm by tag.
These image names are illustrative:

```
ghcr.io/learnstack/learnstack-api:v1.0.0
ghcr.io/learnstack/learnstack-api:v1.0.1
ghcr.io/learnstack/learnstack-hub-api:v1.0.0
ghcr.io/learnstack/learnstack-hub-api:v1.0.1
ghcr.io/learnstack/learnstack-web:v1.0.0
ghcr.io/learnstack/learnstack-hub-operator-portal:v1.0.0
```

For Self-Hosted Air-Gapped, the planned bundle process loads images into the customer's
own registry. `tools/airgapped-image-bundle.sh` names an intended Phase 11 artifact;
the script does not exist yet.

## 10. Release & upgrade cadence

Target operating policy, not an implemented deployment pipeline:

| Mode | Cadence | Trigger |
|------|---------|---------|
| SaaS | Continuous (every merge to main becomes a deployable build) | Automated GitOps; staged rollout: dev → staging → 10% prod → 100% prod |
| Dedicated | Coordinated per customer; typically weekly | LearnStack-led upgrade window |
| Self-Hosted Online | Customer-controlled | Customer schedules Helm upgrade; can lag SaaS by months |
| Self-Hosted Air-Gapped | Customer-controlled, signed-bundle delivery | LearnStack signs and delivers bundle; customer applies |

## 11. Operational runbooks (Phase 11)

To be authored:

- `docs/operations/tenant-operations.md` — Tenant provisioning (per mode), suspend,
  archive, terminate.
- `docs/operations/helm-installation.md` — Self-Hosted Helm chart installation guide.
- `docs/operations/hub-on-prem-setup.md` — Optional customer-hosted Hub OR signed license
  key air-gapped setup.
- `docs/operations/license-key-management.md` — RSA key generation, distribution,
  revocation.
- `docs/operations/migration-orchestration.md` — Schema migration coordination.
- `docs/operations/security-incident.md` — Security incident response.
- `docs/operations/data-export-and-portability.md` — GDPR data export per tenant.
- `docs/operations/resource-fairness.md` — the SaaS contention controls named in
  [§ 2](#2-saas-mode): `statement_timeout` classes, pool partitioning, query cost
  ceilings, Hangfire queue fairness, per-tenant rate limits.

## 12. Non-goals

- **Multi-cloud abstraction.** LearnStack runs on Kubernetes; cloud-provider choice is
  customer's. We don't abstract over cloud APIs.
- **Sovereign-cloud parity.** LearnStack ships in the SaaS configuration aimed at major
  cloud regions; sovereign-cloud (Gaia-X, …) is in scope only via Self-Hosted.
- **Tenant migration tooling beyond mode-change paths.** Tenant CRUD belongs to Hub; cross-
  cluster tenant migration is a one-time engineering engagement, not a product feature.

## References

- ADR-0020 — Triple Deployment + Hybrid License.
- ADR-0019 — LearnStack Hub.
- [ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md) — what
  Row Level Security does and does not guarantee.
- [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) — which modes are
  supported, which are seams, and what promotes a seam.
- [26-hybrid-license-model.md](26-hybrid-license-model.md) — license payload + lifecycle.
- [24-learnstack-hub.md](24-learnstack-hub.md) — Hub architecture.
- [04-technical-architecture.md](04-technical-architecture.md) — overall stack.
- [Phase 11: Production Hardening, Operations, and Scale](../roadmap/phase-11-production-hardening.md)
  — owns resource fairness and the three prepared seams.
- [ADR-0049 — Institution Sites and an Optional Course Marketplace](../decisions/0049-institution-sites-and-course-marketplace.md)
  — **Proposed** product direction; does not change this support matrix or authorize
  cross-installation marketplace participation.
