# Custom Domains — Moved

> **This document moved on 2026-05-18.**
>
> The custom-domain admin workflow is now Hub-owned ([ADR-0019](../decisions/0019-learnstack-hub.md))
> and gated by a feature entitlement ([ADR-0021](../decisions/0021-feature-based-entitlement.md)).
> Operational details — DNS-01 / HTTP-01 challenge flow, Let's Encrypt automation, APISIX
> hot-reload, cert renewal, revocation, customer-provided cert path for Self-Hosted —
> live in:
>
> **➡ [27-custom-domain-tls.md](27-custom-domain-tls.md) — Custom Domain & TLS Provisioning**
>
> The 27- document supersedes the previous 22- content. This stub is retained for
> older links.

## Where each topic moved

| Topic | New location |
|-------|--------------|
| Tenant submission flow | [27-custom-domain-tls.md §1](27-custom-domain-tls.md) |
| DNS verification | [27-custom-domain-tls.md §3](27-custom-domain-tls.md) |
| Let's Encrypt (DNS-01 / HTTP-01) | [27-custom-domain-tls.md §4](27-custom-domain-tls.md) |
| APISIX hot-reload | [27-custom-domain-tls.md §5](27-custom-domain-tls.md), [30-api-gateway.md](30-api-gateway.md) |
| Tenant resolver mapping | [27-custom-domain-tls.md §6](27-custom-domain-tls.md), [09-tenant-isolation.md](09-tenant-isolation.md) |
| Cert renewal | [27-custom-domain-tls.md §7](27-custom-domain-tls.md) |
| Customer-provided cert (Self-Hosted) | [27-custom-domain-tls.md §4c](27-custom-domain-tls.md), [25-deployment-models.md](25-deployment-models.md) |
| Tenant Admin Studio surface | [27-custom-domain-tls.md §8](27-custom-domain-tls.md) |
| Hub operator portal surface | [24-learnstack-hub.md §6](24-learnstack-hub.md) |
| Architecture tests | [27-custom-domain-tls.md §10](27-custom-domain-tls.md) |
| Phasing | [27-custom-domain-tls.md §11](27-custom-domain-tls.md) |

## Why moved

The original 22-custom-domains document was authored before LearnStack Hub existed
([ADR-0019](../decisions/0019-learnstack-hub.md), accepted 2026-05-18). The custom-domain
admin workflow is operator-driven and belongs to Hub, not to the tenant-facing
LearnStack core. The new 27-document reflects this:

- Tenant submits domain via tenant Admin Studio, which proxies to Hub.
- Hub owns DNS verification, Let's Encrypt cert provisioning, cert storage in Vault,
  renewal cadence, revocation list.
- LearnStack runtime carries only a `host → tenant_id` resolver, populated from Hub events.
- The decision lives in [ADR-0022](../decisions/0022-custom-domain-tls.md).
