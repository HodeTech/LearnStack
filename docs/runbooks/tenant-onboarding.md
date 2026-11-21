# Tenant onboarding

Creating a production tenant that resolves to a host and has one administrator who can
sign in, and recovering one that has none. Decided in
[ADR-0102](../decisions/0102-production-tenant-onboarding.md); the storage contract of
the one definer it adds is in
[Database Standards](../standards/05-database.md#operator-subject-resolution). The
development seed is not this procedure: the seeder refuses to run outside a Development
deployment.

## What it needs

An operator with a current token (see [operator access](operator-access.md)) holding
`platform.tenant.write` for the first two acts and `platform.tenant_bootstrap.write` for
the other three. The grant is read from configuration on every act. No platform database
credential is involved: every act below runs on the application role, under the
tenant's own row security, through the same pipeline a request uses.

You also need, before you start:

- A tenant id, an organization id, a role id per template and a membership id, each a
  **UUIDv7** that you generate (`psql -At -c 'SELECT uuidv7()'` against any PostgreSQL
  18, including the local stack's; `uuidgen` makes a version 4, which is refused with a
  `400` that names no field). The ids are the acts' identity: sending an act again
  with the same ids is a retry and never a second write.
- A slug for the tenant and for its default organization, and the host the tenant will
  answer on.
- The first administrator's provider subject (`sub`) at the tenant issuer, read from the
  identity provider's own console. An email address is never accepted.

## The order

Each act commits in its own transaction. Stop between any two and resume at the first
act that has not been answered `200`.

1. **Provision the tenant and its default organization.**
   `PUT /api/platform/v1/tenants/{tenantId}`

   ```json
   {
     "slug": "acme",
     "displayName": "Acme Academy",
     "defaultOrganization": { "id": "<organization UUIDv7>", "slug": "main", "displayName": "Main" },
     "ticket": "CHG-123"
   }
   ```

2. **Map the host.** `PUT /api/platform/v1/tenants/{tenantId}/host-mappings/{host}` with
   `{ "isActive": true, "isPubliclyLive": true, "ticket": "CHG-123" }`. **Both flags are
   required** (a body without them is a `400`), because no act unmaps a host and a
   mapping must never go live by omission. `organizationId` is optional and pins the
   host to one branch. **You assert that the tenant owns the host.** LearnStack checks
   no DNS and issues no certificate: a Self-Hosted operator is responsible for both. A
   SaaS custom domain is the Hub's to approve and is not reachable from here.

3. **The person signs in once at that host.** The tenant has no members yet, and the
   zero-membership entry binds the person to the identity authority the first time they
   sign in. The next acts refuse a subject that is not an Active, bound user of that
   authority (`lockey_bootstrap_required`). The check is global, not per host: a subject
   that signed in anywhere is accepted, so **confirm the `sub` is the intended
   person's** (their own console entry), because a mistyped `sub` of another real person
   is accepted and makes that person the administrator.

4. **Initialize the Role templates** the tenant will need, one act each:
   `PUT /api/platform/v1/tenants/{tenantId}/roles/{roleId}` with
   `{ "builtInKey": "TenantAdmin", "capabilityScope": "Tenant", "ticket": "CHG-123" }`.
   The foundation admits eight templates: at scope `Tenant` the `builtInKey` is
   `TenantAdmin`, `Instructor`, `Learner`, `Guardian` or `Editor`; at scope
   `Organization` it is `OrgAdmin`, `OrgInstructor` or `Learner`. `TenantAdmin` at
   `Tenant` is the one step 5 needs. **Initialize every template the tenant will use
   now:** a Role template cannot be created once the tenant has had a Membership.

5. **Create the first administrator.**
   `PUT /api/platform/v1/tenants/{tenantId}/first-administrator/{membershipId}` with
   `{ "roleId": "<the TenantAdmin role id>", "providerSubject": "<sub>", "ticket": "CHG-123" }`.
   It refuses any tenant that has had a tenant-wide Membership, so it cannot add an
   administrator to a tenant that has members.

The `ticket` in each body is optional and is the operator's own reference: LearnStack
validates its grammar and **does not record it** (it is kept only on the platform's
tenantless rows, which these acts do not write), so keep it with the correlation id in
your own change record. Every answer
is `200 { "result": ..., "correlationId": "..." }`. Keep the correlation id.

## Recovering a tenant with no administrator

`PUT /api/platform/v1/tenants/{tenantId}/administrator-recovery/{membershipId}` with the
same body as step 5. It creates a replacement administrator and is the only act that
adds one to a tenant with tenant-wide Membership history.

**It refuses while any Active tenant-wide administrator Membership exists**
(`lockey_administrator_exists`). The database also refuses to leave a tenant without an
available administrator, so revoking or suspending the last one through the product is
not possible. Recovery therefore applies to a tenant whose administration was lost
outside that guard, for example after an owner-level repair. It does **not** apply to a
tenant whose administrator is Active but cannot sign in: escalate that to the platform
owner, because it is a decision about identity, not a runbook step. The act names an
initialized `TenantAdmin` Role of the tenant and an Active, bootstrapped person, and
leaves the last-administrator guard as it found it.

## When an act answers something else

| Status | Reason (in the problem's `errors`) | What it means and what to do |
|---|---|---|
| 400 | `validation_failed` | The body is malformed, a value is out of its grammar, an id is not a UUIDv7 (or is the platform sentinel), a host mapping omits a flag, the `providerSubject` is blank or over 255 characters, or the ticket is not 1 to 64 characters of `A-Z a-z 0-9 . _ : / # -` |
| 401 | | The token does not meet the profile; there is one answer for every cause, and the cause is in the operator's signals |
| 403 | | The token is good and the operator holds no grant for this act. An admitted act the request pipeline itself refused as forbidden also answers `403`; that is a defect, not a grant, and logs event 7109 |
| 404 | | The tenant does not exist, or the act can no longer see it. Provision first |
| 409 | `lockey_slug_taken`, `lockey_identifier_taken` | Another tenant has the slug, or an id is already used by a different record. Use another value; an exact repeat of your own act is a `200` |
| 409 | `lockey_bootstrap_required` | The named person has not signed in at the tenant's host (or is not Active). Ask them to sign in, then repeat |
| 409 | `lockey_role_not_initialized` | The role id names no initialized `TenantAdmin` template of this tenant. Run step 4 first |
| 409 | `lockey_tenant_has_memberships` | The tenant has had a Membership, so a first administrator or a new template is refused. Use recovery only if no administrator is active |
| 409 | `lockey_administrator_exists` | Recovery refused: an Active administrator exists |
| 409 | `lockey_role_template_unknown`, `lockey_identity_foundation_refused` | The template or the resulting graph is not one the identity foundation admits |
| 409 | `lockey_host_taken` | The host is mapped to another tenant, or to this one with other flags; the answer never says which |
| 409 | `lockey_host_reserved` | The host is a platform host and can never resolve to a tenant |
| 409 | `lockey_organization_not_in_tenant` | The `organizationId` of a host mapping is not one of this tenant's |
| 409 | `lockey_concurrency_conflict` | Another change won, or a statement's lock timed out; repeat the same request. If it repeats, a second template of the same key and scope already exists |
| 503 | | The audit row could not be written, or the act could not take its ordered tenant lock (`lockey_dependency_unavailable`), so nothing changed. Retry after a short delay |

The reason of a `409` is in the problem's `errors`, keyed by the body field it concerns,
as `{"slug": [{"key": "lockey_slug_taken"}]}`, or by `$` when it concerns the tenant as
a whole, as `{"$": [{"key": "lockey_tenant_has_memberships"}]}`. A `400` raised at the
gate or by a route check carries no `errors`.

A business refusal of an admitted act (a `409`) writes a `failed` audit row in the
tenant, so the attempt is on the record. A refusal at the gate (`401`, `403`, or a `400`
for the ticket) writes none: it leaves a counter and a log line, and the operator is not
yet an admitted actor of any tenant.

## Repeating an act

Every act is idempotent on the ids you chose, so any failure that leaves you unsure, a
timeout or a connection reset or a `500` after a commit, is answered by sending the same
request again. The retry finds what was written and answers `200` from the stored
record; it never writes a second tenant, Role, Membership or mapping. It does record
itself: the repeated act commits its ordinary audit rows as a `success` with no entity
or before and after state, so a repeat is visible in the trail as the repeat it is. A
retry whose ids match but whose other values differ, a changed display name or
organization slug for example, is refused as `lockey_identifier_taken` or
`lockey_slug_taken` rather than changing the record. A retry of the first administrator
or of a Role act still answers after the new administrator has edited the template. The
host-mapping retry also drops the host's cached resolution again, which repairs a miss
cached between the first run's invalidation and its commit.

## Reading the record

Each act writes its MUST audit rows in its own transaction, in the target tenant, with
`actor_user_id` NULL, a closed reason slug (`tenant_provision`, `host_map`,
`role_initialize`, `first_admin_create`, `admin_recover`) and `metadata.operator`
carrying the issuer, the operator's subject digest, the accepted `acr` and the executor.
The ticket is not on these rows, because the tenant can read them. As the application
role, announcing the tenant inside a transaction, by the correlation id the answer
returned:

```sql
BEGIN;
SELECT set_config('app.tenant_id', '<tenantId>', true);
SELECT timestamp, operation, outcome, reason, metadata->'operator' AS operator
FROM audit_log WHERE correlation_id = '<correlationId>' ORDER BY timestamp;
COMMIT;
```

The operator's digest is computed as in [Killswitch](killswitch.md#reading-the-record).

## Signals

`learnstack_platform_operator_acts_total{operation,outcome}` counts each act as
`applied`, `replayed`, `refused` or `failed`; a `replayed` count is a setup resumed at
that step. Event 7109 (Warning) means an admitted act was refused as forbidden by the
request pipeline itself, which is a defect and not an operator mistake, and which writes
no audit row. The gate's counters and events 7101 to 7103, 7105 and 7106 are in
[operator access](operator-access.md); 7104, 7107 and 7108 belong to the killswitch.

## Not recoverable here

- **A host mapping's flags and organization pin.** No act updates or removes a mapping,
  and a repeat that changes a flag or the pin is refused as `lockey_host_taken`. A
  mistaken mapping stays until the platform owner repairs it at the database; escalate.
- **A wrong administrator.** A mistyped `sub` makes that person the administrator, and
  this surface has no act to remove or replace them: the first administrator and
  recovery both refuse once an administrator is active, and the database refuses to
  revoke the last one. Confirm the `sub` before step 5, and escalate a mistake.
- **A tenant.** Nothing here renames, suspends or deprovisions one, or moves it to
  another issuer.
- **Role templates after the first Membership.** They can no longer be created here;
  the tenant's own administrators edit them.

## What this does not do

It does not verify DNS, issue TLS, send an invitation, set a plan or an entitlement,
create a Hub record, or deprovision a tenant. A tenant that needs an entitlement before
it serves is waiting on the Hub's push or the operator-configured default, which is the
Hub's runbook. Nothing here moves the deployment between identity issuers; that is a
separate offline act ([runbook](identity-issuer-rebind.md),
[ADR-0104](../decisions/0104-controlled-issuer-rebind.md)).
