# ADR 0020: Triple Deployment Model + Hybrid License

## Status

Accepted

## Date

2026-05-18

## Decision

LearnStack supports **three deployment models** from Day 1, all from a single codebase,
single Helm chart, single set of container images:

| Mode | Description | Hub role | License mechanism |
|------|-------------|----------|-------------------|
| **SaaS** | LearnStack-hosted; many tenants on shared infrastructure | Hub-hosted by LearnStack; tenants phone home for entitlement | Phone-home (24h default) against LearnStack-hosted Hub |
| **Dedicated** | LearnStack-hosted; single-tenant managed deployment (one customer per LearnStack instance) | Hub-hosted by LearnStack; dedicated tenant phones home | Phone-home against LearnStack Hub |
| **Self-Hosted** | Customer runs LearnStack on their own infrastructure (Kubernetes / Helm). Optionally air-gapped. | Customer either runs their own Hub OR uses RSA-signed license keys issued by LearnStack-hosted Hub | RSA-signed license key (offline-capable) + optional phone-home; **30-day grace period** on expiry |

License resolution at the runtime is via `IEntitlementProvider`, with three
implementations:

- **`NullEntitlementProvider`** — default in `Development`; returns "all features allowed,
  no limits"; used when no Hub is wired.
- **`HubEntitlementProvider`** — production SaaS / Dedicated / online Self-Hosted; calls
  Hub `/api/v1/internal/license/verify`, caches result in `platform_entitlement_cache`
  for 15 minutes, invalidates on `tenant.entitlement.updated` event.
- **`SignedLicenseKeyEntitlementProvider`** — air-gapped Self-Hosted; validates RSA-2048
  signature, reads entitlement projection embedded in license key, refreshes via daily
  phone-home if connectivity available, falls back to last-known cached key otherwise.

Selection is driven by the `DeploymentMode` configuration setting bound at startup.

## Context

LearnStack targets three distinct customer profiles:

1. **SaaS customers** — small to mid-size education platforms that want zero-ops; sign up,
   pick a plan, start building.
2. **Dedicated customers** — mid-to-large customers who want LearnStack-managed
   infrastructure but isolated single-tenant deployment (data residency, regulatory, or
   performance reasons).
3. **Self-Hosted customers** — large enterprises, government agencies, regulated industries,
   or air-gapped environments that must run LearnStack on their own infrastructure with
   no outbound dependencies.

A non-trivial fraction of Self-Hosted customers will be in regulated sectors (KVKK,
GDPR data residency, HIPAA-adjacent education-health intersections) and may require:

- Air-gapped operation (no outbound network).
- Data residency in specific regions.
- Long-term license stability (multi-year contracts with no per-month phone-home requirement).

Nexora's experience (see `Nexora/docs/decisions/0030-license-hot-reload-mechanism.md`
and `Nexora/docs/operations/license-and-helm-upgrade.md`) showed the **hybrid license
model** — phone-home preferred, signed-key fallback, grace period on expiry — handles
all three customer profiles from one codebase. Critical decisions from Nexora that
transfer directly:

- **Single binary for all modes.** The same `LearnStack.Host` ships to SaaS, Dedicated, and
  Self-Hosted; only the `IEntitlementProvider` registration differs.
- **License key embeds the entitlement projection.** The RSA-signed key contains the same
  JSON projection that `HubEntitlementProvider` would return — no schema fork.
- **Grace period on expiry.** 30 days of degraded operation (read-only writes, classroom
  disabled, etc.) before hard failure, giving operators time to renew.
- **Revocation list.** Signed revocation bundle fetched daily; revoked keys denied even if
  not yet expired.

## Decision drivers

1. **Single codebase, multiple deployment targets.** Forking the code per mode is a
   maintenance disaster. Pick a port that abstracts the differences.
2. **Air-gapped support is a non-negotiable Self-Hosted requirement.** Some customers
   *cannot* phone home. The license model must allow fully offline operation with bounded
   trust (signed key + revocation list fetched on next online window).
3. **Phone-home is the SaaS-side default** because it gives operators real-time
   entitlement updates without waiting for license renewal cycles.
4. **Grace period matters.** A networking outage between LearnStack runtime and Hub must
   not kill production at the first failed call.
5. **Generations / monotonic version.** When entitlement changes (plan upgrade, compliance
   cap update), the cached projection must be invalidated. A monotonic `generation`
   counter on the projection + cache invalidation on `tenant.entitlement.updated` event
   handles this without polling.
6. **Provider portability** (across signing keys, across signing algorithms). RSA-2048 is
   the default; the verifier accepts a JWKS-style key set so rotation is a config change.

## Considered options

### Option A — Triple deployment with hybrid license (chosen)

Single codebase, three `IEntitlementProvider` implementations, one license format
(signed RSA-2048 with embedded entitlement projection).

**Pros:**
- Same binary everywhere.
- Air-gapped support without per-deployment code forks.
- Battle-tested in Nexora.

**Cons:**
- Three provider implementations to maintain.
- License-key signing key rotation procedure adds operational complexity.

### Option B — SaaS-only; no Self-Hosted (rejected)

Drop Self-Hosted entirely. All customers come to LearnStack SaaS.

**Pros:**
- Single deployment mode; simplest possible model.

**Cons:**
- Excludes regulated, air-gapped, sovereignty-conscious customers — a meaningful market
  segment for education platforms (universities, government training, corporate L&D in
  privacy-sensitive industries).
- Forces "hosted vs not hosted" as the entire product strategy.

### Option C — Self-Hosted-only; LearnStack ships software (rejected)

Drop SaaS; LearnStack becomes a software vendor selling licenses for on-prem installation.

**Pros:**
- No multi-tenancy operational burden.

**Cons:**
- The PaaS positioning (ADR-0018) was specifically that customers don't want to run their
  own infrastructure for an education platform. Forcing on-prem to every customer is the
  wrong product.

### Option D — SaaS + Dedicated; no air-gapped (rejected)

Two modes — SaaS and Dedicated managed — both phone-home capable; reject air-gapped.

**Pros:**
- Phone-home everywhere; no signed-key complexity.

**Cons:**
- Excludes air-gapped customers (a real segment).
- Long-term Self-Hosted contracts can't accommodate networking incidents (24h cache TTL
  is too short for some enterprise deployments).

## Decision outcome

Adopt **Option A**: triple deployment + hybrid license, single codebase.

### `DeploymentMode` configuration

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

Wired in `Program.cs`:

```csharp
var mode = configuration.GetValue<DeploymentMode>("Deployment:Mode");
switch (mode)
{
    case DeploymentMode.Development:
        services.AddSingleton<IEntitlementProvider, NullEntitlementProvider>();
        break;
    case DeploymentMode.SaaS:
    case DeploymentMode.Dedicated:
    case DeploymentMode.SelfHostedOnline:
        services.AddHttpClient<IEntitlementProvider, HubEntitlementProvider>(/* Hub base URL, API key */);
        break;
    case DeploymentMode.SelfHostedAirGapped:
        services.AddSingleton<IEntitlementProvider, SignedLicenseKeyEntitlementProvider>();
        break;
}
```

### License key format (Self-Hosted)

```json
{
  "header": { "alg": "RS256", "typ": "LSL" },
  "payload": {
    "iss": "learnstack-hub",
    "sub": "tenant-uuid",
    "iat": 1747576800,
    "exp": 1779112800,
    "deployment_mode": "SelfHostedOnline" | "SelfHostedAirGapped",
    "entitlement": {
      "tier": "growth",
      "features": { ... },
      "limits": { ... },
      "compliance": { "caps": { ... } },
      "generation": 42
    },
    "issued_at": "2025-05-18T...",
    "expires_at": "2026-05-18T...",
    "grace_until": "2026-06-17T...",
    "phone_home_url": "https://hub.learnstack.dev/api/v1/internal/license/refresh"
  },
  "signature": "base64url(RS256)"
}
```

### License lifecycle (state diagram)

```
[*] → Active (initial activation)
Active → PhoneHome (every 24h if mode != AirGapped)
PhoneHome → Updated (new entitlement returned by Hub)
PhoneHome → Cached  (Hub returns same generation; no change)
PhoneHome → GracePeriod (Hub unreachable; using cached key)
Updated → Active (new entitlements applied; cache invalidated; generation incremented)
Cached → Active (cache refreshed; same generation)
GracePeriod → Active (Hub becomes reachable; phone-home succeeds)
GracePeriod → ReadOnly (30 days elapsed without successful phone-home)
ReadOnly → Active (key renewed manually OR Hub becomes reachable)
Active → ManualUpdate (admin enters new license key in admin panel)
ManualUpdate → Active (key validated, signature verified, replaces previous)
```

### Phone-home implementation

Hangfire recurring job `learnstack:phone-home-refresh`, runs every 24h with random
0–119 minute jitter (mass-tenant SaaS deployments don't all hit Hub at midnight). Per
tenant in the SaaS instance:

```csharp
public async Task ExecuteAsync(PhoneHomeJobParams parameters, CancellationToken ct)
{
    var current = await _entitlementCache.GetAsync(parameters.TenantId, ct);
    try
    {
        var refreshed = await _hubClient.RefreshEntitlementAsync(parameters.TenantId, ct);
        if (refreshed.Generation > current?.Generation)
        {
            await _entitlementCache.SetAsync(parameters.TenantId, refreshed, ct);
            await _eventBus.PublishAsync(new EntitlementUpdatedIntegrationEvent {
                TenantId = parameters.TenantId,
                Generation = refreshed.Generation
            }, ct);
            _logger.LogInformation("Tenant {TenantId} entitlement refreshed to gen {Gen}",
                parameters.TenantId, refreshed.Generation);
        }
        await _entitlementCache.SetLastSuccessAsync(parameters.TenantId, DateTimeOffset.UtcNow, ct);
    }
    catch (HttpRequestException ex)
    {
        // Hub unreachable; grace period continues
        _logger.LogWarning(ex, "Phone-home failed for tenant {TenantId}; grace period active",
            parameters.TenantId);
    }
}
```

Grace period enforcement: when `now > current.ExpiresAt`, check `current.GraceUntil`:
- `now < current.GraceUntil` → degraded but functional.
- `now >= current.GraceUntil` → read-only mode; entitlement queries return
  `features: { everything: false }`.

### Revocation list

LearnStack Hub maintains a signed revocation list (RSA-signed bundle of revoked license
key IDs). Hub publishes `revocation-list.signed.json` at a fixed URL. Self-Hosted
deployments fetch this list daily via Hangfire job `learnstack:revocation-list-refresh`;
license key validation checks revocation list before accepting the key.

Hot-reload pattern (Nexora-equivalent): file written to `/var/learnstack/license/`,
`SIGHUP` to the LearnStack process triggers immediate revocation-list re-read; otherwise
in-process cache TTL is 1 hour.

## Architecture tests

Three blocker-level architecture tests added in Phase 02:

1. `IEntitlementProvider_Implementations_Are_Three` — exactly three concrete
   implementations exist (`NullEntitlementProvider`, `HubEntitlementProvider`,
   `SignedLicenseKeyEntitlementProvider`); none in module code.
2. `NullEntitlementProvider_NotRegistered_OutsideDevelopment` — at runtime, in any
   environment other than `Development`, the registered provider must not be the Null
   variant.
3. `LicenseKey_Validation_Is_Pinned_RSA2048` — verifier rejects keys signed with weaker
   algorithms; key payload schema validated against `LicenseKeyPayloadV1.json` schema.

## Consequences

### Positive

- Same codebase, three deployment paths.
- Air-gapped customers supported.
- Phone-home / signed-key duality covers connectivity spectrum.
- Grace period absorbs networking incidents without hard failure.
- Revocation list provides post-hoc license cancellation.

### Negative

- Three `IEntitlementProvider` implementations to maintain.
- Signed-key flow requires RSA key management procedure (rotation, revocation list signing,
  emergency revocation).
- Test matrix expands: SaaS path, online Self-Hosted path, air-gapped path each need their
  own integration suite.

### Neutral

- The `Dedicated` mode is operationally identical to `SaaS` at the runtime level; the
  difference is "one tenant per LearnStack instance" which is an infrastructure choice
  (separate cluster, separate database, separate Hub registration), not a code choice.

## Implementation notes

- Phase 02a — Platform kernel: `IEntitlementProvider` interface,
  `NullEntitlementProvider`, `platform_entitlement_cache` table, `DeploymentMode`
  config setting.
- Phase 02c — Hub Foundation (parallel with 02b): `HubEntitlementProvider` (calls Hub
  `/internal/license/verify`), Hub-side `Entitlement` aggregate, phone-home refresh
  job.
- Phase 09b — Hub Billing: license key issuance UI, RSA key generation procedure, revocation
  list publication.
- Phase 11 — Production hardening:
  - `SignedLicenseKeyEntitlementProvider` implementation.
  - Air-gapped install runbook (`docs/operations/hub-on-prem-setup.md`).
  - Revocation list signing + distribution.
  - Phone-home retry / backoff tuning.
  - Grace period enforcement integration tests.
  - SIGHUP hot-reload for license keys (Nexora pattern from
    `Nexora/docs/decisions/0030-license-hot-reload-mechanism.md`).

The architecture deep dive, license-key payload schema, RSA key management procedure, and
operational runbook live in [26-hybrid-license-model.md](../architecture/26-hybrid-license-model.md)
and [25-deployment-models.md](../architecture/25-deployment-models.md).

## References

- ADR-0014 — Adopt Dapr (entitlement-updated event via Dapr pub/sub).
- ADR-0019 — LearnStack Hub.
- ADR-0021 — Feature-Based Entitlement Model.
- [25-deployment-models.md](../architecture/25-deployment-models.md) — three-mode topology.
- [26-hybrid-license-model.md](../architecture/26-hybrid-license-model.md) — license format
  + lifecycle + signing.
- Nexora reference: `Nexora/docs/decisions/0030-license-hot-reload-mechanism.md`,
  `Nexora/docs/operations/license-and-helm-upgrade.md`,
  `Nexora/docs/decisions/0023-nmp-billing-model.md`.
