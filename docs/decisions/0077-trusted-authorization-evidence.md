# ADR-0077: Trusted Authorization Evidence

## Status

Proposed — no audit policy or runtime behavior is changed before acceptance.

**Date:** 2026-10-27
**Deciders:** @cemil

**Relationship:** Completes P03-2 G7 and successful claim-only assertion
attribution alongside [ADR-0076](0076-live-scoped-authorization.md). It preserves
ADR-0033/0044 durability and legitimate plural resource intents. It adds a named
denial-evidence population and a separately attributed assertion population,
without extending ADR-0065's host allowance to either.

## Decision Drivers

- HTTP policy denials precede the MediatR audit frame; application/resource
  refusals may otherwise belong to Off, SHOULD or MAY operations.
- Live membership exposes successful row-14 resolution. Its tenant is confirmed
  by Membership, not independently defended by the request host.
- Membership/Role/route/IP provenance must come from admitted execution owners,
  not request fields, realm roles or arbitrary metadata dictionaries.
- A sensitive directory read needs durable evidence before response disclosure,
  while a failed refusal audit must preserve the caller's existing refusal.

## Considered Options

1. **One typed central evidence owner, explicit populations and captured trusted
   context** (chosen). Reuse the existing store and common audit lifecycle.
2. **Audit every outer HTTP 401/403/404** (rejected). This duplicates resource
   evidence and contradicts deliberate validation/public/tenantless omissions.
3. **Rely on the ordinary operation's classification** (rejected). Off and
   override-Off declare no intent; SHOULD/MAY cannot establish MUST evidence.
4. **Promote every normal operation to MUST** (rejected). Denied evidence does not
   justify changing successful-read/mutation classification across the corpus.
5. **Reuse the host allowance for claim-only or authorization refusals**
   (rejected). It has neither that attribution nor that accepted evidence-loss
   policy. A new suppression budget would require a separate explicit decision.
6. **Diagnostic-only successful claim-only assertion mismatches** (not selected).
   Confirmed Membership supplies a tenant attribution; this choice retains a
   durable per-occurrence record and accepts its cost.
7. **Write absorbed denials only after the outer commit** (rejected). A process
   crash loses the in-memory event despite successful business state. An audit
   failure then has no precommit success to refuse.
8. **Durable cross-restart invocation ledger** (not selected). No admitted
   request-replay owner currently persists/restores policy occurrences. The
   pre-success barrier below removes unaudited outer success without claiming
   recovery of every uncompleted refusal after a crash.

## Decision

The common audit infrastructure owns one typed denial event per refused policy
act, shared by HTTP, application and resource adapters. Its MUST security-event
record supplements, rather than replaces, separately declared resource intents.
The evidence carries immutable, bounded context from trusted execution owners.
Successful claim-only resolution followed by a conflicting assertion records a
distinct per-occurrence MUST event under its Membership-confirmed tenant.
Neither population consumes or inherits ADR-0065's host allowance.
An outer operation that absorbs a denial can succeed only after the required
event is durable: atomically with a ReadWrite commit, or acknowledged standalone
before ReadOnly completion/disclosure. Occurrence identity and decision time are
fixed across that event's admitted writes/reconciliation, with exact duplicate
verification; no persistent HTTP/restart replay identity is implied.

## Context

Verified at `daf03a48`: `AuditExecutionCoordinator.DeclareAsync` skips Off and
ordinary unresolved contexts. SHOULD/MAY reconciliation stays best-effort.
`TenantAuthorizationResultHandler` has no common evidence owner. The composer
sets email, IP and user agent to null; Membership/Role/route provenance has no
current capture port. `TenantAssertionMiddleware` routes every resolved header
mismatch into the host-budget recorder without a HostScope distinction.
The deny-all membership reader currently prevents the successful row-14 case.

### Explicit evidence populations

| Observation | Evidence owner and durability |
|---|---|
| HTTP protected-policy rejection with trusted resolved tenant | Central denial owner; one `identity.authorization.deny` MUST standalone record; handler never dispatches. |
| Application/resource capability or ownership rejection | Same owner; one denial event for that act, plus each normally declared resource intent with its existing classification/outcome. |
| HTTP allow followed by application/resource deny | Application/resource owner alone records the denial event. An allow is not a denial event. |
| Nested refused act absorbed by an outer success | Inner denial survives independently; outer success does not turn it into successful authorization. |
| Successful claim-only resolution, then conflicting tenant/org header | Central assertion adapter; one `tenancy.assertion.reject_claim_context` MUST standalone record under confirmed tenant; no host allowance debit. |
| Ordinary successful sensitive directory read | Common in-transaction MUST `identity.membership.list` `ReadSensitive` row, committed before disclosure. |

Each policy invocation has its own identity, not a blanket once-per-HTTP-request
Boolean. Passing its same decided reference between adapters cannot mint a second
event; genuine nested acts retain separate evidence and plural resource intents.
The protected policy catalogue supplies fixed operation/policy IDs. A caller
cannot choose an audit slug, tenant, arbitrary resource payload or attribution.

Normal operation classification remains unchanged. Thus a denied audited command
may yield its declared resource-attempt rows **and** one authorization event;
these are distinct classified facts, not two adapters recording the same denial.
An Off operation yields only the denial event. Tenant overrides cannot remove
that event. Central coordination retains it across rollback/absorbed nested
refusal and clears its captured state after the owning execution finishes.
The denial population has independent per-event state; the normal business
capture's Committed flag cannot suppress an unflushed event. Its actual enclosing
owner supplies the pre-success durability barrier below; a joiner neither commits
nor clears the population. It does not become an ordinary resource intent.

### Pre-success barrier and crash boundary

| Enclosing owner/outcome | Required durability before returning |
|---|---|
| ReadWrite owner would succeed after absorbing a denial | Insert every unresolved denial event on the same business transaction after domain-event draining, beside normal MUST intents and before final commit admission. Failure rolls back business state and refuses outer success with `503 audit_unavailable`. |
| Physical ReadOnly owner would succeed | Acknowledge every unresolved event through its own short evidence transaction before owner completion/disclosure. Failure refuses outer success with `503 audit_unavailable`; no audit INSERT or mode upgrade occurs on the read-only transaction. |
| No business owner; HTTP/application refusal | Attempt the standalone event before returning the refusal; its existing 403/404 survives write failure. |
| Owner refuses, throws, cancels or rolls back | Final reconciliation attempts unresolved events standalone, preserving the actual refusal/exception. |
| Business COMMIT acknowledgement is uncertain | Reconcile the same fixed event; its known denial stays `denied`. Exact evidence proves only the event, never the business COMMIT outcome. |

Track written/pending, acknowledged, rolled-back and uncertain evidence per event
and actual owner generation. Only a confirmed enclosing commit acknowledges its
in-transaction event. A standalone acknowledgement survives a later outer failure.
The pre-success barrier runs before final event/job commit admission, preserving
the existing no-await gap between that admission and physical completion.
It must also cover an otherwise-successful outer execution without a transaction.

An acknowledged ReadWrite commit carries the event even if the process exits
immediately afterward. A ReadOnly or ownerless success cannot publish before
the standalone acknowledgement. There is no successful-business-commit/finally-
only write window. A crash before any applicable write commits can still lose an
in-memory refusal whose enclosing operation never completed successfully; there
is no durable recovery queue for that uncompleted act. Already-refused paths keep
ADR-0033 Amendment 1's explicit audit-outage loss posture. This is a pre-success
durability guarantee, not crash-proof collection of every observed refusal.

### Occurrence identity and exact reconciliation

The central owner mints one opaque decided occurrence reference: one
`AuditEntryId`, one trusted UTC decision timestamp normalized once to PostgreSQL
microseconds, and frozen tenant/org, actor, policy/subject/reason and bounded
provenance. An in-transaction INSERT, standalone attempt and reconciliation of
that reference reuse the identical `(id,timestamp)` and complete draft. They
never use the ordinary resource intent's fresh reconciliation timestamp.

The reference lives only for that captured policy occurrence and its owned
cleanup/reconciliation. Re-entry with the same reference is serialized and
idempotent; reevaluating a policy, a real nested invocation, new HTTP request or
new event/job execution is a new occurrence. Correlation/request/route IDs alone
are not an occurrence identity. A restart does not recreate a lost reference or
deduplicate later requests; no new persistent replay ledger is introduced.

Confined denial-event methods on the existing `IAuditStore`/`PostgresAuditStore`
reuse its one INSERT/parameter binding, catalogue and failure signals. They do
not call generic `WriteStandaloneAsync` and accept its broad 23505 shortcut.
Only duplicate `audit_log_pkey` for the exact `(id,timestamp)` is eligible for
verification. Dispose the failed INSERT transaction, then use a clean READ ONLY
transaction under captured tenant/org RLS to compare every persisted draft field
with null-safe/typed JSONB equality, including fixed event discriminator and
outcome. Missing, mismatched or unreadable evidence is audit failure; an exact
match establishes this event's durability only. No global function, sentinel,
bypass or additional SELECT grant is needed. Ordinary resource reconciliation
keeps its deliberate different-timestamp semantics.

This exact-match contract is bounded to the live occurrence. If later privileged
redaction changes a compared field, an attempted match fails closed; this is not
an indefinitely replayable original-row witness or an extra redaction privilege.
Unknown commit recovery retains uncertain business state even if event evidence
is found. A timer, returned INSERT count or a 23505 alone proves no commit.

### Standalone evidence capacity

The new event methods use a separate type-confined `learnstack_app` evidence
source with two retained-owner slots and at most two physical connectors per
process, independent of the business and four-slot authorization sources. An
admitted evidence operation owns at most one connector at a time, including
sequential duplicate verification. No unbounded admission queue or retry loop
is created. Full capacity is a fixed audit-unavailable failure with the same
refusal/pre-success rules above; it is not a suppression budget or permission
to discard an event and declare success.

One admitted standalone operation has a shared five-second total I/O deadline,
including open, INSERT/COMMIT and any sequential duplicate verification; entering
verification does not reset it. Actual cancellation/disposal retains its slot
until confirmed cleanup. Failed cleanup retains capacity and shutdown joins
actual owners. The source never
acquires authorization/business resources, dispatches a handler or takes their
advisory/row locks; it only announces captured audit scope, inserts or verifies
the event, and completes. No writer retries against its own still-live transaction.
Job callers use a named tracked evidence lane, not an untracked second main unit.
The datasource/capacity/lifetime census includes this lane. Four/two are bounded
local policy choices, not production sizing. ADR-0076 owns downstream transaction-
pool isolation/reservation and the required small-pool/PgBouncer progress proof.

Earlier credential/unknown-host/malformed assertion and validation refusals keep
their existing behavior; this is not a generic 404 audit. Preserve row-7's host
recorder, unaudited row-10 and refused row-14 diagnostic-only contracts. Public
anonymous and tenantless refusals do not invent a tenant or use the platform
sentinel. AccountSite remains Off on success; live membership denials attributable
to a resolved tenant use the new denial event. A resolver refusal before context
publication retains its existing row-specific evidence contract.

Claim-only assertion attribution requires an actual successful
ClaimAndMembership context from the production reader, not a candidate tenant,
raw claim or asserted header. The source is fixed as membership-confirmed header
assertion; raw assertion values are not copied into diagnostics. Its new
evidence includes only the fixed tenant/org dimension and mismatch reason,
confirmed scope and admitted actor, not the caller's asserted UUID. Host-backed
rejections keep their existing budget/source contract. This new slug is neither
refused row-14 membership evidence nor a host-tenant event.

### Immutable enrichment and its limits

Trusted API, seed, event and job adapters supply a typed immutable provenance
snapshot; handlers cannot write a free-form context dictionary. The central
composer merges it with its own exact operation/subject facts before INSERT.
No enrichment UPDATE or new Identity audit table is introduced.

- Actor comes from the verified binding or the enumerated finite source owner.
  Membership/Role IDs come only from that act's live authorization result.
  The current model permits at most two contributing Memberships and eight
  contributing built-in Role IDs; excess/ambiguous evidence fails closed rather
  than silently truncating authority. Sort canonical IDs for stable evidence.
- Store these references, fixed policy/route identifiers and source kind in a
  typed versioned `metadata.authorization` object. Do not copy grants, bodies,
  query strings, raw path values, provider claims or credentials. Retained IDs
  remain personal linkage, not anonymous. Each row keeps its OperationType's
  target retention policy under
  [Standard 18](../standards/18-audit-coverage.md#retention): denial/assertion
  SecurityEvent seven years, directory ReadSensitive two years. These are policy
  targets; no automatic purge, retention column or tenant retention override is
  implemented today. Phase 11 owns that machinery, so data can outlive the target
  until the separately admitted purge/retention work exists.
  Metadata is outside the existing six-column redaction privilege. P03-10's
  Audit participant locates it through retained actor/subject attribution and
  reports surviving references; no new metadata UPDATE privilege is implied.
- The already admitted visitor/transport snapshot supplies the trusted effective
  address, falling back to the physical peer. Do not parse forwarded headers
  again. Existing `inet`/user-agent columns carry these redactable fields.
  User agent is untrusted descriptive data, sanitized to 256 characters with
  controls removed; it supplies no authority. No raw user-agent enters log labels.
- Route identity is an admitted template/descriptor, at most 128 characters,
  never a raw URL. Direct application dispatch has no invented HTTP route/IP/UA.
  An inner operation retains its own policy/subject while preserving source
  provenance. Child scopes cannot inherit another actor's evidence.
- ActorEmail remains null: P03-1 intentionally has no authoritative email mirror.
  P03-6/G10 or an earlier separately accepted source/freshness contract owns it.
  Platform/operator flags cannot become true from claims or a SystemActor UUID;
  P03-9 owns their actual privileged population.
- Existing event/job IDs and causal actor remain separate from effective actor.
  The source-kind catalogue enumerates HTTP, seed, event and job owners and their
  cleanup sites; unknown/direct-untrusted source receives no forged provenance.

The early audit frame may declare intents before authorization. Trusted evidence
is attached by the central owner when the act has an authorization result, not by
making the business handler populate or replace its actor. HTTP early denials
use the same typed composer through a short standalone owner; application
denials are retained by the common frame, included in its pre-success barrier
and reconciled independently of the business outcome on failure/uncertainty.
Legitimate plural intents remain associated with their exact invocation, not an
ambient last-result slot.

### Failure and capacity cost

An already-refused caller preserves its decided 403/404 on audit failure. An
outer execution that absorbs that refusal and would otherwise succeed instead
fails its pre-success barrier before business COMMIT/result disclosure if the
required event cannot become durable. There is no postcommit downgrade of an
otherwise-successful committed mutation merely because a finally write failed.
The existing MUST store owns Critical/counter/health reporting; an independent
composer refusal also has one fixed Critical signal. Do not attach a raw provider
or database exception. An otherwise-successful sensitive read fails closed with
503 and no result payload if its MUST row cannot commit.

These new events have **no aggregate suppression budget**. Existing visitor and
subject quotas bound individual sources, not the total durable population.
At 600 denied acts per minute one subject can generate 864,000 denial rows/day,
in addition to any separately classified resource attempts. An illustrative
1,000 bytes/row is about 864 MB/day before indexes/WAL/replication; real size varies.
The existing security-event retention policy target applies; its automatic purge
remains unimplemented until Phase 11. This is an explicit operational
cost, not a production capacity measurement. Phase 11 owns measured capacity,
distributed admission and alert/runbook hardening; its work does not waive the
current durability rule. A suppression policy or reduced retention reopens this
decision before implementation.

Reopen for larger/custom role populations, a new provenance source, new personal
metadata, privileged operator admission or a different denial-loss policy.

## Consequences

### Positive

- Actual HTTP, application and resource denials have a named evidence owner.
- Off/best-effort ordinary operations cannot silently remove the denial floor.
- Claim-only attribution no longer masquerades as a defended host tenant.

### Negative

- Per-occurrence denial evidence can add substantial database/retention cost.
- Two evidence slots can saturate; an absorbed refusal then can prevent outer
  success. A crash can lose an uncompleted refusal before any write commits;
  there is no persistent invocation ledger or universal restart deduplication.
- Blocked/failed cleanup retains evidence capacity and can delay joined shutdown;
  the five-second I/O budget is not a cleanup-completion guarantee.
- Retained Membership/Role linkage remains personal data; local BFF events still
  have no durable LearnStack session ledger under ADR-0070.
- IP and sanitized user agent are contextual evidence, not proof of a person.

### Neutral

- General mutation/sensitive-read durability and plural resource intents survive.
- No Platform grant, broad sentinel writer, email mirror, cache or new audit
  suppression budget is introduced.

## Implementation Notes

Acceptance appends dated extensions in ADR-0033/0036/0044 and a dated boundary
navigation note in ADR-0065, plus ADR-0047's tracked auxiliary-lane extension.
The ADR-0033/0044 additions explicitly delimit fixed-key denial reconciliation
from ordinary different-timestamp resource reconciliation, and name the enclosing
owner's pre-success barrier. No historical body/status/date is rewritten.
Reconcile Standards 10/11/18/21, Identity/Tenancy/Audit matrices, Architecture
13/23/31/33, glossary, enrichment/composer XML and the complete owner/caller
census before activation. ADR-0076's execution and this evidence path activate
together; accepting either record alone opens no protected surface.

## Tests and Proofs

- Real HTTP and direct application policy denials, resource refusals and nested
  absorbed refusals produce their exact distinct event/resource populations.
- Off/SHOULD/MAY/override-Off operation cases retain the MUST denial floor;
  replay/re-execution, rollback and capture cleanup preserve exact ownership.
  An absorbed nested Off denial under a physical READ ONLY owner still writes
  its standalone event before success without joining that read-only transaction
  or losing it to the outer committed-state shortcut.
- Real ReadWrite outer success carries its absorbed event atomically; process
  termination immediately after COMMIT cannot remove it. Event-store/composer
  failure prevents outer commit; ReadOnly standalone failure prevents disclosure.
- Unknown COMMIT, exact/missing/mismatched duplicates, wrong constraint, stable
  microseconds, two adapters/one reference versus two genuine occurrences, and
  evidence-source saturation/cancellation/cleanup have separate dirty controls.
- Real app-role directory read succeeds only with committed MUST evidence;
  forced store/composer/commit failures disclose no result and preserve refusals.
- Successful row 14 followed by tenant/org header mismatch writes only the
  decided tenant/provenance and consumes no host-budget allowance. Existing
  row-7/10/refused-14 and AccountSite/anonymous controls remain independent.
- Planted spoofed actor/roles/IP/operator/route and sensitive canaries, direct
  non-HTTP execution, two scopes/nested frames and dirty source-call controls
  establish provenance; a populated metadata column alone proves nothing.

## References

- [ADR-0033](0033-audit-durability-model.md),
  [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
  [ADR-0044](0044-audit-write-path.md),
  [ADR-0065](0065-bounded-tenant-assertion-diagnostics.md),
  [ADR-0072](0072-application-owned-identity-bootstrap.md).
- [Audit coverage](../standards/18-audit-coverage.md),
  [Audit subsystem](../architecture/31-audit-subsystem.md),
  [Data Protection](../architecture/23-data-protection.md).
- [Revised P03-2 package](../roadmap/phase-03-delivery-log.md#p03-2-maintainer-review-and-revised-package-2026-10-27).
