# Data erasure: the privileged step

How an operator finds the data requests that need a person and completes an erasure: the
audit redaction only an operator may run, the request's completion and its record, in
one transaction. Decided in
[ADR-0107 § 8](../decisions/0107-data-request-fulfillment-and-account-closure.md) and
[ADR-0108](../decisions/0108-privileged-audit-redaction.md). There is **no dashboard**:
this is an API and this document.

## What exists

| Route | Does | Permission |
|---|---|---|
| `GET /api/platform/v1/data-requests` | Lists the requests and export artifacts that need an operator (an **inspection**) | `platform.data_request.read` |
| `POST /api/platform/v1/data-requests/{requestId}/complete-erasure` | Redacts the subject's audit rows and completes an erasure awaiting its privileged step (a **completion**) | `platform.data_request.write` |

Authentication, the token profile and how a grant is given are in
[Platform operator access](operator-access.md). The served operator document,
`platform-v1`, is the contract for these routes, and it is the only client there is.

Both run on `ConnectionStrings:PlatformAdmin` and need the Identity migration
`20261122200000_grant_platform_data_request_operations` applied; without it the
`expired_artifact` list fails with a permission error and a completion with the request
guard's refusal, both a `500` named in the API's log. The erasure job itself needs
`20261122203000_admit_erasure_profile_clearing`: without it the profile clearing raises
`P0304` and the job dies after its attempts. A completion also needs the Audit
module's redaction executor registered (`IAuditRedactor`): without one it answers `500`,
the log names the port, and nothing changes.

## What the tenant side already did

An erasure is filed for one scope group, by the person or by an administrator for a
Membership that is already Revoked, and waits its 24-hour cancellable window. A job in
that group then revokes the person's export artifacts and unstarted exports, erases the
Membership (its assignments removed, its profile cleared, a Revoked tombstone kept),
revokes the pending invitations the person issued, records what the group retains, and
leaves the request `awaiting_privileged_step`
([ADR-0107](../decisions/0107-data-request-fulfillment-and-account-closure.md) § 5 and
§ 6). An administrator's erasure is filed only for a Membership that is already
Revoked, and that is checked when it is filed: if the person is admitted to the group
again during the 24 hours, the job still runs, and the person, who may cancel the
request in the window, is the safeguard. While it runs, the job holds the tenant's
identity fence exclusively, the Audit participant's counts included, so other
Membership, invitation and data-request writes in the tenant wait up to two seconds and
then answer `503` for a retry; the hold lasts seconds, and longer in a tenant with
millions of audit rows. What it cannot touch is the audit log: no tenant connection may
change an audit row, and `learnstack_platform` alone may update six of its columns.
That is the step left to you.

## What is not here

- **A global account closure**
  ([ADR-0107 § 7](../decisions/0107-data-request-fulfillment-and-account-closure.md)).
  Its acts are in [Account closure](account-closure.md), and these routes never act on
  a `closure` request or on a closure's fan-out erasure: the completion refuses the
  latter, which only its closure's Finalize completes. A person's own erasure that a
  closure adopted can still be completed here; the closure's Finalize completes it with
  the others if it still awaits its privileged step. The two delayed jobs that do not
  read a closing person's status are listed in that runbook's
  [What is not here](account-closure.md#what-is-not-here).
- **Cancelling or failing a request.** The person, or the administrator who filed it,
  cancels within the window. A request `blocked`, `failed` or `cancelled` is terminal;
  the person files a new one.
- **Anonymity.** The completion redacts what is not needed to say who did what. It never
  makes the person anonymous: see [Reading the result](#reading-the-result).
- **A read of one request.** The lists are oldest due first and cannot be filtered by
  tenant or person, so finding one request means paging; each page is one audited scope.
  A request's identifier reaches you from the inspection or from the tenant, never with
  the person's identity.
- **Undoing a redaction.** LearnStack keeps no copy of a redacted value; backups, WAL
  archives and row versions awaiting vacuum can still hold one (see
  [Not recoverable here](#not-recoverable-here)).

## When to look

- At least once a day: an erasure not `completed` 30 days after it was filed is a defect
  ([ADR-0106 § 12](../decisions/0106-subject-data-requests-authority-and-status.md)),
  and the window and the job take the first day.
- A person or a tenant asks why an erasure is not complete.
- `learnstack_job_failed_total` rises with a `job_name` of an
  `identity.data_erasure_*` definition, `learnstack_job_oldest_eligible_age_seconds` or
  `learnstack_job_state_count` shows an erasure job late or dead, or a quarantined
  erasure job appears in the [operator recovery](operator-recovery.md) inspection.
  Nothing alerts on the `awaiting_privileged_step` list itself: a person polls it, and
  [Phase 11](../roadmap/phase-11-production-hardening.md) owns an automated gauge and
  its alert rules.

## Before you complete

- You hold `platform.data_request.write` and a token with a second factor. The keys are
  given through `PlatformOperator:Grants` and evaluated on every act
  ([how a grant is given](operator-access.md#giving-and-withdrawing-a-grant)); they are
  never assignable to a tenant role, and the shipped demo operator holds neither. The
  read and the write are separate grants: an operator who may list may not complete.
- You may send a ticket for the act, 1 to 64 letters, digits or `.` `_` `:` `/` `#` `-`.
  Send one whenever your change process has one: it is recorded on the scope's entry row
  only.
- The request is listed under `awaiting_privileged_step`. You send nothing else: the
  completion reads the person, the group, the request's origin and its frozen
  Memberships from the request row itself, and never from the call. You do not learn,
  and need not know, who the person is.

## Inspect

Each page is one entered platform scope, so each page writes one
`platform.admin_scope.enter` audit row with the reason `data_request_inspect`, your
attribution and your ticket. It does not say which list you read or how far you paged.
Pass the ticket as `?ticket=`, and percent-encode `#` as `%23`.

```sh
curl -s -H "Authorization: Bearer $TOKEN" \
  "$API/api/platform/v1/data-requests?state=awaiting_privileged_step&limit=20&ticket=CHG-123"
```

| `state` | Lists | What to do |
|---|---|---|
| `awaiting_privileged_step` (default) | Erasures whose tenant side finished | [Complete](#complete-an-erasure) each whose `origin` is `self` or `administrator`. A `closure_fanout` item is completed only by its closure and answers `409` here |
| `overdue_scheduled` | Requests still `scheduled` once their window ended (`executeAt`), with no grace | Every erasure is listed here for the moments between the end of its window and its job's pickup. One still listed after a few minutes has a late or dead job. The [quarantined-jobs inspection](operator-recovery.md) shows only quarantined jobs, not one still retrying or out of attempts, so read the job table as [Data export § A stuck or dead export request](data-export.md#a-stuck-or-dead-export-request) does, with the definition keys `identity.data_erasure_tenant.v1` and `identity.data_erasure_organization.v1`. A dead job's request reads `failed` on the person's receipt and stays `scheduled` here until the person files again, which frees the slot and is the recovery: [tell them so](#when-a-person-asks) and escalate the job |
| `stuck_fulfilling` | Requests still `fulfilling` two hours after they fell due (`executeAt`, or `createdAt` when there is no window) | An erasure job commits `fulfilling` only together with `awaiting_privileged_step`, so a dead run rolls back to `scheduled` and is listed above, never here. This list holds a request that starts `fulfilling` and whose job then stopped half-way (a closure's fan-out, [ADR-0107 § 7](../decisions/0107-data-request-fulfillment-and-account-closure.md)). Two hours outlast the job runtime's whole retry schedule: find the job and escalate |
| `expired_artifact` | Export artifacts past `expiresAt` whose purge has not run | See [Data export § An expired artifact whose purge died](data-export.md#an-expired-artifact-whose-purge-died) |

- `limit` is 1 to 100 (default 20; a larger value is clamped, and zero or less is
  refused with `errors.limit`). An unknown `state` is refused with `errors.state` and a
  malformed or foreign `cursor` with `errors.cursor`. Only `state`, `limit`, `cursor`
  and `ticket` are accepted; any other parameter is refused with `errors.query` and
  nothing is read. The
  answer holds `items` and a `pageInfo` with `nextCursor` and `hasNext`; send
  `nextCursor` back as `cursor`. A cursor belongs to the list that issued it.
- Each item carries `requestId`, `tenantId`, `organizationId` (null for the tenant-wide
  group), `kind`, `origin`, `status`, `createdAt`, `executeAt`, `completedAt`,
  `lastRefusal`, `failureReason`, and on `expired_artifact` the `artifact`
  (`artifactId`, `status`, `expiresAt`). It never carries the person, the Memberships, a
  requester, a progress record, a result or an artifact's content: the inspection
  selects none of them, and the platform role holds no grant on the requester, the
  actor stamps or the artifact's person, size or key. The completion reads the person,
  the Memberships and the result of the one request it completes, because it needs
  them, and never returns them.
- Items come oldest due first: by `executeAt`, or `createdAt` when there is no window,
  then by request identifier; artifacts by `expiresAt`.

## Complete an erasure

```sh
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  "$API/api/platform/v1/data-requests/$REQUEST/complete-erasure" -d '{"ticket":"CHG-123"}'
```

The body is `{}` or `{"ticket":"..."}`. In order, and in one transaction:

1. The platform scope is entered: its entry row, under the platform sentinel with the
   reason `erasure_complete`, your attribution and your ticket, commits first and stays
   whatever happens next.
2. The request is locked and read whole. An absent request, one that is not an erasure,
   a closure's fan-out erasure and one that is not awaiting its privileged step are
   refused, and nothing else is written. A completed one answers its recorded result
   (`already_completed`).
3. The redaction is planned from the row. A request the person filed selects the
   person's audit rows across the whole tenant. A request an administrator filed selects
   only the rows of its group's organization, or the rows with no organization for the
   tenant-wide group, so an organization administrator's request never changes another
   branch's record ([ADR-0108 § 4](../decisions/0108-privileged-audit-redaction.md)).
4. The audit rows are redacted by the Audit module's one executor: on every selected row
   `ip_address`, `user_agent` and `actor_email` become NULL, and in `before_state`,
   `after_state` and `changes` a value at a reference a module declares becomes
   `***REDACTED***` when it is the person's identifier or one of the request's
   Memberships. Another person's value at the same reference, such as an administrator
   who updated the record, is left as it is.
5. The request moves to `completed`. The database stamps `completedAt` and the system
   actor itself. The request's audit kinds are replaced by the redaction's exact counts
   (the Audit participant's counts from before the act were a lower bound), and every
   other kind stays as its participant recorded it.
6. The result row, `audit.redaction.apply`, is written in the **request's tenant**: no
   actor, the reason `erasure_complete`, the correlation id, your attribution and **no
   ticket**, `before_state` `{"status":"awaiting_privileged_step"}` and `after_state`
   the closed report.
7. All of it commits together, or none of it does.

| Answer | Meaning |
|---|---|
| `200`, `outcome: completed` | The act committed. `rowsSelected` and `redacted` (rows changed per column, every column listed) are the redaction's; `retained` is the request's whole recorded report, the audit log's kinds included; `completedAt` is the database's |
| `200`, `outcome: already_completed` | The request was already completed, by you or another operator. Its recorded `retained` and `completedAt` are returned, nothing was redacted and **no second result row** was written; `rowsSelected` and `redacted` are null, and the first act's counts are on its result row |
| `404` | No request has that identifier, or the identifier is not a UUID at all (the route does not match) |
| `409`, `errors.kind` = `lockey_data_request_not_erasure` | The request is an export or a closure. Nothing changed |
| `409`, `errors.origin` = `lockey_data_request_closure_fanout` | The erasure is a closure's fan-out, which only that closure's finalization completes. Nothing changed |
| `409`, `errors.status` = `lockey_data_request_not_awaiting_privileged_step` | The erasure is not awaiting its privileged step; `params.cause` is its stored status. `scheduled`: its window has not ended, or its job is late or dead (look at `overdue_scheduled`). `fulfilling`: a closure's fan-out still running or stopped (look at `stuck_fulfilling`). `blocked`, `failed`, `cancelled`: terminal, nothing is left to complete; the person files a new request. Nothing changed |
| `400` | The request identifier is a UUID but not a UUIDv7 (or is the nil or the sentinel identifier), or the body or the ticket is malformed. A refusal at the gate carries no per-field errors |
| `401`, `403`, `413`, `415`, `429` | As in [operator recovery](operator-recovery.md#act): the credential, the grant (`platform.data_request.write` for a completion, `platform.data_request.read` for a list), the body size or type, or the request budget |
| `503`, `audit_unavailable` | The entry row or the result row could not be written: no effect of the act committed (the entry row stays, or was never written). Retry after the delay; error line 7105 is in the API's log |
| `503`, `dependency_unavailable` | The database could not be reached, a redaction statement ran out of time, or another operator's completion of the same request still holds it (see [How long it takes](#how-long-it-takes)). No effect committed and the request is still awaiting; retry after the delay, because the redaction's statements are idempotent. The cause is in the API's log (warning 7110) |
| `503`, `dependency_unavailable` with `errors.outcome` = `lockey_outcome_unknown` | The commit was sent and its answer was lost. It **may have landed**: see [An unknown outcome](#an-unknown-outcome) |
| `500` | An unexpected failure or a deployment defect: no redaction executor registered, a refused credential, a missing grant or migration. The API's log names it. No effect of the act committed (the entry row stays) |

## An unknown outcome

Repeat the same completion. If the first landed, the repeat answers
`200 already_completed` with the recorded result and writes no second result row; if
it did not, nothing of it committed and the repeat completes the request now. To tell
your act from another operator's, read the tenant's result row by the `correlationId`
of the failed response, as in [Reading the result](#reading-the-result). A client that
never received an answer (a timeout, a gateway error) is in the same position.

## How long it takes

The redaction reads the tenant's audit rows in batches of at most 100,000 rows or 64 MiB
of payload, measured decompressed and counted only for the rows in the request's reach,
so the act takes time in proportion to the payload it must read: about twelve seconds
for 1.5 million ordinary rows (1.5 GB stored), about four seconds for 30,000 rows of
250 KB that compress well, about four minutes for a tenant whose rows carry 100 KiB of
incompressible payload (14 GB stored), and the request row stays locked throughout. Each
statement stays at about two seconds or less whatever the tenant's size; only the number
of batches grows. The act redacts the rows committed before it begins. Use a client with
no short timeout. A call dropped before the commit (a client or gateway timeout, a
closed connection) rolls the whole act back, and a retry starts again from the
beginning, so a gateway that cuts the call off earlier than the act ends can never
succeed. Two operators who complete the same request at once are serialized on its row:
the second waits for the first and then answers `already_completed`, unless the first
outlasts the platform connection's command timeout (30 seconds unless `Command Timeout`
is set in `ConnectionStrings:PlatformAdmin`), when the second answers `503
dependency_unavailable`: repeat it after the first finished. Two requests for the same
person (one filed by the person, one by an administrator) wait on each other's rows the
same way, and a deadlock between them answers `503` too: repeat it.

## Reading the result

The response, and the result row's `after_state`, hold closed counts and never a person
identifier: `rows_selected`, `redacted` per column (`ip_address`, `user_agent`,
`actor_email`, `before_state`, `after_state`, `changes`) and `retained` per kind. The
audit log's retained kinds are:

| Kind | Counts |
|---|---|
| `audit_actor_reference` | Selected rows whose `actor_user_id` is the person, which stays |
| `audit_entity_reference` | Selected rows whose `entity_id` is the person or one of their Memberships, which stays |
| `audit_metadata_reference` | Every row of the request's reach whose `metadata` names the person, selected or not, because the metadata stays (exact) |
| `unscoped_audit_rows` | For an administrator's request, rows outside the group's organization whose actor or entity is the person or one of their Memberships, which the selection does not reach (at least: payloads and metadata outside the reach are not read) |

What survives on every row, by decision
([ADR-0108 § 6](../decisions/0108-privileged-audit-redaction.md)): `actor_user_id`,
`entity_id`, all of `metadata` (the causal actor, the authorization references and the
identity subject key among it), the causation and correlation identifiers, the module,
the operation, the outcome and the timestamps. The person's `UserId` stays a
pseudonymous identifier that the subject key keeps linkable: **never call the erased
person anonymous**, in a reply to the person, to the tenant or in a ticket. The
request's other retained kinds are the tenant side's report, and it is a partial list: a
kind it leaves out is retained and uncounted, never zero. The tenant side counts the
consent evidence, the invitation records (the offers the person issued or accepted, not
a pending offer addressed to their address), the request receipts, the attribution
references, the federation actor references and the provider event records of the group
([ADR-0107 § 6](../decisions/0107-data-request-fulfillment-and-account-closure.md)).
It does not count the transport and execution evidence, the idempotency cache, the
external copies or the invitation envelopes, so do not tell anyone that those were
reported or that none exist. The retention target of each audit row's class is in
[Audit Coverage § Retention](../standards/18-audit-coverage.md#retention), which no code
enforces yet: [Phase 11](../roadmap/phase-11-production-hardening.md) owns the purge.

The redaction rewrites the rows that exist when it runs. The tenant's records keep their
creator, updater and deleter attribution, so a later edit of a record the person created
or last updated writes their identifier again, in a new audit row. Never tell the person
that no row will ever carry their identifier again.

The result row is in the **tenant's** trail. There is no audit API: read it as the
application role announcing the tenant (keep the `tenantId` of the inspection item), by
the response's `correlationId`, with the digest recipe of
[Tenant onboarding](tenant-onboarding.md#reading-the-record) and this select list, which
adds the request and the closed report to it:

```sql
BEGIN;
SELECT set_config('app.tenant_id', '<tenantId>', true);
SELECT timestamp, operation, outcome, reason, entity_id, before_state, after_state,
       metadata->'operator' AS operator
FROM audit_log WHERE correlation_id = '<correlationId>' ORDER BY timestamp;
COMMIT;
```

`entity_id` is the request, `before_state` the status it was awaiting from and
`after_state` the report. Your ticket is not on the row, because the tenant can read it;
it is on the entry row, under the platform sentinel. Record the `correlationId` against
your own ticket.

## When a person asks

A person or a tenant asks why an erasure is not complete. The person's own receipt is
the status source: they ask for it under the identifier the request answered with, and
it shows the status and `executeAt`, the end of the 24-hour window in which they may
cancel. No e-mail is sent when an erasure finishes
([ADR-0107 § 10](../decisions/0107-data-request-fulfillment-and-account-closure.md)). A
request cannot be read by identifier through these routes, and the completion is not a
read: never call it to look. Say only what is true: the person's name, profile values
and role assignments in that group were erased, the IP address, user agent and e-mail
on the audit rows that concern them were removed once the operator step ran (backups
and archives are kept and purged on their own schedule), and their identifier stays as
a pseudonymous reference; never say they are anonymous. A `failed`
receipt means the job died: the person files again, and the old request stops blocking
them.

## What an administrator sees

An administrator files an erasure from the member's page in Studio, only for a
Membership that is already Revoked, after confirming a statement of what is erased, what
is kept, the 24-hour window, that the person may cancel in it, and that a re-admission
within it does not stop the erasure
([Frontend Architecture § Subject data requests](../standards/07-frontend-architecture.md#subject-data-requests)).
The request's page in `/studio/data-requests` then shows `Scheduled` and the end of the
window, with a cancel form for the administrator who filed it; `Starting soon` once the
window has passed and the job has not run; `Waiting for an operator` while it waits for
you; and `Completed` with the retained counts after your completion. A blocked erasure
says why (a hold, or the person is the last administrator). A closure is never listed; a
closure's fan-out erasure is, filed by the person's account closure and with no cancel
form. If an administrator asks why an erasure is not complete, the status on that page
is what they see; answer as in [When a person asks](#when-a-person-asks).

## Deploying

Every API host must run a build that registers the erasure definitions
(`identity.data_erasure_*`) before anyone files an erasure, and none may roll back to an
older build while an erasure is `scheduled`: a job is queued 24 hours ahead, and an
older host that claims one does not know its definition, treats it as an invalid
envelope and ends it terminal (`invalid_envelope`). A later version of a definition
keeps the old one registered for at least the window.

## A dead erasure job

A job that exhausted its attempts, or was quarantined, rolled every write back, so its
request stays stored `scheduled`: the person's receipt reads `failed` (the status is
derived from the job once its window has ended), the request cannot be cancelled after
`executeAt`, and the inspection keeps listing it under `overdue_scheduled`. Nothing
here repairs the job. The recovery is a new filing by the person, or by the
administrator who filed the first one: it stores the old request `failed` with
`execution_failed`, frees the one-active slot and schedules a new job. Tell them so, and
escalate the cause with the request identifier and the job's definition key. The job
logs its run as event 7511 (finished, with the outcome), 7512 (not yet due) and 7513
(an attempt failed, with the exception type only).

## Erasure never resets replay

[ADR-0107 § 9](../decisions/0107-data-request-fulfillment-and-account-closure.md) sets
the floor. An erasure never rewrites or deletes an outbox, delivery, inbox or job row:
those are retained as `transport_evidence` and `execution_evidence`. A delivery an
operator replays or resets later re-runs its handler under that handler's own subject
rules, which refuse a Closing or Closed User but not a tenant-erased person, whose User
stays Active. So a replayed delivery about the person may write rows for them again in
that tenant; those rows are retained transport evidence that no report counts, and they
are not undone. A delivery about a person whose closure has begun, or was finalized,
runs its handler, which refuses the person and writes nothing. Before a
[replay or a producer reset](operator-recovery.md#choose-replay-or-reset) in a tenant
where an erasure completed, ask whether the event concerns the erased person.

## Targets and escalation

- Complete every listed erasure well inside the 30 days of
  [ADR-0106 § 12](../decisions/0106-subject-data-requests-authority-and-status.md); the
  count starts when the request was filed.
- An erasure that keeps answering `503` or `500`, one listed under `overdue_scheduled`
  or `stuck_fulfilling`, and a quarantined erasure job are escalations to the
  deployment's owner, with the request identifier and the `correlationId` of each failed
  call. Nothing here repairs a job.
- Never change an audit row or a request by hand. A statement outside the executor and
  the request guard's one arrow writes no record, and the guard refuses every other
  change to a request.

## Signals

`learnstack_platform_operator_admissions_total{outcome,operation}` counts the acts
admitted or refused at the gate, with `operation` `platform.data_request.inspect` or
`audit.redaction.apply`;
`learnstack_platform_operator_results_total{operation="audit.redaction.apply"}` counts
the completions committed, and a repeat that answers `already_completed` is not one;
`learnstack_platform_operator_data_requests_total{operation,outcome}` counts every call
that reached the owner by how it ended (an inspection's `ok` or `invalid_cursor`; a
completion's `completed`, `already_completed`, `not_found`, `not_erasure`,
`closure_fanout` or `not_awaiting`; either's `audit_unavailable`, `unavailable` and
`failed` (an unexpected failure, answered `500`), and a completion's `commit_unknown`),
so a refused, repeated or unknown-outcome act leaves a signal;
`learnstack_platform_operator_audit_failures_total` counts the refusals for an audit
failure. Each inspection page and each completion logs the scope-entry line 7001, a
refused audit write the error line 7105, and an unavailable database or redaction the
warning line 7110, whose exception holds the cause. The admission and refusal counters
are in [Platform operator access](operator-access.md#signals).

## Not recoverable here

- A redacted value: LearnStack keeps no copy, but backups, WAL archives and row versions
  awaiting vacuum can still hold one.
- What survives by decision: `actor_user_id`, `entity_id`, `metadata` and the
  identifiers of every audit row.
- A request that is `blocked`, `failed` or `cancelled`: the person files a new one. A
  `blocked` receipt shows why in its `lastRefusal` (`blocked_last_administrator` or
  `blocked_hold`): the person or the tenant resolves that first.
- Copies outside the database, such as the identity provider's events, backups, WAL
  archives and mail copies (`external_copy`):
  [Phase 11](../roadmap/phase-11-production-hardening.md) owns their purge.
- A dashboard.
