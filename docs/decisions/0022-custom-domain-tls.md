# ADR 0022: Custom Domain Management & TLS Provisioning

## Status

Accepted — **the certificate-delivery mechanism in Amendment 1 (steps 3 and 4) and in
the 2026-05-19 Option B amendment is superseded by
[ADR-0034](0034-hub-contract-surface-invariant.md) (2026-08-08)**

> **What ADR-0034 changed.** The lifecycle decided here is unchanged: Hub owns
> custom-domain administration, DNS-01 and HTTP-01 challenges, Let's Encrypt issuance
> behind `ITlsCertificateProvider`, and Hub never holds Kubernetes credentials on the
> LearnStack cluster.
>
> What changed is **how the result reaches LearnStack**. Amendment 1 routes the
> host → tenant mapping *and replicates the certificate, including its private key*
> through `PUT /api/internal/tenants/{id}/entitlements`, explicitly "to keep the
> four-endpoint surface closed". That payload is cached in
> `platform_entitlement_cache`, logged, audited and mirrored — tunnelling a private key
> through it is strictly worse than declaring another endpoint.
>
> Under ADR-0034: host mappings travel over a dedicated
> `PUT /api/internal/tenants/{id}/host-mappings` carrying the host → tenant tuple only,
> and certificate material moves by secret-store replication, referenced from that
> payload **by path, not by value**.
>
> Wherever an amendment below routes certificate material through
> `PUT /api/internal/tenants/{id}/entitlements`, or asserts that the four-endpoint
> surface is or remains closed, read it as superseded. That applies to Amendment 1
> steps 3 and 4 and to the Option B amendment's `SelfHostedOnline` / `SaaS` /
> `Dedicated` bullet. All three now mean: host mappings over
> `PUT /api/internal/tenants/{id}/host-mappings`, certificate material by secret-store
> replication, referenced by path.
>
> Separately, [Custom Domain + TLS](../architecture/27-custom-domain-tls.md)'s
> `CachedHostToTenantResolver` calls `IHubClient.LookupHostAsync` on cache miss, which
> puts the Hub on the hot path of anonymous public page loads. That call is deleted;
> `platform_host_to_tenant` is the sole authority for host resolution.

## Date

2026-05-18

## Decision

LearnStack tenants can run their platform under their own domain (e.g. `englishhero.com`,
`myyogaschool.io`). The custom domain admin flow is **Hub-owned** and **API-driven**:

1. **Tenant submits domain** via the Admin Studio domain settings (calls Hub via
   tenant-side LearnStack proxy endpoint).
2. **Hub creates `CustomDomain` row** with `Status = "Pending"`.
3. **DNS CNAME verification job** runs (every minute, max 60 attempts) — checks that
   `{tenant-domain}` resolves to the LearnStack edge load balancer's hostname via CNAME.
4. **TLS certificate provisioning** — Hub triggers Let's Encrypt via DNS-01 challenge
   (default) or HTTP-01 challenge (if the customer cannot delegate DNS). Cert stored in
   Vault.
5. **Tenant resolver mapping** updated: `host_to_tenant` table in Hub propagates to
   LearnStack edge (APISIX consumer / route table refresh).
6. **Custom domain `Status = "Active"`** — tenant sees green check; existing
   subdomain (`{tenant-slug}.learnstack.app`) remains as fallback.
7. **Cert renewal** at 60 days (90-day Let's Encrypt cert; 30-day pre-expiry renewal
   window) by a Hangfire recurring job.
8. **Domain revocation** (tenant releases domain) → cert revocation on Let's Encrypt +
   `Status = "Revoked"` + tenant resolver mapping removed.

Custom domain is a **gated feature**: `FeatureKeys.CustomDomain` (ADR-0021); only available
on Growth+ plans.

## Context

LearnStack as a PaaS for education needs to let tenants run on their own domain. A yoga
studio named "Anatolia Yoga" doesn't want their platform served from
`anatolia-yoga.learnstack.app`; they want `anatolia-yoga.com` (or
`learn.anatolia-yoga.com`).

The technical requirements:

1. **HTTPS-only** — every custom domain serves only over TLS; HTTP redirects to HTTPS.
2. **Wildcard not viable** — wildcard certs (`*.learnstack.app`) cover the LearnStack
   subdomain pattern, but not custom domains. Each custom domain needs its own cert.
3. **Domain ownership verification** — we don't issue certs for domains the tenant doesn't
   control.
4. **Cert renewal** — Let's Encrypt certs are 90-day; renewal must be automated.
5. **Tenant resolver mapping** — APISIX (or whichever gateway is fronting LearnStack) must
   know `host: anatolia-yoga.com` → tenant slug `anatolia-yoga`. This mapping must update
   without downtime.
6. **Revocation** — tenant cancels; we revoke the cert and stop serving.

Nexora (see `Nexora/docs/architecture/multi-tenancy.md` and
`Nexora/docs/operations/TENANT_OPERATIONS.md`) does **not** handle custom domains —
Nexora's tenants are CRM/ERP customers who use subdomains and don't typically need
branded domains. LearnStack diverges here because education platforms compete on brand
and discovery (a domain like `englishhero.com` is part of the product).

The patterns are well-established outside Nexora; this ADR adopts standard practices:

- **Let's Encrypt via cert-manager** in Kubernetes; or **acme.sh** / **lego** for non-K8s
  deployments.
- **DNS-01 challenge** preferred (works for wildcard, doesn't expose the verification HTTP
  endpoint).
- **HTTP-01 challenge** fallback (when tenant can't delegate DNS, e.g. customer using their
  own DNS provider with limited automation).
- **`host_to_tenant` lookup table** at APISIX with hot-reload (or etcd-backed if APISIX is
  in etcd mode).

## Decision drivers

1. **Tenant brand identity.** Education platforms live or die by brand; the URL is part
   of the product.
2. **Automation.** Manual cert provisioning + manual domain mapping does not scale across
   100s or 1000s of tenants.
3. **Security.** Domain verification before cert issuance is non-negotiable (prevent abuse
   of LearnStack's Let's Encrypt rate limits / impersonation attempts).
4. **Operator visibility.** Hub operators need to see pending domains, failed verifications,
   cert renewal status; if a tenant's cert expires, the regression should be visible to the
   Hub operator before the tenant notices.
5. **Tenant feature gating.** Custom domain is a paid feature; entitlement gate enforced
   (ADR-0021).
6. **Cert renewal failure handling.** Renewal failures must alert; tenant must have a
   procedure to act (DNS records changed? Let's Encrypt rate-limited?).
7. **Multi-deployment-mode compatibility.** SaaS, Dedicated, and Self-Hosted all support
   custom domains — Self-Hosted customers control their own cert management.

## Considered options

### Option A — Hub-owned admin + Let's Encrypt + APISIX hot-reload (chosen)

Hub owns the domain admin workflow; cert provisioning via Let's Encrypt DNS-01; APISIX
route table updated via configuration hot-reload.

**Pros:**
- Standard, battle-tested stack.
- Free certs (Let's Encrypt).
- Automatic renewal.
- Operator-visible flow via Hub.

**Cons:**
- DNS-01 requires tenant to delegate `_acme-challenge.{domain}` CNAME to a LearnStack-
  controlled endpoint, OR LearnStack-side DNS automation (via the tenant's DNS provider's
  API, where supported). Both paths exist but require operator help in non-automatable
  cases.
- Let's Encrypt rate limits (per-IP, per-domain) require careful retry / batching.

### Option B — Customer-provided certs (rejected for SaaS; allowed for Self-Hosted)

Customer uploads their own TLS cert (e.g. an EV cert they bought separately). Hub stores
in Vault, APISIX picks up.

**Pros:**
- Customers who have specific cert requirements (EV, custom CA) covered.

**Cons:**
- Manual; doesn't scale across many tenants.
- Customer responsible for renewal; we know nothing about expiry.
- Not viable as the SaaS default.

LearnStack supports this as a Self-Hosted-only override (customer's prerogative); SaaS
defaults to Let's Encrypt automation.

### Option C — Wildcard cert across all tenants (rejected)

Single wildcard cert `*.learnstack.app` covers all tenant subdomains; no custom domains.

**Pros:**
- Simplest.

**Cons:**
- Doesn't allow custom domains. Defeats the purpose of this ADR.

### Option D — Cloudflare for SaaS / Cloudflare-style edge cert management (deferred)

Use Cloudflare or a similar service that handles custom-domain SSL automatically.

**Pros:**
- Fully managed; the service handles cert provisioning, DDoS, edge caching.

**Cons:**
- Adds an operational dependency on a managed service.
- Cost scales with traffic.
- Lock-in.
- Defers the question rather than answering it.

For LearnStack SaaS, Cloudflare-equivalent edge service might be adopted as a Phase 11
optimization, but the underlying Hub-driven flow remains the source of truth.

## Decision outcome

Adopt **Option A**: Hub-owned admin + Let's Encrypt + APISIX hot-reload.

### Hub data model addition

```csharp
namespace LearnStack.Hub.Domain;

public sealed class CustomDomain : AuditableEntity<CustomDomainId>
{
    public TenantId TenantId { get; private set; }
    public string Domain { get; private set; }                  // e.g. "anatolia-yoga.com"
    public bool IsPrimary { get; private set; }                 // one primary domain per tenant
    public CustomDomainStatus Status { get; private set; }      // Pending | Verifying | Active | Failed | Revoked

    public DnsChallengeType DnsChallengeType { get; private set; }  // Dns01 | Http01
    public DateTimeOffset? VerifiedAt { get; private set; }
    public string? CertificateVaultKey { get; private set; }    // e.g. "learnstack/certs/anatolia-yoga.com"
    public DateTimeOffset? CertificateIssuedAt { get; private set; }
    public DateTimeOffset? CertificateExpiresAt { get; private set; }
    public DateTimeOffset? CertificateLastRenewedAt { get; private set; }

    public int VerificationAttempts { get; private set; }
    public string? LastVerificationError { get; private set; }

    public static CustomDomain Create(TenantId tenantId, string domain, bool isPrimary)
    {
        // Validation: domain format (FQDN, no scheme), TLD on public-suffix list,
        // domain not already registered to another tenant.
        // Initial Status = Pending; emits CustomDomainSubmittedEvent.
    }

    public void StartVerification() { /* Status = Verifying; emits CustomDomainVerificationStartedEvent */ }
    public void MarkVerified(string certVaultKey, DateTimeOffset certIssuedAt, DateTimeOffset certExpiresAt)
    {
        // Status = Active; sets cert fields; emits CustomDomainActivatedEvent.
        // Triggers tenant resolver mapping update.
    }
    public void RecordVerificationFailure(string error) { /* increment attempts; on max attempts, Status = Failed */ }
    public void Renew(DateTimeOffset newExpiresAt) { /* updates cert fields; emits CustomDomainRenewedEvent */ }
    public void Revoke() { /* Status = Revoked; emits CustomDomainRevokedEvent */ }
}

public enum CustomDomainStatus { Pending, Verifying, Active, Failed, Revoked }
public enum DnsChallengeType { Dns01, Http01 }
```

### Tenant submission flow

Sequence:

```
Tenant Admin Studio                              LearnStack tenant API           Hub API
─────────────────────                            ────────────────────            ───────
                                                                                  
1. POST /api/v1/tenant/custom-domains            ────────────────────────────►   /api/v1/tenants/{id}/custom-domains
   { domain: "anatolia-yoga.com" }
                                                                                  → CustomDomain row created, Status=Pending
                                                                                  → CustomDomainSubmittedEvent published
                                                                                  → DNS verification Hangfire job enqueued

2. Hub Hangfire job: DnsCnameVerificationJob
   - Resolve CNAME for anatolia-yoga.com
   - Expect: learnstack-edge.learnstack.app (or similar)
   - On match: trigger Let's Encrypt cert provisioning
   - On mismatch: increment VerificationAttempts; retry in 60s; after 60 attempts, Status=Failed

3. Let's Encrypt cert provisioning (cert-manager in K8s OR Hub-side lego/acme.sh)
   - DNS-01 challenge preferred:
     - Hub publishes TXT record `_acme-challenge.anatolia-yoga.com = <token>` via tenant's DNS API
       (when configured) OR instructs tenant to add the record manually
     - Let's Encrypt validates
     - Hub fetches cert, stores in Vault at `learnstack/certs/anatolia-yoga.com`
   - HTTP-01 challenge fallback:
     - Hub stages token at https://anatolia-yoga.com/.well-known/acme-challenge/<token>
       (requires CNAME already pointing at LearnStack edge; serves the token from a dedicated route)

4. Tenant resolver mapping update
   - Hub writes new host → tenant_id mapping
   - APISIX route table hot-reloaded with new SNI cert + route
   - Status = Active; CustomDomainActivatedEvent published to Dapr pub/sub
   - LearnStack runtime cache invalidated for tenant resolution

5. Tenant studio polls / receives notification: domain Active
```

### Cert renewal

Hangfire recurring job `learnstack-hub:cert-renewal`, runs daily:

```csharp
public async Task ExecuteAsync(CertRenewalJobParams parameters, CancellationToken ct)
{
    var renewables = await _customDomainRepo.GetExpiringWithinAsync(TimeSpan.FromDays(30), ct);
    foreach (var domain in renewables)
    {
        try
        {
            var newCert = await _certProvisioner.RenewAsync(domain.Domain, ct);
            await _vaultClient.StoreCertAsync(domain.CertificateVaultKey!, newCert, ct);
            domain.Renew(newCert.NotAfter);
            await _eventBus.PublishAsync(new CustomDomainRenewedIntegrationEvent {
                TenantId = domain.TenantId, Domain = domain.Domain,
                NewExpiresAt = newCert.NotAfter
            }, ct);
            _logger.LogInformation("Cert renewed for {Domain}; expires {ExpiresAt}",
                domain.Domain, newCert.NotAfter);
        }
        catch (LetsEncryptRateLimitException ex)
        {
            _logger.LogWarning(ex, "Rate-limited for {Domain}; deferring", domain.Domain);
            // Retry next day; if rate limit persists, alert operator
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Renewal failed for {Domain}", domain.Domain);
            // Alert operator via Hub dashboard
        }
    }
}
```

### APISIX integration

APISIX needs to know:

1. **Host → upstream mapping.** Same upstream (LearnStack API); differentiates by `Host`
   header.
2. **SNI cert per domain.** APISIX SSL plugin loads cert keyed by SNI hostname.
3. **Hot-reload on domain change.** In standalone mode (YAML), Hub writes a new
   `apisix.yaml` partial via a Kubernetes ConfigMap update; APISIX picks it up on file
   change. In etcd-backed mode (Phase 11+), Hub writes directly to APISIX Admin API.

```yaml
# apisix-routes-custom-domains.yaml (managed by Hub)
routes:
  - id: cd-anatolia-yoga
    host: anatolia-yoga.com
    uri: /*
    plugins:
      - openid-connect
      - cors
      - limit-req
      - request-id
    upstream:
      type: roundrobin
      nodes:
        - host: learnstack-api
          port: 5000
          weight: 1
    tls:
      sni: anatolia-yoga.com
      # cert and key referenced via Vault Agent sidecar OR APISIX SSL resource

ssl:
  - id: ssl-anatolia-yoga
    sni: anatolia-yoga.com
    cert: |
      -----BEGIN CERTIFICATE-----
      ...
      -----END CERTIFICATE-----
    key: |
      -----BEGIN PRIVATE KEY-----
      ...
      -----END PRIVATE KEY-----
```

In production, cert / key materialise from Vault via Vault Agent injection (annotation on
APISIX pod) — not stored in cleartext YAML.

### Tenant runtime — host header to tenant_id resolution

LearnStack edge receives request with `Host: anatolia-yoga.com`. Resolution:

```csharp
// Nexora-pattern AsyncLocal tenant context; Host-aware bootstrap
public sealed class TenantMiddleware
{
    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        var tenantId = await _hostToTenantResolver.ResolveAsync(host);
        if (tenantId == null)
        {
            // Unknown host; fall back to learnstack.app subdomain pattern OR 404
            // ...
            return;
        }
        _tenantContextAccessor.SetTenant(tenantId.Value, organizationId: null, userId: null);
        await _next(context);
    }
}
```

`_hostToTenantResolver` is backed by `ICacheService` (Dapr State / Valkey); cache key
`hub:host:{host}` invalidated on `CustomDomainActivatedEvent` / `CustomDomainRevokedEvent`.

### Public suffix list validation

A tenant cannot register `com`, `co.uk`, `gov`, or other public suffix domains. The
`CustomDomain.Create` validator consults a bundled public-suffix list (Mozilla PSL).

## Architecture tests

Two blocker-level architecture tests added (when Hub repository scaffolded — Phase 02c):

1. `CustomDomain_TenantId_NeverReadFrom_RequestBody` — no controller accepts a `tenant_id`
   parameter in body/query for custom-domain submission; tenant always derived from
   authenticated session.
2. `Cert_PrivateKey_NeverLeavesVault_To_Logs` — log redaction filter strips any string
   containing `-----BEGIN PRIVATE KEY-----` before emission.

## Consequences

### Positive

- Tenants own their brand identity via custom domain.
- Automated provisioning + renewal.
- Operator visibility into pending / failed / expired domains.
- Same flow across SaaS / Dedicated / online Self-Hosted; Self-Hosted air-gapped uses
  customer-provided certs (Option B fallback).

### Negative

- One more Hub responsibility (and one more place to monitor).
- Let's Encrypt rate limits are real; mitigated via batching and backoff.
- DNS-01 automation requires tenant DNS provider API support; HTTP-01 fallback covers
  the rest.
- Custom-domain users need DNS knowledge (CNAME setup); operator support process may be
  needed.

### Neutral

- Custom domain status visible in tenant Admin Studio settings; tenant can see DNS
  verification instructions and current status.
- Reverse direction (tenant releases domain to use another tenant slug): supported via
  `Revoke()`; Let's Encrypt cert revocation issued; tenant resolver mapping removed.

## Implementation notes

- Phase 02a — Platform kernel: `IHostToTenantResolver` interface +
  `HostToTenantResolver` implementation with cache; `TenantMiddleware` already in
  scope.
- Phase 02c — Hub Foundation (parallel with 02b): `CustomDomain` aggregate scaffolded;
  submission endpoint ready but verification logic stubbed.
- Phase 04 — CMS / Admin Studio: tenant-side custom-domain settings UI; calls
  LearnStack proxy → Hub API.
- Phase 09b — Hub Billing: feature gate (`FeatureKeys.CustomDomain`) enforced; Hub
  operator portal surfaces pending/failed domains list.
- Phase 11 — Production hardening:
  - cert-manager + Let's Encrypt automation finalised.
  - DNS-01 challenge via supported DNS provider APIs (Cloudflare, Route 53, Google Cloud
    DNS) wired.
  - HTTP-01 fallback path.
  - Cert renewal recurring job tested in production-like environment.
  - APISIX hot-reload pattern validated.
  - SIGHUP / file-watch revocation list cross-check (Nexora pattern).

The architecture deep dive, sequence diagram, public-suffix-list usage, and operational
runbook live in [27-custom-domain-tls.md](../architecture/27-custom-domain-tls.md).

## Amendments

### 2026-05-19 — Cert-and-route propagation is event-driven; Hub does not write LearnStack's K8s state

The Decision and the worked example show Hub "writing to
`infra/apisix/partials/routes-custom-domains.yaml`" / "Kubernetes ConfigMap rolling
update" as the path that puts a new SNI cert and route entry in front of APISIX. To
avoid implying that Hub holds K8s write credentials on the LearnStack cluster — a
coupling that violates the closed Hub HTTPS contract surface — the **authoritative
propagation path** is:

1. Hub completes DNS / TLS issuance and stores the cert in Vault under
   `learnstack-hub/certs/{domain}` (Hub-owned Vault namespace).
2. Hub publishes `learnstack.hub.custom-domain.activated` (and
   `.deactivated` / `.renewed`) via Dapr pub/sub.
3. Hub pushes the host → tenant mapping to LearnStack via
   `PUT /api/internal/tenants/{id}/entitlements` (or a dedicated host-mapping
   sub-field within it — kept inside the four-endpoint surface).
4. **LearnStack core** (not Hub) consumes the event and the projection push:
   - updates `platform_host_to_tenant`,
   - writes the matching APISIX route partial under `infra/apisix/partials/`
     inside the LearnStack cluster (or, in etcd-backed mode, calls the local
     APISIX Admin API),
   - materialises the cert/key via the Vault Agent sidecar attached to the
     APISIX pod, reading from the LearnStack-side Vault path that Hub
     replicates the cert into via the same internal API call.

Hub **never** holds Kubernetes credentials on the LearnStack cluster. The
ConfigMap / file write happens entirely within the LearnStack-owned cluster as a
reaction to the Dapr event + entitlement push. The four-endpoint contract
surface remains closed.

The Decision (Hub-owned admin flow, Let's Encrypt DNS-01 / HTTP-01 preferred,
APISIX hot-reload) is unchanged; only the **operational mechanism** for the
"APISIX picks it up" step is clarified.

### 2026-05-19 — Option B (customer-provided cert) path for `SelfHostedAirGapped`

Option B's original phrasing — "Hub stores in Vault, APISIX picks up" — assumes a
Hub-reachable deployment. For `SelfHostedAirGapped` mode there is **no Hub**;
the path is:

- **`SelfHostedOnline` / `SaaS` / `Dedicated`** (Hub reachable): customer-provided
  cert is uploaded through the Hub operator portal, stored in the **Hub-side
  Vault**, then replicated to the LearnStack-side Vault via the entitlement-push
  internal-API path (same channel as Let's Encrypt-issued certs).
- **`SelfHostedAirGapped`** (no Hub): customer places the cert + key directly in
  their **own Vault** (or the configured `ISecretProvider` backend) at the
  agreed namespace; the LearnStack APISIX pod's Vault Agent sidecar reads from
  there. Renewal is the customer's responsibility; Hub plays no role. The
  signed `.lic` file may carry a `custom_domains` claim listing the air-gapped
  customer's domains so the LearnStack-side host resolver knows the expected
  hosts at boot.

Architecture test `Cert_PrivateKey_NeverLeavesVault_To_Logs` continues to apply
across all modes.

## References

- ADR-0014 — Adopt Dapr (CustomDomain* events via Dapr pub/sub).
- ADR-0015 — APISIX (hot-reload of route table + SSL config).
- ADR-0019 — LearnStack Hub.
- ADR-0021 — Feature-Based Entitlement (`FeatureKeys.CustomDomain` gate).
- [27-custom-domain-tls.md](../architecture/27-custom-domain-tls.md) — architecture deep
  dive.
- [25-deployment-models.md](../architecture/25-deployment-models.md) — Self-Hosted custom
  domain variation (customer-provided certs).
