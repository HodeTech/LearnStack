# Identity issuer rebind

Moving the identity authority from one issuer origin to another, under the same realm
name, without re-keying a single person. Decided in
[ADR-0104](../decisions/0104-controlled-issuer-rebind.md); the storage contract of the
confined role is in
[Database Standards](../standards/05-database.md#controlled-issuer-rebind-storage).
This is a planned, rare act with a downtime window. A development environment never
needs it.

## When to use it, and when not to

Use it when the deployment's tenant identity provider keeps its realm and its users but
changes address: a new host name, a new load balancer origin, a migration to another
cluster that preserved every user's `sub` (a realm export and import does). The path
before `/realms/learnstack` may change, for example from `/auth/realms/learnstack` to
`/realms/learnstack`; the realm name may not.

It does **not** change the stable authority identifier, and it refuses a new realm that
would lose or resurrect a person. It cannot rename the realm, re-key users, merge two
providers or move a tenant between authorities. A new realm whose users have new `sub`
values is a different authority and is not reachable here: escalate before doing
anything.

## What it needs

| Needed | Why |
|---|---|
| An operator with a current token and the grant `platform.identity_authority.admin` | The same operator token and grant model as the other [operator acts](operator-access.md). The grant is read from the tool's own configuration when it runs, never from a claim. See [Two people](#two-people) |
| The sixth database role's connection string | `ConnectionStrings__IdentityRebind` for `learnstack_identity_rebind`, to PostgreSQL **directly**, never through a connection pooler: the tool's session settings and its lock belong to one physical session. It exists only in the tool's environment for the minutes it runs: never in an API, seeder or web configuration, and never in a file under version control |
| A read-only credential for the **new** realm | A confidential client with the single role `realm-management/view-users`, created for this run and deleted after it. It is not a LearnStack credential and is not in the realm export |
| The stable `Identity__AuthorityId` and the new `Authentication__Tenant__Issuer` | The same two values the deployment is about to run with. The authority must equal the stored one; the issuer is the target. Outside Development the target must be HTTPS |
| `Deployment__Mode` and the `PlatformOperator__*` settings | The same as the API's, so the tool validates the token against the same issuer, clients and assurance. The mode is read as the API reads it: a declared name, with case and padding ignored; an ordinal or a list is refused |
| The new realm's administration API on the issuer's **own origin** | The tool asks `<issuer origin>/admin/realms/<realm>/users`. A load balancer that blocks `/admin` on the public host makes the proof impossible from there; run the tool from a host that can reach it. The tool uses no proxy and the operating system's trust store only, so a private authority belongs in that store |

### Two people

The tool checks the operator's token, the grant and the attribution it writes into the
audit rows. The grant and the database password sit in the same process environment, so
the tool cannot tell who wrote them. Separate the roles by practice: whoever deploys the
change writes the tool's environment, including `PlatformOperator__Grants__*`, and
supplies the database password for the window only; the operator who signs in supplies
the token and runs it. The database does not force that separation: whoever holds the
role's password could change the issuer without the tool. The password is therefore
enabled for the window and retired after it (see [After it](#after-it)).

## Before you start

### Build the tool

There is no release artifact that carries the tool. Build it from the same commit as the
deployed migrations, on a host with the .NET 10 SDK, and run the output on a host with
the .NET 10 runtime:

```sh
dotnet publish backend/src/LearnStack.Tools.IdentityAuthorityRebind -c Release -o ./rebind-tool
```

### Enable the role for the window

The role's login is closed outside a window. A role administrator opens it with a
password made for this window and hands that over through the secret channel:

```sql
ALTER ROLE learnstack_identity_rebind LOGIN PASSWORD '<one-time password>';
```

The roles script sets `LOGIN` and the password from `LEARNSTACK_IDENTITY_REBIND_PW`
every time it runs, so rerunning it opens the role; retire it again afterwards. On a
database that predates the role, run the roles script before the migration that grants
it: the migration stops with a message that names the missing role, and the roles script
is the role's only definition.

### Freeze

The tool takes a database lock that every API start and every new user binding queues
behind, and it refuses to run while any session of the application, outbox, ingress or
platform role is open on the database. A start that overlaps the rebind waits for the
lock for up to thirty seconds and then fails with the fixed qualification error, and a
user creation waits two seconds and is refused. The tool cannot see a process that holds
no database connection, so the stop step below is the control for that case.

1. Announce the window and **stop every API and seeder process** of the deployment,
   including the Hangfire server, the outbox dispatcher and the provider-event poller.
2. Check that nothing still holds a session. A stopped process leaves none. A connection
   pooler (PgBouncer) or a process that was missed does: the tool refuses with
   `refused_live_sessions` and a count, and that count is all it reports. To see which
   role, read `pg_stat_activity` yourself.
3. Keep the **old** configuration out of every restart script, and out of anything that
   restarts a crashed service. A process started with the old issuer fails its start on
   the mismatch, which is the safe outcome. A process that starts while the tool runs
   queues behind its lock and holds a session, which makes the tool refuse at its last
   check, again the safe outcome; a process that was already running and idle is the one
   the tool cannot see.
4. Do not create or delete users in the **new** realm while the tool runs. It reads the
   realm twice and refuses a realm whose users changed between the reads.

## Get the access token

Follow [Getting a token](operator-access.md#getting-a-token) for the operator issuer.
The token lasts at most 15 minutes and its `auth_time` must be recent, so sign in just
before the run. Give it to the tool in `LEARNSTACK_OPERATOR_TOKEN`, or pipe it on
standard input within thirty seconds so it does not sit in the environment. **Never pass
it as an argument**: the tool refuses any argument.

## Create the read-only realm credential

In the **new** realm's administration console create a client with *Client
authentication* on, only *Service accounts roles* enabled, and give its service account
the single client role `realm-management` / `view-users`. Copy its id and secret into
`LEARNSTACK_REBIND_REALM_CLIENT_ID` and `LEARNSTACK_REBIND_REALM_CLIENT_SECRET`. Delete
the client when the run is over, whatever the outcome.

## Run it

From a host that can reach the database, the operator issuer and the new realm, with the
settings of the stopped deployment in the environment. Run it in `tmux` or `screen`: a
dropped connection ends the run, as an interrupt does. Under `nohup` the hang-up is
ignored and the run goes on.

```sh
export Deployment__Mode=<the deployment's mode>
export ConnectionStrings__IdentityRebind="Host=$DB_HOST;Database=$DB_NAME;Username=learnstack_identity_rebind;Password='$REBIND_PASSWORD';SSL Mode=VerifyFull"  # leakwatch:ignore:database-connection-string
export Identity__AuthorityId=<the stable authority id>
export Authentication__Tenant__Issuer=<the NEW tenant issuer>
export PlatformOperator__Issuer=... PlatformOperator__AuthorizedParties__0=... PlatformOperator__AcceptedAcr__0=...
export PlatformOperator__Grants__0__Subject=<the operator's sub>
export PlatformOperator__Grants__0__Permissions__0=platform.identity_authority.admin
export LEARNSTACK_REBIND_REALM_CLIENT_ID=... LEARNSTACK_REBIND_REALM_CLIENT_SECRET=...
export LEARNSTACK_REBIND_TICKET=CHG-123            # optional but recommended
printf %s "$TOKEN" | dotnet ./rebind-tool/LearnStack.Tools.IdentityAuthorityRebind.dll
```

The password is single-quoted inside the string, and a quote inside it is doubled, so a
`;` or `=` in it cannot split the string. The tool does not require TLS to the database;
outside a loopback host, ask for it (`SSL Mode=VerifyFull`) because the role's password
crosses that link. The two issuer settings are different things: the operator issuer
signs the person in, the tenant issuer is the realm being moved.

The ticket is 1 to 64 characters of `A-Z a-z 0-9 . _ : / # -`; a ticket outside that
grammar is refused. It is recorded on the two audit rows, which no tenant can read.

It prints exactly one line to standard output, nothing to standard error, and exits:

| Exit | Meaning |
|---|---|
| `0` | The issuer was changed (`outcome=applied`), or already equalled the target (`outcome=already_applied`, nothing written) |
| `2` | Refused. **The state is unchanged**; fix the cause and run it again |
| `3` | `outcome=unknown`: the commit's answer was lost and the stored issuer could not be read back. See [After exit 3](#after-exit-3) |

The line is `rebind: outcome=<class>`, then `correlation=<id>`, which finds the audit
rows of a run that reached the database (a refusal before that writes none), then
`detail=<name>` where the class has more than one cause, then `sessions=<n>` when the
freeze check ran, then the counts of the proof where it ran: `active`, `closing`,
`closed`, `reported`, `unkeyable`, `missing_active`, `reappearing_closed` and
`unbootstrapped`. A refusal that happens before a run has a handle (`refused_input`, and
a cancelled start that was waiting for the token) carries no correlation. The line never
contains a subject, a key, an issuer, an address or a credential; keep it in the change
ticket, because it is the only record of a refusal that happens before the database is
reached.

An interrupt (Ctrl-C), a termination signal or a hang-up ends the run as the ten-minute
deadline does: the run rolls back, an unknown commit is still reconciled, and the line
says `refused_cancelled` or its true outcome. A second interrupt does not shorten the
reconciliation, which can take up to about a minute because it waits for the original
transaction to end.

### After exit 3

The commit may or may not have landed. Read the stored issuer with the same role, from a
host that can reach the database:

```sh
psql "host=$DB_HOST dbname=$DB_NAME user=learnstack_identity_rebind sslmode=verify-full" \
  -c 'SELECT issuer FROM identity_authority'
```

- It equals the **target**: the change landed. Run the tool again; it answers
  `already_applied` and writes nothing. Then continue with [After it](#after-it).
- It equals the **old** value: the change did not land. Run the tool again.
- It is **anything else**: stop and escalate.

## What the tool does, in order

1. Validates the token with the operator validator and admits the act against the
   grant. A refusal here (`refused_operator_...`) writes nothing, not even a row.
2. Opens one direct, never-pooled connection as the rebind role and checks that the role
   is exactly what it was granted, table by table and column by column, and that the
   server applied the session timeouts it asked for. It then takes the exclusive rebind
   lock as the first statement of its transaction. If another rebind holds it, the
   tool waits up to 30 seconds and refuses with `refused_rebind_in_progress`.
3. Reads the stored authority. If the stored issuer already equals the target it ends
   with `already_applied` and **writes nothing**.
4. Otherwise commits an **intent** audit row on its own short transaction. From here on
   an aborted attempt is on record.
5. Refuses while a session of a runtime role is open (`refused_live_sessions`).
6. Reads the new realm's discovery document, key set and users, and requires the realm's
   own `issuer` to equal the target, at least one signing key and a complete
   enumeration. The credential is sent only to the realm's own token endpoint, and only
   once the discovery document names the target.
7. Proves continuity (below), asks the freeze question again with a fresh view of the
   database's sessions, changes the one column, writes the **result** row on the same
   transaction and commits.

Limits that bound the run: statements time out at 120 seconds, a transaction that idles
for 15 minutes is ended by the server, the whole run has a ten-minute deadline, and a
realm of more than 200,000 users is refused because its proof cannot finish in that
deadline. A realm of a size near that limit can end as `refused_cancelled` for the same
reason: each page is one request, twice over, and a slow realm uses the deadline up.

## The continuity proof

- Every **Active** user's exact stored subject must be among the new realm's `sub`
  values. A missing one is `missing_active`.
- No reported `sub` may produce the key of a **Closed** user, whose subject is gone and
  only its key kept, so a closed person cannot reappear. That is `reappearing_closed`.
- A user in the new realm that matches no stored key is allowed: that person has not
  signed in yet.
- A user that is **Closing** and still holds its subject is judged by neither rule,
  because closure owns that transition.
- A `sub` that could not be a LearnStack subject (blank, over 255 characters or text
  that cannot be stored) is counted as `unkeyable` and cannot equal a stored one.

The realm is read in full twice and the two reads must report the same users, with no
user repeated and the same count before and after. A realm that changes while it is read
is refused as incomplete.

`refused_continuity_broken` means the new realm is not the same population. Do not
force it. Read the counts:

- **`missing_active` is close to `active`** (or `reported` shares almost nothing with
  it): the realm's users have new ids, as an import that generated them does. This is a
  different authority and not reachable here. Escalate.
- **A few `missing_active`:** accounts were deleted or not copied. Restore them in the
  new realm with their original `sub`, or close those people in LearnStack first through
  the normal lifecycle, then run it again.
- **Any `reappearing_closed`:** a person LearnStack closed has an account in the new
  realm. Delete that account, then run it again. Never reactivate it here.

To compare by hand, the stored subject is `users.provider_subject`, which the rebind
role can read, and the new realm's is the user's `id` in the administration console.

## When it refuses

`detail` is a closed name that says which check failed and never a value.

| Outcome | What it means and what to do |
|---|---|
| `refused_input detail=<setting>` | A required value is missing, malformed, or an argument was given, and the setting is named: `arguments`, `connection_string` (missing, malformed or not the rebind role's), `operator_token` (missing, empty or over its length bound), `realm_client`, `authority_id`, `issuer` (not the realm grammar, or not HTTPS outside Development) or `deployment_mode` |
| `refused_operator_token_<cause>` | The token failed validation: `token_missing`, `token_invalid`, `token_expired`, `token_audience`, `token_issuer`, `token_signature`, `token_algorithm`, `token_type`, `token_lifetime`, `metadata_unavailable`, `metadata_stale` or a `profile_*` cause. [The profile](operator-access.md#the-token-profile) says what each requires. Get a fresh token |
| `refused_operator_missing_permission` | The token is good and the operator holds no `platform.identity_authority.admin` grant |
| `refused_operator_invalid_ticket` | The ticket is outside its grammar |
| `refused_operator_issuer_not_configured` / `..._configuration_invalid` | The `PlatformOperator__*` settings are absent or unsafe |
| `refused_operator_invalid_witness` / `..._unknown_operation` / `..._not_admitted` | A defect in the tool's build, not an operator mistake. Escalate |
| `refused_not_admitted` | The admission was stale, spent or not for this operation. Run the tool again |
| `refused_role_invalid detail=<check>` | `role_session`: the string is malformed or not the rebind role's. `role_attributes`: the role gained an attribute that escalates. `role_membership`: it belongs to a role or has a member. `role_grants`: it can create objects, owns something, or holds a privilege on a relation beyond its declared grants (a `PUBLIC`-readable relation you added to `public` counts; an extension's own relations do not) or lacks one. `session_settings`: the server did not apply the timeouts the tool asked for, as a pooler in the path can; connect directly. For the first four, re-run the roles script and migrate, then open the role again |
| `refused_rebind_in_progress` | Another rebind holds the lock, or a previous run is still open. Wait and retry |
| `refused_database_unavailable detail=<kind>` | `database_connection`: the host or port could not be reached, or the TLS handshake failed. `database_authentication`: the server refused the credential or `pg_hba` does not admit this host (try the same string with `psql`). `database_permission`: a statement was refused for a missing privilege. `database_timeout`: the server or the network took too long. `authority_state`: the stored authority is missing, is not one row or changed under the run; this is not an outage, so escalate. Nothing changed |
| `refused_authority_mismatch` | The stored authority is not `Identity__AuthorityId`. A different authority would re-key every person: stop and escalate |
| `refused_live_sessions` | A session of a runtime role is open (the count is printed). Stop what holds it and retry |
| `refused_new_realm_unavailable detail=<step>` | The read of the new realm stopped at `realm_discovery` (the discovery document, the issuer's path or an address that is not on the issuer's origin), `realm_key_set`, `realm_token` (the client credential was refused, or the token endpoint did not answer), `realm_user_count` (the administration API refused it: the client lacks `view-users`, `/admin` is blocked on this origin, or the realm is above 200,000 users) or `realm_user_list` (the user list was refused or malformed). It names no address and no body |
| `refused_new_realm_incomplete` / `refused_new_realm_issuer_mismatch` | The new realm published no signing key, changed while it was read or reported inconsistent counts, or names another issuer than the target |
| `refused_continuity_broken` | See [the proof](#the-continuity-proof) |
| `refused_audit_failed` | The audit row could not be written, so the change was rolled back or never begun |
| `refused_commit_failed` | The commit failed and the stored issuer was read back unchanged, after the original transaction had ended. Retry |
| `refused_cancelled` | The ten-minute deadline, an interrupt, a termination signal or a hang-up ended the run |
| `refused_unexpected` | A defect. The state is unchanged, because a commit whose outcome cannot be confirmed is reported as `unknown`. Quote the correlation, which finds the intent row if one was written, and escalate |

Every refusal leaves the stored issuer where it was and the next run repeats the whole
proof. The intent row stays, so the trail shows each attempt.

## After it

The change is done when the tool prints `applied`. **Deploying the new issuer
configuration is the unfreeze.** Do the cleanup only after a sign-in works, because a
rollback needs the old realm, the realm credential and the role's password
([Rolling back](#rolling-back)).

1. Set every setting that names the issuer or the realm to the new value, then start
   the processes. A process that still holds the old issuer fails its start with the
   fixed qualification error: that is the guard, not a bug.

   | Setting | Where |
   |---|---|
   | `Authentication__Tenant__Issuer` | Every API and seeder configuration. The federation adapter reads the same key |
   | `LEARNSTACK_BFF_ISSUER` | The web application, when native sign-in is enabled |
   | `IdentityEvents__RealmId` and `IdentityEvents__ClientSecret` | The API, when the provider-event mirror is configured: the realm's immutable id and the event reader's secret, [as the Keycloak setup describes](../../infra/keycloak/README.md) |
   | The realm's own clients and their secrets | In the new realm. A Keycloak export masks confidential client secrets, so set them again there |

2. Confirm one sign-in per tenant host and look at the audit rows (below).
3. Only then clean up. Delete the read-only realm client and remove
   `ConnectionStrings__IdentityRebind` from the tool's host. Close the role's login and
   discard its password:

   ```sql
   ALTER ROLE learnstack_identity_rebind NOLOGIN PASSWORD NULL;
   ```

   The roles script restores `LOGIN` and the password every time it runs, so close the
   role again after any rerun of it.
4. Keep the old realm until you have no use for a rollback.

The audit rows are readable only by the platform role, because they sit under the
platform sentinel tenant and no tenant policy admits them:

```sql
-- as learnstack_platform, not the rebind role
SELECT timestamp, operation, outcome, reason, entity_id, before_state, after_state,
       metadata->'operator' AS operator, metadata->'continuity' AS continuity
FROM audit_log WHERE correlation_id = '<the printed correlation>' ORDER BY timestamp;
```

The two rows are `identity.authority.rebind_attempt` (`indeterminate`) and
`identity.authority.rebind` (`success`), with no actor. The old and new issuer are in
`before_state` and `after_state`, and the ticket is in `metadata.operator`. A run that
was refused or interrupted leaves its attempt row and no result row. To list those:

```sql
SELECT a.correlation_id, a.timestamp FROM audit_log a
WHERE a.operation = 'identity.authority.rebind_attempt'
  AND NOT EXISTS (SELECT 1 FROM audit_log r
                  WHERE r.correlation_id = a.correlation_id
                    AND r.operation = 'identity.authority.rebind')
ORDER BY a.timestamp DESC;
```

## Rolling back

An applied rebind to a wrong issuer is undone by running the tool again with the old
issuer as the target. The same freeze, proof and rows apply, so it needs everything the
first run did: every runtime process stopped, a fresh operator token, a fresh read-only
client in the **old** realm, the old realm reachable, and the role opened again.

It works only while the old realm still reports every Active user. Once anyone has been
created in the new realm after the change, that person's `sub` is stored and is not in
the old realm, so the proof answers `refused_continuity_broken` with `missing_active`.
Roll back before the new realm takes new people, or fix forward.

## Signals

`learnstack_identity_rebind_runs_total{outcome}` counts each run that read its input:
every printed class, `applied` and `already_applied` and `unknown` included, and the
operator-gate and input refusals under the label the line prints. The tool is a
short-lived process: it exports the counters only when `OTEL_EXPORTER_OTLP_ENDPOINT` is
set in its environment. It writes no log. A refusal at the gate leaves no audit row by
design, so its record is the printed line, which belongs in the change ticket, and the
counter when it is exported. `learnstack_identity_rebind_continuity_total{class}` counts
the proof by class.

Any outcome other than `applied` and `already_applied` outside a planned window means
someone ran the tool; alert on it where the counters are exported. The audit query
above lists every attempt that did not finish.

`learnstack_identity_lock_refusals_total{lock_kind="authority"}` counts a user creation
that could not take the shared rebind lock in time (`reason` `timeout`, `cancelled` or
`fault`) and, with `reason="mismatch"`, one that waited out a rebind and found the
issuer changed. A burst of `timeout` is what a window looks like from the API side.

## Not recoverable here

- **A change of the stable authority identifier.** It re-keys every person and can
  resurrect a closed tombstone. There is no tool for it; escalate.
- **A realm rename.**
- **A running process that held no database connection** when the tool ran. It keeps the
  old issuer until it restarts, and then fails its start on the mismatch. The stop step
  is the control for it, not the database.
- **A rollback after the new realm took new people.** See [Rolling back](#rolling-back).
- **A realm of more than 200,000 users.** The proof cannot finish in the deadline;
  escalate.

## What this does not do

It does not stop or start any process, deploy configuration, create or delete the
realm client, verify DNS or certificates, or move any tenant data. Moving a tenant
between identity authorities is out of scope for the whole platform until a decision
says otherwise.
