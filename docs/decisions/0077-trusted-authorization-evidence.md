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

## Decision

The common audit infrastructure owns one typed denial event per refused policy
act, shared by HTTP, application and resource adapters. Its MUST security-event
record supplements, rather than replaces, separately declared resource intents.
The evidence carries immutable, bounded context from trusted execution owners.
Successful claim-only resolution followed by a conflicting assertion records a
distinct per-occurrence MUST event under its Membership-confirmed tenant.
Neither population consumes or inherits ADR-0065's host allowance.

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
Boolean. Re-execution of the same decided invocation cannot duplicate its event;
genuine nested acts retain separate evidence and plural resource intents.
The protected policy catalogue supplies fixed operation/policy IDs. A caller
cannot choose an audit slug, tenant, arbitrary resource payload or attribution.

Normal operation classification remains unchanged. Thus a denied audited command
may yield its declared resource-attempt rows **and** one authorization event;
these are distinct classified facts, not two adapters recording the same denial.
An Off operation yields only the denial event. Tenant overrides cannot remove
that event. Central coordination retains it across rollback/absorbed nested
refusal and clears its captured state after the owning execution finishes.

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
assertion; raw assertion values are not copied into diagnostics. Host-backed
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
  remain personal linkage under existing security-event retention, not anonymous.
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
denials are retained by the common frame and reconciled independently of the
business outcome. Legitimate plural intents remain associated with their exact
invocation, not an ambient last-result slot.

### Failure and capacity cost

All new refused events preserve the already decided 403/404 on audit failure.
The existing MUST store owns Critical/counter/health reporting; an independent
composer refusal also has one fixed Critical signal. Do not attach a raw provider
or database exception. An otherwise-successful sensitive read fails closed with
503 and no result payload if its MUST row cannot commit.

These new events have **no aggregate suppression budget**. Existing visitor and
subject quotas bound individual sources, not the total durable population.
At 600 denied acts per minute one subject can generate 864,000 denial rows/day,
in addition to any separately classified resource attempts. An illustrative
1 KiB/row is about 864 MB/day before indexes/WAL/replication; real size varies.
The existing security-event retention applies. This is an explicit operational
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
- Retained Membership/Role linkage remains personal data; local BFF events still
  have no durable LearnStack session ledger under ADR-0070.
- IP and sanitized user agent are contextual evidence, not proof of a person.

### Neutral

- General mutation/sensitive-read durability and plural resource intents survive.
- No Platform grant, broad sentinel writer, email mirror, cache or new audit
  suppression budget is introduced.

## Implementation Notes

Acceptance appends dated extensions in ADR-0033/0036/0044 and a dated boundary
navigation note in ADR-0065. No historical body/status/date is rewritten.
Reconcile Standards 10/11/18/21, Identity/Tenancy/Audit matrices, Architecture
13/23/31/33, glossary, enrichment/composer XML and the complete owner/caller
census before activation. ADR-0076's execution and this evidence path activate
together; accepting either record alone opens no protected surface.

## Tests and Proofs

- Real HTTP and direct application policy denials, resource refusals and nested
  absorbed refusals produce their exact distinct event/resource populations.
- Off/SHOULD/MAY/override-Off operation cases retain the MUST denial floor;
  replay/re-execution, rollback and capture cleanup preserve exact ownership.
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
- [P03-2 package](../roadmap/phase-03-delivery-log.md#p03-2-proposed-decision-package-2026-10-27).
