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
| **Audit** | Audit log entries referencing the user | Retained per [18-audit-coverage.md](../standards/18-audit-coverage.md); bounded redaction preserves the action record and immutable identifiers. See [erasure prerequisites](#erasure-authority-and-first-writer-prerequisites). |

Every aggregate's domain spec must record its PII categories.
[Phase 03 G19](../roadmap/phase-03-identity-admin.md#the-decision-register) owns the
classification census and planted missing-annotation control before its first
identity PII writer; G9 extends it for custom-field revision history. The
implemented mapped-property census requires explicit semantics; it does not infer personal data from an email/phone/address name or arbitrary stored text.

<a id="proposed-identity-foundation-and-classification-ownership"></a>

### Identity foundation and classification ownership

**Classification implemented in P03-1 Step 2; global Identity storage/common
audit implemented in Step 3; contextual storage and fulfillment remain pending.** Jointly Accepted
[ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md) and
[ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md)
require these controls before the first identity writer. Step 3 adds 17 reviewed
global scalars, bringing the census to six contexts, 21 entities and 218 scalar
properties. Neither classification nor storage fulfills an erasure request.

P03-1 Step 2 implements reviewed declarations in each module's EF mappings and
`PersonalDataModelCensus` in common Infrastructure. Each module owns declarations
for every property in its mapped models, including inherited references. The Infrastructure data-protection coordinator
and `DataProtectionConventionTests` compare those declarations with the complete,
nonempty EF property population before identity writes.
`Mapped_Properties_Have_Explicit_Personal_Data_Classification` is Implemented
in the [architecture catalogue](../standards/21-architecture-tests-catalogue.md),
with real production-model omission and positive controls. An
undeclared property fails; `None` requires an explicit nonpersonal purpose and is
never a default. Module audit specs own exact reference/payload paths and snapshot
redaction markers. The explicit common audit-column declarations live in
[AuditColumnMapping](../../backend/src/LearnStack.Infrastructure/Persistence/AuditColumnMapping.cs):
actor references are Identity, change timestamps Audit, and the technical
row-version counter explicitly None. Classification never reads row values or
replaces snapshot markers, disclosure authority or erasure/retention rules. SQL-only, control and vendor stores stay in their existing
inventories; this check creates no duplicate durable-store inventory.

The Accepted User contract retains a stable authority identifier, subject key, actor and
admission lifecycle. Raw subject is required while Active, explicitly marked
`[PiiSensitive]` for snapshot capture, and nullable for removal at Closed by the
authorized P03-10 closure writer. No raw issuer is copied onto each User. The
platform-scoped, application-read-only `identity_authority` singleton owns the
stable authority UUID and current exact issuer URI. The deployment installer
creates it once with migration credentials before bootstrap; application code
cannot insert, update or automatically replace it. Configuration issuer drift
refuses identity admission. P03-9/G13 owns an approved issuer rebind before the
first change, proving provider continuity and freezing bootstrap and old grants
while preserving authority, keys, actors and closed tombstones. Until that owner
is implemented, issuer renaming is unsupported; aliases and email matching supply
no continuity proof.

The subject key is SHA-256 over domain-separated, length-delimited stable
authority UUID and exact opaque subject, using the
[canonical Standard 05 encoding](../standards/05-database.md#proposed-p03-1-identity-storage-annex).
It supports
stable equality and audit linkage, not secrecy: low-entropy subjects remain
vulnerable to dictionary guessing, and no provider entropy guarantee is assumed.
HMAC is not selected; selecting it for secrecy would also require durable key
custody, rotation and restore decisions. Hash and actor linkage are PII-Identity,
retained under their declared purposes, not anonymous data. An Active key match
with a different raw subject refuses; a Closed key match always refuses admission
and never merges identities. The permanent key/actor tombstone prevents
recreation indefinitely; closure removes the raw subject rather than claiming
that the retained tombstone is erased.

P03-1 adds no email/verification columns or claim processing. A first earlier
consumer or P03-6/G10 owns the minimum mirror fields and freshness contract before
their writer. Snapshot markers, diagnostic controls and semantic classification
remain separate requirements; none implies general exception-message scrubbing.

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

Tenant/org RLS is one boundary, not the subject predicate. Every participating
module additionally identifies the requested person and permitted recipient/
purpose, including third-party references inside shared JSON/audit records.
[Phase 03 G15/G16](../roadmap/phase-03-identity-admin.md#the-decision-register)
settle self/admin/delegate and org scope, eligibility/holds, artifact retrieval
authorization/revocation, expiry, cleanup and durable progress before their first
writers. PII category alone does not decide disclosure. The private artifact needs
an explicit Phase 03 prerequisite slice or an approved replacement contract; the
full Phase 04 media and Phase 08a notification engines are not implicit blockers.

### Right to Rectification

Profile edit is the everyday path. The platform must accept name / email / phone updates via the user's own profile screen or via the tenant admin's user management screen.

- Email change re-runs Keycloak's email verification.
- Audit entries record the change.

This does not grant tenant admins a global identity editor. Provider canonical
email stays mirror-owned; contextual contact data stays membership-owned under
[Phase 03's ownership table](../roadmap/phase-03-identity-admin.md#tenant-data-ownership-dsar-boundary-and-pii-classification).
G1/G3 decide trusted mirror updates and self/admin field permissions before those
writers. A matching email never merges global identities or grants membership.

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
- Audit → redact the six permitted human-facing columns; retain the action record.
  Immutable identifiers and metadata require the bounded decision below before the
  first erasure writer can claim complete anonymisation.

Deletion is a workflow, not a single SQL statement — and there are **two** of them,
separated by authority. Conflating them lets a tenant admin close a person's account
([Phase 03 § Tenant Data Ownership](../roadmap/phase-03-identity-admin.md)).

**Tenant-scoped erasure.** Initiated by a tenant admin, or by the person acting inside
that tenant.

1. Eligibility is validated (no active payment dispute, no legal hold).
2. The `Membership` and its `MembershipProfile` are removed.
3. That tenant's behaviour rows are anonymised; its audit entries keep the action
   record, with redaction subject to the [first-writer prerequisites](#erasure-authority-and-first-writer-prerequisites).
4. The `users` row survives for as long as any membership remains anywhere. Nothing in
   this flow touches Keycloak.

G15 defines the authorized subject/org scope: an org admin cannot erase a person's
other branch, and the subject needs a request/status path without admin rights.
G16 retains sufficient workflow authority and a scoped status receipt after the
membership is removed; it settles the last-membership case without silently
turning tenant erasure into provider-account closure.

**Global account closure.** A **platform-scoped** operation. There is no tenant-admin
endpoint for it.

The person authorizes the account request through its separate surface; this
does not assign the person a Platform permission. Phase 03 G13/G16 distinguish
requesting person, any required approver, service actor and privileged executor.
The participant inventory/write fence must reconcile new memberships, pending
invites and delayed mirrors so completed closure cannot recreate account data.

1. The platform admin enters scope through the audited `EnterPlatformAdminScope(reason)`
   — the only path that reads across tenants.
2. Eligibility is validated (no active payment dispute, no legal hold).
3. `UserAnonymisationRequestedV1` fans out to every tenant the person belongs to.
4. Each module consumes the event and performs its part: anonymise rows, delete storage
   objects. **The Keycloak user is invalidated once, at the end, by the platform-scoped
   step — never by a module reacting to a tenant-scoped request.**
5. A final reconciliation job confirms every module reported completion within 30 days.
   Failures escalate to platform admin.

### Durable asynchronous store inventory

This is the canonical inventory for the durable asynchronous copies introduced by
Phase 02b, including references added to existing audit storage. Identifier-only
values can still identify a person; absence of free text is no erasure exemption.
The inventory records subject locators and existing constraints. It grants no
erasure authority and selects no retention period or deletion order.

The ten first-party tables are in LearnStack's database. Canonical DDL and grants
remain in Database Standards: [Outbox](../standards/05-database.md#outbox),
[Inbox and delivery ledger](../standards/05-database.md#inbox), and
[Background jobs](../standards/05-database.md#background-jobs).

<!-- async-store-first-party:start -->
| Store | Copies and subject linkage | Existing constraint |
|---|---|---|
| `outbox_messages` | `actor_user_id`, identifier-only `payload`, tenant/org, partition/causation/event IDs and `correlation_id`; new producer `metadata` is SQL NULL | Platform has SELECT/DELETE, no envelope rewrite grant |
| `event_deliveries` | Independent per-subscription envelope/`payload` copy with `actor_user_id`, tenant/org, causation/trace/partition, lease, closed failure descriptor and dead-letter/replay state | No DELETE grant to runtime roles; producer cleanup does not erase this copy |
| `inbox_messages` | `(tenant_id, subscription_id, event_id)` and `recorded_at`; indirect subject linkage without actor, payload or org columns | Immutable completion history; no generic Platform purge |
| `background_jobs` | `causal_actor_user_id`, identifier-only `parameters`/`plan`, trace/causation, lease, closed failure/terminal descriptors and parent/recipient/operation/final-attempt links | Envelope/prepared facts stay immutable; uncertain effects retain occupancy |
| `background_job_attempts` | `job_id`, identifier-only `outcome`, operation/quiescence IDs, fingerprint, selected-outcome link and finite `failure_policy` | Write-once retry/effect/cleanup facts are not a mutable person profile |
| `background_job_capacity` | `tenant_id`, finite capacity-group key and timestamp | Coordination metadata; no person content or person-erasure target |
| `background_job_reservations` | Tenant/job/attempt linkage, finite group and reservation/release facts | Erasure does not prove quiescence and cannot free unresolved occupancy |
| `background_job_recurrences` | Finite definition/revision/manifest/target/schedule/recipient-policy metadata; no user or arbitrary parameters | Infrastructure control; no ordinary app access |
| `background_job_occurrences` | Manifest/slot identity and frozen tenant/org sweep bounds/checkpoints; no person column or arbitrary payload | Preserve occurrence history and monotonic progress |
| `background_job_recipients` | Immutable tenant/org route, occurrence/manifest/target and accepted child-job receipt | Recipient is a tenant/org route, not a person; reciprocal job links constrain future purge order |
<!-- async-store-first-party:end -->

The complete Platform migration census also includes this explicit scope exclusion.
Exclusion from this asynchronous inventory is not an erasure exemption.

<!-- async-store-exclusions:start -->
| Excluded table | Scope boundary |
|---|---|
| `idempotency_keys` | Synchronous HTTP request replay, outside asynchronous transport scope; cached body/headers can still contain personal data |
<!-- async-store-exclusions:end -->

Other module tables remain explicit scope exclusions, so a new table cannot
silently escape the complete migration census. Their own module specifications
govern personal data; exclusion here grants no erasure exemption.

<!-- async-store-module-exclusions:start -->
| Excluded table | Scope boundary |
|---|---|
| `tenants` | Tenancy aggregate |
| `organizations` | Tenancy aggregate |
| `tenant_domains` | Tenancy host configuration |
| `tenant_locales` | Tenancy locale configuration |
| `tenant_settings` | Tenancy configuration |
| `tenant_feature_flags` | Tenancy feature configuration |
| `platform_entitlement_cache` | Entitlement projection |
| `platform_host_to_tenant` | Host-resolution projection |
| `platform_killswitches` | Platform feature configuration |
| `customization_generations` | Customization publication metadata |
| `tenant_content_types` | Customization definitions |
| `tenant_level_taxonomies` | Customization definitions |
| `tenant_level_taxonomy_items` | Customization definition items |
| `courses` | Education aggregate |
| `lessons` | Education aggregate |
| `course_translations` | Education localized content |
| `lesson_translations` | Education localized content |
| `audit_config` | Audit coverage configuration |
| `users` | Synchronous global identity binding/tombstone; retained raw subject, digest and creation-audit linkage follow the Identity contract and P03-10 closure owner |
| `identity_authority` | Deployment-owned stable authority registration, not a per-person profile; installation and rebind remain separately confined |
<!-- async-store-module-exclusions:end -->

LearnStack job-table no-delete triggers also bind the owner; BYPASSRLS is not
an exemption. Restrictive job/attempt/recipient foreign keys constrain deletion
order. No ordinary erasure operation can remove those facts under current DDL.

All twelve guarded Hangfire schema-23 tables are in `public`, in the same database.
The pinned table/column census is `HangfireStorageContract.Columns`; canonical
[vendor storage definitions and guards](../standards/05-database.md#p02b-4-stock-hangfire-control-storage)
forbid business payloads and credentials. Opaque identifiers remain linkable.
Vendor maintenance grants are confined transport authority, not a person-erasure
API or a generic Platform grant.

<!-- async-store-vendor:start -->
| Vendor table | Copy or control classification |
|---|---|
| `schema` | Schema-version bookkeeping; no person content |
| `counter` | Finite control counters and expiry; no person content |
| `aggregatedcounter` | Aggregated control counters and expiry; no person content |
| `hash` | Guarded recurrence/control fields and values; opaque job references can link to authoritative rows |
| `job` | Guarded invocation/argument wakeup references and state linkage; no business parameters |
| `state` | Job/state linkage, finite state metadata and redacted reason/data |
| `jobqueue` | Opaque job reference, finite queue and fetch metadata |
| `list` | Guarded control keys/values; opaque references remain linkable |
| `server` | Guarded server identity, queue/worker and heartbeat metadata |
| `set` | Guarded control/recurrence keys and opaque job references |
| `jobparameter` | Job-linked guarded parameter name/value metadata |
| `lock` | Guarded resource, acquisition and update-control metadata |
<!-- async-store-vendor:end -->

The inventory also covers these distinct copies and custody boundaries:

<!-- async-store-custody:start -->
| Store or custody boundary | Subject linkage and limit |
|---|---|
| Existing `audit_log` | Asynchronous terminal/causal `metadata`, including `causal_actor_user_id`, beside immutable actor/entity/causation identifiers. Redacting human-facing fields leaves references; see [Audit data model](31-audit-subsystem.md#7-data-model) |
| Development Keycloak user events, `EVENT_ENTITY` | Provider `USER_ID`, `SESSION_ID`, `IP_ADDRESS`, client/realm and event-detail JSON. Separate provider storage and identity namespace; account invalidation does not erase prior events. Selection and qualified finite retention remain under [ADR-0070](../decisions/0070-owned-tenant-bff-sessions.md#reads-and-durable-event-ownership) |
| External diagnostics and local error files | Log, trace, Sentry and `LocalFileErrorTracker` JSON archives can retain linkable exception/context copies; source-row deletion does not erase them |
| Database, WAL and backups | Retained copies need backup and post-restore reconciliation; source-row deletion alone does not remove them |
| Native BFF session custody | `InMemoryBffSessionStore` is volatile process-local custody under ADR-0070, not a durable ledger. Local invalidation does not erase provider events or diagnostic archives |
<!-- async-store-custody:end -->

ADR-0072/0073 add identity linkage to the existing `audit_log` boundary above,
not another physical store or custody owner. Implementation remains P03-1 Step 3:
`metadata.identity_subject_key` carries the stable binding key, including failed
attempts without a User; proposed target and action references remain personal
data. Existing SecurityEvent retention applies, and metadata stays outside the
six-column UPDATE grant. P03-10 locates and reports retained linkage under the
same inventory boundary.

### Erasure authority and first-writer prerequisites

- [Phase 03](../roadmap/phase-03-identity-admin.md#tenant-data-ownership-dsar-boundary-and-pii-classification)
  implements tenant-scoped DSAR export/erasure, separate global account closure,
  subject/actor mapping, request authority and audit redaction. Its first writer
  must account for every applicable reference in this inventory. This does not
  transfer those deliverables to Phase 11.
- [Phase 11](../roadmap/phase-11-production-hardening.md#data-operations) extends
  those workflows across later modules and retained copies, and owns purge,
  retention, backup, provider/diagnostic and operational reconciliation.
- Before either phase's first affected writer, its bounded decision names the
  subject mapping, permissible transformations, exact principal and minimum
  grants, immutable-fact treatment, dependency/deletion order, replay/deduplication
  floor and evidence of completion. No such authority or policy is chosen by this
  inventory. Ordinary SystemActor delivery and dispatcher credentials supply none.
- `learnstack_platform` has no grants on inbox, delivery or LearnStack job
  tables. `BYPASSRLS` bypasses policies, not SQL privileges, no-delete triggers or
  write-once facts. Any required authority change needs its own bounded decision
  and migration before the writer ships.
- Live/uncertain effects, reservation accounting, immutable execution evidence,
  monotonic progress and same-transaction MUST audit remain intact. Erasure,
  elapsed time and operator authority prove neither quiescence nor permission to
  reset replay or release capacity.
- Audit redaction currently permits only `actor_email`, `ip_address`,
  `user_agent`, `before_state`, `after_state` and `changes`.
  `actor_user_id` remains immutable and `metadata` is outside that grant
  ([Audit append-only enforcement](31-audit-subsystem.md#append-only-enforcement)).
  Phase 03 must decide the surviving actor/entity/causation references and new
  causal metadata before its redaction/erasure handler lands; this inventory
  does not claim that complete anonymisation already works.

**Accepted identity fulfillment ownership — implementation remains P03-10.**
P03-10's Identity closure coordinator owns the authorized lifecycle transition
and raw-subject removal. The Audit redaction participant uses
`IUserReferenceLocator` to locate exact global failed-attempt paths by subject key
even when no User was committed, together with successful actor references. Its
request/result contract reports retained immutable linkage and the applicable
retention truthfully; it does not call the tombstone or subject key anonymous.
The key/actor tombstone serves indefinite anti-recreation, while audit copies
follow the SecurityEvent period in [Audit Coverage](../standards/18-audit-coverage.md#retention).
This foundation contract does not widen metadata UPDATE grants. P03-10 settles exact
principal, minimum grants and participant completion before its first writer;
Phase 11 owns later purge, backup and retained-copy reconciliation operations.

### Right to Restriction of Processing

A learner can request that processing be paused while a dispute is resolved.
The restriction belongs to the tenant/membership context, never a tenant-authored
column on global `User`. [Phase 03 G3/G15/G16](../roadmap/phase-03-identity-admin.md#the-decision-register)
settle its storage, authority and fulfillment before the writer. The intended
restriction gates analytics ingestion and notifications while preserving the
person's authorized access to their data; this paragraph claims no implemented
restriction mechanism.

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
- Valkey (entitlement cache, L1 invalidation).
- SeaweedFS / S3-compatible object storage (recordings, media).
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
  while serving tenants in multiple regions. The contract surface is small,
  audit-friendly, and — by the first invariant of
  [ADR-0034](../decisions/0034-hub-contract-surface-invariant.md) — carries no tenant
  content, only plan / entitlement / licence metadata. Tenants whose plan forbids cross-region operator access are served by a
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

**Proposed marketplace impact (2026-10-01).** The arrangement below describes the
institution service. It is not an accepted legal-role assignment for central buyer,
order, fraud or support processing. [ADR-0049](../decisions/0049-institution-sites-and-course-marketplace.md)
requires a purpose-based role, consent, DSAR, retention, transfer and media-rights
decision before the first marketplace PII writer. Its
[scoping companion](34-course-marketplace-scoping.md#privacy-residency-and-media)
records those open boundaries; no new legal role or retention period is selected here.

For tenants subject to KVKK / GDPR, LearnStack acts as a **data processor** while the tenant is the **data controller**. The processor agreement template lives outside this repository (legal). Engineering-side commitments:

- Sub-processor list maintained in the tenant onboarding pack (Keycloak host, LiveKit host, S3 / SeaweedFS provider, email provider, SMS provider, payment provider).
- Sub-processor changes are notified to tenants 30 days in advance via the Tenancy module's notification channel.
- Security incident notification timeline: within 72 hours of discovery (GDPR Article 33 baseline).

## Operational Requirements

- **PII redaction in logs.** The Serilog enricher redacts structured properties
  whose names match the shared token matcher. `[PiiSensitive]` controls audit
  snapshots, not arbitrary log values or exception text; see
  [Observability](../standards/10-observability.md#errors).
- **PII redaction in error reports.** Sentry and local-file adapters redact
  matching `AdditionalTags` names. They retain exception messages and stacks;
  no whole-payload redaction or bypass-proof integration test is claimed.
  Callers must use fixed messages/closed descriptors. Phase 03 owns category
  handling at its first PII writer; Phase 11 qualifies diagnostic egress and
  retention before production use.
- **Encryption at rest.** Storage provider (S3 / SeaweedFS) + PostgreSQL volumes use the provider's at-rest encryption; specific configuration documented in [12-infrastructure.md](../standards/12-infrastructure.md).
- **TLS in transit.** [11-security.md](../standards/11-security.md) § Transport.
- **Backups.** Phase 11 owns retained backup/WAL handling and post-restore
  reconciliation against the [inventory](#durable-asynchronous-store-inventory).
  The existing propagation target remains the platform's RPO + one backup cycle;
  Phase 11 qualifies the mechanism. Source-row deletion does not prove removal
  from backups; Phase 02b delivers no nightly `delete-from-backups` job.
- **Subject-access timeline.** GDPR Article 12 requires response within 30 days. Engineering target: export bundle ready within 7 days of request; deletion completed within 30 days.

## Roadmap Touchpoints

- **Phase 03** — Tenant-scoped DSAR export/erasure, separate global closure and
  audit redaction, subject to the [first-writer prerequisites](#erasure-authority-and-first-writer-prerequisites).
- **Phase 04** — First content holding PII (page metadata, content entries with author fields). Categories declared.
- **Phase 07** — Learner portal adds PII-Behaviour. Its first such writer must
  integrate with Phase 03's export/erasure workflows.
- **Phase 08c** — Recordings (PII-Sensitive). Consent flow + retention.
- **Phase 09** — Billing payment data. 7-year retention.
- **Phase 11** — Complete the Phase 03 workflows across later modules and retained
  copies; decide purge/retention operations, backup reconciliation, sub-processor
  list, incident-notification runbook and KVKK / GDPR compliance review.

A separate data-protection runbook (`docs/runbooks/data-protection.md`) lands in Phase 11 and links to this architecture document.

## Risks

- **Deletion incompleteness.** Phase 03 must implement and test every applicable
  module's erasure response before its first writer; Phase 11 extends that
  coverage to later modules and retained copies. The event and coverage control
  described above are planned, not current implementations.
- **Backup re-introduction.** Phase 11 must qualify post-restore reconciliation
  and document it in its DR runbook; no current replay job or runbook is claimed.
- **Cross-tenant data in exports.** Phase 03's DSAR integration tests must prove
  that an export excludes another user's identifiable data before release.
- **Sub-processor surprise.** Adding a new sub-processor without notice breaks the processor agreement. The Tenancy module tracks the sub-processor list and emits a notice 30 days before any change.
- **Audit-vs-erasure tension.** Redacting the six permitted columns leaves immutable
  actor/entity/causation identifiers and causal metadata. Phase 03's bounded
  first-writer decision must reconcile those references with accountability; the
  existing redaction grant does not establish complete anonymisation.
