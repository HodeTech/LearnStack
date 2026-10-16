# ADR-0061: Known External Job Failure Policy

## Status

Proposed — maintainer approval pending. No dependent runtime is authorized.

**Date:** 2026-10-16
**Deciders:** LearnStack maintainer

This proposal completes P02b-4 Step 7's external report contract. It adds bounded
report/status facts and a terminal reason, and narrowly supersedes ADR-0047's
code/type-only classification input restriction for completed external failures.
Step 6 capacity work remains authorized independently.

## Decision Drivers

- [ADR-0047](0047-transactional-background-job-runtime.md#external-profile-and-quiescence)
  permits another external attempt only after known effect, positive quiescence
  and the adapter's logical-operation deduplication permission. Those are three
  distinct facts; a quiescence evidence ID cannot supply retry permission.
- The shipped `BackgroundJobExternalResult.Completed` carries state, identifier
  outcome and quiescence, without failure classification or same-operation retry
  permission. Both retry and finalization satisfy today's storage guards.
- Execute and Reconcile must produce the same policy after a lost response or
  process restart. A prior transport exception and mutable `last_error` cannot
  classify the provider's eventual operation outcome.
- Plan/outcome DTOs remain identifier-only. Policy facts belong to the bounded
  framework report, with status-only dispatcher reads and no new provider method.
- The stable operation identity and six-normal/six-finalize budgets are already
  Accepted. Classification cannot evade either by creating another operation ID.

## Considered Options

1. **Explicit failure class and same-operation retry permission** (recommended).
   Separate policy eligibility from adapter safety; retain both with the known
   outcome. Add an explicit `external_retry_forbidden` terminal reason.
2. **Normalized standard failure code plus retry permission** (viable alternative).
   Reuse the existing code classifier, but require a new closed code set and
   adapter normalization contract. The two-class report is smaller for known
   completed outcomes; invocation/Prepare/Finalize code classification remains.
3. **Every completed Failed/Cancelled report is final** (rejected).
   Simpler metadata, but removes legitimate transient completed-failure retries
   that ADR-0047 and storage controls deliberately permit.
4. **Default every completed failure to unexpected-fault retry** (rejected).
   Assumes both transience and same-operation permission. A provider that caches
   final failure can consume all attempts without useful or authorized execution.
5. **Static per-definition policy** (rejected).
   Cannot describe different failures and deduplication permission for the same
   definition without further report data.

## Decision

LearnStack classifies a known completed external failure using two explicit
top-level report facts: `Transient` or `Permanent`, and permission to retry the
same logical operation. The runtime persists both with outcome/quiescence and
applies the deterministic policy below. This proposed decision becomes binding
only after maintainer acceptance; dependent Step 7 code remains unimplemented.

### Report boundary

`IBackgroundJobExternalOperation<TPlan,TOutcome>` retains exactly Execute and
Reconcile. Both use the same closed report contract:

- Succeeded requires its typed outcome and positive quiescence report, with no
  failure class or retry-permission facts.
- Failed and Cancelled require typed outcome, positive quiescence, one finite
  failure class and explicit same-operation retry permission. No omitted/default
  value means Transient, Permanent, allowed or forbidden.
- Running, Unknown and NeverStarted carry no completed-failure facts. Provider
  NotFound after ExecuteIntent remains Unknown.
- Invalid or incomplete reports grant no release, retry or finalization. They
  take bounded unresolved recovery/quarantine; no default classification repairs
  missing facts.

The concrete factory shape must make those combinations explicit. Existing
completion calls cannot silently acquire a failure default. The adapter owns
translation from provider facts; the runtime owns budgets, backoff, fencing and
finalization. A previous invocation/probe exception cannot overwrite a later
valid completed report. Unknown effect still precedes all failure policy.

### Deterministic normal-stage policy

This table applies only after exact operation/plan/attempt validation, positive
quiescence and a confirmed outcome checkpoint. Current shutdown/deadline gates
still forbid new execution; recovery may preserve facts for a later eligible
owner. Neither a report nor this table creates execution authority.

| Known facts, in precedence order | Normal-stage result | Terminal projection after failure finalization |
|---|---|---|
| Succeeded | Select success finalization; never Execute again | None |
| Failed/Cancelled, Permanent | Select known final failure | `permanent_failure` |
| Failed/Cancelled, Transient, same-operation retry forbidden | Select known final failure | `external_retry_forbidden` |
| Failed/Cancelled, Transient, retry permitted, six normal admissions spent | Select final failure for exhaustion | `retry_exhausted` |
| Failed/Cancelled, Transient, retry permitted, fewer than six admissions spent | Release the positively quiescent old occupancy; schedule the existing ordinal backoff and reacquire capacity at the next normal admission | None |

Retries retain the same JobId, canonical plan/fingerprint and stable OperationId.
A new AttemptId is the next counted fence, never a substitute operation identity.
Permission means the adapter supports another attempt for that same operation
after fencing the prior one; it is stronger than accepting a duplicate request.
Positive quiescence answers whether old effects can still occur, not whether a
provider permits useful repeat execution under its remembered operation key.

Permanent classification wins before permission or exhaustion. Retry prohibition
therefore has its own honest terminal code when the failure is Transient; it does
not relabel the provider's failure Permanent. Budget exhaustion applies when retry
would otherwise be permitted. Neither branch adds a seventh normal attempt.

The module finalization reason remains ExternalFailed or ExternalCancelled for
known operation failure, and NormalAttemptsExhausted for exhaustion. Its typed
outcome preserves the operation result; immutable failure metadata separately
explains policy. Successful application of domain Failed still returns Result.Ok
and commits its business/MUST audit with job terminal state and terminal MUST
audit. Finalize refusal/exhaustion retains ADR-0047's separate reasons and budget;
it does not rerun the external operation or claim domain Failed was committed.

### Durable facts and privilege boundary

Two write-once status facts belong to the exact normal attempt: failure class
and same-operation retry permission. Canonical names/DDL live only in
[Database Standards](../standards/05-database.md#background-jobs).

- Both are absent before completed outcome and on Succeeded; both are required
  on Failed/Cancelled. Their update commits with canonical outcome, known state
  and positive quiescence, under the exact existing app-role checkpoint owner.
- Identity/plan/operation/token/time guards remain. Neither fact can later change,
  clear or be copied from a different attempt. Admission cannot seed them.
- Retry guards require the matching Transient/permitted facts alongside existing
  known-effect/quiescence conditions. No-start retains its separate proof; a
  missing field never grants retry or converts Unknown to Failed.
- Extend only app checkpoint UPDATE and dispatcher status SELECT for these two
  fields. Add no dispatcher UPDATE or outcome/plan/parameter-byte SELECT, no new
  role and no broader table grant.
- A locked post-COMMIT probe reads the exact immutable attempt facts in a fresh
  statement. Payload-free control can select scheduling/finalization policy; typed
  outcome bytes still require an announced app-role read.
- `last_error` may carry a bounded diagnostic projection, never another decision
  authority. Transport/probe errors cannot prefill operation-outcome facts.

A forward migration must not fabricate classification or permission for retained
legacy Failed/Cancelled outcomes. The current activated runtime has no external
definitions. Migration verifies that no such incomplete historical outcomes
exist and refuses with a bounded diagnostic if they do; it does not rewrite
history. Qualification includes this refusal. Any future live migration of
incomplete external history requires its own explicit recovery design.

### Exact relationship to ADR-0047

The additive seams are the report facts, their durable projection, the explicit
retry-permission mechanism and `external_retry_forbidden` finite terminal code.
There is no existing direct Failed/Cancelled mapping to replace.

The narrowly replaced input restriction is ADR-0047
[Attempts, terminal audit and diagnostics](0047-transactional-background-job-runtime.md#attempts-terminal-audit-and-diagnostics):
“Classify the top-level code/type only.” Its replacement is bounded to this branch:
ordinary requests and invocation failures retain that code/type policy; known
completed external Failed/Cancelled reports use their explicit top-level class
and same-operation permission. All existing table rows and their precedence,
stable-operation/dedup restrictions, quiescence rules, budgets, authority and H1
ownership remain unchanged.

This is not an ADR-0041 correction of a false historical fact. Acceptance appends
a dated ADR-0047 amendment disclosing the precise bounded replacement and additive
seams, without changing its Accepted body/date/deciders or retiring the record.

## Context

Step 1 delivered the typed external contract; Step 2 qualified storage semantics,
including known Failed/Cancelled retries and selected-final-outcome retry refusal.
Those controls establish admissible transitions, not a runtime report classifier.
Step 7 has not activated external execution. The preparation review at source
`6a2ded99` identified this missing policy before dependent implementation.

An adapter can report a final remembered failure under a stable key, or a known
transient failure whose same operation safely supports another fenced attempt.
Neither behavior is universal. The two explicit facts distinguish them without
placing provider-specific code in the runtime or policy strings in outcome IDs.
Phase 04 qualifies its actual provider/process adapter; P02b-4 uses clearly named
fixture adapters through the real engine, without production definitions.

Using `permanent_failure` for every nonrepeatable transient outcome would save a
finite code, but blur provider permanence and job-policy finality in the MUST
audit trail. The proposed dedicated terminal code makes that distinction visible.

### What would change this decision

A concrete provider contract or measured implementation can motivate a different
finite report taxonomy. It must preserve stable identity, explicit same-operation
permission, durable restart equivalence and positive quiescence. A third provider
method, new provider request identity, TTL release or default retry grant is not
an implementation shortcut under this decision.

### What remains owned elsewhere

Phase 04 owns real provider/process-tree supervision and domain definitions.
Phase 03 owns audited operator reconciliation. Phase 11 owns production readiness,
alerts and retention. This proposal adds none of those surfaces, no production
definition/key and no change to Step 6's accepted capacity contract.

## Consequences

### Positive

- Direct Execute and Reconcile reports choose the same policy after restart.
- Transient classification cannot silently grant unsafe same-operation retry.
- Known final failure and exhaustion remain distinguishable in terminal audit.
- Identifier DTOs, two-method adapters and payload-free bypass reads survive.

### Negative

- Adapters must supply and qualify two extra facts for every known failure.
- A forward migration, finite terminal-code extension and write-once guards add
  implementation and regression work before external activation.
- Missing legacy facts refuse migration rather than receiving a convenient guess.

### Neutral

- Quiescence releases old occupancy; a later retry still reacquires capacity.
- No exactly-once effect or useful repeat execution for every provider is claimed.

## Implementation Notes

After acceptance, P02b-4 Step 7 implements report/storage/guard/control/finalizer
changes before external activation. Step 9 closes full enforcement and regression.
Update ADR-0047 through its dated amendment, the decision index, P02b-4 record,
glossary, Standards 05/09/18/20/21 and Events/Outbox architecture. The Tenancy
terminal-audit validation keeps its existing operation and sixth audit seam,
adding only the named finite reason where applicable.

Extend the existing Registered rules
`BackgroundJob_External_Work_Has_No_Business_Transaction`,
`BackgroundJob_External_Recovery_Never_Assumes_Quiescence` and
`BackgroundJob_Capacity_Is_Tenant_Wide_And_Quiescence_Bound`. Acceptance records
their new Asserts obligations; it claims no implemented rule or passing proof.
Do not invent a second spelling or increment Implemented counts prematurely.

Required discriminating controls include:

- Direct Execute and Reconcile Failed/Cancelled reports cover every policy row
  without a previous exception. Missing/invalid metadata cannot choose a default.
- Lost Execute response followed by Succeeded wins; a transport/probe exception
  cannot classify a reconciled provider failure or override eventual success.
- Lost outcome-checkpoint ACK/restart preserves exact classification/permission
  through locked status reads, without bypass payload access.
- Transient forbidden retry never reexecutes; permitted retry retains the exact
  operation/plan and creates one fresh counted attempt after positive quiescence.
- Real app grants/guards reject fact overwrite, foreign/stale attempt and false
  retry permission. Removed-predicate/permission dirty controls must fail.
- Normal/finalize six-admission ceilings, terminal audit reason, domain failure
  finalization and Unknown retention agree; legacy incomplete-fact migration
  refuses rather than fabricating a backfill.

All obligations are mandatory and pending. This proposal contains source review
and design evidence only; no new runtime, migration or execution proof is claimed.

## References

- [ADR-0047: Transactional Background Job Runtime](0047-transactional-background-job-runtime.md)
- [ADR-0040: Ambient Unit of Work](0040-ambient-unit-of-work.md)
- [ADR-0044: Audit Write Path](0044-audit-write-path.md)
- [Events and Outbox](../architecture/15-event-and-outbox.md)
- [Database Standards](../standards/05-database.md)
- [Error Handling](../standards/09-error-handling.md)
- [Documentation Standards](../standards/13-documentation.md)
- [Audit Coverage](../standards/18-audit-coverage.md)
- [Infrastructure Stack](../standards/20-infrastructure-stack.md)
- [Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md)
- [P02b-4 decision package](../roadmap/phase-02b-events-auth.md#p02b-4-decision-package-2026-10-15)
