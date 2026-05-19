# Phase 08c: In-App Live Classroom

## Goal

Deliver the in-app WebRTC classroom on top of the Phase 08b scheduling primitives: provider-agnostic room runtime via `ILiveClassProvider`, scoped LiveKit join tokens, attendance computed from classroom events, recording metadata and consent flow.

Phase 08c is the highest-density architectural milestone in the roadmap. It is split out from the original combined "08B: Scheduling and In-App Classroom" so that scheduling failures and classroom failures do not blend into a single risk surface.

Decisions consumed in this phase:

- [ADR 0005 — Live Classroom Media Stack](../decisions/0005-live-classroom-media-stack.md) — self-hosted LiveKit OSS by default.
- [07-in-app-live-classroom.md](../architecture/07-in-app-live-classroom.md)
- [08-livekit-cost-model.md](../architecture/08-livekit-cost-model.md)
- [16-media-pipeline.md](../architecture/16-media-pipeline.md) — recording, consent, retention.
- [18-webrtc-build-vs-adopt.md](../architecture/18-webrtc-build-vs-adopt.md)

## Scope

### Provider Abstraction

- `ILiveClassProvider` interface implemented by `LiveKitSelfHostedProvider`.
- `LiveKitCloudProvider` registered behind the same interface for one-switch fallback.
- `ManualMeetingLinkProvider` registered as explicit fallback only — not the target architecture.
- Domain code and application code reference the interface; LiveKit SDK types live only in `Infrastructure.LiveClassroom.LiveKit`.

### Runtime Domain Concepts

- `LiveRoom` — runtime room bound 1:1 to a `LiveSession` while open.
- `LiveRoomProvider` — discriminator (`livekit_self_hosted`, `livekit_cloud`, `manual_link`).
- `LiveRoomToken` — short-lived join token (≤ 5 min TTL), scoped per user + room + role.
- `LiveAttendance` — computed participation record per participant.
- `LiveRecording` — recording metadata + consent + retention.
- `LiveSessionEvent` — append-only event stream (`room_opened`, `participant_joined`, `screen_share_started`, ...).

### Classroom UX (MVP)

The target experience is an embedded classroom inside the LearnStack portal, not an external Zoom or Google Meet link.

- Instructor joins from the portal.
- Learner joins from the portal.
- Backend issues scoped LiveKit join tokens only for authorised participants.
- Role-based room permissions (instructor publishes by default; learner can request).
- Audio, video, screen sharing.
- Participant list.
- In-room chat via LiveKit data channels.
- Session start / end tracking from provider webhooks.
- Reconnection state visible to the user.
- Recording indicator visible to all participants whenever recording is active.

### Attendance

- Computed from `LiveSessionEvent` streams (`participant_joined`, `participant_left`, `network_drop`).
- Configurable rules per tenant: minimum duration, late join tolerance, rejoin coalescing.
- Attendance status drives no-show notifications via the Phase 08a notification engine.

### Recording and Consent

Recording is **opt-in**, off by default per tenant. The consent flow is part of the join path, not a separate setup step. See [16-media-pipeline.md](../architecture/16-media-pipeline.md) § Recordings.

- Per-tenant recording policy.
- Per-session recording flag.
- Per-participant consent state captured before the room is joined; absence blocks the join.
- Recording metadata persisted: storage key, duration, consent state, retention deadline.
- Recording can be configured at policy / metadata level even when execution is disabled — for tenants that want the audit trail without the bandwidth.
- LiveKit Egress writes the recording to S3 / SeaweedFS; LearnStack does not transcode in this phase.

### Provider Webhooks

- `https://api.<domain>/webhooks/livekit` with HMAC signature verification.
- Idempotent: `(provider, event_id)` deduplicated.
- Tenant id derived from the stored provider account, never trusted from the payload.

### Operational Readiness

- Coturn (TURN / STUN) configured and reachable.
- LiveKit running in dev compose alongside the application (already true since Phase 01).
- Cost dashboards instrumented from Phase 09 are populated from `LiveSessionEvent` streams.

## Authorization Surface

Joining a live session requires:

1. Authenticated user (Phase 02b + Phase 03).
2. Membership in the resolved tenant (Phase 03).
3. Permission to join the session (Phase 03 + Phase 07 entitlement check).
4. Resource-scope match (the user is a booked participant or an instructor on this session).
5. Recording consent (if recording is enabled for this session).

Failures at any layer produce a Problem Details response with a specific `code` (`unauthorized`, `forbidden`, `recording_consent_required`); the classroom UI maps these to actionable copy.

## Deliverables

- `ILiveClassProvider` abstraction + `LiveKitSelfHostedProvider` implementation.
- In-app classroom UX MVP with audio / video / screen / chat / participant list.
- Token issuance pipeline with TTL and role scoping.
- Attendance computation from event streams.
- Recording consent flow + recording metadata model + provider webhook handler.
- LiveKit usage events surface in the Phase 09 analytics pipeline.

## Completion Criteria

- Instructor and learner can join a scheduled session inside the portal.
- Token issuance is < 200 ms p95; classroom join time is < 1.5 s p95 (per [15-performance.md](../standards/15-performance.md)).
- Cross-tenant join attempt (identity `{tenantA}:{userId}` against a room owned by tenant B) is rejected.
- Attendance reflects participant join/leave streams correctly across reconnections.
- Recording cannot start without consent; consent state is recorded in `LiveRecording`.
- Recording, when enabled, writes to S3 / SeaweedFS with the correct key prefix.
- LiveKit provider webhook handler is signature-verified, idempotent, and tenant-scoped.

## Risks

- Treating live classes as simple meeting links instead of a product capability.
- Coupling core session logic directly to LiveKit SDK types — guarded by the architecture test forbidding provider SDK imports in Domain / Application.
- Underestimating TURN / STUN, bandwidth, and recording operations — see [08-livekit-cost-model.md](../architecture/08-livekit-cost-model.md).
- Enabling recording without consent and retention policy.
- Treating this phase as "demo-quality" — the classroom is the vertical-slice exit criterion for English Learning.

## Phase Exit Decision

Phase 09 (Billing, Integrations, Analytics) can begin when classroom join, attendance, recording metadata, and provider webhook handling are stable and integrated with notifications.
