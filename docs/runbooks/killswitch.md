# Killswitch

Switching a capability off for every tenant during an incident, and switching it back.
Decided in [ADR-0101 § 13](../decisions/0101-platform-operator-authority.md); the
registry and the overlay are described in
[Feature Flags](../architecture/21-feature-flags.md).

## When to use it

Only during an incident, to stop an expensive or risky path for all tenants at once. It
is not a way to take a feature away from one tenant; tenant flags and the plan do that.

| Key | Capability |
|---|---|
| `killswitch.classroom.recording` | Recording a live class |
| `killswitch.notifications.email` | Outbound email |
| `killswitch.analytics.ingest` | Analytics ingest |

No production code reads any of these yet. Flipping one is recorded and visible to the
overlay, but changes no behavior until the capability that guards a path reads it. The
owner of that capability states it in its own runbook.

## What it needs

An operator holding the Platform permission `platform.killswitch.write`, a current token
(see [operator access](operator-access.md)), and the key from the table.

## Switching a key off

`PUT /api/platform/v1/killswitches/{key}` with body
`{"enabled": false, "ticket": "INC-123"}`. The answer is
`{ "key", "before", "after", "correlationId" }`. `before` is `true` when no row existed,
because absent and enabled mean the same.

Two audit rows are written under the platform sentinel, which no tenant can read: the
entry into the platform scope, and the result row `tenancy.killswitch.toggle` with the
before and after state and the operator's digest. The ticket is on those rows and in the
`reason` column of the switch's own row. Find them by the correlation id.

## When it takes effect

Immediately on the instance that handled the request, which drops its cached copy after
the commit. Every other instance refreshes within 60 seconds. Plan for that minute.

**The read fails open.** If the overlay cannot read the table or the cache, it answers
the key's default, which is enabled. A switch flipped off during a database or cache
incident can therefore read as on again once its cached copy expires. Watch the log for
the overlay's read-failure error, and repeat the flip if it appears.

## Switching it back

Send the same request with `"enabled": true`. There is no other restore, and no delete.
Confirm by the response (`after` is `true`) and by the result row.

## If the answer is 503

The audit row could not be written, so the table was not changed. Repeat the request.

## Not recoverable here

A flip is not undone by the audit log: the log records it, and only another flip
reverses it. Two operators flipping the same key at once is last writer wins; the row
has no version, and both flips are in the audit log.
