# ADR 0005: Live Classroom Media Stack

## Status

Accepted

## Decision

LearnStack builds an in-app classroom product experience, but does not build a custom WebRTC SFU or recording pipeline from scratch.

Preferred media stack:

- WebRTC in the browser.
- LiveKit as the preferred SFU/provider.
- Self-hosted LiveKit OSS as a supported path.
- LiveKit Cloud as an optional managed path.
- Provider-agnostic `ILiveClassProvider` abstraction.

## Context

WebRTC does not include a complete classroom platform. Production live education requires signaling, STUN/TURN, SFU routing, reconnect behavior, recording/export, monitoring, bandwidth management, and operational response.

LearnStack's strategic value is education workflow and platform composition, not writing a new media server.

## Consequences

- Live classroom domain concepts must not depend directly on LiveKit SDK types.
- Recording must be tenant-configurable and consent-aware.
- Classroom usage and cost metrics must be tracked.
- Future provider adapters remain possible.

