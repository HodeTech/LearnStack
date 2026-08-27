# Custom Domain & TLS Provisioning

**Derives from:** [ADR-0022](../decisions/0022-custom-domain-tls.md),
[ADR-0019](../decisions/0019-learnstack-hub.md),
[ADR-0021](../decisions/0021-feature-based-entitlement.md).

LearnStack tenants can run their platform under their own domain (e.g. `englishhero.com`,
`anatoliayoga.com`, `learn.bigcorp.com`). The admin flow is Hub-owned and automated end-
to-end: domain submission → DNS verification → TLS cert provisioning → tenant resolver
mapping → renewal.

Custom domain is a **plan-gated feature**: `FeatureKeys.CustomDomain` (ADR-0021); Starter
plan does not include it; Growth+ does.

## 1. Flow overview

```mermaid
sequenceDiagram
    participant Tenant as Tenant Admin
    participant Studio as LearnStack Admin Studio
    participant LSApi as LearnStack API
    participant HubAPI as Hub API
    participant HubJob as Hub DNS Verification Job
    participant DNS as Customer DNS provider
    participant LE as Let's Encrypt
    participant APISIX as APISIX gateway

    Tenant->>Studio: Enter domain "anatoliayoga.com"
    Studio->>LSApi: POST /api/v1/tenant/custom-domains (acts as proxy)
    LSApi->>HubAPI: POST /api/v1/internal/tenants/{id}/custom-domains<br/>(via IHubTenantSync; mTLS + JWT + HMAC)
    HubAPI->>HubAPI: Insert CustomDomain row, status=Pending
    HubAPI->>Studio: 201 + verification instructions<br/>(CNAME instructions)

    Tenant->>DNS: Add CNAME: anatoliayoga.com → edge.learnstack.app
    HubAPI->>HubJob: Enqueue verification job

    loop Every 60s, max 60 attempts
        HubJob->>HubJob: Resolve CNAME for anatoliayoga.com
        alt CNAME matches edge.learnstack.app
            HubJob->>HubJob: Status=Verifying
            HubJob->>LE: ACME order via DNS-01 (or HTTP-01 fallback)
            LE-->>HubJob: Cert issued
            HubJob->>HubAPI: Store cert + key in the Hub secret store;<br/>update CustomDomain Status=Active, cert_expires_at
            HubAPI->>HubAPI: Replicate cert + key to the LearnStack-side secret store<br/>(secret-store replication — never over HTTP payload)
            HubAPI->>HubAPI: Publish learnstack.hub.custom-domain.activated event
            HubAPI->>APISIX: Hot-reload route table partial<br/>(SNI entry referencing the secret BY PATH)
            HubAPI->>LSApi: PUT /api/internal/tenants/{id}/host-mappings<br/>(host to tenant/org mapping only — no key material)
        else CNAME mismatch
            HubJob->>HubJob: Increment attempt; retry in 60s
        end
    end

    HubAPI->>Studio: Webhook / poll: Status=Active
    Studio-->>Tenant: "Custom domain active"
```

## 2. Hub data model

```csharp
namespace LearnStack.Hub.Domain;

public sealed class CustomDomain : AuditableEntity<CustomDomainId>
{
    public TenantId TenantId { get; private set; }
    public string Domain { get; private set; }
    public bool IsPrimary { get; private set; }
    public CustomDomainStatus Status { get; private set; }
    public DnsChallengeType DnsChallengeType { get; private set; }

    public DateTimeOffset? VerifiedAt { get; private set; }
    public string? CertificateVaultKey { get; private set; }
    public DateTimeOffset? CertificateIssuedAt { get; private set; }
    public DateTimeOffset? CertificateExpiresAt { get; private set; }
    public DateTimeOffset? CertificateLastRenewedAt { get; private set; }

    public int VerificationAttempts { get; private set; }
    public string? LastVerificationError { get; private set; }

    // factory + methods omitted; see ADR-0022
}

public enum CustomDomainStatus { Pending, Verifying, Active, Failed, Revoked }
public enum DnsChallengeType { Dns01, Http01, CustomerProvided }
```

## 3. DNS verification

The verification job (Hangfire recurring or one-shot per submission) resolves the
customer's domain via DNS and checks for:

```
anatoliayoga.com.   IN   CNAME   edge.learnstack.app.
```

Both `CNAME` and `ALIAS` records accepted; A/AAAA pointing at our edge LB is also
accepted (when the customer can't use CNAME at the apex).

Max 60 attempts at 60s intervals (1 hour total). On failure, status → `Failed`; tenant
sees the error in Admin Studio and can retry submission.

## 4. Let's Encrypt integration

Two challenge types supported:

### 4a. DNS-01 (preferred)

Most reliable; works for wildcard certs; doesn't require HTTP exposure at verification
time.

```
1. LearnStack Hub starts ACME order with Let's Encrypt for "anatoliayoga.com"
2. ACME server returns DNS challenge: TXT _acme-challenge.anatoliayoga.com = <token>
3. Hub publishes TXT record via tenant's DNS provider API
   (supported providers: Cloudflare, Route 53, Google Cloud DNS, Azure DNS, ...)
   OR: surfaces the TXT record to the tenant in Admin Studio for manual placement
4. ACME server polls TXT record; once verified, returns cert
5. Hub stores cert+key in Vault: secret/learnstack-hub/certs/anatoliayoga.com
6. Hub removes TXT record (or instructs tenant to remove)
```

DNS provider API integration is **opt-in per provider**; LearnStack supports a curated set
(Cloudflare, Route 53, Google Cloud DNS, Azure DNS) initially. Other providers fall back
to manual TXT record placement (slower, requires tenant action).

### 4b. HTTP-01 (fallback)

Used when DNS-01 isn't available (no supported provider API, tenant can't delegate).

```
1. LearnStack Hub starts ACME order
2. ACME server returns HTTP challenge: place token at /.well-known/acme-challenge/<token>
3. Hub places token at the LearnStack edge's challenge endpoint
   (a dedicated route that responds with the challenge for the requested domain)
4. ACME server fetches the token via HTTP-01 to anatoliayoga.com
5. Cert issued; stored in Vault
```

HTTP-01 prerequisite: CNAME must already point at the edge. Bootstrap sequence: customer
CNAME points → Hub serves HTTP-01 challenge → cert issued → HTTPS starts working.

### 4c. Customer-provided cert (Self-Hosted only)

For air-gapped Self-Hosted deployments, the customer uploads their own TLS cert. Hub
stores in Vault (or customer manages locally outside Hub). Renewal is the customer's
responsibility.

## 5. APISIX hot-reload

When a domain is verified and the cert is in Vault, Hub writes to
`infra/apisix/partials/routes-custom-domains.yaml`:

```yaml
routes:
  - id: cd-anatoliayoga
    host: anatoliayoga.com
    uri: /*
    plugins:
      openid-connect: { ... }
      cors: { ... }
      limit-req: { ... }
      request-id: { include_in_response: true }
    upstream:
      type: roundrobin
      nodes: { learnstack-api:5000: 1 }

ssl:
  - id: ssl-anatoliayoga
    sni: anatoliayoga.com
    # Materialised from the LEARNSTACK-side secret store by the secret-agent sidecar.
    # The path is the replication target of the Hub-side secret; the value never
    # travels in an HTTP payload. See § 6.
    cert_ref: secret://learnstack/certs/anatoliayoga.com/cert
    key_ref:  secret://learnstack/certs/anatoliayoga.com/key
```

> **Phasing note.** APISIX itself is demand-gated to
> [Phase 11](../roadmap/phase-11-production-hardening.md) per
> [ADR-0035](../decisions/0035-demand-gated-infrastructure.md); until it arrives, host
> routing and TLS termination are the deployment's ingress concern and
> `platform_host_to_tenant` remains the sole authority for host → tenant resolution
> inside the application. The route-partial mechanism described here is the target
> design, not a running system.

APISIX watches the file (standalone mode); changes are picked up within seconds.

In Kubernetes, the file is in a ConfigMap; rolling update of the ConfigMap propagates;
APISIX hot-reloads on file change inside the pod.

Etcd-backed APISIX (Phase 11+) replaces file-write with direct Admin API call.

## 6. Tenant resolver mapping

LearnStack runtime needs to map `Host: anatoliayoga.com` → `(tenant_id,
organization_id?)`.

### Host resolution never calls the Hub

An earlier version of this document showed `CachedHostToTenantResolver` taking an
`IHubClient` and calling `LookupHostAsync` on every cache miss. That broke three rules at
once, and the third is the one that matters operationally:

1. It was an **unrecorded endpoint** — no ADR, no entry in the contract surface, and no
   counterpart in the Hub's own API documentation.
2. It was a **Hub call from outside the sanctioned adapters**. Only
   `IEntitlementProvider`, `IUsageReporter` and `IHubTenantSync` may hold a Hub client
   ([ADR-0034](../decisions/0034-hub-contract-surface-invariant.md)).
3. It put the **Hub on the hot path of anonymous public page loads**. Every cache miss on
   every marketing page of every tenant became a synchronous dependency on the control
   plane. A Hub outage — or a cold cache after a deploy during one — would have taken
   every tenant's public site down, for a lookup whose answer LearnStack already stores.

**`IHubClient.LookupHostAsync` is deleted.** `IHostToTenantResolver` reads
`platform_host_to_tenant` and nothing else:

```csharp
namespace LearnStack.Infrastructure.MultiTenancy;

public interface IHostToTenantResolver
{
    Task<HostResolution?> ResolveAsync(string host, CancellationToken ct = default);
}

public sealed record HostResolution(TenantId TenantId, OrganizationId? OrganizationId);

public sealed class CachedHostToTenantResolver(
    ICacheService cache,
    TenancyDbContext db) : IHostToTenantResolver
{
    public Task<HostResolution?> ResolveAsync(string host, CancellationToken ct = default)
        => cache.GetOrSetAsync(
            // Composed by the factory, never interpolated: CacheKey.EnsureValid is
            // what stops an unnormalized spelling creating a parallel entry.
            CacheKey.ForHostMapping(host),
            async token =>
            {
                // The policy on this table admits exactly the row the resolver
                // ANNOUNCES. Without the SET LOCAL the predicate is NULL and the
                // query returns nothing, so the miss path opens its own
                // transaction: SET LOCAL outside a transaction block emits
                // "WARNING: SET LOCAL can only be used in transaction blocks" and
                // has no effect, and a session-level set_config(..., false) would
                // survive on a pooled connection into the next request.
                await using var tx = await db.Database.BeginTransactionAsync(token);

                // set_config(..., true) is SET LOCAL's function form and is
                // transaction-local for the same reason. It has to be this form:
                // `SET LOCAL app.resolving_host = $1` is a syntax error —
                // PostgreSQL's SET takes no bind parameter — so the parameterised
                // spelling every other query uses is unavailable here, and string
                // interpolation into SET would be an injection site on the
                // anonymous page-load path.
                await db.Database.ExecuteSqlAsync(
                    $"SELECT set_config('app.resolving_host', {host}, true)", token);

                var resolution = await db.HostMappings
                    .AsNoTracking()
                    // is_publicly_live, per ADR-0036 § HostOnly — NOT the
                    // Hub-side `is_active` in the payload sample below. A
                    // domain can be active (owned, verified) and not yet
                    // publicly live, and only the latter may answer an
                    // anonymous page load.
                    .Where(m => m.Host == host && m.IsPubliclyLive)
                    .Select(m => new HostResolution(m.TenantId, m.OrganizationId))
                    .SingleOrDefaultAsync(token);

                await tx.CommitAsync(token);
                return resolution;
            },
            new CacheOptions(L1Ttl: TimeSpan.FromMinutes(2), L2Ttl: TimeSpan.FromMinutes(15)),
            ct);
}
```

`platform_host_to_tenant` is the one **platform-scoped** table — the row is what
*determines* the tenant, so it cannot be filtered by a tenant context that does not
exist yet. That does **not** make the read unscoped: row security is enabled and forced
here as everywhere else, and the read is keyed on `app.resolving_host`, which the
resolver declares for exactly the host it is about to resolve
([Database Standards § Table classes](../standards/05-database.md)). The failure mode of
forgetting the `SET LOCAL` is an empty result and a 404 — never a wider read. Writes stay
tenant-keyed, so a session that can see another tenant's host through
`app.resolving_host` still cannot repoint it.

Consequences of the change:

- A Hub outage degrades **billing and provisioning**. It does not touch page loads.
- The cache in front of the table is a latency optimisation, not an availability
  mechanism. Even a total cache failure leaves a single indexed primary-key lookup.
- `TenantResolverMiddleware` calls this resolver first, before JWT validation, because
  anonymous public routes need a tenant context too.

### How mappings arrive

Hub pushes host mappings over a dedicated endpoint, **not** through the entitlement
payload:

```text
PUT /api/internal/tenants/{id}/host-mappings
```

The endpoint is part of the enumerated Hub → LearnStack surface in
[ADR-0034](../decisions/0034-hub-contract-surface-invariant.md), carries the same auth
chain as every other internal call (mTLS + RS256 JWT with `aud=learnstack-internal` +
HMAC body signature + `jti` replay protection), and is handled by `IHubTenantSync`. The
handler upserts `platform_host_to_tenant` and invalidates the resolver cache for the
affected hosts on `learnstack.hub.custom-domain.activated` /
`.revoked`.

### Certificate material never rides the mapping payload

The host-mapping payload carries **hosts and identifiers only**. TLS certificates and
private keys are never carried in an HTTP payload that LearnStack caches, logs, audits or
mirrors — which is precisely what the superseded design did when it tunnelled cert
material through `PUT /api/internal/tenants/{id}/entitlements`, a payload that lands in
`platform_entitlement_cache`.

Key material moves by **secret-store replication** between the Hub-owned and
LearnStack-owned secret stores, and the mapping payload references it **by path**:

```json
{
  "host": "anatoliayoga.com",
  "tenant_id": "…",
  "organization_id": null,
  "certificate_ref": "learnstack/certs/anatoliayoga.com",
  "is_active": true
}
```

`certificate_ref` is a path, resolvable only by a principal that already holds read
access to that secret store. Possession of the payload — in a log line, an audit row, a
cache entry, or a support ticket screenshot — grants nothing.
[ADR-0022 Amendment 1](../decisions/0022-custom-domain-tls.md)'s step 3 is superseded
accordingly; its central guarantee, that the Hub never holds Kubernetes credentials on
the LearnStack cluster, is unchanged.

## 7. Cert renewal

Hangfire recurring job (`learnstack-hub:cert-renewal`), runs daily at 03:00 UTC:

```csharp
public sealed class CertRenewalJob : HubJob<CertRenewalJobParams>
{
    protected override async Task ExecuteAsync(CertRenewalJobParams parameters, CancellationToken ct)
    {
        var renewableWindow = TimeSpan.FromDays(30);   // start renewing 30 days before expiry
        var renewables = await _customDomainRepo
            .GetActiveExpiringWithinAsync(renewableWindow, ct);

        foreach (var domain in renewables)
        {
            try
            {
                var newCert = await _acmeClient.RenewAsync(domain.Domain, domain.DnsChallengeType, ct);
                await _vault.StoreCertAsync(domain.CertificateVaultKey!, newCert, ct);
                domain.Renew(newCert.NotAfter);
                await _eventBus.PublishAsync(new CustomDomainRenewedIntegrationEvent
                {
                    TenantId = domain.TenantId.Value,
                    Domain = domain.Domain,
                    NewExpiresAt = newCert.NotAfter
                }, ct);
                _logger.LogInformation("Cert renewed for {Domain}; expires {ExpiresAt}",
                    domain.Domain, newCert.NotAfter);
            }
            catch (LetsEncryptRateLimitException ex)
            {
                _logger.LogWarning(ex, "Rate-limited for {Domain}; deferring", domain.Domain);
                // Retry next day; if persistent, alert operator via Hub dashboard
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Renewal failed for {Domain}", domain.Domain);
                // Hub operator portal flags this domain on the Renewal Watch dashboard.
                await _operatorAlertService.RaiseAsync(new RenewalFailureAlert(domain, ex.Message), ct);
            }
        }
    }
}
```

Operators see failed renewals on the Renewal Watch dashboard in Hub operator portal.

## 8. Tenant Admin Studio surface

Tenant admin manages domains via Admin Studio settings:

```
Settings → Custom Domain
├── Current domains
│   ├── anatoliayoga.com         [Primary]  [Active]  [Renewal in 67 days]
│   └── besiktas.anatoliayoga.com           [Active]  [Renewal in 67 days]
│       (Mapped to Studio Beşiktaş organization)
└── Add domain
    └── [Submit new domain]
        After submission:
        - Status: Pending → Verifying → Active (or Failed)
        - DNS instructions shown (CNAME target)
        - Real-time progress (poll Hub status every 5s)
```

For org-scoped subdomains (`besiktas.anatoliayoga.com` for the Beşiktaş studio org), the
flow is the same but the resolver maps `host → (tenant_id, organization_id)` tuple
instead of just `tenant_id`.

## 9. Edge cases

| Scenario | Handling |
|----------|----------|
| Tenant submits a domain already owned by another tenant | Hub returns 409 `domain.already_in_use`. |
| Tenant submits a public-suffix-list TLD (`com`, `co.uk`) | Validator rejects with `domain.public_suffix_forbidden`. |
| DNS-01 challenge: tenant's DNS provider API down | Fallback to HTTP-01 if CNAME already in place; else manual TXT placement instructions. |
| Cert renewal: Let's Encrypt rate-limited | Retry next day; operator alerted; emergency manual renewal procedure available. |
| Customer releases domain | `Revoke()` method: cert revocation on Let's Encrypt, `Status = Revoked`, resolver mapping removed, APISIX route entry removed. |
| Customer wants EV cert (commercial CA) | Customer-provided cert path (Self-Hosted only); they upload via Hub, signature checked, stored in Vault, used by APISIX. |
| Custom domain pointed at LearnStack but tenant unsubscribes | Domain marked Revoked when subscription enters PastDue → Canceled → Expired; eventually removed. |
| Tenant on Starter plan tries to add custom domain | Hub returns 403 `feature.not_in_plan`; UI hides the option. |

## 10. Architecture tests

- `CustomDomain_PublicSuffixList_Enforced` — unit test asserts validator rejects PSL
  TLDs.
- `Cert_PrivateKey_NeverLeavesVault_To_Logs` — log redaction filter strips
  `-----BEGIN PRIVATE KEY-----` blocks before log emission.
- `CustomDomain_TenantId_NeverRead_FromRequest` — controller test asserts tenant_id is
  always derived from authenticated session, never from request body / query.
- `CustomDomain_Revocation_RemovesTenantResolverMapping` — integration test ends with the
  resolver returning null for the revoked host.
- `Hub_Client_Referenced_Only_By_Named_Adapters` — from
  [ADR-0034](../decisions/0034-hub-contract-surface-invariant.md); a resolver, a
  middleware or a controller holding an `IHubClient` fails the build. This is the
  mechanical guard against the deleted `LookupHostAsync` pattern reappearing.
- `Host_Resolution_Makes_No_Outbound_Calls` — integration test resolves a host with the
  Hub client registered as a throwing stub and asserts the resolution still succeeds.

## 11. Phasing

| Phase | Deliverable | Trigger |
|-------|-------------|---------|
| [02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md) | `platform_host_to_tenant` table | One-way door — the table that determines the tenant |
| [02a Packet 7](../roadmap/phase-02a-kernel-tenancy.md) | `IHostToTenantResolver` + `CachedHostToTenantResolver` reading `platform_host_to_tenant` and nothing else; two hosts wired to two seed tenants | — |
| [02d](../roadmap/phase-02d-walking-skeleton.md) | Host-based resolution exercised end to end: two hosts, two tenants, one binary | — |
| [02c](../roadmap/phase-02c-hub-foundation.md) | LearnStack-side `PUT /api/internal/tenants/{id}/host-mappings` handler behind `IHubTenantSync`; Hub-side `CustomDomain` aggregate and submission endpoint live in the Hub repository | A tenant is provisioned through the Hub |
| [04](../roadmap/phase-04-cms-media-pages.md) | Admin Studio custom-domain settings page: submission UI, DNS instructions, real-time status | — |
| [09b](../roadmap/phase-09b-hub-billing.md) | Hub operator portal: Pending Queue, Active List, Renewal Watch dashboards; compliance-caps editor includes domain gating | Commercial billing needed |
| [11](../roadmap/phase-11-production-hardening.md) | **The TLS automation itself**: ACME client, Let's Encrypt integration, DNS provider APIs (Cloudflare, Route 53, GCP DNS, Azure DNS), HTTP-01 fallback, renewal job at scale, secret-store replication, APISIX hot-reload validation | A tenant needs its own domain in production ([ADR-0035](../decisions/0035-demand-gated-infrastructure.md)) |

The split is deliberate: **the mapping is a one-way door and ships early; the automation
that populates it is additive and ships on demand.** A tenant can run on a custom domain
before Phase 11 by having an operator insert the mapping row and place the certificate in
the secret store by hand. What Phase 11 removes is the operator, not the capability.

## 12. Operational runbook (Phase 11)

`docs/operations/custom-domain-flow.md` will cover:

- Tenant-facing submission walkthrough.
- Hub operator review queue.
- Manual recovery: re-run verification, regenerate cert, force revoke.
- Customer DNS provider integration setup (per provider).
- Let's Encrypt rate limit incident response.
- Wildcard cert for `*.learnstack.app` lifecycle.

## 13. Non-goals

- **Domain registration.** LearnStack does not register domains on behalf of customers;
  customers bring their own.
- **DNSSEC management.** Customer's DNS provider concern.
- **Email-domain reputation (DMARC / SPF / DKIM for tenant outbound email).** Phase 11+
  notifications module handles this for tenant outbound; not part of custom-domain admin.

## References

- ADR-0022 — Custom Domain & TLS. Amendment 1's step 3 (certificate material inside the
  entitlement payload) is superseded by ADR-0034.
- [ADR-0034](../decisions/0034-hub-contract-surface-invariant.md) — Hub contract surface
  invariant: the `host-mappings` endpoint, key material leaving the entitlement payload,
  and the rule that host resolution never calls the Hub.
- [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) — when the TLS automation
  ships.
- ADR-0019 — LearnStack Hub.
- ADR-0021 — Feature-Based Entitlement (gate).
- ADR-0015 — APISIX Gateway (hot-reload of route table + SSL config).
- [30-api-gateway.md](30-api-gateway.md) — APISIX deep dive.
- [24-learnstack-hub.md](24-learnstack-hub.md) — Hub deep dive.
- [25-deployment-models.md](25-deployment-models.md) — Self-Hosted customer-provided cert
  variation.
