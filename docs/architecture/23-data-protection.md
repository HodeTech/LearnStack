# Data Protection (KVKK / GDPR)

LearnStack stores personal data about learners, instructors, guardians, tenant admins, and platform admins. The first vertical (English Learning) serves Turkish learners; KVKK is the baseline. EU-resident tenants will require GDPR. This document defines what the platform must support so a tenant can meet its obligations under either regime — and where each capability lives in the roadmap.

This document does not replace legal counsel. It is the **engineering** surface: the rights we honour, the data shapes that make them possible, and the operational practices.

## Personal Data Inventory

Every aggregate that may hold personal data must declare its PII category at design time. The categories used in LearnStack:

| Category | Examples | Treatment |
|----------|----------|-----------|
| **PII-Identity** | User email, display name, phone, address, national ID | Encrypted at rest where possible; redacted in logs; exportable; deletable on request. |
| **PII-Behaviour** | Learning progress, lesson views, assessment attempts, classroom attendance | Exportable as part of the user's data; anonymisable on deletion (replaced with a pseudonymous id). |
| **PII-Sensitive** | Recording audio / video, transcripts, medical accommodation notes (post-MVP) | Stored only when consent is captured; deletable with the recording itself; never logged. |
| **Payment** | Billing addresses, last-four card digits, invoice records | Retained 7 years for tax compliance (cannot be deleted on request); separate retention from other PII. |
| **Audit** | Audit log entries referencing the user | Retained per [18-audit-coverage.md](../standards/18-audit-coverage.md); deletion request anonymises the actor field but keeps the action record. |

Every aggregate's domain spec records its PII categories; a CI check ensures every entity with an email / phone / address column is annotated.

## Rights We Support

The capabilities below are the engineering primitives. The tenant-facing data-protection UX (consent forms, privacy policy, DSAR portal) is built on top.

### Right of Access (Data Export)

Both KVKK Madde 11 ("kişisel verileri elde etme") and GDPR Article 15 require giving the user their data.

- The user (or the tenant admin on their behalf) requests an export.
- The platform emits a `DataExportRequestedV1` integration event.
- Each module exports its data for that user into a structured JSON bundle.
- The bundle is delivered as a downloadable file with a signed URL; expires in 7 days.
- Exports include: identity, memberships, profiles, enrollments, progress, attempts, bookings, attendance, recordings (metadata + signed download links), audit entries where the user is the actor.
- Exports exclude: another user's data, tenant-internal admin notes about the user, internal system identifiers that have no meaning outside LearnStack.

### Right to Rectification

Profile edit is the everyday path. The platform must accept name / email / phone updates via the user's own profile screen or via the tenant admin's user management screen.

- Email change re-runs Keycloak's email verification.
- Audit entries record the change.

### Right to Erasure (Right to be Forgotten)

Both regimes recognise exceptions (legal obligation, contract performance). LearnStack distinguishes:

- **Soft delete** — sets `deleted_at` / `deleted_by`; row is hidden from queries but retained per aggregate retention policy.
- **Anonymisation** — replaces PII fields with pseudonymous values; the row stays for analytics / audit integrity.
- **Hard delete** — row is removed; allowed only when no retention obligation applies.

Per category:

- PII-Identity → anonymisation (`name = "deleted user"`, `email = "deleted_<id>@learnstack.invalid"`).
- PII-Behaviour → anonymisation (progress / attempts retained for cohort statistics; actor field is replaced).
- PII-Sensitive → hard delete (recordings deleted from storage; metadata anonymised).
- Payment → retained for legal period; user notified of the exception.
- Audit → actor field anonymised; action record retained.

Deletion is a workflow, not a single SQL statement:

1. Tenant admin (or user via self-service post-MVP) initiates the request.
2. Platform validates eligibility (no active payment dispute, no legal hold).
3. `UserAnonymisationRequestedV1` event published.
4. Each module consumes the event and performs its part: anonymise rows, delete storage objects, invalidate Keycloak user.
5. A final reconciliation job confirms every module reported completion within 30 days. Failures escalate to platform admin.

### Right to Restriction of Processing

A learner can request that processing be paused while a dispute is resolved. Implementation: a tenant-scoped flag on the user that gates analytics ingestion and notifications. Reads continue (the user can still log in and see their own data) but no new events are processed.

### Right to Object

Specific to direct marketing under GDPR. Notifications module honours per-channel opt-out at the user level.

### Right to Data Portability

The export bundle is structured JSON suitable for machine reading. Future LTI / xAPI export is a natural extension.

## Consent Management

Consent is captured per purpose; the model lives in the Tenancy module:

- **Terms of service** — accepted at signup; version-pinned.
- **Privacy policy** — same pattern; tenant-specific URL.
- **Recording consent** — per-session (see [16-media-pipeline.md](16-media-pipeline.md)).
- **Marketing communications** — opt-in by default off; per-channel.
- **Analytics tracking** — opt-out post-MVP; honoured by the analytics ingestion layer.

Consent records are append-only; "I changed my mind" creates a new record with a different state, never edits the old one.

## Data Residency

LearnStack supports **one geographic region per deployment instance** for the MVP.
Tenants subject to data-residency obligations (Turkish data on Turkish soil, EU data
in EU, KSA data in KSA) are served by deploying a **regional instance** of the
platform; cross-region replication of tenant content is out of scope until enterprise
demand justifies it. The choice is documented in tenant onboarding.

### How "data residency" surfaces today

Per [ADR-0021](../decisions/0021-feature-based-entitlement.md), the entitlement
projection carries a `compliance.data_residency` cap:

```json
{
  "compliance": {
    "data_residency": {
      "forced": true,
      "region": "eu-west"
    }
  }
}
```

When the cap is set, the LearnStack admin surface refuses to enable any storage /
provider option whose region does not match. **Today the only enforcement is at the
deployment-mode-and-config level** — there is no runtime cross-region routing inside
a single LearnStack deployment. In practice:

| Deployment mode | Residency enforcement |
|-----------------|-----------------------|
| `SaaS` | Hub provisions the tenant on the regional instance whose `region` matches the plan's `data_residency.region`. Cross-region tenant moves are operator-driven (back up + restore in target region; not online). |
| `Dedicated` | The dedicated cluster is region-pinned at provisioning. Hub records the region in the entitlement projection; tenant-side admin sees it read-only. |
| `SelfHosted` | The customer chooses the deployment region. The signed license key can carry a `region` claim that LearnStack core validates against `IConfiguration["Deployment:Region"]`; mismatch refuses to start. |

### Per-component region pinning

Within a regional instance, the following components must run in-region:

- PostgreSQL primary + WAL archive.
- Redis (entitlement cache, L1 invalidation).
- MinIO / S3-compatible object storage (recordings, media).
- Meilisearch.
- Kafka.
- LiveKit SFU + Egress (a learner joining from another continent still has their
  media routed through the in-region SFU; this is acceptable latency-vs-residency
  trade-off; cross-region SFU is post-MVP).

Vault may run regionally **or** be replicated cross-region for operator-key
distribution — secret content is encrypted at rest, and the trade-off is operational
not residency-bound.

### Cross-region operations that remain centralised

Two flows legitimately cross regions:

- **LearnStack ↔ Hub** internal API. Hub may live in a single region (e.g. eu-west)
  while serving tenants in multiple regions. The four-endpoint contract is small,
  audit-friendly, and carries no tenant content — only plan / entitlement / license
  metadata. Tenants whose plan forbids cross-region operator access are served by a
  region-local Hub instance (Hub federation is a Phase-11+ topic, not MVP).
- **Telemetry → centralised OpenTelemetry collector**. Trace and metric data can be
  shipped cross-region for unified observability; PII redaction in the pipeline keeps
  this clear of residency obligations. Tenants with stricter rules get a region-local
  collector with no upstream forwarder.

### Open questions (Phase 11+)

- Multi-region tenant pools where one tenant has learners across regions.
- Active-active region pairs with synchronous replication.
- Per-organization residency overrides within a single tenant (rare).
- Cross-region disaster-recovery RPO/RTO targets (`docs/runbooks/dr.md`, Phase 11).

These are tracked as Phase-11+ work and are not implementation blockers for the MVP.

## Processor Agreements

For tenants subject to KVKK / GDPR, LearnStack acts as a **data processor** while the tenant is the **data controller**. The processor agreement template lives outside this repository (legal). Engineering-side commitments:

- Sub-processor list maintained in the tenant onboarding pack (Keycloak host, LiveKit host, S3 / MinIO provider, email provider, SMS provider, payment provider).
- Sub-processor changes are notified to tenants 30 days in advance via the Tenancy module's notification channel.
- Security incident notification timeline: within 72 hours of discovery (GDPR Article 33 baseline).

## Operational Requirements

- **PII redaction in logs.** Configured at the logging pipeline; the `[PiiSensitive]` attribute on a property excludes it from log emission ([10-observability.md](../standards/10-observability.md)).
- **PII redaction in error reports.** Sentry receives redacted payloads; integration tests assert the redaction layer cannot be bypassed.
- **Encryption at rest.** Storage provider (S3 / MinIO) + PostgreSQL volumes use the provider's at-rest encryption; specific configuration documented in [12-infrastructure.md](../standards/12-infrastructure.md).
- **TLS in transit.** [11-security.md](../standards/11-security.md) § Transport.
- **Backups.** Per-tenant deletion must propagate to backups within the platform's RPO + 1 backup cycle; the `delete-from-backups` job runs nightly.
- **Subject-access timeline.** GDPR Article 12 requires response within 30 days. Engineering target: export bundle ready within 7 days of request; deletion completed within 30 days.

## Roadmap Touchpoints

- **Phase 04** — First content holding PII (page metadata, content entries with author fields). Categories declared.
- **Phase 07** — Learner portal stores PII-Behaviour. Export skeleton lands.
- **Phase 08c** — Recordings (PII-Sensitive). Consent flow + retention.
- **Phase 09** — Billing payment data. 7-year retention.
- **Phase 11** — Full data-export pipeline, anonymisation workflow, sub-processor list, incident-notification runbook, KVKK / GDPR compliance review.

A separate data-protection runbook (`docs/runbooks/data-protection.md`) lands in Phase 11 and links to this architecture document.

## Risks

- **Deletion incompleteness.** A module that doesn't subscribe to the anonymisation event leaks the user's data. Mitigation: architecture test asserts every tenant-owned aggregate either anonymises or hard-deletes on `UserAnonymisationRequestedV1`.
- **Backup re-introduction.** Restoring a backup re-introduces deleted PII. The post-restore job replays the deletion log against the restored state. Documented in the DR runbook.
- **Cross-tenant data in exports.** An export must never include another user's identifiable data. Cohort statistics are aggregated and anonymous; integration tests assert this.
- **Sub-processor surprise.** Adding a new sub-processor without notice breaks the processor agreement. The Tenancy module tracks the sub-processor list and emits a notice 30 days before any change.
- **Audit-vs-erasure tension.** Audit entries must be retained for accountability but cannot identify a deleted user. Resolved by anonymising the actor field while keeping the action record.
