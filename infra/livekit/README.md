# LiveKit OSS + Coturn (Dev)

Self-hosted live-classroom media stack per
[ADR-0005 (Live Classroom Media Stack)](../../docs/decisions/0005-live-classroom-media-stack.md).
The .NET application talks to LiveKit through the `ILiveClassProvider`
abstraction (Phase 08c); the LiveKit server SDK is never imported by any
LearnStack module — only by `LearnStack.Infrastructure.LiveClassroom.LiveKit`.

## Access

| Endpoint | Address | Purpose |
|----------|---------|---------|
| WebSocket signaling | `ws://localhost:7880` | LiveKit client connects here for room join + signaling |
| TCP fallback | `tcp://localhost:7881` | WebRTC TCP fallback when UDP is blocked |
| TURN/TLS | `tcp://localhost:7882` | Encrypted TURN listener (dev: no real cert) |
| WebRTC media | UDP `50000-50100` | LiveKit media plane |
| STUN/TURN (Coturn) | `udp/tcp://localhost:3478` | NAT reflexive-address + media relay |
| TURN/TLS (Coturn) | `tcp://localhost:5349` | Encrypted TURN listener |
| Relay range (Coturn) | UDP `49152-49200` | TURN relay port range (dev: narrowed from production) |

## Dev API key

LiveKit room tokens are signed with a single dev key/secret pair declared in
`infra/livekit/livekit.yaml`:

| Key | Secret |
|-----|--------|
| `devkey` | `devsecret-32-byte-min-length-padding-xyz` |

The secret padding satisfies LiveKit's >= 32-byte requirement. Production
key/secret pairs come from Vault via `ISecretProvider` per
[Standards 12 § Secrets Management](../../docs/standards/12-infrastructure.md);
do not reuse these strings.

## Dev Coturn credentials

| User | Password |
|------|----------|
| `devuser` | `devsecret` |

These are **static long-term credentials** — fine for a developer poking at
TURN with `turnutils_uclient`, but production switches to the
`use-auth-secret` shared-secret pattern so LiveKit can issue per-session
ephemeral credentials.

## How LearnStack uses LiveKit

The full integration arrives in Phase 08c. Summary:

- **`ILiveClassProvider`** abstraction in `LearnStack.Application.Contracts`;
  LiveKit-specific implementation in `LearnStack.Infrastructure.LiveClassroom.LiveKit`.
- **Token issuance** happens server-side from `learnstack-api` using the dev
  key above; tokens scoped per `(tenant_id, session_id, user_id)` with a
  short TTL.
- **Recording** consumes LiveKit Egress (separate service, lands in
  Phase 08c) writing to MinIO via the existing storage provider abstraction.
  Recording is **tenant-configurable** and **consent-aware** per ADR-0005 +
  [16-media-pipeline.md](../../docs/architecture/16-media-pipeline.md).
- **Cost metrics** (participant minutes, bandwidth, recording minutes)
  surface in the Phase 09 analytics pipeline.

## What does NOT live here

- LiveKit Egress (recording) — Phase 08c.
- Production TLS cert provisioning — ADR-0022 + the same Let's Encrypt
  adapter family APISIX uses.
- Custom WebRTC SFU — explicitly out of scope per ADR-0005 + ADR-0018.
- Cloud LiveKit (managed) — supported as a swap-in path through the same
  `ILiveClassProvider` adapter; dev defaults to self-hosted OSS.
