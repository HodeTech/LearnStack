# ADR-0065: Bounded Tenant Assertion Diagnostics

## Status

Accepted — 2026-10-20. The maintainer authorizes implementation after these review
corrections without another approval gate. Acceptance precedes dependent code;
this record does not claim the new budget or its proofs are delivered.

**Date:** 2026-10-20
**Deciders:** @cemil
**Bounded supersession:** ADR-0036 authenticated assertion recording and mismatch
metric labels; ADR-0064 per-occurrence recording. The outbound metric projection
ratifies the process-wide privacy boundary shipped in `c6108bf9`.

## Decision Drivers

- Many valid subjects and peers can repeatedly probe one independently defended
  host tenant. Request admission does not bound aggregate audit writes there.
- Security counters need finite dimensions; tenant UUIDs belong in attributed
  logs and durable rows, rather than metric series.
- Native HTTP and DNS metrics ignore request-local trace suppression. Configured
  metadata destinations must remain private in every exported channel.
- Losing individual durable occurrences and dependency destination dimensions
  has an operational cost that must be accepted explicitly.

## Considered Options

1. **Bound authenticated audit attempts per defended tenant and project outbound
   metrics** (chosen). Every admitted event retains exact attribution; subsequent
   occurrences retain their refusal, counter and warning without a durable row.
2. **Keep every authenticated occurrence** (rejected). This preserves complete
   actor evidence, but many valid subjects/peers can amplify target-tenant audit
   storage and writes without an aggregate limit.
3. **Guarantee a first durable row for every actor** (rejected). With unbounded
   distinct actors, this cannot also guarantee a hard aggregate write bound.
   Splitting the budget by actor, subject, source or dimension has the same flaw.
4. **Write only when the claimed tenant matches the host** (rejected). This
   removes the cross-tenant probes the detector exists to retain.
5. **Coalesce actors into a durable summary** (rejected). A summary writer needs
   its own durability, shutdown and bounded actor-retention contract; a first
   occurrence is not a summary of later actors.
6. **Restore native destination tags for all requests** (rejected). This restores
   dependency dashboards but also exports private metadata destinations. Tracing
   suppression does not control the native DNS meter.
7. **Add a finite dependency alias now** (rejected for this packet). There is no
   current consumer. Source-declared aliases still need a closed identity set and
   independent privacy proofs; Phase 11 qualifies them before their first use.

## Decision

LearnStack admits at most 600 authenticated assertion audit write attempts per
independently defended host tenant per API instance in each one-minute window,
shared across actors, subjects, sources and dimensions. Later occurrences retain
one refusal, mismatch count and warning without database I/O. Admitted events
remain MUST-class; this named admission exception does not relax general MUST
mutation durability or permit a tenant `AuditConfig` override. Mismatch metrics
omit tenant identifiers. Native outbound HTTP/DNS metrics omit destination
identity for every request while retaining their bounded measurement dimensions.

### Audit admission and ownership

`TenantAssertionAuditBudget` is a singleton in `LearnStack.Api/Tenancy`, shared by
all request scopes. The scoped `AuditingTenantAssertionRecorder` consults it just
before an authenticated standalone write. Admission is atomic under concurrency;
spending occurs before I/O and a failed write does not restore a permit. An
admitted row keeps its exact actor, asserted UUID and captured correlation, under
the independently defended host tenant. Its organization scope stays null; the
trusted host organization remains metadata. Anonymous burst detection and row-14
no-tenant recording retain their separate contracts and do not spend this budget.

The only key is the actual independently confirmed host tenant, never a
signer-chosen claim. One window begins on that tenant's first attempt and expires
one minute later, measured with monotonic `TimeProvider` timestamps. Suppressed
requests do not move its start. The same tenant restarts a consumed window only
after elapsed expiry. No actor/source/dimension partitions, configuration key or
tenant override multiply or alter the fixed allowance.

> **Erratum — 2026-10-20.** The paragraph below calls the request registry
> "ADR-0064's retained 4096-subject registry". ADR-0064's Decision selects no
> partition count; its quota section and later code show the false attribution.
> This Decision is unchanged. Current bound authority:
> [ADR-0066](0066-bounded-tenant-subject-admission.md). Recorded in Amendment 2.

The singleton owns an in-memory dictionary and queue with at most one retained
entry per actual host tenant. Each call inspects at most 128 queued entries and
reclaims only expired windows; live windows are never evicted or reset under
pressure. Same-tenant expiry is checked directly even if sweeping has not reached
it. Queue rotation/restart never duplicates a retained tenant entry. The keyspace
is bounded by actual known host tenants, rather than arbitrary claim UUIDs. There
is no independent 4096-tenant admission ceiling: the 4097th actual host tenant
gets the same allowance. This is distinct from ADR-0064's retained 4096-subject
registry. No background timer, owned external resource or disposal lifecycle is
introduced for the audit budget.

### Signals and failure semantics

Every mismatch still increments
`learnstack_tenant_assertion_mismatch_total{dimension,source,authenticated}`.
Every budget-suppressed authenticated occurrence additionally increments
`learnstack_tenant_assertion_audit_suppressed_total{dimension,source,reason}`,
where `reason` is only `audit_budget_exhausted`. Both counters exclude tenant,
actor, subject and correlation labels. The existing unresolved counter retains
its closed row-14 dimensions.

Each mismatch emits exactly one Warning occurrence event: normal event 4001 when
not suppressed, or event 4006 `AuditBudgetExhausted` when suppressed. Event 4006
retains typed tenant/actor/correlation properties and the closed diagnostic
reason. It does not also emit event 4001. This reason belongs only to logging and
telemetry: it is neither `AuditEntryDraft.Reason` nor a Problem Details code.
Suppression makes no audit-store call, opens no connection and creates no draft
or durable row. An admitted write failure preserves the original refusal and
the store's existing Critical, failure-counter and health behavior.

The write bound does not bound real-time warnings: every occurrence remains
counted and logged, so aggregate log volume is unbounded. That operational cost
is explicitly accepted here; log/counter visibility is not durable actor-complete
audit evidence, and this decision adds no sampling or log-volume guarantee.

### Why 600, and the visibility cost

600 matches the existing additional subject ceiling only in the absence of
contention; it is a deliberate aggregate write-cost choice, not a measured
capacity result. Earlier 60/min visitor and 600/min physical-peer floors still
apply. Ten independently admitted IPs can supply 600 attempts through a peer and
exhaust a target tenant's allowance; multiple peers/subjects do not gain a larger
audit allowance. Other actors may receive fewer or zero durable records within
that window. Neither the number nor subject admission guarantees actor coverage.

ADR-0036 explicitly objects that:

> ten cheap requests against a tenant of the attacker's choosing would silence
> every subsequent authenticated mismatch against that tenant for the window.

The objection remains valid in kind. This decision raises that example's ten
attempts to 600, accepts that an attacker can consume the durable allowance, and
chooses a hard aggregate write bound over complete actor visibility. Warnings
and counters continue; they do not repair the durable evidence lost.

At continuous steady saturation, 600 attempts/minute gives a nominal 864,000
successful rows per 24 hours per tenant/instance. Fixed windows can straddle
calendar-day boundaries; this is not a calendar-day hard cap. **Assuming** an
illustrative 1 kB per row, the nominal rate is 864 MB per day before indexes, WAL,
replicas or backups; seven 365-day years total 2.20752 TB. These decimal estimates
are assumptions, not row measurements,
retention-sizing results or a guarantee that this fits production. Failed writes
consume attempts, so the maximum successful-row estimate is not a promise of
rows. Multiple instances multiply allowance and cost. Phase 11 owns distributed
admission and measured audit capacity/retention sizing before production use.

### Outbound telemetry scope

The export projection already shipped in `c6108bf9`; this decision explicitly
ratifies it and discloses its process-wide cost. All exported instruments from
`System.Net.Http` retain only `http.request.method`, `http.response.status_code`,
`network.protocol.version`, `url.scheme` and `error.type` dimensions. All exported
`System.Net.NameResolution` instruments retain only `error.type`; DNS does not
acquire HTTP method/status dimensions. Counts and durations remain observable.

Destination hostname/address/port, DNS question name, full URL, path and query
are removed from these metrics for metadata and ordinary Hub/payment/media
traffic alike. Ordinary application logs and HTTP traces remain observable;
metadata spans remain suppressed. Destination-based native metric dashboards
are unavailable. A finite source-declared dependency alias is an alternative
requiring Phase 11 design and qualification before a consumer depends on it.

Proofs distinguish channels. Metadata hostname/address canaries remain absent
from all exported payloads. A separate ordinary HTTP request to a destination
with a distinct IP/port must lack destination/path/query canaries in **metrics**,
while its ordinary trace remains a positive control. Searching all payloads for
that ordinary destination would contradict preserved tracing and is not the
privacy contract. Real HTTP/DNS durations and bounded tags must remain visible;
an empty export cannot satisfy the proof.

### Exact relationship to Accepted decisions

ADR-0036's authenticated recording clause says:

> written **for every occurrence** with **no coalescing and no per-tenant ceiling**

Its metric declaration is:

> `learnstack_tenant_assertion_mismatch_total{tenant,dimension,source,authenticated}`

ADR-0064's recorder contract says:

> Separate requests remain separate
> occurrences: ADR-0036's per-occurrence, no-coalescing authenticated contract is
> unchanged.

ADR-0036 Amendment 13's repeated per-occurrence/no-per-tenant-ceiling promise is
also replaced within this same bound. Only those durable-admission and
metric-label clauses are replaced. Distinct
requests remain distinct signal occurrences, and admitted rows are not coalesced.
The host destination, MUST classification of admitted events, tenant-wide scope,
refusal status, row-7/10/14 dispositions and anonymous burst contract retain their
meaning. The named admission exception changes no general mutation MUST floor
or tenant `AuditConfig` rule. Dated disclosures in ADR-0036 Amendment 14 and
ADR-0064 Amendment 1 preserve every Accepted body and earlier disclosure. This
is normative bounded supersession, not an ADR-0041 false-when-written correction.

## Context

The authenticated recorder followed the earlier every-occurrence policy, while
native meter views already discarded outbound destinations. Maintainer review
explicitly authorizes this revised acceptance and subsequent implementation.
Acceptance is committed before dependent audit-policy code. Had the proposal
been rejected, the merge would stay blocked and a replacement privacy-safe
metric design would be reopened; rejection would not justify restoring the
leaking native attributes.

Reopen if complete durable actor evidence becomes mandatory, shared tenant
allowance is unacceptable, actual tenant-registry memory/cost is unsuitable, or
dependency attribution is needed before Phase 11. Acceptance does not establish
production traffic fit or completed implementation.

## Consequences

### Positive

- Many valid subjects cannot create unlimited audit attempts per target window.
- Metrics have no tenant-series growth or native outbound destination leakage.
- Every retained row describes one real occurrence with exact attribution.

### Negative

- An attacker can consume a target's 600-attempt allowance; later actors lose
  durable evidence for the rest of that window, despite continued diagnostics.
- Aggregate warning volume remains unbounded; per-instance row costs can still
  be substantial and multiply across API instances.
- Native outbound metrics lose dependency destination attribution until a
  separately qualified finite identity design has an actual consumer.

### Neutral

- No role, grant, schema, credential, membership or authorization change occurs.
- Subject admission and anonymous burst accounting remain separate mechanisms.

## Implementation Notes

Commit acceptance and dated older-ADR disclosures before dependent code. The
complete acceptance-carrier register is:

- [ADR index](README.md),
  [ADR-0036 Amendment 14](0036-tenant-resolution-trusted-inputs.md#2026-10-20--amendment-14-bounded-assertion-diagnostics),
  [ADR-0064 Amendment 1](0064-tenant-api-authentication-boundary.md#amendment-1--bounded-assertion-diagnostics-2026-10-20).
- [Standards 10](../standards/10-observability.md),
  [Standards 11](../standards/11-security.md),
  [Standards 18](../standards/18-audit-coverage.md),
  [Standards 21](../standards/21-architecture-tests-catalogue.md).
- [Architecture 13](../architecture/13-identity-and-auth.md),
  [Architecture 31](../architecture/31-audit-subsystem.md),
  [Architecture 33](../architecture/33-cross-cutting-concerns.md).
- [Tenancy audit matrix](../modules/tenancy/audit.md),
  [Glossary](../glossary.md), [CLAUDE.md](../../CLAUDE.md), and the
  [Phase 02b record](../roadmap/phase-02b-events-auth.md).

The prior
[Step 5 closeout](../roadmap/phase-02b-events-auth.md#p02b-5-step-5--local-implementation-and-review-closeout-2026-10-19)
remains historical. The
[2026-10-20 remediation record](../roadmap/phase-02b-events-auth.md#p02b-5-external-review-remediation-2026-10-20)
owns this correction's implementation, review and current PR-head CI receipts.
Acceptance alone does not mark any new proof Implemented.

## Tests and Proofs

Extend existing catalogue obligations without inventing a new Implemented rule.
The subject-limit obligation remains about subject admission, rather than being
used as evidence for the independent target-tenant audit limit.

- Unit `TenantAssertionAuditBudgetTests` owns exact 600 concurrent admissions,
  independent tenants, first-request monotonic expiry, consumed permits,
  suppressed requests not extending expiry, direct same-tenant reset,
  bounded 128-entry sweep, no live eviction, one queued entry per retained tenant
  and independent admission for more than 4096 actual host tenants.
- Unit `AuditingTenantAssertionRecorderTests` owns failure consuming allowance,
  no I/O after suppression, shared actor/source/dimension accounting, unchanged
  anonymous/no-tenant behavior, exact metric labels, typed attribution and one
  Warning (4001 or 4006) per occurrence.
- Integration `TenantAssertionHttpTests` owns unchanged HTTP refusals and the
  actual production DI singleton registration across request scopes.
- Integration `TenantAssertionAuditAdmissionTests` owns at least 601 concurrent
  calls through actual registered scoped recorders sharing the singleton and one
  observable fake `IAuditStore`; exactly 600 store attempts prove scope sharing.
- Integration `TenantAuthenticationDiagnosticsTests.Metrics` owns real exported
  HTTP/DNS positives, metadata absence across all channels and ordinary distinct
  HTTP IP/port/path/query absence from metrics with a preserved trace positive.
- Structural `TenancyConventionTests` extends
  `Assertion_Budget_Does_Not_Depend_On_ICacheService` to the new singleton and
  `Assertion_Recorder_Is_The_Only_Mismatch_Writer` to the third, suppression
  counter. Behavioural accounting remains with the owners above.

## References

- [ADR-0033](0033-audit-durability-model.md),
  [ADR-0044](0044-audit-write-path.md),
  [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
  [ADR-0064](0064-tenant-api-authentication-boundary.md),
  [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md).
- [Observability Standards](../standards/10-observability.md),
  [Security Standards](../standards/11-security.md),
  [Audit Coverage](../standards/18-audit-coverage.md),
  [Architecture test catalogue](../standards/21-architecture-tests-catalogue.md),
  [Tenancy audit matrix](../modules/tenancy/audit.md),
  [P02b-5 package](../roadmap/phase-02b-events-auth.md#p02b-5-decision-package-2026-10-19).

## Amendments

### Amendment 1 — exemplar export enforcement (2026-10-20)

The outbound metric privacy contract includes exemplar filtered attributes,
not only metric-point attributes. OpenTelemetry can retain attributes removed
by a stream view in its exemplars. The API therefore sets the programmatic
exemplar filter to `AlwaysOff`; standard and histogram-specific environment
settings cannot enable them. This is an implementation qualification of the
existing destination-exclusion contract, not a new identity-label exception or
a change to ordinary trace/log visibility.
Metric-to-trace exemplar correlation is consequently unavailable; ordinary
traces remain exported under their existing rules.

`TenantAuthenticationDiagnosticCollector.Metrics` decodes exemplar attributes,
identifiers and values. `TenantAuthenticationDiagnosticsTests.Metrics` retains
unprojected positives and production negatives under both `always_on` and
`trace_based`, for both environment-setting families. Point-only decoding is
insufficient evidence. [Observability Standards](../standards/10-observability.md)
and the [catalogue](../standards/21-architecture-tests-catalogue.md) carry the
same proof boundary. Source-bound review and hosted qualification belong to the
[second remediation record](../roadmap/phase-02b-events-auth.md#p02b-5-second-external-review-follow-up-2026-10-20).
The original Accepted body, decision, date and deciders remain intact.

### Amendment 2 — subject registry source attribution (2026-10-20)

The reference to "ADR-0064's retained 4096-subject registry" was false when
written: ADR-0064's original quota Decision selects no partition count. The
adjacent inline erratum preserves that text under ADR-0041. The maintainer now
accepts [ADR-0066](0066-bounded-tenant-subject-admission.md), the additive
subject-admission/resource decision. This ADR's audit allowance, tenant-only
keyspace, destination/exemplar privacy, original Decision, date and deciders
remain unchanged. Subject-registry fault and quota are not audit-store failure
or assertion suppression.

The correction carriers are ADR-0066, this erratum/amendment, ADR-0064's own
erratum/amendment, the [decision index](README.md), Standards
[04](../standards/04-api-design.md), [10](../standards/10-observability.md) and
[11](../standards/11-security.md), Architecture
[13](../architecture/13-identity-and-auth.md) and
[33](../architecture/33-cross-cutting-concerns.md), the
[glossary](../glossary.md), [CLAUDE.md](../../CLAUDE.md), the
[roadmap index](../roadmap/README.md), [Phase 11](../roadmap/phase-11-production-hardening.md)
and the [acceptance/delivery record](../roadmap/phase-02b-events-auth.md#adr-0066-acceptance-and-implementation-2026-10-20).
The catalogue and actual runtime/test owners are updated with the implementation;
this acceptance is not a claim that those controls already run.
