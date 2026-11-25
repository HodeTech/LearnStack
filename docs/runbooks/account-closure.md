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

Begin runs on `ConnectionStrings:PlatformAdmin` and needs the Identity migrations up to
`20261123130000_harden_closure_request_guard` applied: without
`20261123100000_admit_account_closure` the platform role cannot read the person's
Memberships, and the act fails with a permission error, a `500` named in the API's log.
Its fan-out runs on the application role, as each group's own act, and needs
`ConnectionStrings:Default`. A host with no fan-out registered refuses every Begin with
a `500` before it enters the platform scope, so nothing changes. Begin also asks the
[account removal client](account-removal-client.md) whether it could ever delete the
person's identity-provider account: with no client secret configured, or for a provider
identifier the client refuses, a Begin of a closure that has not begun is refused on
`errors.provider` before anything changes (a repeat of a begun closure resumes and
asks nothing). Finalize runs on the same credential and migrations, and deletes the
account through that client.

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
- **A closure of a person past its bounds.** One closure is admitted for at most 256
  participant groups, served or not, and at most 256 Memberships of any status, Revoked
  ones included, in one participant tenant. Begin refuses a person past either with
  `409` on `errors.participants` while their account is still Active, and nothing
  changes;
  [Phase 11](../roadmap/phase-11-production-hardening.md) data operations serve them.
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
  then withdraws it). You need do nothing: the group's erasure job ends both. Closing
  them needs a read of the person's account status in those two jobs, which
  [Phase 03](../roadmap/phase-03-identity-admin.md) owns.
- **Three records this closure leaves as they are.** An administrator who files an
  export or an erasure for a closed person's Revoked Membership is refused with `409`
  `lockey_subject_not_admitted`, and the refusal's row in that tenant names the
  person's identifiers, written after the closure's redaction. A person's or an
  administrator's own erasure in an unserved group whose job finished between Finalize's
  two transactions is completed with the others, and the rows its job wrote then are
  not redacted. An unserved group's tenant has its audit rows redacted by Finalize with
  no row of its own saying so: the intent row under the platform sentinel lists each
  tenant redacted, up to 200, and your ticket holds the rest.

## When to look

- At least once a day: a closure not `completed` 30 days after it was filed is a defect
  ([ADR-0106 § 12](../decisions/0106-subject-data-requests-authority-and-status.md)),
  and the window takes the first day.
- `overdue_scheduled` lists every request still `scheduled` once its window ended,
  closures (`kind` `closure`) among them: each is a closure waiting for your Begin.
- `stuck_fulfilling` lists a begun closure still `fulfilling` two hours after its
  `executeAt`, which every closure is until its Finalize, so a begun closure listed
  there is no escalation by itself, and a closure's fan-out erasure still running two
  hours after it was created. Repeat the closure's Begin to see where its groups stand.
- `awaiting_privileged_step` lists each fan-out erasure (`origin` `closure_fanout`)
  whose group finished; only its closure's Finalize completes it.

## Before you begin

1. **You hold `platform.data_request.write`** and a token with a second factor, as for
   an erasure completion. The read and the write are separate grants.
2. **The account removal client is configured** in this deployment and passes
   [its verification](account-removal-client.md#verify-the-confined-client). Without it
   a first Begin is refused on `errors.provider` and nothing changes, because no
   Finalize could delete the account.
3. **Confirm the request with the person** through the e-mail address the identity
   provider holds for them, before the first Begin of the closure. A repeated Begin of a
   closure that began needs none, and the query below returns no row for it. This is the
   control a hijacked session cannot supply: a session can file a closure, and only the
   mailbox's owner answers you. LearnStack stores no e-mail address, so read it in the
   realm console:
   1. A database administrator holding the `learnstack_platform` credential reads the
      person's provider subject, the identity provider's own user identifier, for the
      request. The role's column grants cover exactly these columns.

      ```sql
      -- as learnstack_platform
      SELECT u.provider_subject
      FROM data_subject_requests AS r JOIN users AS u ON u.id = r.subject_user_id
      WHERE r.id = '<requestId>' AND r.kind = 'closure' AND r.status = 'scheduled';
      ```

   2. In the realm console of the tenant realm, open **Users**, type `id:` immediately
      followed by that identifier into the search box (a bare identifier finds
      nothing), and read the user's e-mail address there. The console also opens the
      user directly at
      `<provider base>/admin/master/console/#/learnstack/users/<identifier>/settings`.
   3. Write to that address, name the closure's request identifier and its
      consequences (the account stops working at once, for every tenant, and cannot be
      reopened), and wait for the person's explicit answer from that address.

   Write the provider subject and the address nowhere: not in a ticket, not in the
   Begin's ticket, not in a log. If the person does not confirm, do not begin; they may
   cancel the request themselves while their account is Active.
4. **Send a ticket** for the act whenever your change process has one, 1 to 64 letters,
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
3. A closure already `fulfilling` was begun earlier: skip to step 9.
4. **Could it ever finish.** The account removal client is asked, with the person's
   provider identifier and without any call to the provider, whether it could delete the
   account. If it could not, the act answers `409` on `errors.provider` and writes
   nothing more; the request records no code, because the repair is yours.
5. **The fences**, on one transaction: the person's User fence, then the participant
   groups read under it (every tenant and organization, or none, in which the person
   holds a Membership of any status, a Revoked one included) with the Memberships the
   person holds in each, then the fence of every participant tenant, in a fixed order.
   A person in more than 256 participant groups, or with more than 256 Memberships in
   one participant tenant, is refused before any tenant fence, with `409` on
   `errors.participants` and nothing more written: no later step of the closure could
   finish for them. Each fence waits at most three seconds for another act that holds
   it. While they are held, every Membership, invitation and data request write of the
   person, and every identity write in those tenants, waits.
6. The request is locked and read again, with the person's account status: what step 2
   read may have changed while the fences were awaited.
7. **The judgment**, the first refusal standing:
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
8. **The transition**: the account moves from Active to Closing, and the request from
   `scheduled` to `fulfilling`. If the account is already Closing, because another of
   the person's closures began, only the request moves. The result row,
   `platform.closure.begin`, is written under the platform sentinel with your ticket,
   `before_state` `{"status":"scheduled","account":"active"}` and `after_state` with
   the status, `"account":"closing"` and the counts of served and unserved groups; no
   person and no tenant is named. All of it commits together, and the fences end with
   that commit.
9. **The fan-out**, after the commit: each served group, one tenant with one
   organization or none, whose tenant allows ordinary work and whose organization is
   Active, gets its own act in its own transaction, admitted for you by the gate with
   the same key and ticket. It creates the group's erasure (`origin`
   `closure_fanout`, born `fulfilling`, with no window) and enqueues its job, or adopts
   the erasure the group already has in flight. Each group's act writes its own MUST
   rows in its own tenant, which is that tenant's evidence of the closure.

The database checks every part of step 8 itself: it admits the account's move only from
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
| `409`, `errors.participants` = `lockey_data_request_closure_too_large` | The person is in more than 256 participant groups, served or not, or holds more than 256 Memberships in one participant tenant. Nothing changed, no code was recorded, and the account stays Active. See [A refused Begin](#a-refused-begin) |
| `409`, `errors.provider` = `lockey_account_removal_unavailable` | The account removal client is not configured, or would refuse the person's provider identifier, so no Finalize could delete the account. Nothing changed, no code was recorded, and the account stays Active. See [A refused Begin](#a-refused-begin) |
| `400` | The request identifier is a UUID but not a UUIDv7 (or is the nil or the sentinel identifier), or the body or the ticket is malformed |
| `401`, `403`, `413`, `415`, `429` | As in [operator recovery](operator-recovery.md#act): the credential, the grant (`platform.data_request.write`), the body size or type, or the request budget |
| `503`, `audit_unavailable` | The entry row or the result row could not be written: no effect of the act committed. Retry after the delay; error line 7105 is in the API's log |
| `503`, `dependency_unavailable` | Another act held one of the person's fences for more than three seconds (a running erasure job of the person, a Membership change in one of their tenants), or the database could not be reached. Nothing committed; retry after the delay. The cause is in the API's log (warning 7110) |
| `503`, `dependency_unavailable` with `errors.outcome` = `lockey_outcome_unknown` | The commit was sent and its answer was lost. It **may have landed**: see [An unknown outcome](#an-unknown-outcome) |
| `500` | An unexpected failure or a deployment defect: no fan-out registered, or a missing grant or migration. The API's log names it. Nothing committed beyond the entry row, and a host with no fan-out writes not even that |

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

Two refusals record nothing on the request, because neither is the person's to act on,
and the account stays Active:

- `errors.participants`: the person is in more participant groups, or holds more
  Memberships in one participant tenant, than one closure is admitted for. Do not
  repeat it: escalate it to the deployment's owner with the request identifier;
  [Phase 11](../roadmap/phase-11-production-hardening.md) data operations serve such a
  person.
- `errors.provider`: the account removal client is not configured, or it would refuse
  the person's provider identifier because the identifier is not in the canonical form
  the provider issues (an imported user's, say). Configure and verify the client by
  [its runbook](account-removal-client.md#verify-the-confined-client) and Begin again. A
  configured client that still refuses is an escalation to the deployment's owner: no
  Finalize could delete that account.

## A partial answer

`complete: false` means the account is Closing and the request `fulfilling`, so the
closure stands and cannot be cancelled, and some served group is not yet started:

- `failed`: that group's act did not report a start or an adoption (its tenant changed
  its lifecycle, a fence was busy, the gate refused you, or the act failed). Repeat
  Begin: it judges nothing again, takes no fence on the platform connection, and runs
  the fan-out again, which adopts what started and retries the rest. Why a group failed
  is in the API's log, never in the answer: warning 7542 names the closed refusal key or
  the exception type, and information 7541 each group that finished. Attach the 7542
  lines to an escalation.
- `waiting`: the group has an erasure the person or an administrator filed whose window
  has ended and whose job has not run. Repeat Begin once that job has run; the
  [erasure runbook](data-erasure.md#inspect) shows its request under
  `overdue_scheduled` meanwhile.

Repeat until `complete: true`. A served group's erasure then runs its job in that
group and ends `awaiting_privileged_step`, after which the closure is finalized.

## An unknown outcome

Repeat the same Begin. If the move to Closing landed, the repeat answers `200 resumed`
and runs the fan-out, which the first never reached; if only a refusal's record landed,
the repeat answers the same refusal; if nothing landed, the repeat begins the closure
now. A client that never received an answer (a timeout,
a gateway error) is in the same position: repeat it.

## How long it takes

A Begin's transaction takes milliseconds when no other act holds a fence; each fence
waits up to three seconds and then answers `503`. While it is open, other identity
writes in the person's tenants wait for it. The fan-out then runs one act per served
group in turn, each a short transaction, and does not wait for the erasure jobs it
enqueues.

A Finalize's first transaction lasts as long as its redaction: one pass over each
participant tenant's audit rows, about as long as
[an erasure's](data-erasure.md#how-long-it-takes) over the same tenant and up to about
twice that where the person holds hundreds of Memberships, and one over the platform
sentinel tenant's, a few seconds for 1.5 million global creation rows. It holds
the person's User fence only, so the person's own identity writes wait and no tenant's
do. The second transaction takes milliseconds under every fence. A client or gateway
timeout shorter than the first transaction rolls it back on every attempt, so run a
Finalize from a client whose timeout outlasts it; the API sets no request timeout of its
own. Once the first transaction committed, a repeat's redaction finds nothing left to
change and takes the time of a read.

## When to finalize

Finalize once every required group's erasure has run: the Begin answered `complete:
true` (or a repeat did), and each group's erasure job has finished, so that group's
newest erasure of the person awaits its privileged step, or completed after the Begin
through an [erasure completion](data-erasure.md#complete-an-erasure). The groups'
erasures, `origin` `closure_fanout` or the person's own adopted one, appear under
`awaiting_privileged_step` in the [inspection](data-erasure.md#inspect). You need not
check them one by one: a Finalize before they are ready answers `409` on `errors.groups`
and changes nothing, unless the provider step had already run (the answer then carries
`params.provider`, see [Finalize](#finalize)). Every served group is required. An
unserved group is never waited for, even when its group stopped allowing ordinary work
after the Begin while its fan-out erasure was still running: nothing can re-queue that
job once the group is Archived, and a closure that waited for it could not finish. Its
erasure is completed with the others if it had reached the privileged step, and the
group is then not reported; otherwise the group is reported as unserved, with whatever
data its job had not yet erased, and an erasure that stays `fulfilling` there is left as
it is. Finalize after the group's job finished leaves nothing to report.

A required group is not ready while its newest erasure is still `scheduled` or
`fulfilling`, failed or was cancelled, completed before the person's first closure began
(the person was admitted to the group again since), or does not hold every Membership
the person has in the group. Each is repaired by a repeated Begin, whose fan-out starts
a new erasure or re-queues a dead job, followed by that group's job. A person's own
erasure that awaits its privileged step but holds fewer Memberships than the person has
in the group is adopted as it is by the fan-out. The `409` names no group, and the
inspection cannot be filtered by person, so complete every due erasure of `origin`
`self` or `administrator` listed under `awaiting_privileged_step` through the [erasure
completion](data-erasure.md#complete-an-erasure), which is your routine duty anyway,
then repeat Begin: its fan-out replaces a completed short erasure with a fan-out of the
whole group.

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
3. **The first transaction.** The person's User fence alone (it waits at most three
   seconds) and the participant groups read under it; the request locked and read again
   with the person's account; and every required group's readiness. A group not ready
   refuses the act with nothing written. No participant tenant's fence is taken: this
   transaction changes no account and no request, so the tenants' identity writes do
   not wait for its redaction.
4. **The redaction**, on that transaction: the person's audit rows in every participant
   tenant, served or not, by the person and every Membership they hold there, then in
   the platform sentinel tenant by the person's subject key
   ([ADR-0108](../decisions/0108-privileged-audit-redaction.md) § 4). The intent row,
   `platform.closure.finalize_attempt`, is written under the sentinel with your ticket
   and the summed counts, and commits with the redaction. The fence ends there.
5. **The provider** deletes the person's account through the
   [account removal client](account-removal-client.md). A missing account is success.
   Nothing is held in the database while it runs.
6. **The second transaction.** The person's User fence and every participant tenant's
   fence, as Begin takes them, and everything judged again under them. The account
   moves from Closing to Closed and its raw provider subject is removed; every
   participant group's erasure that awaits its privileged step is locked and completed
   with its tenant's retained audit counts, unless an
   [erasure completion](data-erasure.md#complete-an-erasure) finished it meanwhile,
   which leaves it as it completed; this closure and every other open
   closure of the person, begun or not, are completed with the summed audit counts and
   the number of unserved groups; and the result row, `platform.closure.finalize`, is
   written under the sentinel with your ticket and closed counts. All of it commits
   together.

The database checks every part of step 6 itself: it admits the account's move to
Closed only from the platform role, only from Closing, only with the raw subject
removed, and only with the person's fence and every participant tenant's fence held by
the moving connection; a closure's completion only for a Closed person under the same
fences; and a fan-out erasure's completion only for a Closing or Closed person under the
person's fence and its own tenant's. A person's or an administrator's own erasure is
completed through the erasure completion's arm, which constrains only its result.

## Answers to a Finalize

| Answer | Meaning |
|---|---|
| `200`, `outcome: finalized` | This call finished the closure: see [Reading the result](#reading-the-result) |
| `200`, `outcome: already_finalized` | An earlier call finished it. `retained` is what it recorded. Nothing was written but the entry row, unless this call found the finalization only after its own provider step, when it also wrote its intent row and a result row saying so |
| `404` | No request has that identifier, or the identifier is not a UUID at all |
| `409`, `errors.kind` = `lockey_data_request_not_closure` | The request is an export or an erasure. Nothing changed |
| `409`, `errors.status` = `lockey_data_request_closure_not_finalizable` | The closure was never begun (`scheduled`) or was cancelled; `params.cause` is its stored status. Begin it first, or, once another closure of the person is finalized, it is completed with that one |
| `409`, `errors.groups` = `lockey_data_request_closure_groups_pending` | A required group is not ready: see [When to finalize](#when-to-finalize). `params` holds the closed counts `served`, `ready`, `pending` and `unserved`, and no group is named; `ready` and `pending` count the required groups. When `params.provider` is `removed` or `absent`, the second transaction found the group pending after the provider step: the person's account is already gone, the closure stays `fulfilling`, and a repeat finishes it once the group is ready. Without `params.provider` nothing changed |
| `400` | The request identifier is a UUID but not a UUIDv7 (or is the nil or the sentinel identifier), or the body or the ticket is malformed |
| `401`, `403`, `413`, `415`, `429` | As in [operator recovery](operator-recovery.md#act) |
| `503`, `audit_unavailable` | The entry row or the intent row could not be written: the provider was not asked, and nothing of the closure changed. Retry after the delay; error line 7105 is in the API's log |
| `503`, `dependency_unavailable` | Another act held one of the person's fences for more than three seconds, the database could not be reached, or the result row of the second transaction could not be written after the provider step. Nothing of the closure's state changed, but after the provider step the person's identity-provider account may already be deleted. A redaction that had committed with its intent row is repeated harmlessly, and a repeat that finds the account absent finishes the closure. Retry after the delay; warning 7110 is in the API's log |
| `503`, `dependency_unavailable` with `errors.provider` | The provider did not delete the account: see [When the provider step does not finish](#when-the-provider-step-does-not-finish) |
| `503`, `dependency_unavailable` with `errors.outcome` = `lockey_outcome_unknown` | The final commit was sent and its answer was lost: see [An unknown outcome of a Finalize](#an-unknown-outcome-of-a-finalize) |
| `500` | An unexpected failure or a deployment defect (a missing grant or migration, a state no admitted path writes, which error line 7122 names without the person: a person past the closure's bounds, which Begin refuses, is `participants_beyond_bound` or `redaction_plan_refused`). Nothing of the closure changed since its last commit; a Finalize that fails after its provider step (`closure_changed_between_transactions`, `account_not_closed`, `open_closures_beyond_bound`, `erasure_not_completed`) may follow a deleted account, so read the closure before repeating |

## When the provider step does not finish

The redaction and its intent row committed, a result row records where the act stopped,
the closure stays `fulfilling` and the account Closing: the person is already locked out
everywhere. `errors.provider` names the provider's closed answer, and warning 7121 the
same in the API's log:

- `lockey_provider_refused`: nothing was deleted. Begin admits only a closure the
  client could delete, so the client's configuration has changed since the Begin (the
  secret removed or wrong, the client disabled or broadened, the admin address
  blocked); there is nothing to delete by hand. Repair the client by
  [its runbook](account-removal-client.md#when-a-closures-last-step-does-not-finish) and
  repeat.
- `lockey_provider_unavailable`: no deletion was confirmed; the provider was
  unreachable or busy. Repeat after the delay: a deletion that had landed is found
  absent.
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

- `groups`: `served`, `ready` and `pending` (zero), which count the required groups,
  `unserved` (the unserved groups whose data this closure did not erase),
  `erasuresCompleted` (the erasures this call completed, not one an erasure completion
  finished meanwhile) and `closuresCompleted` (this closure and every other open one of
  the person).
- `unservedGroups`: each unserved group by `tenantId` and `organizationId`, except one
  whose erasure had reached the privileged step. Their data stays and is reported;
  [Phase 11](../roadmap/phase-11-production-hardening.md) offboarding owns it. Their
  tenants' audit rows were redacted all the same, with no row in those tenants saying
  so: record the list on your ticket.
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
  the client was checked, one that still answers `lockey_provider_unavailable` or
  `lockey_provider_outcome_unknown` a day after its first such answer, and a group that
  stays pending over several repeated Begins and jobs are escalations to the
  deployment's owner, with the request identifier and the `correlationId` of each call.
- A Begin refused on `errors.participants`, and one refused on `errors.provider` while
  the client passes its verification, are escalations to the deployment's owner with
  the request identifier.
- A Begin that keeps answering `503` or `500`, a group that stays `failed` over several
  repeats (attach its warning 7542 lines), and a closure listed under `stuck_fulfilling`
  whose groups are all started but whose erasures do not finish are escalations to the
  deployment's owner, with the request identifier and the `correlationId` of each call.
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
`blocked_hold`, `blocked_last_administrator`, `refused_too_large`, `refused_provider`,
`not_found`, `not_closure`, `not_beginnable`, `account_closed`, `audit_unavailable`,
`unavailable`, `commit_unknown` and `failed` (an unexpected failure, answered `500`). A
`*_partial` outcome is a Begin that owes a repeat. With `operation`
`identity.data_request.fan_out_closure` the same counter counts each group's fan-out act
by its outcome: `fan_out_started`, `fan_out_adopted`, `fan_out_waiting` and
`fan_out_failed`; information 7541 logs each group that finished and warning 7542 each
that failed, with the closed refusal key or the exception type and never the person, the
group or the ticket. A fan-out that threw after its Begin committed logs error line
7120. With `operation` `platform.closure.finalize` the same counter's
outcomes are `finalized`, `already_finalized`, `not_found`, `not_closure`,
`not_finalizable`, `groups_pending`, `provider_refused`, `provider_unavailable`,
`provider_uncertain`, `audit_unavailable`, `unavailable`, `commit_unknown` and `failed`;
`learnstack_platform_operator_results_total{operation="platform.closure.finalize"}`
counts the Finalize transactions that committed a result row, a stop at the provider
included. A Finalize logs warning 7121 when it stops at the provider, warning 7124 when
the provider port threw (its exception's type only), warning 7125 when the account
removal adapter refused the deletion (a closed reason code; see its
[runbook](account-removal-client.md#when-a-closures-last-step-does-not-finish)),
warning 7126 when it did not complete the call (a closed cause, listed in the same
runbook), error 7122 for a state no admitted
path writes, and critical 7123 when a stop's result row could not be written; none names
the person, a tenant or the ticket. The admission and refusal counters are in
[Platform operator access](operator-access.md#signals).

## Not recoverable here

- An account that is Closing or Closed: it never returns to Active.
- An identity-provider account Finalize deleted.
- The data of an unserved group, until
  [Phase 11](../roadmap/phase-11-production-hardening.md) offboarding.
- A dashboard.
