# Identity issuer rebind

Moving the identity authority from one issuer origin to another, under the same realm
path, without re-keying a single person. Decided in
[ADR-0104](../decisions/0104-controlled-issuer-rebind.md); the storage contract of the
confined role is in
[Database Standards](../standards/05-database.md#controlled-issuer-rebind-storage).
This is a planned, rare act with a downtime window. A development environment never
needs it.

## When to use it, and when not to

Use it when the deployment's tenant identity provider keeps its realm and its users but
changes address: a new host name, a new load balancer origin, a migration to another
cluster that preserved every user's `sub` (a realm export and import does).

It does **not** change the stable authority identifier, and it refuses a new realm that
would lose or resurrect a person. It cannot rename the realm path (the issuer must keep
ending in `/realms/learnstack`), re-key users, merge two providers or move a tenant
between authorities. A new realm whose users have new `sub` values is a different
authority and is not reachable here: escalate before doing anything.

## What it needs

| Needed | Why |
|---|---|
| An operator with a current token and the grant `platform.identity_authority.admin` | The same operator token and grant model as the other [operator acts](operator-access.md). The grant is read from configuration when the tool runs, never from a claim |
| The sixth database role's connection string | `ConnectionStrings__IdentityRebind` for `learnstack_identity_rebind`. It exists only in the tool's environment for the minutes it runs: never in an API, worker, seeder or web configuration, and never in a file under version control |
| A read-only credential for the **new** realm | A confidential client with the single role `realm-management/view-users`, created for this run and deleted after it. It is not a LearnStack credential and is not in the realm export |
| The stable `Identity__AuthorityId` and the new `Authentication__Tenant__Issuer` | The same two values the deployment is about to run with. The authority must equal the stored one; the issuer is the target |
| `Deployment__Mode` and the `PlatformOperator__*` settings | The same as the API's, so the tool validates the token against the same issuer, clients and assurance |

## Before you start: freeze

The tool takes a database lock that makes every API start and every new user binding
wait, and it refuses to run while any session of the application, outbox, ingress or
platform role is open on the database. It cannot see a process that holds no database
connection, so the stop step below is the control for that case.

1. Announce the window and **stop every API and worker process** of the deployment,
   including the Hangfire server, the outbox dispatcher and the provider-event poller.
2. Wait for their connections to close. A pooled connection stays a session until its
   idle lifetime ends (about five minutes by default), so the tool will refuse with
   `refused_live_sessions` and a count until the pools have drained. A count is all it
   reports; to see which role, read `pg_stat_activity` yourself.
3. Keep the **old** configuration out of every restart script. A process that is started
   with the old issuer after the change fails its start on the mismatch, which is the
   safe outcome; a process that was running and idle is the one the tool cannot see.

## Get the access token

Follow [Getting a token](operator-access.md#getting-a-token) for the operator issuer. The
token lasts at most 15 minutes and its `auth_time` must be recent, so sign in just
before the run. Give it to the tool in `LEARNSTACK_OPERATOR_TOKEN`, or pipe it on
standard input so it does not sit in the environment. **Never pass it as an argument**:
the tool refuses any argument.

## Create the read-only realm credential

In the **new** realm's administration console create a client with *Client
authentication* on, only *Service accounts roles* enabled, and give its service account
the single client role `realm-management` / `view-users`. Copy its id and secret into
`LEARNSTACK_REBIND_REALM_CLIENT_ID` and `LEARNSTACK_REBIND_REALM_CLIENT_SECRET`. Delete
the client when the run is over, whatever the outcome.

## Run it

From a host that can reach the database and both issuers, with the stopped deployment's
settings in the environment:

```sh
export Deployment__Mode=<the deployment's mode>
export ConnectionStrings__IdentityRebind="Host=$DB_HOST;Database=$DB_NAME;Username=learnstack_identity_rebind;Password=$REBIND_PASSWORD"  # leakwatch:ignore:database-connection-string
export Identity__AuthorityId=<the stable authority id>
export Authentication__Tenant__Issuer=<the NEW tenant issuer>
export PlatformOperator__Issuer=... PlatformOperator__AuthorizedParties__0=... PlatformOperator__AcceptedAcr__0=...
export PlatformOperator__Grants__0__Subject=<your sub>
export PlatformOperator__Grants__0__Permissions__0=platform.identity_authority.admin
export LEARNSTACK_REBIND_REALM_CLIENT_ID=... LEARNSTACK_REBIND_REALM_CLIENT_SECRET=...
export LEARNSTACK_REBIND_TICKET=CHG-123            # optional, 1 to 64 characters
printf %s "$TOKEN" | dotnet LearnStack.Tools.IdentityAuthorityRebind.dll
```

It prints exactly one line and exits:

| Exit | Meaning |
|---|---|
| `0` | The issuer was changed (`outcome=applied`), or already equalled the target (`outcome=already_applied`, nothing written) |
| `2` | Refused. **The state is unchanged**; fix the cause and run it again |
| `3` | `outcome=unknown`: the commit's answer was lost and the stored issuer could not be read back. Read `identity_authority.issuer` before anything else |

Every line carries `correlation=<id>`, which finds the audit rows, and counts where the
proof ran: `active`, `closing`, `closed`, `reported`, `unkeyable`, `missing_active`,
`reappearing_closed` and `unbootstrapped`. It never prints a subject, a key, an issuer,
an address or a credential.

## What the tool does, in order

1. Validates the token with the operator validator and admits the act against the
   grant. A refusal here (`refused_operator_...`) writes nothing, not even a row.
2. Opens one direct, never-pooled connection as the rebind role, checks that the role is
   exactly what it was granted, and takes the exclusive rebind lock as its first
   statement. If another rebind holds it, the tool waits up to 30 seconds and refuses
   with `refused_rebind_in_progress`.
3. Reads the stored authority. If the stored issuer already equals the target it ends
   with `already_applied` and **writes nothing**.
4. Otherwise commits an **intent** audit row on its own short transaction. From here on an
   aborted attempt is on record.
5. Refuses while a session of a runtime role is open (`refused_live_sessions`).
6. Reads the new realm's discovery document, key set and users, and requires the realm's
   own `issuer` to equal the target, at least one signing key and a complete enumeration.
7. Proves continuity (below), changes the one column, writes the **result** row on the
   same transaction and commits.

## The continuity proof

- Every **Active** user's exact stored subject must be among the new realm's `sub`
  values. A missing one is `missing_active`.
- No reported `sub` may produce the key of a **Closed** user, whose subject is gone and
  only its key kept, so a closed person cannot reappear. That is `reappearing_closed`.
- A user in the new realm that matches no stored key is allowed: that person has not
  signed in yet.
- A user that is **Closing** and still holds its subject is judged by neither rule,
  because closure owns that transition.
- A `sub` that could not be a LearnStack subject (blank, over 255 characters) is counted as
  `unkeyable` and cannot equal a stored one.

`refused_continuity_broken` means the new realm is not the same population. Do not
force it: find out why the users differ. The usual cause is an import that generated new
ids.

## When it refuses

| Outcome | What it means and what to do |
|---|---|
| `refused_input` | A required value is missing, malformed, or an argument was given. The tool says no more, so that a mistake cannot echo a secret. Check `Deployment__Mode`, the connection string's role, the authority id, the issuer, the token and the realm credential |
| `refused_operator_token_<cause>` | The token failed validation: `token_missing`, `token_expired`, `token_audience`, `token_issuer`, `token_signature`, `token_algorithm`, `token_type`, `token_lifetime`, `metadata_unavailable`, `metadata_stale` or a `profile_*` cause. [The profile](operator-access.md#the-token-profile) says what each requires. Get a fresh token |
| `refused_operator_missing_permission` | The token is good and the operator holds no `platform.identity_authority.admin` grant |
| `refused_operator_issuer_not_configured` / `..._configuration_invalid` | The `PlatformOperator__*` settings are absent or unsafe |
| `refused_role_invalid` | The connection string is not the rebind role's, or the role holds something beyond its grants. Re-run the roles script |
| `refused_rebind_in_progress` | Another rebind holds the lock, or a previous run is still open. Wait and retry |
| `refused_database_unavailable` | The database could not be reached or answered a failure. Nothing changed |
| `refused_authority_mismatch` | The stored authority is not `Identity__AuthorityId`. A different authority would re-key every person: stop and escalate |
| `refused_live_sessions` | A session of a runtime role is open (the count is printed). Stop what holds it and retry |
| `refused_new_realm_unavailable` / `refused_new_realm_incomplete` / `refused_new_realm_issuer_mismatch` | The new realm could not be read completely, published no signing key, changed while it was read, or names another issuer. Check the credential's role and the realm's address |
| `refused_continuity_broken` | See above |
| `refused_audit_failed` | The audit row could not be written, so the change was rolled back |
| `refused_commit_failed` | The commit failed and the stored issuer was read back unchanged. Retry |
| `refused_cancelled` | The ten-minute deadline or an interrupt ended the run |

Every refusal leaves the stored issuer where it was and the next run repeats the whole
proof. The intent row stays, so the trail shows each attempt.

## After it

The change is done when the tool prints `applied`. **Deploying the new issuer
configuration is the unfreeze.**

1. Set `Authentication__Tenant__Issuer` to the new value in every API and worker
   configuration, and start them. A process that still holds the old value fails its
   start with the fixed qualification error: that is the guard, not a bug.
2. Delete the read-only realm client and remove `ConnectionStrings__IdentityRebind`
   from the tool's host.
3. Confirm one sign-in per tenant host and look at the audit rows:

   ```sql
   -- as a role that can read audit_log, not the rebind role
   SELECT timestamp, operation, outcome, reason, entity_id, before_state, after_state,
          metadata->'operator' AS operator, metadata->'continuity' AS continuity
   FROM audit_log WHERE correlation_id = '<the printed correlation>' ORDER BY timestamp;
   ```

   The two rows are `identity.authority.rebind_attempt` (`indeterminate`) and
   `identity.authority.rebind` (`success`), under the platform sentinel tenant, with no
   actor. The ticket is in `metadata.operator`, because no tenant can read these rows.

## Signals

`learnstack_identity_rebind_runs_total{outcome}` counts each run and
`learnstack_identity_rebind_continuity_total{class}` counts the proof by class. The tool
is a short-lived process: it exports them only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set
in its environment, and the printed line and the audit rows are the record otherwise.
`learnstack_identity_lock_refusals_total{lock_kind="authority"}` counts a user creation
that waited out a rebind and found the issuer changed.

## Not recoverable here

- **A change of the stable authority identifier.** It re-keys every person and can
  resurrect a closed tombstone. There is no tool for it; escalate.
- **A realm rename or a different realm path.**
- **A running process that held no database connection** when the tool ran. It keeps the
  old issuer until it restarts, and then fails its start on the mismatch. The stop step
  is the control for it, not the database.
- **A wrong rebind that was applied.** Run the tool again with the old issuer as the
  target; the same proof and the same rows apply.

## What this does not do

It does not stop or start any process, deploy configuration, create or delete the
realm client, verify DNS or certificates, or move any tenant data. Moving a tenant
between identity authorities is out of scope for the whole platform until a decision
says otherwise.
