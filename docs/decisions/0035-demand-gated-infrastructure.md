# ADR-0035: Demand-Gated Infrastructure

## Status

Accepted

**Date:** 2026-08-08

## Decision Drivers

- **The "Day-1 foundation" doctrine does not distinguish two very different kinds of
  decision.** [Engineering Principles](../standards/00-principles.md) and `CLAUDE.md`
  correctly insist that irreversible decisions be taken early. That instinct swept
  additive, port-isolated technology choices into the same bucket as genuinely
  irreversible schema and isolation decisions.
- **The dev stack runs services the backend cannot call.**
  `backend/Directory.Packages.props` declares no client library for Dapr, Kafka, Vault,
  Valkey, Meilisearch, LiveKit or Hangfire. The local compose stack runs fourteen
  services; the backend has a client for one of them. Infrastructure that cannot be
  invoked is not a foundation — it is carrying cost. (The Phase 02a Packet 3
  cross-cutting set — Serilog, OpenTelemetry, Polly and Sentry — *is* declared and wired,
  and is not part of this ADR's gated set.)
- **The ports are the cheap half, and they land before their first caller.**
  `ISecretProvider` and `IProviderResilience<TPort>` already live in
  `LearnStack.SharedKernel`; `IEventBus`, `ICacheService`, `IEntitlementProvider` and
  `IHostToTenantResolver` land in
  [Phase 02a Packet 5](../roadmap/phase-02a-kernel-tenancy.md), ahead of any consumer. A
  port is three files and a registration. That asymmetry — a cheap, early interface
  against an expensive, deferrable adapter — is what makes the deferral reversible:
  swapping an implementation is a composition-root edit, while retrofitting an
  abstraction is a refactor across every call site. The three concrete Dapr adapters live
  in `LearnStack.Infrastructure.{Messaging,Caching,Secrets}`; see
  [29-dapr-integration.md § 4](../architecture/29-dapr-integration.md).
- **A hypothetical deployment mode has been steering unrelated decisions.**
  `SelfHostedAirGapped` — which ships no earlier than
  [Phase 11](../roadmap/phase-11-production-hardening.md) and has no contracted
  customer — is already the first-listed justification for rejecting `pg_partman` in
  [ADR-0028](0028-audit-log-partition-management.md) and the stated load-bearing case
  for an abstraction in [Cross-Cutting Concerns](../architecture/33-cross-cutting-concerns.md).
- **Deferral must be a written decision, not silence.** "We'll get to it" rots. A
  deferred building block needs a named port, a named owning phase, and a written
  trigger condition, or it is simply missing.

## Considered Options

1. **Demand-gate the additive building blocks behind their existing ports** (chosen).
   The port and its default implementation ship now; the vendor adapter ships in a
   named phase when a named trigger fires.
2. **Ship every adapter in Phase 02a as originally planned** (rejected). Pays the full
   operational and test-matrix cost of six technologies before a single tenant exists,
   and blocks the first user-visible artefact behind work no user can see.
3. **Delete the ports too and add abstractions when needed** (rejected). The ports are
   the cheap half; they are what makes deferral reversible. Removing them would turn a
   configuration change into a refactor.
4. **Keep everything but stop running it locally** (rejected). Hides the cost rather
   than removing it, and produces adapters that are never exercised.

## Decision

LearnStack classifies every infrastructure decision with the **one-way-door test**:

> If I add this six months from now, will I have to touch code that is already written?

- **Yes → one-way door.** Ship it now. Tenant and organization isolation, the
  `outbox_messages` table and its ownership, strongly-typed identifiers, and the
  localization schema are one-way doors: adding them later means touching every query,
  every migration, and every job payload.
- **No → additive.** Ship the **port** and a **default implementation** now; ship the
  vendor adapter in the phase named below, when its trigger fires.

A demand-gated building block is not "deferred". It has a port, a working default
implementation, an owning phase, and a trigger condition — all four, written down, or
it does not qualify.

**A deployment mode or customer segment without a signed contract cannot be the
deciding factor in a technical choice.** It may break a tie when the alternatives are
otherwise equal. This rule is added to
[Engineering Principles](../standards/00-principles.md).

### The gated set

| Building block | Port | Default implementation shipping now | Adapter lands in | Trigger |
|---|---|---|---|---|
| Dapr pub/sub | `IEventBus` | `InProcessEventBus` | [Phase 11](../roadmap/phase-11-production-hardening.md) | A second process needs to consume an integration event |
| Kafka | (behind `IEventBus`) | `InProcessEventBus` | [Phase 11](../roadmap/phase-11-production-hardening.md) | Event volume, replay, or ordering across processes is required |
| Dapr state / Valkey | `ICacheService` | `InMemoryCacheService` | [Phase 11](../roadmap/phase-11-production-hardening.md) | More than one application instance runs concurrently |
| Vault | `ISecretProvider` | `ConfigurationSecretProvider` | [Phase 11](../roadmap/phase-11-production-hardening.md) | A production secret must rotate without a redeploy, **or** more than one operator needs access to production secrets |
| APISIX | (composition root) | ASP.NET middleware | [Phase 11](../roadmap/phase-11-production-hardening.md) | A non-dev deployment needs edge rate limiting, host routing, or JWT pre-validation |
| Hub entitlement | `IEntitlementProvider` | `NullEntitlementProvider` | [Phase 02c](../roadmap/phase-02c-hub-foundation.md) | A tenant must be billed or plan-gated |
| Signed licence key | `IEntitlementProvider` | `NullEntitlementProvider` | [Phase 11](../roadmap/phase-11-production-hardening.md) | A Self-Hosted contract is signed |
| Custom-domain TLS automation | `IHostToTenantResolver` + `ITlsCertificateProvider` | `platform_host_to_tenant` rows managed by configuration | [Phase 11](../roadmap/phase-11-production-hardening.md) | A tenant needs its own domain in production |
| `audit_log` partitioning + retention job | — (schema-internal) | Single correct table | [Phase 11](../roadmap/phase-11-production-hardening.md) | Measured `audit_log` growth justifies partition maintenance |
| Meilisearch | `ITenantSearch` | PostgreSQL full-text search | [Phase 09](../roadmap/phase-09-billing-integrations-analytics.md) | Search quality or scale exceeds PostgreSQL FTS |
| LiveKit | `ILiveClassProvider` | — (scheduled, not gated — see the exception below) | [Phase 08c](../roadmap/phase-08c-classroom.md) | Live classes become a product requirement |
| Managed video transcoding | `IVideoTranscoder` | ffmpeg-backed worker ([Phase 04](../roadmap/phase-04-cms-media-pages.md)) | [Phase 11](../roadmap/phase-11-production-hardening.md) | In-house transcode backlog or per-minute cost exceeds the managed alternative |

**Two rows carry a deliberate exception to the four-element rule, named here so they read
as decided rather than overlooked.** A **schema-internal** block — `audit_log`
partitioning — has no port because there is no consumer to isolate: the correct
un-partitioned table plays the default implementation's role, and the conversion is a
migration, not a swap. A block whose absence is a **missing product feature rather than a
missing implementation** — LiveKit — has no default, because there is nothing to default
to before the feature exists; its trigger is the phase that introduces live classes, and
that is the honest statement of it. It is listed here for completeness of the
infrastructure picture, not because it is gated in the same sense as the rest. No third
exception is added without amending this ADR.

`DeploymentMode` keeps all five of its values and the composition root keeps branching
on it. What changes is the **support claim**: only `Development` and `SaaS` are wired
end to end before Phase 11. `Dedicated`, `SelfHostedOnline` and `SelfHostedAirGapped`
are prepared seams, not supported deployments, until their integration suites exist.

That makes `SaaS` a **non-development deployment running on
`ConfigurationSecretProvider`**, which is why the Vault trigger above is written as a
rotation and access condition rather than "a non-dev deployment exists" — the latter
would be satisfied the moment SaaS ships, which is not what the row means. Reading
secrets from `IConfiguration` is a bounded position, not a permanent one: it is
defensible while one operator holds the deployment's secrets and a redeploy is an
acceptable way to change them. The first production secret that must rotate without a
redeploy, or the second operator needing access, fires the trigger — and that may well
be before Phase 11's other work.

## Context

### Why this is not "defer everything"

The platform-first instinct is right, and this ADR preserves it. Tenant isolation,
module boundaries, the outbox table's ownership and the typed-identifier convention are
all genuinely cheaper now than later, and all of them stay in Phase 02a. What this ADR
removes from Phase 02a is the set of choices that will cost exactly the same in six
months as they do today, because a port stands between them and every consumer.

The distinction is mechanical, not a matter of taste: tenant isolation touches every
query ever written, so its cost grows with the codebase. A Dapr adapter touches three
classes at the composition root, so its cost does not.

### What would reverse this decision

Any of the trigger conditions above firing earlier than its phase. The triggers are
written as observable conditions rather than dates precisely so that the roadmap can
respond to reality — if a second process needs an integration event during Phase 05,
the Dapr adapter moves to Phase 05 and this table is amended.

### What this ADR does not do

It does not withdraw [ADR-0014](0014-adopt-dapr.md),
[ADR-0015](0015-api-gateway-apisix.md), [ADR-0019](0019-learnstack-hub.md),
[ADR-0020](0020-triple-deployment-hybrid-license.md) or
[ADR-0022](0022-custom-domain-tls.md). Those decisions stand: when LearnStack needs a
cross-process event bus it uses Dapr, when it needs an edge gateway it uses APISIX, and
when it needs a control plane it uses the Hub. This ADR decides **when** each arrives,
not **whether**.

## Consequences

### Positive

- The local development loop drops from fourteen services to roughly seven, and the
  daily feedback cycle gets correspondingly faster.
- The first browser-visible artefact — [Phase 02d](../roadmap/phase-02d-walking-skeleton.md)
  — stops being blocked behind infrastructure no user can see.
- The test matrix shrinks: two live deployment modes instead of five, one event
  transport instead of two.
- Every deferral is now legible. A reader can see what is missing, which port covers
  it, which phase owns it, and what makes it urgent.

### Negative

- The adapters are written later, against a codebase that has more consumers. The ports
  make this a bounded cost, but it is not zero.
- `Phase 11` accumulates a substantial amount of previously-distributed work and must
  be planned as a real phase rather than a hardening checklist.
- Claiming five deployment modes and supporting two requires the marketing and
  documentation surfaces to say so plainly.

### Neutral

- The `DeploymentMode` enum, the ports, and the composition-root branching are all
  unchanged. Only the implementations behind three of the five values are absent.

## Implementation Notes

- `ISecretProvider` and its default `ConfigurationSecretProvider` already shipped in
  [Phase 02a Packet 3](../roadmap/phase-02a-kernel-tenancy.md). The remaining defaults
  (`InProcessEventBus`, `InMemoryCacheService`, `NullEntitlementProvider`) ship in
  [Phase 02a Packet 5](../roadmap/phase-02a-kernel-tenancy.md). Together they are the
  only registered implementations in every deployment mode until their own triggers
  fire — which is Phase 11 for the event bus, the cache and secrets, but
  [Phase 02c](../roadmap/phase-02c-hub-foundation.md) for `IEntitlementProvider`, whose
  `NullEntitlementProvider` must not be registered outside `Development` once the
  Hub-backed implementation exists.
- `InProcessEventBus` is a first-class transport, not a stub: it uses the same
  `IIntegrationEventHandler<T>` interface, the same `IInboxGuard`, the same
  tenant-context restoration, and the same per-partition-key ordering (concurrent
  across keys, sequential within one) as the durable path — see
  [15-event-and-outbox.md](../architecture/15-event-and-outbox.md). A dev path that skips those is a dev
  path that never exercises the isolation code.
- `ICacheService.RemoveByPrefixAsync` is removed or redesigned before Packet 5 ships:
  the current contract cannot be honoured across instances by any of the candidate
  backends. See [Phase 02a Packet 5](../roadmap/phase-02a-kernel-tenancy.md).
- `NullEntitlementProvider` must not be registered outside `Development` once
  [Phase 02c](../roadmap/phase-02c-hub-foundation.md) lands
  (`NullEntitlementProvider_NotRegistered_OutsideDevelopment`).
- Architecture test `Modules_Do_Not_Reference_DeploymentMode` continues to hold — the
  composition root branches once, modules never.

## References

- [ADR-0014 Adopt Dapr](0014-adopt-dapr.md)
- [ADR-0015 API Gateway: APISIX](0015-api-gateway-apisix.md)
- [ADR-0019 LearnStack Hub](0019-learnstack-hub.md)
- [ADR-0020 Triple Deployment + Hybrid License](0020-triple-deployment-hybrid-license.md)
- [ADR-0022 Custom Domain + TLS](0022-custom-domain-tls.md)
- [ADR-0028 Audit Log Partition Management](0028-audit-log-partition-management.md)
- [ADR-0033 Audit Durability Model](0033-audit-durability-model.md)
- [Engineering Principles](../standards/00-principles.md)
- [Infrastructure Stack Standards](../standards/20-infrastructure-stack.md)
