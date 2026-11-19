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
  **UUIDv7** that you generate. The ids are the acts' identity: sending an act again
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
   `{ "isActive": true, "isPubliclyLive": true, "ticket": "CHG-123" }`. `organizationId`
   is optional and pins the host to one branch. **You assert that the tenant owns the
   host.** LearnStack checks no DNS and issues no certificate: a Self-Hosted operator is
   responsible for both. A SaaS custom domain is the Hub's to approve and is not
   reachable from here.

3. **The person signs in once at that host.** The tenant has no members yet, and the
   zero-membership entry binds the person to the identity authority the first time they
   sign in. The next acts refuse a subject that has not done this
   (`lockey_bootstrap_required`).

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
   It refuses any tenant that has ever had a Membership, so it cannot add an
   administrator to a tenant that has members.

The `ticket` in each body is optional, and is the operator's own reference. Every answer
is `200 { "result": ..., "correlationId": "..." }`. Keep the correlation id.

## Recovering a tenant with no administrator

`PUT /api/platform/v1/tenants/{tenantId}/administrator-recovery/{membershipId}` with the
same body as step 5. It creates a replacement administrator and is the only act that
adds one to a tenant with Membership history.

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
| 400 | `validation_failed` | The body is malformed, a value is out of its grammar, an id is not a UUIDv7, or the ticket is not 1 to 64 characters of `A-Z a-z 0-9 . _ : / # -` |
| 401 | | The token does not meet the profile; there is one answer for every cause, and the cause is in the operator's signals |
| 403 | | The token is good and the operator holds no grant for this act |
| 404 | | The tenant does not exist, or the act can no longer see it. Provision first |
| 409 | `lockey_slug_taken`, `lockey_identifier_taken` | Another tenant has the slug, or an id is already used by a different record. Use another value; an exact repeat of your own act is a `200` |
| 409 | `lockey_bootstrap_required` | The named person has not signed in at the tenant's host (or is not Active). Ask them to sign in, then repeat |
| 409 | `lockey_role_not_initialized` | The role id names no initialized `TenantAdmin` template of this tenant. Run step 4 first |
| 409 | `lockey_tenant_has_memberships` | The tenant has had a Membership, so a first administrator or a new template is refused. Use recovery only if no administrator is active |
| 409 | `lockey_administrator_exists` | Recovery refused: an Active administrator exists |
| 409 | `lockey_role_template_unknown`, `lockey_identity_foundation_refused` | The template or the resulting graph is not one the identity foundation admits |
| 503 | | The audit row could not be written or a dependency could not answer, so nothing changed. Retry after a short delay |

A refused act writes a `failed` audit row in the tenant, so the attempt is on the
record.

## Repeating an act

Every act is idempotent on the ids you chose, so any failure that leaves you unsure, a
timeout or a connection reset or a `500` after a commit, is answered by sending the same
request again. The retry finds what was written and answers `200`; it never writes a
second row. A retry whose ids match but whose other values differ is refused as
`lockey_identifier_taken` or `lockey_slug_taken` rather than changing the record.

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

## What this does not do

It does not verify DNS, issue TLS, send an invitation, set a plan or an entitlement,
create a Hub record, or deprovision a tenant. A tenant that needs an entitlement before
it serves is waiting on the Hub's push or the operator-configured default, which is the
Hub's runbook. Nothing here moves a tenant between identity issuers; that is a separate
act ([ADR-0104](../decisions/0104-controlled-issuer-rebind.md)).
