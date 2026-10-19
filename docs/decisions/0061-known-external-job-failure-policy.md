# ADR-0061: Known External Job Failure Policy

## Status

Accepted — 2026-10-17. Step 7 implements this decision before external activation;
its runtime, migration and proof obligations remain pending.

**Date:** 2026-10-17
**Deciders:** @cemil

## Decision Drivers

- [ADR-0047](0047-transactional-background-job-runtime.md#external-profile-and-quiescence)
  requires known effect, positive quiescence and same-operation deduplication
  permission before another external attempt. None implies either of the others.
- The delivered completed-report shape carries state, identifier outcome and
  quiescence. It cannot distinguish a permanent failure, a repeatable transient
  failure and a transient failure remembered finally under the provider's key.
- Current storage admits retry or failure finalization for known Failed/Cancelled
  outcomes. It does not select between them or enforce the proposed reason mapping.
- Lost responses, checkpoint acknowledgements and process restarts must preserve
  policy. Mutable diagnostics and an earlier invocation exception are not authority
  for an eventual completed outcome.
- Runtime-owned cancellation must not become an invented permanent provider failure.
  An in-memory stop flag cannot determine report meaning after process restart.
- Identifier-only DTOs, two adapter methods, app-owned checkpoints, payload-free
  control reads, stable operation identity and both six-admission budgets remain.

## Considered Options

1. **One explicit three-state completed-failure policy** (chosen).
   Permanent, TransientRepeatable and TransientNotRepeatable encode the useful
   classification/permission combinations without a redundant Permanent/permitted
   state. Store one immutable value; derive finite audit facts from it.
2. **Separate failure class and retry permission** (viable alternative).
   Expresses the two concepts independently, but requires combination validation
   and two immutable fields. A permission attached to Permanent has no retry effect.
3. **Normalized standard code plus retry permission** (viable alternative).
   Normalize reports into an explicit closed subset of existing standard codes and
   reuse the classifier. Extend the subset only if necessary; a new code set is
   not inherently required. Permission remains independent. The chosen option
   keeps completed reports small but adds a parallel classification mechanism.
4. **Every completed Failed/Cancelled report is final** (rejected).
   Removes legitimate transient retries allowed by ADR-0047's same-operation contract.
5. **Default completed failure to unexpected-fault retry** (rejected).
   Invents transience and permission; a remembered final failure can waste all attempts.
6. **Static per-definition policy** (rejected).
   Cannot express different failure and repeatability facts for the same definition.
7. **Permanent Cancelled with durable cancellation-origin discrimination**
   (not selected). Requires a concrete causal protocol, including storage failure and
   crash ordering. ExecuteIntent, deadline expiry and a stop-request marker alone
   cannot establish why an operation ended. The selected narrower taxonomy avoids
   this extra authority; it does not certify the truth of adapter observations.

## Decision

LearnStack uses one explicit completed-failure policy: Permanent,
TransientRepeatable or TransientNotRepeatable. Permanent is valid only for Failed;
Cancelled requires one of the two Transient policies. The exact attempt stores the
policy with its outcome and quiescence, and storage enforces retry, finalization and
terminal-reason selection.

### Closed report boundary

`IBackgroundJobExternalOperation<TPlan,TOutcome>` retains exactly Execute and
Reconcile. Both obey the same report matrix:

| Report state | Required facts | Allowed failure policy |
|---|---|---|
| Succeeded | Typed outcome and positive quiescence | Absent |
| Failed | Typed outcome and positive quiescence | Any of the three explicit values |
| Cancelled | Typed outcome and positive quiescence | TransientRepeatable or TransientNotRepeatable |
| Running, Unknown, NeverStarted | No completed outcome, quiescence or failure policy | Absent |

Replace the existing public Completed factory with distinct Succeeded, Failed and
Cancelled factories; retain no Completed overload. Failed/Cancelled require the
explicit policy parameter. The policy enum has three nonzero values; omitted,
default/zero and undefined values are invalid. Cancelled/Permanent is rejected
unchanged on both adapter paths.
No runtime conversion repairs it into Transient or changes it to Failed.

The adapter translates provider/supervisor facts. It grants repeatability only when
another fenced attempt of the same OperationId and immutable plan is supported;
accepting a duplicate request alone is insufficient. Permanent supplies no retry
grant, without asserting that the provider cannot physically accept duplicates.

For cancellation, Transient is this policy taxonomy, not a promise that a cancelled
operation is resumable. TransientNotRepeatable explicitly forbids another attempt
under that operation identity. Neither outcome IDs nor quiescence IDs encode policy.
Invalid/incomplete reports grant no release, retry or finalization; bounded unresolved
recovery/quarantine applies. NotFound after ExecuteIntent remains Unknown.

### Durable authority and owned cancellation

An acknowledged or locked-confirmed outcome checkpoint is authoritative. Once
selected, the immutable final-outcome pointer supplies the normal outcome and policy;
the current finalize AttemptId supplies only the fresh finalization fence. A later
conflicting Execute/Reconcile report produces at most bounded diagnostics, never an
overwrite, conflict refusal/quarantine, new Execute or obstruction of that decision.
Do not probe the provider to validate an already committed outcome.

An uncertain checkpoint COMMIT requires locking the actual row and reading its
receipt in a fresh statement. Unlocked NULL and probe timeout do not prove rollback.
Only confirmed absence permits a first checkpoint from a valid report. Transport
exceptions, probe errors and `last_error` cannot prefill or replace report policy.

Lease loss, execution deadline and host shutdown close the current start/commit
gates and request owned cancellation. A new Failed/Cancelled report cannot bypass
that precedence: retain its exact facts and quiescence, and defer disposition to
fresh eligible recovery authority. Do not reclassify or erase the observation.
If stop wins the commit gate, the old owner cannot issue a new outcome checkpoint
or admit its COMMIT, even while its lease remains live. Uncommitted observations
remain in memory for eligible recovery; already issued SQL may still return and
roll back. If COMMIT admission won first, preserve its actual acknowledged or
uncertain result and reconcile it; later stop cannot declare rollback. Positive
completed success remains success; an already committed outcome wins. No report
reopens a closed gate or authorizes new execution by recovery.

A token request or OperationCanceledException proves neither completed cancellation
nor quiescence. When an operation actually ends by honoring runtime cancellation,
the adapter reports Cancelled with truthful same-operation repeatability. Reconcile
must preserve that meaning for the same completed attempt after restart; it cannot
report Failed/Permanent because the old stop token is no longer visible. Insufficient
provider/supervisor knowledge remains unresolved rather than receiving invented facts.

A genuinely independent Failed/Permanent outcome racing shutdown stays representable
and unchanged; fresh recovery may apply it. The generic runtime cannot distinguish
that truth from an adapter falsely calling its own cancellation Failed/Permanent.
Adapter qualification must establish this boundary, just as it establishes truthful
quiescence. This decision claims no runtime certification of external causality.

Restart equivalence means identical valid immutable facts select identical policy,
independently of ephemeral stop flags. It does not make a stopped owner equivalent
to fresh recovery authority or claim that uncommitted observations survive death.
Without a checkpoint, the adapter must reproduce completed-attempt facts through
Reconcile. Existing [ADR-0059](0059-owned-hangfire-server-generations.md) and
[ADR-0060](0060-fatal-hangfire-startup-and-unsafe-unwind.md) ownership rules remain.

### Deterministic selection and occupancy release

Apply this table only after exact attempt/plan/operation validation and a confirmed
valid outcome/quiescence checkpoint, under eligible authority. Unknown effect takes
recovery precedence. Neither classification nor elapsed time supplies quiescence.

The app outcome checkpoint atomically records the valid completed facts and releases
the exact old reservation. This applies to retry, success finalization and failure
finalization alike. Retry prohibition or a later finalizer fault cannot retain old
occupancy whose release committed. An unconfirmed COMMIT retains uncertainty until
locked reconciliation proves its receipt. Actual task/callback cleanup remains owned
separately; a later normal attempt reacquires capacity through ordinary admission.

| Durable facts, in precedence order | Finalization status | Module reason / stored finalization reason | Terminal reason after successful domain failure finalization |
|---|---|---|---|
| Succeeded | Succeeded | None / `none` | None; complete successfully |
| Failed / Permanent | Failed | ExternalFailed / `external_failed` | `permanent_failure` |
| Failed / TransientNotRepeatable | Failed | ExternalFailed / `external_failed` | `external_retry_forbidden` |
| Cancelled / TransientNotRepeatable | Failed | ExternalCancelled / `external_cancelled` | `external_retry_forbidden` |
| Failed or Cancelled / TransientRepeatable; six normal admissions | Failed | NormalAttemptsExhausted / `normal_attempts_exhausted` | `retry_exhausted` |
| Failed or Cancelled / TransientRepeatable; one–five normal admissions | No final selection; schedule normal retry | None | None |

Permanent and nonrepeatability take precedence over exhaustion even on admission
six. The module reason describes its finalization input; the terminal reason explains
job policy. `external_retry_forbidden` is not a new module-finalization enum member.
The immutable selected normal-attempt report and stored outcome kind preserve
Failed versus Cancelled even when exhaustion is selected.

Retries preserve JobId, canonical plan/fingerprint and OperationId. A fresh AttemptId
is the next counted fence. Existing ordinal backoffs and the six-normal ceiling apply.
No-start and pre-effect Prepare failures retain their separate Accepted policy and
proofs; they receive no fabricated provider outcome or completed-failure policy.

Domain failure finalization returns Result.Ok when it successfully applies Failed.
Business changes/MUST audit and terminal state/MUST audit share the app owner commit.
Permanent finalizer refusal requests `finalize_refused`; transient exhaustion of six
finalize admissions requests `finalize_exhausted`. Both preserve selected operation
facts and report `business_finalization_incomplete`; neither reruns Execute or claims
domain Failed committed. FinalizePending remains eligible until its accepted closure.

### Storage, checkpoint ownership and terminal witness

One nullable write-once policy value belongs to the exact normal attempt. Canonical
column names and SQL live only in
[Database Standards](../standards/05-database.md#background-jobs). The future forward
migration and runtime must implement all of these gates:

1. **Report shape and immutability.** Policy is absent before outcome and on success,
   required on Failed/Cancelled, and absent on finalize/no-start-only records.
   Outcome, kind, policy and positive quiescence commit together. Reject invalid
   combinations, admission seeding, later filling/clearing/overwrite and reuse from
   another attempt. Preserve exact scope/plan/operation/token/time validation.
2. **Both retry gates.** Return to Pending and next normal admission require every
   prior effectful attempt to be resolved, quiescent and TransientRepeatable.
   Success, selected finalization, Permanent, nonrepeatability or incomplete policy
   forbids reopening normal execution. No-start retains its distinct positive proof.
3. **Finalization selection.** Validate the selected current normal attempt, matching
   plan/operation/ordinal and the exact table row. Reject early repeatable failure
   finalization, false exhaustion, success-as-failure and mismatched module reason.
   All prior ExecuteIntent effects must be resolved. Selection remains write-once.
4. **Terminal reason.** App failure-finalization transition and terminal completion
   validate the selected outcome/policy and exact terminal reason again. A pending
   reason rewrite cannot evade the gate. Keep live finalize-attempt authority and
   the fresh database-time check after locking. Control cannot bypass the prepared
   domain finalizer with an ordinary completed-failure terminal reason.
5. **Distinct bypass routes.** Preserve only the Accepted uninvokable-work and
   refused/exhausted-finalizer routes, their own evidence/budgets, incomplete flag
   and retained outcome. Do not treat finalizer policy as provider classification.
6. **Privileges and ownership.** App owns outcome/quiescence/release and initial
   final-outcome/status/reason selection with FinalizePending. Control may calculate
   the next action from status, schedule an eligible retry, or arrange a bounded
   app-role checkpoint. It cannot write finalization facts or invoke module work.
   Extend only app checkpoint UPDATE and dispatcher status SELECT for the policy;
   add no dispatcher attempt/reservation UPDATE, payload SELECT, role or broad grant.
7. **Uncertain commits.** Locked status probes preserve exact outcome policy and
   selection without bypass payload access. Typed outcome bytes require an announced
   app-role read. A later conflicting report cannot replace these immutable receipts.
8. **Actual-row witness.** Extend the terminal claim, actual UPDATE RETURNING and
   JobTerminalTransition for the applicable selected normal outcome/policy, module
   reason, attempt counts and finalization status. The app mint also binds its exact
   root/generation and finalize attempt. Preserve stored scope/token and the live
   connection/transaction; substituted or draft-only facts refuse state and audit.

Terminal metadata carries the applicable stored policy plus finite class and
same-operation retry meaning derived solely from it: Permanent/no grant,
Transient/permitted or Transient/forbidden. They are not separately writable facts.
On retry exhaustion, permitted describes adapter repeatability; six admissions
still forbid another normal attempt.
Preserve them on finalizer refusal/exhaustion alongside the incomplete projection;
leave them absent on paths without a completed failure. No provider text or payload
bytes enter audit. Business MUST audit precedes the terminal fence/MUST row and
the same commit gate; terminal audit failure rolls both state and business back.

A forward migration refuses retained Failed/Cancelled history missing complete
policy, or incompatible combinations, with a bounded diagnostic. It never guesses
classification or backfills permission. The activated runtime has no external
definitions; qualification must still prove refusal. A future live migration of
incomplete history requires an explicit recovery design.

### Exact relationship to ADR-0047

The narrowly replaced input restriction is
[Attempts, terminal audit and diagnostics](0047-transactional-background-job-runtime.md#attempts-terminal-audit-and-diagnostics):
“Classify the top-level code/type only.” Ordinary requests and invocation,
Prepare and Finalize failures retain that classifier. Known completed external
Failed/Cancelled reports instead use the explicit policy defined here.

The additive decisions are the report taxonomy, cancellation restriction, immutable
status projection, explicit repeatability meaning, deterministic storage gates and
`external_retry_forbidden` with bound audit facts. There was no direct completed-report
mapping to replace. Existing table precedence, unknown-effect rules, stable identity,
budgets, privileges, quiescence and H1 ownership remain, including owned cancellation
before ordinary failure classification. This decision makes those boundaries explicit.

This is a changed normative decision, not an
[ADR-0041 correction](0041-correcting-false-statements-in-accepted-adrs.md#decision)
of a false historical statement. ADR-0047
[Amendment 4](0047-transactional-background-job-runtime.md#amendment-4--known-completed-external-failure-policy-2026-10-17)
names the bounded replacement and additive seams. Its Accepted body/date/deciders
remain intact; the record is not retired.

## Context

Step 1 delivered the typed external contract; Step 2 qualified known-failure retry
and selected-outcome retry refusal. These controls establish admissible transitions,
not the missing classifier. Step 7 preparation at source `6a2ded99` identified that
gap before external activation. The report contract and storage guards need a
shared policy that remains reproducible after lost responses and process restart.

A stable provider key may remember final failure or permit another fenced attempt.
The policy distinguishes those cases without provider-specific runtime code.
The dedicated terminal reason separates nonrepeatable transient policy from permanent
failure. Restricting Permanent to Failed also prevents Cancelled/Permanent acquiring
different interpretations when an old stop flag disappears.

Actual provider contracts or a requirement for runtime-enforced causal discrimination
could reopen the taxonomy. That requires an explicit durable protocol; no new provider
method, request identity, stop-marker inference or default retry grant is implicit.

## Consequences

### Positive

- Valid durable facts choose the same policy across Execute, Reconcile and restart.
- Retry and finalization cannot select contradictory policy at the storage boundary.
- Terminal audit distinguishes permanent failure, prohibited repeat and exhaustion.
- Two-method adapters, identifier-only DTOs and confined control reads remain.

### Negative

- Completed-report policy and Result/exception classification are parallel mechanisms;
  adapters and runtime must qualify their boundary, precedence and future consistency.
- Adapters must reproduce truthful completed-attempt facts, including cancellation
  meaning. Generic runtime validation cannot establish provider causality.
- An adapter honoring shutdown may truthfully report Cancelled/TransientNotRepeatable.
  A graceful shutdown or deploy can therefore lead to final failure with
  `external_retry_forbidden` and MUST dead-letter audit for a nonrepeatable provider.
- A forward migration, guards, reason carriers, witness metadata and proofs add work.
- Legacy incomplete history refuses migration rather than receiving invented policy.

### Neutral

- Positive completed quiescence releases old occupancy on every policy branch.
- No exactly-once effect or universally useful repeat execution is claimed.
- Step 6's accepted capacity contract and transactional cleanup receipt are unchanged.

## Implementation Notes

Step 7 implements this report/storage/control/finalizer contract
before external activation. Step 9 closes full enforcement and regression. Phase 04
owns real providers/process supervision and domain definitions; P02b-4 qualifies
explicit fixture adapters through the real engine with no production definition.
Phase 03 owns audited operator reconciliation; Phase 11 owns readiness and retention.

Synchronize these finite reason carriers: canonical Standard 05 CHECK/guards through
a forward migration; `BackgroundJobFailurePolicy` terminal enum/conversion;
`BackgroundJobControlModels` terminal claim; and `JobTerminalTransition` validation
and metadata. Existing migrations remain history. `BackgroundJobExternalContracts`
owns report/module reason types; the terminal store and app finalization participant
own their respective actual returned projections and minting/caller paths.

Tenancy retains `platform.job.dead_letter`, MUST/SecurityEvent/off-path, and its
audit matrix. `PostgresAuditStore` preserves the sixth audit seam and live witness
validation; Tenancy's declaration is not the job-reason validator. Add no audit method.

Acceptance updates the ADR-0047 amendment, decision index, glossary, current P02b-4
record and the assigned catalogue assertions and implementation steps. Step 7
synchronizes Standards 05/09/18/20, Events/Outbox architecture and the applicable
Tenancy audit description with implementation. Error Handling's inherited retry
summary must separately
match Accepted ADR-0047's six normal/six finalize admissions, five fixed delays,
authoritative-row terminal state and Phase 03 operator ownership. Synchronizing
that carrier drift is not a new budget decision or passing runtime proof.

## Architecture Tests

Extend these seven existing **Registered** rules with the assigned assertions.
Acceptance updates Asserts and the assigned implementation steps in Status;
it promotes no rule and changes no catalogue count.
All proofs are mandatory and pending in Step 7, with Step 9's full closure gate.

| Canonical rule | Assigned obligations |
|---|---|
| `BackgroundJob_External_Work_Has_No_Business_Transaction` | Closed explicit report/factory matrix; exactly Execute/Reconcile; no UoW or callback; adapter translation and direct completed-report cases. |
| `BackgroundJob_External_Recovery_Never_Assumes_Quiescence` | Unknown-first recovery, cancellation taxonomy and restart truth, invalid-report refusal, late-start fencing, lost response/ACK, durable receipt precedence and bounded quarantine. |
| `BackgroundJob_Capacity_Is_Tenant_Wide_And_Quiescence_Bound` | App-owned atomic outcome/release for retry and both finalization branches; retained uncertainty; later capacity reacquisition; no TTL inference. |
| `BackgroundJob_Storage_And_Credentials_Are_Confined` | One immutable policy, null/identity/time gates, retry/finalization/reason guards, exact grants and locked status probes; incomplete-history migration refusal. |
| `BackgroundJob_Terminal_Audit_Is_Row_Bound` | Actual returned selected-outcome/policy binding, derived finite metadata, exact app/bypass callers, audit rollback, uncertain COMMIT and honest finalizer-incomplete projection. |
| `BackgroundJob_Finalization_Retries_Are_Bounded` | Module/terminal reason mapping, six separate finalize admissions, Result.Ok domain failure, owner-atomic business/terminal audits, preserved outcome and no Execute rerun. |
| `BackgroundJob_Retries_And_Terminal_Faults_Preserve_Progress` | Deterministic priority including admission six, fixed delays, stable operation/fresh attempt, forbidden-repeat refusal, no seventh start and fair control progress. |

Discriminating controls must cover every policy row through Execute and Reconcile
without a prior exception; invalid/default factories; genuine independent permanent
failure racing stop; honored cancellation across restart; and invalid Cancelled/
Permanent rejected unchanged. A planted adapter misreport is a protocol violation,
not proof that deadline expiry establishes causality.

Use actual app/control roles to reject overwritten/foreign/stale facts, false retry,
early finalization, false exhaustion and mismatched terminal reasons/witnesses.
Remove each relevant predicate/grant in a dirty control and demonstrate its effect.
Exercise SQL NULL truth tables for absent or partially persisted report facts.
Owned-stop cases cover lease loss, deadline and shutdown before invocation, during
the checkpoint, after committed outcome and after restart without the old stop flag.
Lost checkpoint ACK, conflicting later reports, finalizer refusal/exhaustion and
audit rollback must preserve the exact selected outcome, occupancy and both budgets.
All observations, including positive quiescence, must survive the intended recovery
path without claiming that uncommitted facts survive process death.

## References

- [Decision index](README.md) and [Background Jobs glossary](../glossary.md#background-jobs).
- [ADR-0040](0040-ambient-unit-of-work.md), [ADR-0044](0044-audit-write-path.md) and [Documentation Standards](../standards/13-documentation.md).
- [P02b-4 proposal and acceptance gate](../roadmap/phase-02b-events-auth.md#p02b-4-external-completed-failure-proposal-2026-10-16).
- [ADR-0047](0047-transactional-background-job-runtime.md), [ADR-0058](0058-bounded-stock-hangfire-maintenance.md), [ADR-0059](0059-owned-hangfire-server-generations.md), [ADR-0060](0060-fatal-hangfire-startup-and-unsafe-unwind.md).
- [Database Standards](../standards/05-database.md#background-jobs), [Error Handling](../standards/09-error-handling.md#background-jobs), [Infrastructure Stack](../standards/20-infrastructure-stack.md#background-jobs-and-hangfire).
- [Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md#p02b-4-background-job-obligations), [Audit Coverage](../standards/18-audit-coverage.md), [Events and Outbox](../architecture/15-event-and-outbox.md).
