# Account closure: Begin and Finalize

How an operator closes one person's account across every tenant they belong to: the
confirmation with the person, the Begin that makes the account stop working and fans
the closure out to each of the person's groups, and the Finalize that finishes it.
Decided in
[ADR-0107 § 7](../decisions/0107-data-request-fulfillment-and-account-closure.md) and
§ 8. There is **no dashboard**: this is an API and this document.

## What exists

| Route | Does | Permission |
|---|---|---|
| `GET /api/platform/v1/data-requests` | Lists the requests that need an operator, closures included (the [inspection](data-erasure.md#inspect)) | `platform.data_request.read` |
| `POST /api/platform/v1/data-requests/{requestId}/begin-closure` | Fences the person, judges the closure, makes the account Closing and fans the closure out (a **Begin**) | `platform.data_request.write` |
| `POST /api/platform/v1/data-requests/{requestId}/finalize-closure` | Redacts the person's audit rows, deletes their identity-provider account, makes the account Closed and completes every request of the closure (a **Finalize**) | `platform.data_request.write` |

Authentication, the token profile and how a grant is given are in
[Platform operator access](operator-access.md). The served operator document,
`platform-v1`, is the contract for these routes, and it is the only client there is.

Begin runs on `ConnectionStrings:PlatformAdmin` and needs the Identity migration
`20261123100000_admit_account_closure` applied: without it the platform role cannot
read the person's Memberships, and the act fails with a permission error, a `500`
named in the API's log. Its fan-out runs on the application role, as each group's own
act, and needs `ConnectionStrings:Default`. A host with no fan-out registered refuses
every Begin with a `500` before it enters the platform scope, so nothing changes.
Finalize runs on the same credential and also needs the Identity migration
`20261123120000_admit_closure_finalize`, and the
[account removal client](account-removal-client.md): with no client secret configured
the port refuses every deletion, and every Finalize stops at its provider step with
`errors.provider` = `lockey_provider_refused` after its redaction committed.

## What the person already did

The person filed the closure themselves, from one of their groups, its originating
group: through their session or the retained entry. No administrator files, reads or
cancels a closure. The request is `scheduled` with an `executeAt` 24 hours after it
was filed, its earliest Begin. The person may cancel it until a Begin commits, also
after `executeAt`. While the account is Active the person reads the request's receipt,
which shows `lastRefusal` when a Begin was refused; once a Begin commits the account
admits no session, and no in-product status remains
([Identity module](../modules/identity/README.md#account-closure-filing-p03-10-step-3)).

## What is not here

- **A closure of a person with no Membership.** They hold no tenant data and cannot
  file one;
  [Phase 11](../roadmap/phase-11-production-hardening.md) data operations own them.
- **Undoing a closure.** Once a Begin commits, the account is Closing and nothing
  returns it to Active: no route, no repeat, no database change. A person who changes
  their mind needs a new account.
- **A fan-out route.** The per-group fan-out is an act Begin performs; repeat Begin to
  repeat it.
- **The data of a group whose tenant is suspended or archived, or whose organization is
  not Active** (an `unserved_group`). No fan-out runs there; the data stays and is
  reported, and [Phase 11](../roadmap/phase-11-production-hardening.md) offboarding
  owns it.
- **A read of one request.** As in [data erasure](data-erasure.md#what-is-not-here),
  the lists are oldest due first and cannot be filtered by tenant or person.
- **Restoring a deleted account.** Once Finalize's provider step deleted the person's
  identity-provider account, nothing here recreates it.
- **Two delayed consumers that do not read the account's status.** Between a Begin and
  the erasure job of a group, an invitation e-mail the person issued in that group may
  still be sent (the job then revokes the invitation, and its acceptance is refused),
  and an export the person filed before the Begin may still store its artifact (the job
  then withdraws it). Both are known gaps of P03-10, carried open in its Step 3 record
  for the maintainer: closing them needs a status read of the person in those jobs.

## When to look

- At least once a day: a closure not `completed` 30 days after it was filed is a defect
  ([ADR-0106 § 12](../decisions/0106-subject-data-requests-authority-and-status.md)),
  and the window takes the first day.
- `overdue_scheduled` lists every request still `scheduled` once its window ended,
  closures (`kind` `closure`) among them: each is a closure waiting for your Begin.
- `stuck_fulfilling` lists a begun closure still `fulfilling` two hours after its
  `executeAt`, which every closure is until its Finalize, and a closure's fan-out
  erasure still running two hours after it was created. Repeat the closure's Begin to
  see where its groups stand.
- `awaiting_privileged_step` lists each fan-out erasure (`origin` `closure_fanout`)
  whose group finished; only its closure's Finalize completes it.

## Before you begin

1. **You hold `platform.data_request.write`** and a token with a second factor, as for
   an erasure completion. The read and the write are separate grants.
2. **Confirm the request with the person** through the e-mail address the identity
   provider holds for them, before every Begin. This is the control a hijacked session
   cannot supply: a session can file a closure, and only the mailbox's owner answers
   you. LearnStack stores no e-mail address, so read it in the realm console:
   1. A database administrator holding the `learnstack_platform` credential reads the
      person's provider subject, the identity provider's own user identifier, for the
      request. The role's column grants cover exactly these columns.

      ```sql
      -- as learnstack_platform
      SELECT u.provider_subject
      FROM data_subject_requests AS r JOIN users AS u ON u.id = r.subject_user_id
      WHERE r.id = '<requestId>' AND r.kind = 'closure' AND r.status = 'scheduled';
      ```

   2. In the realm console of the tenant realm, open **Users**, search by that
      identifier, and read the user's e-mail address there.
   3. Write to that address, name the closure's request identifier and its
      consequences (the account stops working at once, for every tenant, and cannot be
      reopened), and wait for the person's explicit answer from that address.

   Write the provider subject and the address nowhere: not in a ticket, not in the
   Begin's ticket, not in a log. If the person does not confirm, do not begin; they may
   cancel the request themselves while their account is Active.
3. **Send a ticket** for the act whenever your change process has one, 1 to 64 letters,
   digits or `.` `_` `:` `/` `#` `-`. It is recorded on the scope's entry row and on
   the Begin's result row, both under the platform sentinel, which no tenant reads.

## Begin

```sh
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  "$API/api/platform/v1/data-requests/$REQUEST/begin-closure" -d '{"ticket":"CHG-123"}'
```

The body is `{}` or `{"ticket":"..."}`. In order:

1. The platform scope is entered: its entry row, under the platform sentinel with the
   reason `closure_begin`, your attribution and your ticket, commits first and stays
   whatever happens next.
2. The request is read. An absent request, one that is not a closure, a closure that
   is `completed` or `cancelled`, and one whose person is already Closed are refused,
   and nothing else is written.
3. A closure already `fulfilling` was begun earlier: skip to step 8.
4. **The fences**, on one transaction: the person's User fence, then the participant
   groups read under it (every tenant and organization, or none, in which the person
   holds a Membership of any status, a Revoked one included), then the fence of every
   participant tenant, in a fixed order. Each fence waits at most three seconds for
   another act that holds it. While they are held, every Membership, invitation and data
   request write of the person, and every identity write in those tenants, waits.
5. The request is locked and read again, with the person's account status: what step 2
   read may have changed while the fences were awaited.
6. **The judgment**, the first refusal standing:
   - `blocked_window`: the request's `executeAt` has not come, by the database clock.
   - `blocked_hold`: a hold source reports a hold for the closure. No hold source exists
     yet; [Phase 09](../roadmap/phase-09-billing-integrations-analytics.md) owns the
     first.
   - `blocked_last_administrator`: the person is the only eligible tenant-wide
     administrator (an Active account with an Active tenant-wide administrator
     Membership) of a participant tenant that allows ordinary work (`Trial` or
     `Active`). A tenant that is suspended or archived is not judged.

   A refusal is recorded on the request as its `last_refusal`, with a result row; the
   account is untouched and the request stays `scheduled`. A refusal for the code
   already standing writes nothing more.
7. **The transition**: the account moves from Active to Closing, and the request from
   `scheduled` to `fulfilling`. If the account is already Closing, because another of
   the person's closures began, only the request moves. The result row,
   `platform.closure.begin`, is written under the platform sentinel with your ticket,
   `before_state` `{"status":"scheduled","account":"active"}` and `after_state` with
   the status, `"account":"closing"` and the counts of served and unserved groups; no
   person and no tenant is named. All of it commits together, and the fences end with
   that commit.
8. **The fan-out**, after the commit: each served group, one tenant with one
   organization or none, whose tenant allows ordinary work and whose organization is
   Active, gets its own act in its own transaction, admitted for you by the gate with
   the same key and ticket. It creates the group's erasure (`origin`
   `closure_fanout`, born `fulfilling`, with no window) and enqueues its job, or adopts
   the erasure the group already has in flight. Each group's act writes its own MUST
   rows in its own tenant, which is that tenant's evidence of the closure.

The database checks every part of step 7 itself: it admits the account's move only from
the platform role, only Active to Closing, and only with the person's fence and every
participant tenant's fence held by the moving connection; and the request's move only
after its `executeAt`, for a Closing person, under the same fences.

## Answers

| Answer | Meaning |
|---|---|
| `200`, `outcome: begun`, `complete: true` | This call made the account Closing, and every served group's erasure was started or adopted |
| `200`, `outcome: resumed`, `complete: true` | The closure had begun (by an earlier call, or the account was already Closing for another closure of the person), and every served group is started or adopted |
| `200`, `complete: false` | The Begin stands, and some served group is `waiting` or `failed`: see [A partial answer](#a-partial-answer) |
| `404` | No request has that identifier, or the identifier is not a UUID at all (the route does not match) |
| `409`, `errors.refusal` = `lockey_data_request_closure_refused` | The closure was refused; `params.cause` is the code now recorded on the request: `blocked_window`, `blocked_hold` or `blocked_last_administrator`. Nothing else changed. See [A refused Begin](#a-refused-begin) |
| `409`, `errors.kind` = `lockey_data_request_not_closure` | The request is an export or an erasure. Nothing changed |
| `409`, `errors.status` = `lockey_data_request_closure_not_beginnable` | The closure is `completed` or `cancelled`; `params.cause` is its stored status. Nothing changed |
| `409`, `errors.account` = `lockey_data_request_closure_account_closed` | The person's account is already Closed by an earlier closure. Nothing changed |
| `400` | The request identifier is a UUID but not a UUIDv7 (or is the nil or the sentinel identifier), or the body or the ticket is malformed |
| `401`, `403`, `413`, `415`, `429` | As in [operator recovery](operator-recovery.md#act): the credential, the grant (`platform.data_request.write`), the body size or type, or the request budget |
| `503`, `audit_unavailable` | The entry row or the result row could not be written: no effect of the act committed. Retry after the delay; error line 7105 is in the API's log |
| `503`, `dependency_unavailable` | Another act held one of the person's fences for more than three seconds (a running erasure job of the person, a Membership change in one of their tenants), or the database could not be reached. Nothing committed; retry after the delay. The cause is in the API's log (warning 7110) |
| `503`, `dependency_unavailable` with `errors.outcome` = `lockey_outcome_unknown` | The commit was sent and its answer was lost. It **may have landed**: see [An unknown outcome](#an-unknown-outcome) |
| `500` | An unexpected failure or a deployment defect: no fan-out registered, a missing grant or migration, or a person with Memberships in more than 1,024 groups. The API's log names it. Nothing committed beyond the entry row, and a host with no fan-out writes not even that |

The answer's `groups` carries closed counts and never an identifier: `served`,
`started`, `adopted`, `waiting`, `failed` and `unserved`. `correlationId` is on every
row the act and its fan-out wrote.

## A refused Begin

The request stays `scheduled` with its code, which the person sees on their receipt as
`lastRefusal` while their account is Active. Tell the person through the address you
confirmed against:

- `blocked_window`: the 24 hours have not passed. Begin again after `executeAt`.
- `blocked_last_administrator`: the person is the last administrator of a tenant that
  is in use. They hand the administration over in that tenant first (another person's
  administrator Membership, admitted by the tenant), then you begin again. You learn
  neither the tenant nor its other members from the answer; the person knows which
  tenants they administer.
- `blocked_hold`: something the platform must keep holds the closure. Escalate it.

A refused Begin is repeated as it is: the request is judged again, and a refusal for a
different reason replaces the recorded code.

## A partial answer

`complete: false` means the account is Closing and the request `fulfilling`, so the
closure stands and cannot be cancelled, and some served group is not yet started:

- `failed`: that group's act did not report a start or an adoption (its tenant changed
  its lifecycle, a fence was busy, the gate refused you, or the act failed). Repeat
  Begin: it judges nothing again, takes no fence on the platform connection, and runs
  the fan-out again, which adopts what started and retries the rest.
- `waiting`: the group has an erasure the person or an administrator filed whose window
  has ended and whose job has not run. Repeat Begin once that job has run; the
  [erasure runbook](data-erasure.md#inspect) shows its request under
  `overdue_scheduled` meanwhile.

Repeat until `complete: true`. A served group's erasure then runs its job in that
group and ends `awaiting_privileged_step`, after which the closure is finalized.

## An unknown outcome

Repeat the same Begin. If the first landed, the repeat answers `200 resumed` and runs
the fan-out, which the first never reached; if it did not, nothing of it committed and
the repeat begins the closure now. A client that never received an answer (a timeout,
a gateway error) is in the same position: repeat it.

## How long it takes

The transaction takes milliseconds when no other act holds a fence; each fence waits up
to three seconds and then answers `503`. While it is open, other identity writes in the
person's tenants wait for it. The fan-out then runs one act per served group in turn,
each a short transaction, and does not wait for the erasure jobs it enqueues.

## When to finalize

Finalize once every served group's erasure has run: the Begin answered `complete: true`
(or a repeat did), and each group's erasure job has finished, so that group's newest
erasure of the person awaits its privileged step, or completed after the Begin through
an [erasure completion](data-erasure.md#complete-an-erasure). The groups' erasures,
`origin` `closure_fanout` or the person's own adopted one, appear under
`awaiting_privileged_step` in the [inspection](data-erasure.md#inspect). You need not
check them one by one: a Finalize before they are ready answers `409` on `errors.groups`
and changes nothing. Unserved groups need no erasure.

A served group is not ready while its newest erasure is still `scheduled` or
`fulfilling`, failed or was cancelled, completed before this closure began (the person
was admitted to the group again since), or does not hold every Membership the person has
in the group. Each is repaired by a repeated Begin, whose fan-out starts a new erasure
or re-queues a dead job, followed by that group's job. A person's own erasure that
awaits its privileged step but holds fewer Memberships than the person has in the group
is adopted as it is by the fan-out: complete it through the
[erasure completion](data-erasure.md#complete-an-erasure), then repeat Begin, which
replaces the completed one with a fan-out of the whole group.

## Finalize

```sh
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  "$API/api/platform/v1/data-requests/$REQUEST/finalize-closure" -d '{"ticket":"CHG-9"}'
```

The body is `{}` or `{"ticket":"..."}`. In order:

1. The platform scope is entered: its entry row, under the platform sentinel with the
   reason `closure_finalize`, your attribution and your ticket, commits first.
2. The request is read. An absent request, one that is not a closure, a closure never
   begun (`scheduled`) or cancelled are refused, and nothing else is written. A closure
   already `completed` whose person is Closed answers `200 already_finalized` with what
   it recorded, and writes nothing more.
3. **The first transaction.** The fences, as Begin takes them (each waits at most three
   seconds); the request locked and read again with the person's account; and every
   served group's readiness. A group not ready refuses the act with nothing written.
4. **The redaction**, on that transaction: the person's audit rows in every participant
   tenant, served or not, by the person and every Membership they hold there, then in
   the platform sentinel tenant by the person's subject key
   ([ADR-0108](../decisions/0108-privileged-audit-redaction.md) § 4). The intent row,
   `platform.closure.finalize_attempt`, is written under the sentinel with your ticket
   and the summed counts, and commits with the redaction. The fences end there.
5. **The provider** deletes the person's account through the
   [account removal client](account-removal-client.md). A missing account is success.
   Nothing is held in the database while it runs.
6. **The second transaction.** The fences again, and everything judged again under
   them. The account moves from Closing to Closed and its raw provider subject is
   removed; every participant group's erasure that awaits its privileged step is
   completed with its tenant's retained audit counts; this closure and every other open
   closure of the person, begun or not, are completed with the summed audit counts and
   the number of unserved groups; and the result row, `platform.closure.finalize`, is
   written under the sentinel with your ticket and closed counts. All of it commits
   together.

The database checks every part of step 6 itself: it admits the account's move to
Closed only from the platform role, only from Closing, only with the raw subject
removed, and only with the person's fence and every participant tenant's fence held by
the moving connection; and each request's completion only for a Closed person under the
same fences.

## Answers to a Finalize

| Answer | Meaning |
|---|---|
| `200`, `outcome: finalized` | This call finished the closure: see [Reading the result](#reading-the-result) |
| `200`, `outcome: already_finalized` | An earlier call finished it. `retained` is what it recorded; nothing was written but the entry row |
| `404` | No request has that identifier, or the identifier is not a UUID at all |
| `409`, `errors.kind` = `lockey_data_request_not_closure` | The request is an export or an erasure. Nothing changed |
| `409`, `errors.status` = `lockey_data_request_closure_not_finalizable` | The closure was never begun (`scheduled`) or was cancelled; `params.cause` is its stored status. Begin it first, or, once another closure of the person is finalized, it is completed with that one |
| `409`, `errors.groups` = `lockey_data_request_closure_groups_pending` | A served group is not ready: see [When to finalize](#when-to-finalize). `params` holds the closed counts `served`, `ready`, `pending` and `unserved`, and no group is named |
| `400` | The request identifier is a UUID but not a UUIDv7 (or is the nil or the sentinel identifier), or the body or the ticket is malformed |
| `401`, `403`, `413`, `415`, `429` | As in [operator recovery](operator-recovery.md#act) |
| `503`, `audit_unavailable` | The entry row or the intent row could not be written: the provider was not asked, and nothing of the closure changed. Retry after the delay; error line 7105 is in the API's log |
| `503`, `dependency_unavailable` | Another act held one of the person's fences for more than three seconds, or the database could not be reached. Nothing of the closure changed; a redaction that had committed with its intent row is repeated harmlessly. Retry after the delay; warning 7110 is in the API's log |
| `503`, `dependency_unavailable` with `errors.provider` | The provider did not delete the account: see [When the provider step does not finish](#when-the-provider-step-does-not-finish) |
| `503`, `dependency_unavailable` with `errors.outcome` = `lockey_outcome_unknown` | The final commit was sent and its answer was lost: see [An unknown outcome of a Finalize](#an-unknown-outcome-of-a-finalize) |
| `500` | An unexpected failure or a deployment defect (a missing grant or migration, a state no admitted path writes, which error line 7122 names without the person). Nothing of the closure changed since its last commit |

## When the provider step does not finish

The redaction and its intent row committed, a result row records where the act stopped,
the closure stays `fulfilling` and the account Closing: the person is already locked out
everywhere. `errors.provider` names the provider's closed answer, and warning 7121 the
same in the API's log:

- `lockey_provider_refused`: nothing was deleted. The account removal client is not
  configured or is refused, or the person's provider identifier is not one the provider
  issued. Repair it by
  [its runbook](account-removal-client.md#when-a-closures-last-step-does-not-finish) and
  repeat. An identifier the adapter refuses for ever (an imported user's) is deleted by
  hand in the realm console, after which a repeat finds it absent.
- `lockey_provider_unavailable`: nothing was deleted; the provider was unreachable or
  busy. Repeat after the delay.
- `lockey_provider_outcome_unknown`: the deletion was sent and its answer was lost, so
  the account may be gone. Repeat: a deleted account is found absent, which is success.

Every repeat redacts again, which changes no row, writes an intent row of its own and
asks the provider again.

## An unknown outcome of a Finalize

Repeat the same Finalize. If the second transaction had landed, the repeat answers
`200 already_finalized` and asks the provider nothing; if it had not, the repeat
redacts again (a no-op), finds the account absent at the provider and finishes. A
client that never received an answer is in the same position.

## Reading the result

The answer of a finalization carries closed counts and no person:

- `groups`: `served`, `ready`, `pending` (zero), `unserved`, `erasuresCompleted` and
  `closuresCompleted` (this closure and every other open one of the person).
- `unservedGroups`: each unserved group by `tenantId` and `organizationId`. Their data
  stays and is reported; [Phase 11](../roadmap/phase-11-production-hardening.md)
  offboarding owns it. Record the list on your ticket.
- `redaction`: this call's passes summed: the participant `tenants` redacted, the
  `rowsSelected`, the rows changed per column (zero on a repeat, whose earlier attempt's
  intent row holds the counts that changed rows) and the retained references per kind.
- `retained`: the closure request's recorded result: the audit log's retained kinds
  summed over every pass, and `unserved_group`.

What remains of the person is the tombstone: the `users` row with its identifier, its
subject key, its authority and its creation evidence, Closed and with no raw provider
subject; each served group's Revoked Membership; the requests as receipts; and in the
audit log the actor and entity identifiers and all of `metadata`, which stay linkable
through the subject key and are never called anonymous
([ADR-0108](../decisions/0108-privileged-audit-redaction.md) § 6). The person has no
in-product status: confirm the completion to them through the address you confirmed the
request against.

**A second closure.** A person may have filed a closure in more than one group. Begin
any one of them; Finalize of the one you began completes every other open closure of
the person with it, also one never begun or still in its window. A second closure that
is `fulfilling` because you began it too may be finalized instead; the first is then
completed with it. Finalizing a closure that was never begun answers `409` on
`errors.status`.

## Targets and escalation

- Begin every confirmed closure soon after its `executeAt`, and finalize it well inside
  the 30 days of
  [ADR-0106 § 12](../decisions/0106-subject-data-requests-authority-and-status.md);
  the count starts when the request was filed.
- A Finalize that keeps answering `errors.provider` = `lockey_provider_refused` after
  the client was checked, and a group that stays pending over several repeated Begins
  and jobs, are escalations to the deployment's owner, with the request identifier and
  the `correlationId` of each call.
- A Begin that keeps answering `503` or `500`, a group that stays `failed` over several
  repeats, and a closure listed under `stuck_fulfilling` whose groups are all started
  but whose erasures do not finish are escalations to the deployment's owner, with the
  request identifier and the `correlationId` of each call.
- Never change a User, a Membership or a request by hand. The database refuses the
  closure's changes without the fences, and a change outside the acts writes no record.

## Signals

`learnstack_platform_operator_admissions_total{outcome,operation}` counts the acts
admitted or refused at the gate, with `operation` `platform.closure.begin`, and each
group's fan-out act under its own operation;
`learnstack_platform_operator_results_total{operation="platform.closure.begin"}` counts
the Begins that committed a change, a recorded refusal included, and a resume that
changed nothing is not one;
`learnstack_platform_operator_data_requests_total{operation,outcome}`, with
`operation` `platform.closure.begin`, counts every call that reached the owner by how
it ended: `begun`, `begun_partial`, `resumed`, `resumed_partial`, `blocked_window`,
`blocked_hold`, `blocked_last_administrator`, `not_found`, `not_closure`,
`not_beginnable`, `account_closed`, `audit_unavailable`, `unavailable`,
`commit_unknown` and `failed` (an unexpected failure, answered `500`). A `*_partial`
outcome is a Begin that owes a repeat. A fan-out that threw after its Begin committed
logs error line 7120. With `operation` `platform.closure.finalize` the same counter's
outcomes are `finalized`, `already_finalized`, `not_found`, `not_closure`,
`not_finalizable`, `groups_pending`, `provider_refused`, `provider_unavailable`,
`provider_uncertain`, `audit_unavailable`, `unavailable`, `commit_unknown` and `failed`;
`learnstack_platform_operator_results_total{operation="platform.closure.finalize"}`
counts the Finalize transactions that committed a result row, a stop at the provider
included. A Finalize logs warning 7121 when it stops at the provider, warning 7124 when
the provider port threw (its exception's type only), error 7122 for a state no admitted
path writes, and critical 7123 when a stop's result row could not be written; none names
the person, a tenant or the ticket. The admission and refusal counters are in
[Platform operator access](operator-access.md#signals).

## Not recoverable here

- An account that is Closing or Closed: it never returns to Active.
- An identity-provider account Finalize deleted.
- The data of an unserved group, until
  [Phase 11](../roadmap/phase-11-production-hardening.md) offboarding.
- A dashboard.
