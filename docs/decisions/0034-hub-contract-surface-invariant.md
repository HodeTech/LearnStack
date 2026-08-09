# ADR-0034: Hub Contract Surface Invariant

## Status

Accepted

**Date:** 2026-08-08
**Amends:** [ADR-0019](0019-learnstack-hub.md) (the "closed at four endpoints" rule and
the LearnStack → Hub API-key auth),
[ADR-0022](0022-custom-domain-tls.md) (Amendment 1's entitlement-payload tunnelling)

## Decision Drivers

- **The "closed at four endpoints" rule is not true, and has not been true since it was
  written.** [ADR-0019](0019-learnstack-hub.md)'s own Decision section enumerates six
  paths and never uses the word "four"; its later amendment retroactively claims four.
  [Architecture 24](../architecture/24-learnstack-hub.md) declares four canonical paths
  and then adds four more it calls "auxiliary lifecycle endpoints" on the grounds that
  they are specialisations of the canonical four. An HTTP endpoint is a path plus a
  method. `DELETE /api/internal/tenants/{id}` is not `POST /api/internal/tenants`
  because one is the inverse of the other.
- **Protecting the number damaged the design.** To keep the count at four,
  [ADR-0022 Amendment 1](0022-custom-domain-tls.md) routes host→tenant mappings **and
  TLS certificate material, including private keys**, through
  `PUT /api/internal/tenants/{id}/entitlements` — a payload that is cached in
  `platform_entitlement_cache`, logged, audited, and mirrored. Tunnelling a private key
  through a cached projection is strictly worse than declaring a fifth endpoint.
- **A fifth caller appeared anyway.**
  [Custom Domain + TLS](../architecture/27-custom-domain-tls.md) shows
  `CachedHostToTenantResolver` calling `IHubClient.LookupHostAsync` on every cache
  miss. That breaks three rules at once: it is an unrecorded endpoint, it is a Hub call
  from outside the sanctioned adapters, and it places the Hub on the hot path of
  anonymous public page loads — so a Hub outage takes tenant marketing sites down.
- **The property everyone actually cares about is different from the count.** No
  reviewer has ever needed the Hub contract to have exactly four endpoints. What they
  need is that the Hub cannot reach tenant content, and that every crossing is
  auditable and goes through a named adapter.

## Considered Options

1. **Replace the count with the invariant it was standing in for** (chosen). Enumerate
   the real endpoint set, enforce "Hub stores no tenant data" and "every crossing goes
   through a named adapter" mechanically, and let the set grow by ADR when the design
   needs it.
2. **Genuinely reduce the surface to four** (rejected). Would mean folding `status`,
   `usage`, `delete` and `license/refresh` into the remaining four as mode flags —
   which is how the certificate tunnelling happened in the first place. It optimises a
   metric at the expense of the thing the metric was proxying for.
3. **Leave the contradiction in place** (rejected). The corpus would keep saying "four"
   while shipping eight, and the next designer under count pressure would tunnel the
   next secret through the next payload.

## Decision

The Hub contract surface is governed by **two invariants**, not by a count:

1. **The Hub stores no tenant content.** Courses, lessons, learners, enrollments,
   classroom sessions, media and content entries live exclusively in LearnStack. The
   Hub holds tenant *metadata* — plan, subscription, licence, custom domain, compliance
   caps, aggregated usage.
2. **Every LearnStack↔Hub crossing goes through a named adapter**:
   `IEntitlementProvider`, `IUsageReporter`, `IHubTenantSync`. No other type in the
   codebase may hold a Hub client. Nothing resolves a host by calling the Hub.

Adding an endpoint still requires an ADR — not because the count is sacred, but because
the surface is a cross-repository contract and both repositories have to agree.

### The endpoint set

**Hub → LearnStack** (`/api/internal/*`, internal listener only, mTLS + RS256 JWT +
HMAC body signature):

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/internal/tenants` | Create tenant + default organization |
| `PUT` | `/api/internal/tenants/{id}/entitlements` | Push the entitlement projection |
| `PUT` | `/api/internal/tenants/{id}/status` | Suspend / activate / archive |
| `DELETE` | `/api/internal/tenants/{id}` | Terminate |
| `GET` | `/api/internal/tenants/{id}/usage` | Pull aggregated usage |
| `PUT` | `/api/internal/tenants/{id}/host-mappings` | **New.** Push host → `(tenant_id, organization_id?)` mappings |

**LearnStack → Hub** (same auth chain — see § One auth chain, both directions):

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/internal/license/verify` | Verify / pull the entitlement projection |
| `POST` | `/api/v1/internal/license/refresh` | Scheduled phone-home refresh |
| `POST` | `/api/v1/usage/report` | Report a usage metric (idempotent) |
| `POST` | `/api/v1/internal/tenants/{id}/custom-domains` | Submit a custom domain on behalf of a tenant admin. Handled by `IHubTenantSync`; the Admin Studio never calls the Hub directly, because a `learnstack` realm token is rejected there ([ADR-0004](0004-authentication-strategy.md)) |

The Hub's own tenant-facing and operator-facing APIs (`/api/v1/tenants/*`,
`/api/v1/subscriptions/*`, `/api/v1/webhooks/*`) are **not** part of this surface. They
are the Hub's public API, governed by the Hub repository.

### One auth chain, both directions

The full three-layer chain — mTLS with a LearnStack-internal CA-signed client
certificate, an RS256 JWT with `aud=learnstack-internal` and `exp ≤ 5 min`
replay-protected on `jti`, and an HMAC-SHA256 body signature in `X-Signature` — applies
to **every** endpoint in both tables above.

This **extends** the chain to the LearnStack → Hub direction, which
[ADR-0019](0019-learnstack-hub.md) § Inter-system contracts specified as a per-instance
API key with a 100 req/min limit. That API key and its rate limit are superseded. A
bearer key on a path that returns a tenant's whole entitlement set has no replay
protection and no per-request integrity; and the two directions holding different
postures is how a reviewer loses track of which one is which — the contradiction this
subsection exists to close had both spellings twenty lines apart in the same
architecture document. Rate limiting for the LearnStack → Hub direction is the Hub
gateway's concern, not a property of the credential.

A `SelfHostedOnline` instance therefore needs a client certificate. It is issued with
the licence bundle and rotated on the same cadence — see
[ADR-0020](0020-triple-deployment-hybrid-license.md) and the Hub repository's `P02c-6`.
`SelfHostedAirGapped` makes no outbound call at all, so the question does not arise
there.

### Certificate material leaves the entitlement payload

`PUT /api/internal/tenants/{id}/host-mappings` carries host → tenant mappings only. TLS
certificates and private keys are **never** carried in an HTTP payload that LearnStack
caches, logs, audits or mirrors. Cert material moves between the Hub-owned and
LearnStack-owned secret stores by secret-store replication, referenced from the
host-mapping payload by path rather than by value.
[ADR-0022 Amendment 1](0022-custom-domain-tls.md)'s step 3 is superseded accordingly;
its central guarantee — that the Hub never holds Kubernetes credentials on the
LearnStack cluster — is unchanged.

### Host resolution never calls the Hub

`IHostToTenantResolver` reads `platform_host_to_tenant` and nothing else.
`IHubClient.LookupHostAsync` is deleted.
[Custom Domain + TLS](../architecture/27-custom-domain-tls.md) is corrected to match
the rest of the corpus, which already makes `platform_host_to_tenant` the sole
authority for host resolution. An anonymous page load must never depend on the Hub
being reachable.

### The entitlement read path

`HubEntitlementProvider` resolves in this order, and the order is normative:

```text
L1 in-process cache
  → L2 distributed cache (ICacheService)
    → platform_entitlement_cache   (durable; carries valid_until and grace_until)
      → Hub  (POST /api/v1/internal/license/verify)
```

A Hub outage on a cold cache falls through to `platform_entitlement_cache` and honours
the grace window recorded there. It does not throw out of a feature-flag check. Each
feature key class declares fail-open or fail-closed explicitly; the projection's wire
shape is pinned by a checked-in `entitlement-v1.schema.json` and a snapshot test in
both repositories.

## Context

### Why an invariant beats a count

A count is easy to check and easy to satisfy dishonestly. The two invariants above are
harder to state but can be enforced mechanically — one by scanning the Hub schema for
tenant-content tables, the other by scanning for Hub client references outside the
three adapters. Both already have architecture tests; neither had teeth while the count
was the headline rule.

### What we explicitly did not change

[ADR-0019](0019-learnstack-hub.md)'s decision to run the Hub as a separate repository
with an mTLS-guarded internal API stands. The Hub → LearnStack chain is unchanged; the
LearnStack → Hub direction is the one this ADR strengthens, in § One auth chain, both
directions.

### When this reopens

If the endpoint set grows past roughly a dozen, or if any endpoint starts carrying
tenant content, the separation itself is the thing to re-examine — not the count.

## Consequences

### Positive

- The corpus stops asserting something demonstrably false, so the rule regains
  authority.
- TLS private keys leave a cached, logged, audited payload.
- Host resolution no longer depends on Hub availability; a Hub outage degrades billing
  and provisioning, not public pages.
- The entitlement read path finally honours the grace window that
  [ADR-0021](0021-feature-based-entitlement.md) promised, instead of collapsing it into
  a cache TTL.

### Negative

- Two repositories must agree on a longer endpoint list, and both snapshot tests must
  be kept in step.
- A new endpoint (`host-mappings`) and a secret-store replication path have to be
  built; the tunnelled version, however unsound, was less work.

### Neutral

- "Adding an endpoint requires an ADR" survives verbatim; only its justification
  changes.

## Implementation Notes

- LearnStack-side handlers and the `HubEntitlementProvider` / `IUsageReporter` adapters
  land in [Phase 02c](../roadmap/phase-02c-hub-foundation.md); the Hub-side halves land
  in the Hub repository's `P02c-2` and `P02c-5`.
- `entitlement-v1.schema.json` is checked into both repositories and asserted by a
  snapshot test in each.
- Architecture tests: `Hub_NeverStores_TenantData`,
  `LearnStack_Modules_DoNotReference_Hub`, `Internal_API_Endpoints_AreNot_Public`,
  `IEntitlementProvider_Implementations_Are_Three`,
  `NullEntitlementProvider_NotRegistered_OutsideDevelopment`, and a new
  `Hub_Client_Referenced_Only_By_Named_Adapters`.
- [Infrastructure Stack Standards § Hub HTTPS Contract Surface](../standards/20-infrastructure-stack.md)
  is rewritten to state the invariants and carry the endpoint table.

## References

- [ADR-0019 LearnStack Hub](0019-learnstack-hub.md)
- [ADR-0021 Feature-Based Entitlement](0021-feature-based-entitlement.md)
- [ADR-0022 Custom Domain + TLS](0022-custom-domain-tls.md)
- [ADR-0035 Demand-Gated Infrastructure](0035-demand-gated-infrastructure.md)
- [Architecture 24 LearnStack Hub](../architecture/24-learnstack-hub.md)
- [Custom Domain + TLS](../architecture/27-custom-domain-tls.md)
- [Infrastructure Stack Standards](../standards/20-infrastructure-stack.md)
