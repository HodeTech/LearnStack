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
`{"enabled": false, "ticket": "INC-123"}`, as shown in the
[operator access](operator-access.md#making-a-call) example. The answer is
`{ "key", "before", "after", "correlationId" }`. `before` is `true` when no row existed,
because absent and enabled mean the same.

Concurrent flips of one key take turns on the row, so each answer's `before` is the
previous flip's `after`, and a first flip of a key creates the row at the default before
it reads it.

Two audit rows are written under the platform sentinel, which no tenant can read: the
entry into the platform scope, and the result row `tenancy.killswitch.toggle` with the
before and after state and the operator's digest. The ticket is on those rows and in the
`reason` column of the switch's own row (empty again after a flip without one). Find the
rows by the correlation id.

## Reading the record

The sentinel rows are visible only to the platform credential, so a database
administrator reads them. By the correlation id the answer returned:

```sql
SELECT timestamp, operation, reason,
       metadata->'operator'->>'subjectDigest' AS operator_digest,
       metadata->'operator'->>'ticket' AS ticket,
       before_state, after_state
FROM audit_log
WHERE tenant_id = '00000000-0000-7000-8000-000000000002'
  AND correlation_id = '<correlationId>'
ORDER BY timestamp;
```

The operator is recorded as a digest, never as the raw `sub`. To find every row one
operator wrote, compute their digest from the issuer and the `sub` and filter on
`metadata->'operator'->>'subjectDigest'`:

```sh
printf 'learnstack.operator.subject.v1\n%s\n%s' "$ISSUER" "$SUB" \
  | openssl dgst -sha256 -binary | openssl base64 -A | tr '+/' '-_' | tr -d '='
```

The current state of a switch is its own row, if it has one:

```sql
SELECT key, is_enabled, reason, toggled_at
FROM platform_killswitches WHERE key = '<key>';
```

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

## If the answer is not 200

| Answer | What happened | What to do |
|---|---|---|
| 503 | The audit row could not be written, so the table was not changed | Repeat the request after `Retry-After` |
| 500 | Something failed unexpectedly. If it was the final commit, its outcome is unknown and the flip may have landed; the instance still dropped its cached copy | Read the switch's row (above) before repeating |
| 404 | The key is not one of the three in the table | Check the spelling |

## Not recoverable here

A flip is not undone by the audit log: the log records it, and only another flip
reverses it. Both flips of a pair are in the audit log with their true before and after
states.
