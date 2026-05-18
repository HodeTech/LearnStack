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
    LSApi->>HubAPI: POST /api/v1/tenants/{id}/custom-domains<br/>(internal API, mTLS)
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
            HubJob->>HubAPI: Store cert in Vault; update CustomDomain<br/>Status=Active, cert_expires_at=2026-08-16
            HubAPI->>HubAPI: Publish learnstack.hub.custom-domain.activated event<br/>via Dapr pub/sub
            HubAPI->>APISIX: Hot-reload route table partial<br/>(add SNI cert + route entry)
            HubAPI->>LSApi: Cache invalidation for host→tenant lookup
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
    # cert/key materialized via Vault Agent sidecar in production
    cert_ref: vault://learnstack-hub/certs/anatoliayoga.com/cert
    key_ref:  vault://learnstack-hub/certs/anatoliayoga.com/key
```

APISIX watches the file (standalone mode); changes are picked up within seconds.

In Kubernetes, the file is in a ConfigMap; rolling update of the ConfigMap propagates;
APISIX hot-reloads on file change inside the pod.

Etcd-backed APISIX (Phase 11+) replaces file-write with direct Admin API call.

## 6. Tenant resolver mapping

LearnStack runtime needs to map `Host: anatoliayoga.com` → `tenant_id`. Implementation:

```csharp
namespace LearnStack.Infrastructure.MultiTenancy;

public interface IHostToTenantResolver
{
    Task<Guid?> ResolveAsync(string host, CancellationToken ct = default);
}

public sealed class CachedHostToTenantResolver(
    ICacheService cache,
    IHubClient hubClient) : IHostToTenantResolver
{
    public async Task<Guid?> ResolveAsync(string host, CancellationToken ct = default)
    {
        return await cache.GetOrSetAsync(
            $"hub:host:{host}",
            async _ => await hubClient.LookupHostAsync(host, _),
            new CacheOptions(L1Ttl: TimeSpan.FromMinutes(2), L2Ttl: TimeSpan.FromMinutes(15)),
            ct);
    }
}
```

Cache invalidated on `learnstack.hub.custom-domain.activated` and
`learnstack.hub.custom-domain.revoked` Dapr pub/sub events. `TenantMiddleware` calls this
resolver first (before JWT validation, since the tenant context is needed for some
public anonymous routes too).

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

## 11. Phasing

| Phase | Deliverable |
|-------|-------------|
| 02a | `IHostToTenantResolver` interface + `CachedHostToTenantResolver` implementation in LearnStack. |
| 02c | `CustomDomain` aggregate in Hub. Submission endpoint scaffolded; verification logic stubbed (always returns success in dev). |
| 04 | Admin Studio custom-domain settings page: submission UI, DNS instructions, real-time status. |
| 09b | Hub operator portal: Pending Queue, Active List, Renewal Watch dashboards. Compliance caps editor includes domain-gating policy. |
| 11 | Production hardening: cert-manager + Let's Encrypt automation finalised; DNS provider API integrations (Cloudflare, Route 53, GCP DNS, Azure DNS); HTTP-01 fallback; renewal job tested at scale; APISIX hot-reload validated. |

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

- ADR-0022 — Custom Domain & TLS.
- ADR-0019 — LearnStack Hub.
- ADR-0021 — Feature-Based Entitlement (gate).
- ADR-0015 — APISIX Gateway (hot-reload of route table + SSL config).
- [30-api-gateway.md](30-api-gateway.md) — APISIX deep dive.
- [24-learnstack-hub.md](24-learnstack-hub.md) — Hub deep dive.
- [25-deployment-models.md](25-deployment-models.md) — Self-Hosted customer-provided cert
  variation.
