# Operator recovery of asynchronous work

How an operator inspects dead-lettered events and quarantined jobs, and resets one
dead-lettered delivery or outbox row under a fence. Decided in
[ADR-0103](../decisions/0103-operator-recovery-of-asynchronous-work.md). There is **no
dashboard** and **no job recovery**: this is an API and this document.

## What exists

| Route | Does | Permission |
|---|---|---|
| `GET /api/platform/v1/asynchronous-work/outbox-messages` | Lists dead-lettered or terminal-requested outbox rows | `platform.delivery.read` |
| `GET /api/platform/v1/asynchronous-work/event-deliveries` | Lists dead-lettered or terminal-requested subscriber deliveries | `platform.delivery.read` |
| `GET /api/platform/v1/asynchronous-work/quarantined-jobs` | Lists quarantined jobs with their reservation occupancy | `platform.delivery.read` |
| `POST .../event-deliveries/{tenantId}/{subscriptionId}/{eventId}/replay` | Resets one dead-lettered delivery (a **replay**) | `platform.delivery.write` |
| `POST .../outbox-messages/{tenantId}/{eventId}/reset` | Resets one retry-exhausted outbox row (a **producer reset**) | `platform.delivery.write` |

Authentication, the token profile and how a grant is given are in [Platform operator
access](operator-access.md). The served operator document, `platform-v1`, is the
contract for these routes, and it is the only client there is.

An act changes a row. An inspection changes nothing: it reads metadata only, and it
cannot read an event body, a job's parameters or plan, a causal id or a lease token,
because the database refuses those columns to the platform role. The inspections need
the Platform migration `20261120120000` applied; without it the first one fails with a
permission error in the API's log.

Three names for one thing trip people. The act on a subscriber delivery is a **replay**
and writes `platform.delivery.replay`. The act on an outbox row is a **producer reset**
and writes `platform.delivery.reset`. The rows the dispatcher itself writes when it
gives up are `platform.delivery.dead_letter` (a delivery) and
`platform.outbox.dead_letter` (an outbox row).

## What is not here

- **Job recovery.** A quarantined job stays frozen, keeps its reservation and keeps the
  `jobs` health check unhealthy. Nothing here requeues, retries, releases or re-arms
  it, because the only proof that would make that safe is a provider's fence or the
  termination of the process that started the effect, and no adapter can supply one
  today. The inspection shows the job (`phase`, `quarantineReason`), how many
  reservations it holds (`heldReservations`) and how many its capacity group holds
  (`groupOccupancy`); it is an operator's evidence for an escalation, not a remedy.
- **A recurrence skip, pause or resume.** None exists.
- **The Hangfire dashboard.** `/admin/hangfire` answers 404 in every deployment mode.
- **A privileged subscription.** A delivery that belongs to one is refused.
- **A read of one row.** The lists are oldest first and cannot be filtered by tenant or
  event, so finding one row means paging; each page is one audited scope.

## When to look

- `learnstack_outbox_deadletter_total` or `learnstack_inbox_deadletter_total` rises, or
  the `jobs`, `outbox` or `delivery` health check goes unhealthy.
- A tenant reports an effect that never happened and the audit trail shows a
  `platform.outbox.dead_letter` or `platform.delivery.dead_letter` row for it.

## Inspect

Each page is one entered platform scope, so each page writes one
`platform.admin_scope.enter` audit row with the reason `delivery_inspect`, your
attribution and your ticket. It does not say which of the three lists you read or how
far you paged. Pass the ticket as `?ticket=`: 1 to 64 letters, digits or `.` `_` `:`
`/` `#` `-`, and percent-encode `#` as `%23`, because a bare `#` ends the query.
Inspections run on `ConnectionStrings:PlatformAdmin`; the two acts run on
`ConnectionStrings:OutboxDispatcher`
([setup](operator-access.md#what-must-be-configured)).

```sh
curl -s -H "Authorization: Bearer $TOKEN" \
  "$API/api/platform/v1/asynchronous-work/outbox-messages?state=dead_lettered&limit=20&ticket=CHG-123"
```

- `state` is `dead_lettered` (default) or `terminal_requested`. A **terminal-requested**
  row has a terminal reason recorded and the commit of its dead-letter not yet made: its
  state is shown and it is **not actionable**. It becomes dead-lettered by itself, or it
  does not. One that stays means the dispatcher cannot write its audit row or has
  stopped: look at the `outbox`, `delivery` and audit health checks and the error
  line 7105 in the dispatcher's host log.
- `limit` is 1 to 100 (default 20; a larger value, however large, is clamped, and zero
  or less is refused with `errors.limit`). The answer holds `items` and a `pageInfo`
  with `nextCursor` and `hasNext`; send `nextCursor` back as `cursor`. A cursor belongs
  to the route and state that issued it. The quarantined-jobs list takes no `state`.
- Each row carries `eventType` as the registry's label (`unknown` and `retired` mean
  the registry no longer dispatches that stored type), `lastError` and `actionable`.
  `lastError` is the dispatcher's closed descriptor of the last failure, whose first
  part says where it failed (`publish:`, `execute:`, `recovery:` or `terminal:`), or
  `unrecognized`. It carries no handler message: that is in the dispatcher's log, found
  by event type and subscription. Treat `actionable` as advice about the row and the
  registry: the act also checks the tenant's service state and the fence, so a suspended
  tenant's row can be listed actionable and refused.
- **The fence travels with the row.** A delivery shows `replayCount`, `deadLetteredAt`,
  `terminalReason`; an outbox row shows `attempts` and `deadLetteredAt`. Send **every**
  fence field back exactly as returned. `deadLetteredAt` has microsecond precision: a
  client that parses it and writes it back at millisecond precision, or changes the
  case of a reason, sends a fence that never matches.

## Choose: replay or reset

- **Replay** resets one *delivery*: one subscription of one event. Use it when a
  subscriber's handler failed for a reason that is now fixed, or its subscription became
  Active. Its reasons are `retry_exhausted`, `unknown_type`, `retired_type` and
  `invalid_subscription`. It renews the delivery's attempts to zero too. The completed
  siblings, the inbox row, the producer row and every other event are untouched.
- **Producer reset** resets one *outbox row* whose terminal reason is `retry_exhausted`:
  a burst of dispatch failures, and not the event itself, used up its six attempts. It
  **renews the six-attempt budget**, so a row that fails for a lasting reason fails six
  more times and is dead-lettered again, after about an hour and six minutes of
  backoff, with a new audit row. The reset reaches the subscriptions that have no
  delivery row yet, and a publication that already reached a subscriber is absorbed by
  the event's stable identity; a delivery that already ended terminal needs its own
  replay.
- **Neither** if the reason is `invalid_envelope`: the stored envelope is immutable and
  cannot become valid. For a *replay*, a reason of `unknown_type` or `retired_type`
  follows the registry now: an event the registry still does not know, or has retired,
  is refused, and one it knows again is replayed. A *producer reset* acts only on
  `retry_exhausted`; any other reason is refused. A reset of a type that is not yet
  Active is applied, and the dispatcher claims the row once the type is Active.

## Act

Send the fence read at inspection. A ticket is optional and is **checked, not stored**:
the audit row is readable by the tenant, so it never carries one (see [Check it
worked](#check-it-worked)).

```sh
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  "$API/api/platform/v1/asynchronous-work/event-deliveries/$TENANT/$SUBSCRIPTION/$EVENT/replay" \
  -d '{"replayCount":0,"deadLetteredAt":"2026-10-01T12:00:00.123456+00:00","terminalReason":"retry_exhausted","ticket":"CHG-123"}'
```

```sh
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  "$API/api/platform/v1/asynchronous-work/outbox-messages/$TENANT/$EVENT/reset" \
  -d '{"attempts":6,"deadLetteredAt":"2026-10-01T12:00:00.123456+00:00","ticket":"CHG-123"}'
```

A refusal names its reason inside `errors`, as a list under the field, so with `jq` a
stale fence is `.errors.fence[0].key`, a refused state is `.errors.state[0].key` and its
rule is `.errors.state[0].params.cause`, and an unknown commit outcome is
`.errors.outcome[0].key`:

```json
{ "status": 409, "code": "business_rule_violation",
  "errors": { "state": [ { "key": "lockey_not_recoverable", "params": { "cause": "tenant_not_serving" } } ] } }
```

| Answer | Meaning |
|---|---|
| `200`, `outcome: applied` | The row was reset and its MUST audit row committed with it. `countAfter` is the new replay count, or zero attempts |
| `409`, `errors.fence` = `lockey_stale_fence` | The row no longer shows the fence you sent: the dispatcher, another operator or a retry moved it, or a fence field was altered on the way back. Nothing changed and nothing was written. **List again** and decide again. If it persists on a fence you just read, a field was not sent exactly as returned |
| `409`, `errors.state` = `lockey_not_recoverable` | The fence holds and the row is not one the act may take. The rule is `params.cause`: `processed`, `leased`, `invalid_envelope`, `reason_refused` (a producer reset acts only on `retry_exhausted`), `subscription_not_active`, `type_not_dispatchable` (the registry has retired the type or no longer knows it) or `tenant_not_serving` (not Trial or Active, or deleted: nothing here changes a tenant's state, so escalate). Nothing changed |
| `404` | No such row for that tenant |
| `401` | The credential is missing or does not meet the token profile; the cause is in [the operator's signals](operator-access.md#signals) |
| `403` | You hold no grant for the route: `platform.delivery.read` for a list, `platform.delivery.write` for an act. Nothing was opened |
| `400` | The route, the body or the ticket is malformed. A refusal at the gate carries no per-field errors; a refused page parameter names itself (`errors.state`, `errors.limit`, `errors.cursor`) |
| `413`, `415`, `429` | The body is over 4096 bytes, is not `application/json`, or the caller exceeded the per-peer request budget |
| `500` | An unexpected failure, a missing connection string or a deployment defect such as a refused credential or a missing grant: the API's log names it. An act's commit may have landed: list again before changing anything |
| `503`, `audit_unavailable` | The MUST row could not be written, so nothing changed. Retry after the delay; error line 7105 is in the API's log |
| `503`, `dependency_unavailable` | A connection, the one operator lane or an inspection's read was unavailable. Nothing changed. Only one operator call runs per store at a time, and a second is refused at once and not queued; retry after the delay. The cause is in the API's log (warning 7110), found by the response's `correlationId`, and never in the response; see [A `503` that does not clear](#a-503-that-does-not-clear) |
| `503`, `dependency_unavailable` with `errors.outcome` = `lockey_outcome_unknown` | The commit was sent and its answer was lost, or a fault followed it. It **may have landed**. **Do not repeat blindly**: see [An unknown outcome](#an-unknown-outcome) |

An applied replay makes the delivery due at once; the dispatcher claims it like any
other. A client that never received any answer (a timeout, a gateway error) is in the
same position as an unknown outcome.

## An unknown outcome

A landed act leaves the dead-lettered list: it clears the terminal fields. So an
advanced count is not what you will find. Reconcile in this order:

1. List again. If the row is **gone**, or it is there with a **new** fence (a new
   `deadLetteredAt`), the act landed; if it still carries the fence you sent, it did not
   land, and repeating it is safe.
2. To tell *your* act from another operator's, read the tenant's audit row by the
   `correlationId` of the failed response, as in [Check it worked](#check-it-worked).
   A repeat of an act that had landed answers `409` on `errors.fence`, and that answer
   alone cannot say whose act it was.

## Check it worked

1. List again: the row is no longer in the dead-lettered list (an applied act moves it
   back to the ordinary queue).
2. Watch the dispatcher claim it: `learnstack_delivery_pending_count` or
   `learnstack_outbox_pending_count` moves, unless the type is not yet Active, and the
   executor runs the handler. A row that fails for a lasting reason is dead-lettered
   again after its attempts, with a second `platform.*.dead_letter` row. Fix the cause
   before you act again.
3. Read the audit trail. The row is in the **tenant's** trail (it names the real event
   tenant) with no actor, the operation `platform.delivery.replay` or
   `platform.delivery.reset`, the reason `delivery_replay` or `delivery_reset`, your
   attribution in `metadata.operator` and the fence before and after in
   `metadata.fence`. There is no audit API: read it as the application role
   announcing the tenant, by the response's `correlationId`, with the query and the
   digest recipe of [Tenant onboarding](tenant-onboarding.md#reading-the-record) and
   [Killswitch](killswitch.md#reading-the-record). The ticket is not on it, because the
   tenant can read it: record the `correlationId` against your own change reference.

## A 503 that does not clear

A `503` `dependency_unavailable` is retryable, and one that never clears is not a
transient fault. Find the cause in the API's host log by the response's `correlationId`
(warning 7110; it carries the operation and whether the outcome is unknown, and the
exception holds the cause):

- **The lane is busy.** Another operator call is running on that store: wait for it and
  send acts one at a time.
- **The database or the credential.** `ConnectionStrings:OutboxDispatcher` (acts) or
  `ConnectionStrings:PlatformAdmin` (inspections) cannot connect. A refused credential
  or a missing grant is not reported as a `503`: it is a `500` and the log names it. A
  connection string that is not set is a `500` on an inspection and a `503` on an act,
  and in both the log names the key.
- **The store is sealed.** A connection-cleanup fault on a lane seals that store, for
  the dispatcher's own lanes too: its claims fail, so the outbox or the delivery
  dispatch halts, and every call to it answers `503` until the API instance restarts.
  The remedy is to restart the instance. The three lanes of one store share the seal, and the two
  stores do not.

## Signals

`learnstack_platform_operator_recoveries_total{operation,outcome}` counts every
transition by `applied`, `stale_fence`, `not_recoverable`, `not_found`, `unavailable`
and `commit_unknown`. A rising `stale_fence` is two operators, or a script, replaying
the same rows. `learnstack_platform_operator_audit_failures_total` counts the refusals
for an audit failure, and the admission and refusal counters are in [Platform operator
access](operator-access.md#signals). Each inspection page also logs the scope-entry line
7001, as a killswitch act does.

## Limits

- **Erasure.** The rule that the tenant is serving is the guard until P03-10 (G16)
  defines what a replay may reach after a tenant's data is erased; it re-verifies these
  transitions. The check is not locked against a suspension that commits at the same
  moment. Do not reset anything for a tenant that is being erased.
- **The invitation path.** A stuck or unknown outbound invitation email is a *job*, and
  nothing here releases it. Reissuing an invitation helps only while the tenant has a
  free slot; it neither reconciles nor releases the old effect, and four unresolved
  effects can hold every slot with no operator remedy.
- **Capacity.** The operator lane is one connection per store, so acts are one at a
  time, and a second concurrent call is refused with `503` at once. Send them one after
  another.

## Not recoverable here

- A quarantined job, a held reservation, a recurrence.
- An `invalid_envelope` delivery.
- A reset of a row that was processed, or an event the registry no longer knows.
- A dashboard.
