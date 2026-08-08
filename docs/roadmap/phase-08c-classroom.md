# Phase 08c: In-App Live Classroom

## Goal

Deliver the in-app WebRTC classroom on top of the [Phase 08b](phase-08b-scheduling.md)
scheduling primitives: a provider-agnostic room runtime behind `ILiveClassProvider`,
scoped join tokens, attendance computed from classroom events, and a recording flow
that cannot start without consent.

Phase 08c is the highest-density architectural milestone in the roadmap. It is split
out from the original combined "08B: Scheduling and In-App Classroom" so that
scheduling failures and classroom failures do not blend into a single risk surface.

This phase is also where LiveKit actually arrives.
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md) demand-gates it: unlike
the other gated building blocks, `ILiveClassProvider` ships with **no default
implementation**, because a null classroom is not a degraded classroom — it is no
product. The trigger condition ADR-0035 records for LiveKit is literally "the classroom
phase begins", which is this phase.

Decisions consumed here:

- [ADR-0005 — Live Classroom Media Stack](../decisions/0005-live-classroom-media-stack.md)
  — LiveKit is the SFU; self-hosted OSS and LiveKit Cloud are both supported paths
  behind one abstraction.
- [ADR-0035 — Demand-Gated Infrastructure](../decisions/0035-demand-gated-infrastructure.md)
  — the port ships early, the adapter ships when the trigger fires.
- [07-in-app-live-classroom.md](../architecture/07-in-app-live-classroom.md)
- [08-livekit-cost-model.md](../architecture/08-livekit-cost-model.md)
- [16-media-pipeline.md](../architecture/16-media-pipeline.md) — recording, consent,
  retention.
- [18-webrtc-build-vs-adopt.md](../architecture/18-webrtc-build-vs-adopt.md)

## Scope

### Deployment posture: which LiveKit, and when

The cost model in
[08-livekit-cost-model.md](../architecture/08-livekit-cost-model.md) does not agree
with itself. Its Recommendation section says the first production deployment should be
self-hosted LiveKit OSS. Its own scenarios say otherwise at the volumes LearnStack will
actually see first:

| Cost-model scenario | Workload | Cheaper mode |
|---|---|---|
| A — 100 one-on-one sessions / month, no recording | pilot | **LiveKit Cloud** |
| C — 100 four-person group sessions / month, no recording | pilot | **LiveKit Cloud** |
| B — 1,000 one-on-one sessions / month, no recording | growth | roughly break-even |
| D — 1,000 sessions / month, every session recorded | growth + recording | **self-hosted**, by ~5× |
| E — 5,000 sessions / month, every session recorded | scale | **self-hosted**, by ~6× |

The scenarios are right and the recommendation is stale. Self-hosting pays back on
**recording volume and bandwidth**, not on existing. At pilot volume a self-hosted
deployment buys a TURN server, a Valkey node, an Egress node and an on-call rotation in
exchange for saving nothing, and it buys them during the phase with the most novel
failure modes. [ADR-0005](../decisions/0005-live-classroom-media-stack.md) does not
force the issue either way: it names self-hosted OSS "a supported path" and Cloud "an
optional managed path", and leaves the sequencing to the roadmap. This phase sets it.

**The decision rule.**

1. **Start on LiveKit Cloud**, behind `ILiveClassProvider`. It is the fastest path to a
   working classroom, it removes SFU and TURN operations from the phase that already
   carries WebRTC reconnection, token scoping, consent and webhook idempotency, and at
   pilot volume it is also the cheaper one.
2. **Emit the cost counters from day one** — participant-minutes, downstream bytes,
   recorded minutes, concurrent peak — so the crossing is measured rather than guessed.
   See § Cost instrumentation below.
3. **Move to self-hosted when measured volume crosses the break-even the cost model
   computes.** Two independent crossings, either of which is sufficient:
   - **Recorded minutes.** Cloud bills recording per minute past a small included
     quota; a self-hosted Egress node plus its storage is a flat monthly cost. This
     crossing arrives first and arrives at a volume well below the live-minute one —
     the cost model's Scenario D is already past it by roughly 5×.
   - **Participant-minutes and downstream transfer.** The live-only crossing sits
     around the cost model's Scenario B.
4. **The move is a configuration change, not a code change.** That is the entire reason
   `ILiveClassProvider` exists, and it is the claim this phase must make true rather
   than assume.

The cost model owns the arithmetic and the recheck cadence; this phase owns the rule
and the measurement. Re-derive the crossing at each recheck rather than treating a
2026-05-14 snapshot as a constant — LiveKit Cloud plan tiers and Hetzner / AWS egress
rates both move.

`ManualMeetingLinkProvider` is a third path and is neither of these. It exists so a
tenant with no LiveKit account at all can still schedule a session against an external
meeting URL. It is an escape hatch, not a deployment posture: attendance, recording and
in-room events are all unavailable behind it, and the classroom UX degrades to a link.

### Provider abstraction

- `ILiveClassProvider` — the port. Domain and application code reference only this.
- One `LiveKitProvider` adapter in `Infrastructure.LiveClassroom.LiveKit`. Cloud and
  self-hosted are the **same adapter against a different server URL and credential**,
  because LiveKit OSS and LiveKit Cloud speak the same API. Two adapter classes that
  differ only in configuration would mean two code paths to test and a "one-switch
  fallback" that is not actually one switch.
- `ManualMeetingLinkProvider` as the explicit escape hatch described above.
- The `LiveRoomProvider` discriminator records **which target served each room**, so a
  recording's provenance and a session's cost attribution survive the migration from
  Cloud to self-hosted. Rooms opened before the switch keep pointing at where their
  media actually lived.
- LiveKit SDK types stay inside the adapter namespace; SDK exceptions are translated to
  `ProviderException` subclasses at the boundary, per
  [21-architecture-tests-catalogue.md](../standards/21-architecture-tests-catalogue.md).
- The adapter is wrapped in `IProviderResilience<ILiveClassProvider>` with retry,
  timeout and circuit breaker read from `appsettings.Resilience:liveClassProvider:`.

### Runtime domain concepts

- `LiveRoom` — runtime room bound 1:1 to a `LiveSession` while open.
- `LiveRoomProvider` — discriminator (`livekit_self_hosted`, `livekit_cloud`,
  `manual_link`).
- `LiveRoomToken` — short-lived join token (≤ 5 min TTL), scoped per user + room +
  role.
- `LiveAttendance` — computed participation record per participant.
- `LiveRecording` — recording metadata + consent + retention.
- `LiveSessionEvent` — append-only event stream (`room_opened`, `participant_joined`,
  `screen_share_started`, …).

All six are `[TenantOwned]`; `LiveRoom` and `LiveAttendance` inherit the
`[OrganizationScoped]` marker from the `LiveSession` they hang off. Each gets an EF
global query filter and a Row Level Security policy from the canonical template in
[Database Standards](../standards/05-database.md).

### Classroom UX (MVP)

The target experience is an embedded classroom inside the LearnStack portal, not an
external Zoom or Google Meet link.

- Instructor joins from the portal.
- Learner joins from the portal.
- Backend issues scoped join tokens only for authorised participants.
- Role-based room permissions (instructor publishes by default; learner can request).
- Audio, video, screen sharing.
- Participant list.
- In-room chat via LiveKit data channels.
- Session start / end tracking from provider webhooks.
- Reconnection state visible to the user.
- Recording indicator visible to all participants whenever recording is active.

### Authorization surface

Joining a live session requires, in order:

1. Authenticated user ([Phase 02b](phase-02b-events-auth.md) +
   [Phase 03](phase-03-identity-admin.md)).
2. Membership in the resolved tenant and organization
   ([Phase 03](phase-03-identity-admin.md)).
3. Permission to join the session ([Phase 03](phase-03-identity-admin.md) +
   [Phase 07](phase-07-enrollment-learner-portal.md) entitlement check).
4. Resource-scope match — the user is a booked participant or an instructor on **this**
   session.
5. Recording consent, if recording is enabled for this session.

Failures at any layer produce a Problem Details response with a specific `code`
(`unauthorized`, `forbidden`, `recording_consent_required`); the classroom UI maps
these to actionable copy. The token is minted only after all five pass — a token that
exists is a token that was authorised, so an expired-token retry re-runs the whole
chain rather than refreshing a previously granted claim.

### Attendance

- Computed from `LiveSessionEvent` streams (`participant_joined`, `participant_left`,
  `network_drop`).
- Configurable rules per tenant: minimum duration, late join tolerance, rejoin
  coalescing. These are tenant configuration values, not tenant DSL — attendance is a
  platform computation with tenant-supplied thresholds.
- Attendance status drives no-show notifications via the
  [Phase 08a](phase-08a-assessment-notifications.md) notification engine, closing the
  loop [Phase 08b](phase-08b-scheduling.md) left open.

### Recording and consent

Recording is **opt-in**, off by default per tenant. The consent flow is part of the
join path, not a separate setup step. See
[16-media-pipeline.md](../architecture/16-media-pipeline.md) § Recordings.

- Per-tenant recording policy.
- Per-session recording flag.
- Per-participant consent state captured **before** the room is joined; absence blocks
  the join. Consent is not a checkbox the UI can skip past — the token is not issued
  without it.
- Recording metadata persisted: storage key, duration, consent state, retention
  deadline.
- Recording can be configured at policy / metadata level even when execution is
  disabled — for tenants that want the audit trail without the bandwidth.
- Consent grant and withdrawal are MUST-class audit operations per
  [ADR-0033](../decisions/0033-audit-durability-model.md): the row is written as
  durable intent inside the same transaction as the consent state change, so a crash
  cannot leave a recording whose consent trail is missing.
- LiveKit Egress writes the recording to S3 / SeaweedFS; LearnStack does not transcode
  in this phase. Transcoding and the media pipeline proper belong to
  [Phase 04](phase-04-cms-media-pages.md).

### Provider webhooks

- `https://api.<domain>/webhooks/livekit` with HMAC signature verification.
- Idempotent: `(provider, event_id)` deduplicated.
- Tenant id derived from the stored provider account, never trusted from the payload.
- Webhook receipt is the only path that opens and closes a `LiveRoom`. The classroom UI
  reports what it observes; the server records what the provider confirms.

### Cost instrumentation

The decision rule above is unactionable without measurement, so the counters ship in
this phase even though the reporting surface does not:

- Emitted here: per-session participant-minutes, downstream bytes, recorded minutes,
  concurrent-participant peak, egress CPU-seconds, and provider error counts — all
  carried on `LiveSessionEvent` and on OpenTelemetry metrics per
  [10-observability.md](../standards/10-observability.md).
- Reported in [Phase 09](phase-09-billing-integrations-analytics.md): the classroom
  usage and cost report that turns those counters into the monthly figures the
  break-even rule reads.
- Alerted in [Phase 11](phase-11-production-hardening.md): budget thresholds, and the
  cost-model recheck on the production-launch checklist.

A classroom that runs without these counters cannot answer "should we self-host yet?",
which means the answer defaults to "no" forever.

### Operational readiness

- LiveKit and Coturn have been in the dev compose file since
  [Phase 01](phase-01-repository-tooling.md).
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) leaves them out of the
  default local loop until a phase can call them; this is that phase, and it turns them
  back on. Local development runs against LiveKit OSS in compose regardless of which
  target production uses — the adapter is the same.
- Coturn (TURN / STUN) configured and reachable, and verified from a network that
  blocks UDP, which is where TURN either works or silently does not.
- LiveKit Cloud credentials live behind `ISecretProvider`, not in configuration files.

## Deliverables

- `ILiveClassProvider` port and the `LiveKitProvider` adapter, target-selectable by
  configuration, wrapped in `IProviderResilience<ILiveClassProvider>`.
- `ManualMeetingLinkProvider` escape hatch.
- In-app classroom UX MVP with audio / video / screen share / chat / participant list.
- Token issuance pipeline with TTL and role scoping.
- Attendance computation from event streams, and no-show notifications through the
  [Phase 08a](phase-08a-assessment-notifications.md) engine.
- Recording consent flow, recording metadata model, and the provider webhook handler.
- Classroom cost counters emitted and surfaced to
  [Phase 09](phase-09-billing-integrations-analytics.md)'s analytics pipeline.
- A written record of which LiveKit target production runs on, and the measured numbers
  that justify it.

## Completion Criteria

- Instructor and learner can join a scheduled session inside the portal.
- Token issuance is < 200 ms p95; classroom join time is < 1.5 s p95 (per
  [15-performance.md](../standards/15-performance.md)).
- Cross-tenant join attempt — identity `{tenantA}:{userId}` against a room owned by
  tenant B — is rejected, and the rejection is tested rather than assumed.
- Attendance reflects participant join / leave streams correctly across reconnections,
  including the case where a participant drops and rejoins inside the coalescing
  window.
- Recording cannot start without consent; consent state is recorded in `LiveRecording`
  and audited durably.
- Recording, when enabled, writes to S3 / SeaweedFS with the correct key prefix.
- The provider webhook handler is signature-verified, idempotent, and tenant-scoped.
- Switching the LiveKit target from Cloud to self-hosted is a configuration change with
  no recompile, demonstrated at least once in a non-production environment. Untested,
  this claim is a hope.
- The cost counters produce non-zero, plausible values for a real session.

## Risks

- **Treating live classes as meeting links** instead of a product capability. The
  `ManualMeetingLinkProvider` exists to serve a tenant edge case; if it becomes the path
  most sessions take, the phase failed.
- **The cost model's stale recommendation gets followed anyway.** Self-hosting at pilot
  volume costs more money and far more attention than Cloud, during the phase least able
  to spare either. The decision rule in this document overrides that recommendation
  until the cost model is rechecked.
- **The provider switch is never exercised.** A one-switch fallback that has never been
  switched is not a fallback. The completion criterion above exists because this failure
  is silent until the day it matters.
- **Cost counters slip to Phase 09.** They are cheap here and expensive to retrofit —
  they hang off `LiveSessionEvent`, which this phase defines. Without them the migration
  decision has no input.
- **Coupling core session logic to LiveKit SDK types**, guarded by the architecture test
  forbidding provider SDK imports in Domain / Application, and by the
  `ProviderException` translation rule at the adapter boundary.
- **Enabling recording without a consent and retention policy.** Recording is the single
  highest-consequence capability in the platform: it produces a durable artefact of
  identifiable people, often minors. Consent blocks the join; retention has a tenant-
  level cap; deletion is two-step.
- **Underestimating TURN / STUN and bandwidth operations** — see
  [08-livekit-cost-model.md](../architecture/08-livekit-cost-model.md). TURN failures
  look like "the app is broken" to the user and like nothing at all in the logs.
- **Treating this phase as demo-quality.** The classroom is the capability that makes
  LearnStack a platform for education businesses that teach live rather than a course
  catalog with videos.

## Phase Exit Decision

[Phase 09](phase-09-billing-integrations-analytics.md) begins when two people in
different networks can join a scheduled session from the portal and hold a working
class — audio, video, screen share, chat, reconnection — with attendance computed from
the event stream, recording blocked without consent and audited durably when granted,
the webhook handler signature-verified and idempotent, cross-tenant join attempts
rejected under test, and the classroom cost counters producing real numbers.

The provider question does not gate the exit. Running on LiveKit Cloud is the expected
state at exit; what gates the exit is that the switch to self-hosted has been performed
once outside production and the counters that will call for it are live.
