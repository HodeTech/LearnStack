---
name: add-background-job
description: >
  Add a module-owned transactional or external background-job definition, delayed
  submission, or durable recurrence using LearnStack's typed scheduler and H1
  runtime. USE FOR: job parameters/roots, prepare-execute-finalize adapters,
  capacity declarations, recurrence manifests and their proofs. DO NOT USE FOR:
  direct Hangfire calls, operator replay, privileged maintenance, or changing
  runtime ownership/storage policy without its decision pass.
---

# Adding a background job

## Purpose

Deliver a feature's job through the existing trusted submission, ordinary
transaction owner and retained-work boundaries. The architecture is decided in
[ADR-0047](../../../docs/decisions/0047-transactional-background-job-runtime.md),
with lifecycle refinements in ADR-0058/0059/0060 and known external failure policy
in [ADR-0061](../../../docs/decisions/0061-known-external-job-failure-policy.md).
Use [implement-task](../implement-task/SKILL.md) as the substantive entry point.

## When to use

- Add a feature-owned transactional or external definition and its request metadata.
- Schedule an ordinary job or declare a code-owned recurring manifest.
- Bind a feature's external operation port, dedicated capacity group or worker queue.

## When not to use

- Publish cross-module facts: use [add-integration-event](../add-integration-event/SKILL.md).
- Change the scheduler, credential grants, transaction owner, quiescence policy or
  native invocation contract: run the owning decision pass first.
- Add operator replay, privileged maintenance or a cancellation/rescheduling API:
  verify the named feature phase's accepted contract before implementation.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Owning feature phase/module | Yes | Confirm this is the phase that delivers the business definition. |
| Profile and scope | Yes | Transactional/external; TenantWide/Organization. |
| Typed parameter and request contracts | Yes | Exact module Contracts/Application types and all allowed nested requests. |
| Queue/capacity declaration | Yes | Compatible pool; external work requires its declared capacity group. |
| External evidence contract | External | Stable logical operation, Execute/Reconcile, known outcome and positive quiescence. |
| Recurrence manifest | Recurring | Monotonic revision, UTC schedule, target and closed lifecycle recipient policy. |

## Workflow

### 1. Check the delivered boundary

Read the feature roadmap and current
[P02b-4 record](../../../docs/roadmap/phase-02b-events-auth.md#p02b-4-decision-package-2026-10-15).
P02b-4 intentionally carries no production business definitions or recurrence
manifests. The first feature must explicitly reconcile that infrastructure-only
census when its own accepted phase delivers real definitions.

Read the relevant [glossary](../../../docs/glossary.md),
[Events and Outbox architecture](../../../docs/architecture/15-event-and-outbox.md),
[Database Standards](../../../docs/standards/05-database.md#background-jobs), and
[Observability Standards](../../../docs/standards/10-observability.md#required-metrics).
Verify new capabilities have an owning decision rather than inventing a new entry
path, general tenant selector or platform authority.

### 2. Declare inert types and exact request metadata

Use the actual contracts in
[`SharedKernel/Jobs`](../../../backend/src/LearnStack.SharedKernel/Jobs).
Put a sealed, init-only identifier parameter record implementing
`IBackgroundJobParameters` in the owning `Application.Contracts`. Follow the
payload guards for identifier types and size; custom getters, plaintext business
content and mutable object graphs are refused.

Declare the module's code-owned definition in `Application`, with its exact
`BackgroundJobKey<TParameters>`, scope, readiness and profile. List every allowed
root/nested request with positive `Ordinary` scope metadata. Roots belong to the
owning Application assembly; nested cross-module work uses the existing Contracts
mechanism. Use [add-mediatr-handler](../add-mediatr-handler/SKILL.md) and
[add-audit-coverage](../add-audit-coverage/SKILL.md) for each actual handler.

Wire declarations through the existing module/definition/infrastructure registries
and API composition; inspect their current constructors and validation instead of
copying a stale registration example. Verify root parameter constructors, queue
profile, module ownership and scope before constructing any handler. Add an
explicit retired tombstone for an intentionally retired identity; unknown and
Pending definitions preserve work for a compatible deployment.
Composition is explicit: preserve `BackgroundJobFrameworkRequests.All` and the
catalog aliases when supplying feature registries. Verify the resolved API
registry and every submitting composition root, including Seeder when applicable.

### 3. Submit on the owning transaction

Inject only `IBackgroundJobScheduler` into feature code. Its real methods are
`EnqueueAsync(key, parameters, ct)` and `ScheduleAsync(key, parameters, dueAt, ct)`.
Submit inside the existing enrolled writable module transaction. Tenant,
organization, producer and causal metadata come from trusted enrollment; a key or
payload cannot supply them. The returned identity does not commit the owner.

Keep schedule/business changes and the job INSERT atomic. Prove commit and
rollback, including an absorbed refusal after produced work. An org-bound context
cannot submit a TenantWide definition by relabeling the input. Never write job
rows, call a Hangfire client or mint routing/start/recipient receipts in a module.

### 4. Implement the selected execution profile

**Transactional:** use the ordinary MediatR root and its existing owner. Do not
open another transaction, change tenant context or acknowledge the job directly.
Completion, outbox and MUST audit remain one owned transaction.

**External:** define identifier-only `IBackgroundJobPlan` and
`IBackgroundJobOutcome` records in Contracts. Its module Contracts port inherits
one closed `IBackgroundJobExternalOperation<TPlan,TOutcome>`; the concrete adapter
lives in the same module's Infrastructure. Prepare/finalize are ordinary typed
requests; Execute/Reconcile hold no business transaction or sender/service locator.

Qualify stable logical-operation deduplication and positive provider/supervisor
quiescence. A timeout, cancellation, missing process or NotFound after ExecuteIntent
is not proof that effects stopped. Use the explicit Succeeded/Failed/Cancelled
factories and the actual three-state failure policy. Cancelled cannot be Permanent;
nonrepeatable outcomes are not silently retried. Preserve selected durable facts
across restart and conflicting later reports. Finalization must not repeat Execute.

Declare the module's tenant-wide `BackgroundJobCapacityGroupRegistration` through
the infrastructure registry. Use [add-feature-key](../add-feature-key/SKILL.md) for
any optional registered Hard `LimitKey`. An unknown external effect keeps its
reservation; expiry, host exit or replacement never frees capacity.

### 5. Declare recurrence through the existing manifest path

Use `BackgroundJobRecurrenceRegistration` and its actual registry validation.
The target belongs to the same module, accepts `BackgroundJobOccurrenceParameters`
and matches the selected recipient scope. That input deliberately does not
implement `IBackgroundJobParameters`; recurrence does not call the ordinary
submission port directly.

Preserve revision/hash compatibility, UTC CoalesceLatest and bounded lifecycle
paging. Native ticks are opaque hints; the durable occurrence/recipient records
own progress. Do not add a module tenant-list reader or a per-route scheduler loop.
Prove duplicate/late ticks, mixed revisions, tenant/org eligibility, bounded raw
paging and one reciprocal accepted child receipt. No recurring tick grants
business execution authority.

### 6. Validate and reconcile carriers

Run [run-tests-locally](../run-tests-locally/SKILL.md) for affected Unit,
Architecture and real PostgreSQL suites, using the nonempty/zero-skip runner.
Use [add-integration-test](../add-integration-test/SKILL.md) for tenant/org pairs
as `learnstack_app`; independently verify the dispatcher's narrow status grants.

Exercise actual registrations and dirty controls. Include rollback, hidden/cross-org
input, Pending/unknown identities, current/stale ownership, duplicate wakeups and
confirmed failure versus unknown COMMIT. External definitions additionally need
accepted-before-ACK, restart reconciliation, retained cleanup, no-effect restart
proof and reservation retention/release. Recurrence needs actual held-control
progress and late native duplicates, not only a mocked tick.

Update the owning roadmap, module audit/permission matrices and applicable feature
handoffs. Standards 05 owns schema/grants, Standards 10 instruments and Standards 21
the canonical obligations. Preserve Accepted ADR bodies; new decisions follow
[write-adr](../write-adr/SKILL.md). Commit with
[commit-and-pr](../commit-and-pr/SKILL.md) and perform the requested review rounds.

## Validation

- Production registration is nonempty for the feature and matches its owner/scope.
- Submission is atomic; execution uses the existing ordinary transaction owner.
- Failure, uncertainty, cancellation and quiescence remain distinct in actual tests.
- Finite registered metric dimensions contain no tenant/org/job/occurrence identities.
- All touched rules and handoffs describe delivered behavior and actual evidence.

## Common pitfalls

- Treating a definition key, tick, lease expiry or cleanup timeout as authority.
- Calling Hangfire or selecting all tenants from feature code.
- Moving long external I/O into a database transaction.
- Releasing capacity without verified positive completion/no-start/quiescence evidence,
  or retrying a nonrepeatable effect.
- Losing prior selected outcome facts while retrying finalization.
- Inventing cancel, replace, dedup or replay semantics absent from the owning contract.
