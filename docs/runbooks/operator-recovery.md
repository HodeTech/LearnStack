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

A reset changes a row. An inspection changes nothing: it reads metadata only, and it
cannot read an event body, a job's parameters or plan, a causal id or a lease token,
because the database refuses those columns to the platform role.

## What is not here

- **Job recovery.** A quarantined job stays frozen, keeps its reservation and keeps the
  `jobs` health check unhealthy. Nothing here requeues, retries, releases or re-arms
  it, because the only proof that would make that safe is a provider's fence or the
  termination of the process that started the effect, and no adapter can supply one
  today. The inspection shows the job and how many reservations hold capacity; it is an
  operator's evidence for an escalation, not a remedy.
- **A recurrence skip, pause or resume.** None exists.
- **The Hangfire dashboard.** `/admin/hangfire` answers 404 in every deployment mode.
- **A privileged subscription.** A delivery that belongs to one is refused.

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
`/` `#` `-`. Inspections run on `ConnectionStrings:PlatformAdmin`; the two acts run on
`ConnectionStrings:OutboxDispatcher` ([setup](operator-access.md#what-must-be-configured)).

```sh
curl -s -H "Authorization: Bearer $TOKEN" \
  "$API/api/platform/v1/asynchronous-work/outbox-messages?state=dead_lettered&limit=20&ticket=CHG-123"
```

- `state` is `dead_lettered` (default) or `terminal_requested`. A **terminal requested**
  row has a terminal reason recorded and the commit of its dead-letter not yet made: its
  state is shown and it is **not actionable**. It becomes dead-lettered by itself, or it
  does not.
- `limit` is 1 to 100 (default 20; a larger value is clamped, zero or less is refused
  with `errors.limit`). The answer holds `items` and a `pageInfo` with `nextCursor`
  and `hasNext`; send `nextCursor` back as `cursor`. A cursor belongs to the route and
  state that issued it. The quarantined-jobs list takes no `state`.
- Each row carries `eventType` as the registry's label (never stored text), `lastError`
  (a descriptor such as `publish:failed:provider`, or `unrecognized`) and `actionable`.
  Treat `actionable` as advice about the row and the registry: the act also checks the
  tenant's service state and the fence, so a suspended tenant's row can be listed
  actionable and refused.
- **The fence travels with the row.** A delivery shows `replayCount`, `deadLetteredAt`,
  `terminalReason`; an outbox row shows `attempts` and `deadLetteredAt`.
  `deadLetteredAt` has microsecond precision: send it back **exactly as returned**.

## Choose: replay or reset

- **Replay** resets one *delivery*: one subscription of one event. Use it when a
  subscriber's handler failed for a reason that is now fixed, or its subscription became
  Active. The completed siblings, the inbox row, the producer row and every other event
  are untouched.
- **Producer reset** resets one *outbox row* whose terminal reason is `retry_exhausted`:
  a burst of dispatch failures used up its six attempts. Use it when the event was never
  published. It **renews the six-attempt budget**, so a row that fails for a lasting
  reason fails six more times and is dead-lettered again with a new audit row. The reset
  reaches the subscriptions that have no delivery row yet, and a publication that
  already reached a subscriber is absorbed by the event's stable identity; a delivery
  that already ended terminal needs its own replay.
- **Neither** if the reason is `invalid_envelope`: the stored envelope is immutable and
  cannot become valid. For a *replay*, a reason of `unknown_type` or `retired_type`
  follows the registry now: an event the registry still does not know, or has retired,
  is refused, and one it knows again is replayed. A *producer reset* acts only on
  `retry_exhausted`; any other reason is refused. A reset of a type that is not yet
  Active is applied, and the dispatcher claims the row once the type is Active.

## Act

Send the fence read at inspection, and a ticket if you have one:

```sh
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  "$API/api/platform/v1/asynchronous-work/event-deliveries/$TENANT/$SUBSCRIPTION/$EVENT/replay" \
  -d '{"replayCount":0,"deadLetteredAt":"2026-10-01T12:00:00.123456Z","terminalReason":"retry_exhausted","ticket":"CHG-123"}'
```

```sh
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  "$API/api/platform/v1/asynchronous-work/outbox-messages/$TENANT/$EVENT/reset" \
  -d '{"attempts":6,"deadLetteredAt":"2026-10-01T12:00:00.123456Z","ticket":"CHG-123"}'
```

| Answer | Meaning |
|---|---|
| `200`, `outcome: applied` | The row was reset and its MUST audit row committed with it. `countAfter` is the new replay count, or zero attempts |
| `409`, `errors.fence` = `lockey_stale_fence` | The row no longer shows the fence you sent: the dispatcher, another operator or a retry moved it. Nothing changed and nothing was written. **Inspect again** and decide again |
| `409`, `errors.state` = `lockey_not_recoverable` | The fence holds and the row is not one an act may take: `invalid_envelope`, a reason a producer reset refuses, a subscription that is no longer Active, an event type the registry no longer knows, or a tenant that is not serving (not Trial or Active, or deleted). Nothing changed |
| `404` | No such row for that tenant |
| `403` | You hold no `platform.delivery.write` grant. Nothing was opened |
| `400` | The route, the body or the ticket is malformed. A refusal at the gate carries no per-field errors; a refused page parameter names itself (`errors.state`, `errors.limit`, `errors.cursor`) |
| `503`, `audit_unavailable` | The MUST row could not be written, so nothing changed. Retry after the delay |
| `503`, `dependency_unavailable` | A connection, the one operator lane or an inspection's read was unavailable. Nothing changed. Only one operator call runs per store at a time, and a second is refused at once and not queued; retry after the delay. The cause is in the API's log, never in the response |
| `503`, `dependency_unavailable` with `errors.outcome` = `lockey_outcome_unknown` | The commit was sent and its answer was lost, or a fault followed it. It **may have landed**. **Inspect the row before repeating**: a landed reset shows an advanced `replayCount` (or `attempts` of zero), and a repeat of the same request would answer `409` `lockey_stale_fence` |

An applied replay makes the delivery due at once; the dispatcher claims it like any
other.

## Check it worked

1. Inspect the row: it is no longer in the dead-lettered list (an applied reset moves it
   back to the ordinary queue).
2. Watch the dispatcher claim it: `learnstack_delivery_pending_count` or
   `learnstack_outbox_pending_count` moves, and the executor runs the handler. A reset
   row that fails for a lasting reason is dead-lettered again after its attempts, with a
   second `platform.*.dead_letter` row. Fix the cause before you reset again.
3. Read the audit trail. The row is in the **tenant's** trail (it names the real event
   tenant) with no actor, the operation `platform.delivery.replay` or
   `platform.delivery.reset`, the reason `delivery_replay` or `delivery_reset`, your
   attribution in `metadata.operator` and the fence before and after in
   `metadata.fence`. The ticket is not on it, because the tenant can read it: a
   ticket sent with an act is checked and not stored, so record the response's
   `correlationId` against your own change reference.

## Signals

`learnstack_platform_operator_recoveries_total{operation,outcome}` counts every
transition by `applied`, `stale_fence`, `not_recoverable`, `not_found`, `unavailable`
and `commit_unknown`. A rising `stale_fence` is two operators, or a script, replaying
the same rows. `learnstack_platform_operator_audit_failures_total` counts the refusals
for an audit failure, and the admission and refusal counters are in [Platform operator
access](operator-access.md#signals).

## Limits

- **Erasure.** The rule that the tenant is serving is the guard until P03-10 (G16)
  defines what a replay may reach after a tenant's data is erased; it re-verifies these
  transitions. The check is not locked against a suspension that commits at the same
  moment. Do not reset anything for a tenant that is being erased.
- **The invitation path.** A stuck or unknown outbound invitation email is a *job*, and
  nothing here releases it. Reissuing an invitation helps only while the tenant has a
  free slot; it neither reconciles nor releases the old effect, and four unresolved
  effects can hold every slot with no operator remedy.
- **Capacity.** The operator lane is one connection per store, so replays are one at a
  time, and a second concurrent call is refused with `503` at once. Send them one after
  another. A connection-cleanup fault on that lane seals its store as one on the
  dispatcher's lanes does, and only a restart clears it.

## Not recoverable here

- A quarantined job, a held reservation, a recurrence.
- An `invalid_envelope` delivery.
- A reset of a row that was processed, or an event the registry no longer knows.
- A dashboard.
