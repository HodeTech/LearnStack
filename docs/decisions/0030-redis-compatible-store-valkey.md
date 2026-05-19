# ADR-0030: Redis-compatible Store — Valkey

## Status

Accepted

**Date:** 2026-05-19
**Deciders:** @platform
**Supersedes (partial):** ADR-0002 — Initial Architecture (the "Redis" choice on
the cache + state-store row only; the rest of ADR-0002 stands)

## Decision Drivers

- **Redis Inc. left BSD in 2024-03.** Redis 7.4 was the last BSD-3-Clause
  release. From 7.4 onward the project ships under a triple-license
  (AGPLv3 / RSALv2 / SSPLv1) where the user picks one. None of those are
  permissive OSS in the BSD sense, and SSPL in particular has not been
  tested in court for indirect-SaaS usage — a gray area we do not want a
  Self-Hosted tenant's legal team to discover months into a deployment.
- **Valkey** is the Linux-Foundation-governed BSD-3-Clause fork of Redis
  7.2.4. AWS, Google, Oracle, Ericsson, Snap, Tencent and others are
  first-tier sponsors; the governance is vendor-neutral by design.
  Valkey 8.x reached protocol + command parity with Redis 7.4 in
  2024-11 and added meaningful CPU/RAM improvements on top
  (multi-threaded I/O, smarter command pipelining).
- **Drop-in compatibility.** Valkey speaks the RESP protocol identically
  to Redis. Every library the LearnStack stack uses — `StackExchange.Redis`
  (consumed only inside `LearnStack.Infrastructure` per ADR-0014),
  Dapr's `state.redis` component, `ICacheService` over Dapr — sees the
  same wire protocol. Switching backends is one image tag and one
  `redisHost` value.
- **Provider portability is non-negotiable.** Per
  [Standards 20 § Composition Root](../standards/20-infrastructure-stack.md)
  the cache + state backend already sits behind `ICacheService` (Dapr
  building block). The provider portability claim of ADR-0014 only
  holds when the underlying provider is not on a license-shift
  trajectory; Valkey honours that claim, Redis Inc. 8.x trajectory
  weakens it.
- **Self-Hosted Air-Gapped must work.** Triple-deployment model
  ([ADR-0020](0020-triple-deployment-hybrid-license.md)) requires a
  backend with no phone-home, no license-key check, no commercial-tier
  gating. Valkey satisfies that; the SSPL/RSALv2 sides of Redis 8.x
  introduce ambiguity at the Self-Hosted boundary.
- **Ecosystem signal.** Debian 13, Ubuntu 25.04, RHEL 10 made Valkey the
  default `redis`-named package. AWS ElastiCache for Valkey is ~20%
  cheaper than ElastiCache for Redis. The boring-choice principle
  (Standards 00 § 9) now points at Valkey, not Redis.

## Considered Options

1. **Valkey** (chosen). BSD-3-Clause, Linux Foundation governance,
   RESP-protocol drop-in for Redis 7.4.
2. **Stay on Redis 7.4 indefinitely** (rejected). The 7.4 line gets
   security patches under the old license, but no new features arrive;
   the rest of the world moves to either Valkey or Redis 8.x, and our
   image freeze becomes an EOL clock.
3. **Adopt Redis 8.x (AGPLv3 selection)** (rejected). AGPL applied to a
   network-reachable backend in a SaaS context is a copyleft surface a
   proprietary platform like LearnStack should not adopt without legal
   review; the gain over Valkey is small (vendor parity), the risk is
   real (license interpretation).
4. **DragonflyDB** (rejected). High-performance RESP-compatible
   in-memory store, BSL-licensed (also source-available, not OSS by OSI
   definition). Solves the wrong problem — performance is not the
   bottleneck, license clarity is.
5. **KeyDB** (rejected). Active development slowed substantially after
   Snap acquisition; the project's future under EVA Information Security
   is unclear. Not a stable bet.

## Decision

LearnStack adopts **Valkey** as the Redis-compatible cache + state
backend behind `ICacheService` (Dapr `state.redis` component) for all
four deployment modes. The Dapr component name `state.redis` is the
**Dapr provider-type identifier** (RESP-compatible store) — it does not
imply the Redis Inc. brand and does not change.

Image (dev compose): `valkey/valkey:8.1-alpine`, pinned per
[Standards 12 § Image Conventions](../standards/12-infrastructure.md).
Production swaps to AWS ElastiCache for Valkey / equivalent managed
offering through the composition root.

This ADR **supersedes the cache + state-store row of ADR-0002 only** —
the rest of ADR-0002 (.NET 10, EF Core, Next.js, modular monolith)
stays. Together with [ADR-0029 (SeaweedFS)](0029-object-storage-seaweedfs.md),
both backend rows of ADR-0002 now have explicit successor decisions.

## Context

The original choice (Redis) was correct in 2023 — BSD-3-Clause, widely
known, every library + cloud provider supported it. The 2024-03 license
shift removed the unambiguous BSD path; the BSD-3-Clause continuation
moved to the Valkey fork.

The platform-level commitment to keep Self-Hosted Air-Gapped first-class
(ADR-0020) is what forces the choice now rather than later: a Self-
Hosted tenant inherits whatever license the backend ships with, and we
do not want their security/legal review to find SSPL-laden code paths
they then have to interpret. Valkey moves that conversation off the
table entirely.

### Backward-compat: what does NOT change

- **`ICacheService`** interface (`LearnStack.SharedKernel`) — unchanged.
- **Dapr `state.redis` component name** — unchanged. The component is the
  RESP-protocol adapter; Valkey is RESP, so it consumes the same
  component.
- **`StackExchange.Redis` library usage** — unchanged. The library
  connects to anything speaking RESP.
- **`IConnectionMultiplexer` forbidden-in-modules rule** (Standards 20)
  — unchanged. The rule is about not importing the library outside
  `LearnStack.Infrastructure`; the rule does not care about the backend
  vendor.
- **Architecture tests** `ICacheService_Is_OnlyCacheAbstraction`,
  `Modules_DoNotReference_DaprPackage` — unchanged.

### What DOES change

- `infra/compose/dev.yml`: service `redis` → `valkey`; image
  `redis:7.4-alpine` → `valkey/valkey:8.1-alpine`; volume `redis-data`
  → `valkey-data`; `depends_on: redis:` callers → `valkey:`.
- `infra/dapr/components/statestore-redis.yaml`: `redisHost: redis:6379`
  → `redisHost: valkey:6379` (the file name stays — `state.redis` is the
  Dapr provider-type convention).
- The "boring choice" enumeration in standards / CLAUDE.md / README.md
  / ADR-0002: "Redis 7" → "Valkey 8".
- `.gitignore`: `redis-data/` → `valkey-data/`.

## Consequences

### Positive

- License surface stays BSD-3-Clause — no AGPL/SSPL/RSALv2 to interpret.
- Self-Hosted Air-Gapped stays first-class.
- Vendor-neutral governance (Linux Foundation under) — no single-vendor
  policy-shift risk.
- AWS ElastiCache for Valkey ~20% cheaper than for Redis — direct SaaS
  margin win.
- Drop-in: zero application-code change required; Dapr component swap is
  one YAML edit.

### Negative

- One-time dev image swap + the doc/scaffold sweep. Both mechanical.
- Smaller commercial-support market than Redis Inc.'s, today. Mitigated
  by Linux Foundation backing + the major cloud providers committing
  managed offerings.
- A subset of Redis Inc.'s newest commercial features (Redis Search,
  Redis JSON commercial extensions in 8.x) does not exist in Valkey.
  None of these are on LearnStack's roadmap; flagging so a future
  feature ADR that wants them knows the choice.

### Neutral

- The `MINIO_`-style env-var rename trap does not apply here — Valkey
  honours the same `REDISCLI_AUTH` / connection-string conventions
  Redis does.
- Production swap to AWS / GCP / Azure managed offerings remains a
  composition-root edit through `ICacheService`.

## Implementation Notes

- **This commit** (Phase 01 packet 6 cleanup): dev compose service swap;
  Dapr component `redisHost` update; doc + standards sweep; `.gitignore`
  volume name update.
- **Phase 02a** (Cache adapter): `LearnStack.Infrastructure.Caching.Dapr`
  consumes `state.redis` component against Valkey — no code-level
  awareness of the backend vendor.
- **Phase 11** (production hardening): production sizing + Valkey HA
  topology decision (Sentinel vs Cluster mode) lives in its own ADR if
  it diverges from the default.

## References

- [ADR-0002 Initial Architecture](0002-initial-architecture.md) — original cache + state row, now partially superseded.
- [ADR-0014 Adopt Dapr](0014-adopt-dapr.md) — `ICacheService` over Dapr `state.redis`.
- [ADR-0020 Triple Deployment + Hybrid License](0020-triple-deployment-hybrid-license.md) — Self-Hosted Air-Gapped requirement that motivated the move.
- [Standards 12 § Local Infrastructure](../standards/12-infrastructure.md)
- [Standards 20 § Composition Root](../standards/20-infrastructure-stack.md)
- Valkey upstream: <https://github.com/valkey-io/valkey>.
