# Live Classroom Cost Model

This document captures the cost model for LearnStack's in-app classroom, including both **LiveKit OSS self-hosted** (the default) and **LiveKit Cloud** (optional). It also explains why a fully custom WebRTC stack is not on the table.

Pricing values are based on public LiveKit Cloud pricing and public infrastructure provider rates reviewed on 2026-05-14. Numbers must be rechecked before any production commitment. The production release checklist in [Phase 11](../roadmap/phase-11-production-hardening.md) explicitly includes this recheck.

## Three Operating Modes

### 1. LiveKit OSS Self-Hosted (Default)

LearnStack runs the LiveKit media server itself, with no LiveKit Cloud fees.

LearnStack owns:

- Compute for the SFU and Egress workers.
- Outbound bandwidth (often the largest single line item).
- TURN/STUN infrastructure (Coturn).
- Redis for multi-node coordination.
- Object storage for recordings.
- Monitoring, alerting, scaling, regional deployment, incident response.

**When this wins:** when bandwidth pricing is controlled (Hetzner, OVH, Contabo, or bandwidth-included colo), when recording volume is non-trivial, when long-term cost predictability matters, or when compliance requires private infrastructure.

### 2. LiveKit Cloud (Optional)

Managed by LiveKit Inc, billed on usage.

Primary cost drivers:

- WebRTC participant minutes.
- Downstream data transfer.
- Recording / egress / transcoding minutes.
- Optional AI, transcription, telephony, or agent features.

**When this wins:** very early pilot, no DevOps capacity, unpredictable usage spikes, multi-region requirement before LearnStack is ready to operate multi-region.

### 3. Custom WebRTC Stack (Not on the table)

This would mean building a custom SFU on top of an open-source WebRTC library (Mediasoup, Pion, Janus). We explicitly chose against this; see [ADR 0005](../decisions/0005-live-classroom-media-stack.md). The cost arithmetic is in the comparison below.

## Cost Comparison: Same Usage Across Three Modes

**Workload:** 1,000 learners × 4 sessions per month × 60 minutes × 2 participants (1-on-1) = 480,000 participant minutes, ~3.4 TB downstream bandwidth, no recording.

| Line item | LiveKit Cloud (Ship plan) | LiveKit OSS self-hosted (Hetzner) | LiveKit OSS self-hosted (AWS) | Custom Mediasoup (Hetzner) |
|---|---|---|---|---|
| Base / minutes | $50 + $165 overage | $0 | $0 | $0 |
| Bandwidth | $381 | ~$0 (in plan) | ~$310 | ~$0 (in plan) |
| Compute (SFU + Redis + TURN) | included | ~$150 | ~$200 | ~$150 |
| Storage (recordings) | included up to 600 min | $5 | $5 | $5 |
| Engineering (amortised) | ~$0 | ~$300/mo SRE slice | ~$300/mo SRE slice | **~$3,000–5,000/mo for 12 months, then ongoing** |
| **Monthly steady-state** | **~$596** | **~$155** | **~$515** | **~$3,155–5,155** |

Notes:

- The "engineering amortised" line for the custom Mediasoup column reflects 6–12 months of senior development to reach feature parity with LiveKit OSS, then ongoing maintenance. This is the line that kills custom builds for a small team.
- Hetzner bandwidth quota includes 20 TB on most VM tiers; LiveKit traffic at this scale fits comfortably. AWS charges egress at roughly $0.09/GB after the first 100 GB, which is where the AWS column climbs.
- LiveKit Cloud numbers come from the LiveKit Cloud pricing page (see snapshot below).

## LiveKit Cloud Plan Snapshot (2026-05-14)

| Plan | Base | Included WebRTC minutes | Overage / min | Included downstream | Overage / GB | Concurrent connections |
|---|---|---|---|---|---|---|
| Build | $0/mo | 5,000 | n/a | 50 GB | n/a | 100 |
| Ship | $50/mo | 150,000 | $0.0005 | 250 GB | $0.12 | 1,000 |
| Scale | $500/mo | 1,500,000 | $0.0004 | 3 TB | $0.10 | 5,000 |

Recording/egress (same plans):

- Build: 60 included video transcode minutes.
- Ship: 600 included, then $0.02 / video minute.
- Scale: 8,000 included, then $0.015 / video minute.

## Formulas

Participant minutes:

```text
participant_minutes = participants × session_duration_minutes
```

Approximate downstream bandwidth (GB):

```text
bandwidth_gb = (avg_downstream_mbps_per_participant × session_duration_seconds × participants) / 8 / 1024
```

Recording transcode minutes:

```text
recording_minutes = recorded_session_duration_minutes
```

Self-hosted SFU compute estimate (rough):

```text
sfu_cores_needed ≈ ceil(concurrent_participants / 250)
sfu_ram_gb       ≈ 4 × sfu_cores_needed
```

Egress (recording) compute estimate (rough):

```text
egress_cores_needed ≈ ceil(concurrent_recordings × 1.5)
```

A single 4-core SFU node comfortably handles ~250 concurrent participants for typical education video bitrates. Egress is CPU-bound; one composite recording is roughly 1.5 cores at 720p.

## Scenarios

### Scenario A: 100 one-on-one sessions per month

- 100 sessions × 60 min × 2 participants = 12,000 participant minutes.
- Downstream at 1 Mbps each: ~84 GB.
- No recording.

| Mode | Estimated monthly cost |
|---|---|
| LiveKit Cloud Ship | $50 (well within included quota) |
| LiveKit OSS Hetzner | ~$130 (1 small SFU + Redis + TURN) |
| LiveKit OSS AWS | ~$140 |

At this scale, **LiveKit Cloud is cheaper** because the fixed infra cost of self-hosting is not yet amortised.

### Scenario B: 1,000 one-on-one sessions per month (no recording)

- 1,000 × 60 min × 2 = 120,000 participant minutes.
- Downstream: ~840 GB.
- No recording.

| Mode | Estimated monthly cost |
|---|---|
| LiveKit Cloud Ship | $50 base + ~$71 transfer overage ≈ **$121** |
| LiveKit OSS Hetzner | ~**$150** |
| LiveKit OSS AWS | ~$220 |

Roughly break-even between Cloud and self-host at this scale.

### Scenario C: 100 four-person group sessions per month

- 100 × 60 min × 4 = 24,000 participant minutes.
- Downstream at 1.5 Mbps: ~251 GB.
- No recording.

| Mode | Estimated monthly cost |
|---|---|
| LiveKit Cloud Ship | ~**$50** (just barely over transfer quota) |
| LiveKit OSS Hetzner | ~**$150** |

Cloud still wins at this volume.

### Scenario D: 1,000 one-on-one sessions + recording every session

- 1,000 sessions × 60 min recorded = 60,000 recording minutes.
- Ship plan includes 600 minutes. Overage: 59,400 × $0.02 = **$1,188**.
- Cloud total: ~$121 (live) + $1,188 (recording) = **~$1,309/mo**.

| Mode | Estimated monthly cost |
|---|---|
| LiveKit Cloud Ship | ~$1,309 |
| LiveKit OSS Hetzner | $150 SFU + $80 dedicated Egress node + $25 storage ≈ **$255** |

**This is where self-hosting pays back massively.** Recording is the line item that flips the decision.

### Scenario E: 5,000 one-on-one sessions + recording every session

- 5,000 × 60 = 600,000 participant minutes.
- Downstream: ~4.2 TB.
- Recording: 300,000 minutes.

| Mode | Estimated monthly cost |
|---|---|
| LiveKit Cloud Scale | $500 base + ~$120 transfer overage + $4,380 recording overage ≈ **$5,000** |
| LiveKit OSS Hetzner | 3× SFU + 2× Egress + Redis + TURN + 1 TB recording storage ≈ **$650–800** |

By this scale, the self-hosted advantage is ~6×.

## Where the Custom-WebRTC Math Falls Apart

The non-engineering cost of a custom Mediasoup or Pion stack is similar to self-hosted LiveKit. The engineering cost is the killer:

- 6–12 months of senior engineering to reach feature parity (signaling, room state, simulcast, reconnection, recording, web/iOS/Android SDKs).
- Ongoing maintenance — WebRTC moves, browsers ship breaking changes, simulcast strategies evolve.
- LearnStack's product surface is large enough already. WebRTC infrastructure is not a competitive moat for an education platform.

This is the basis for the firm decision in [ADR 0005](../decisions/0005-live-classroom-media-stack.md).

## Product Implications

- Recording is **off by default**, per session and per tenant.
- Each tenant can enable recording, with retention configurable up to a tenant-level cap.
- Track classroom cost metrics (participant minutes, bandwidth, recording minutes, egress CPU) from the first production deployment. See [Phase 09](../roadmap/phase-09-billing-integrations-analytics.md).
- Default retention: 30 days. Long-term retention requires explicit tenant action.
- Recording files live in S3/MinIO; metadata lives in PostgreSQL. Deletion is a two-step process.
- Bandwidth provider choice for the SFU node is a real architectural decision. Default to a bandwidth-friendly provider; only run the SFU on AWS/GCP when there is a regulatory or proximity reason.

## Recommendation

1. Start development on **LiveKit OSS** locally via Docker Compose (Phase 01).
2. First production deployment: **LiveKit OSS self-hosted** on a bandwidth-friendly provider, single region close to the primary user base.
3. Keep `LiveKitCloudProvider` ready behind the same `ILiveClassProvider` so a sudden need for managed cloud can be served with one configuration switch.
4. Do not enable recording globally; let it be a per-tenant, per-course decision.
5. Add classroom cost dashboards before opening the in-app classroom to paying users (Phase 11).
