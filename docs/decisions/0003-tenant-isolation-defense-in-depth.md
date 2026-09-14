# ADR 0003: Tenant Isolation Defense in Depth

## Status

Accepted (Amendment 1: 2026-05-18 — adds Organization scope; Amendment 2: 2026-05-19 —
identity row terminology; **Amendment 3: 2026-08-08 — corrects the RLS policy template
and adds the database role model**; Amendment 4: 2026-08-29 — the Phase 02a table list
has gone stale; **Amendment 5: 2026-09-04 — the write guards exclude an
organization-scoped session from tenant-wide rows**; see bottom of document)

## Decision

LearnStack uses defense-in-depth tenant isolation:

- Application tenant context.
- EF Core global query filters.
- PostgreSQL Row Level Security for tenant-owned tables.
- Architecture tests that detect unprotected tenant-owned entities.
- Explicit platform-admin paths for cross-tenant operations.
- Tenant context propagation for background jobs and outbox processing.

## Context

LearnStack uses a shared database and shared schema initially. Query filters alone are not enough because raw SQL, forgotten filters, `IgnoreQueryFilters()`, or background job mistakes can leak tenant data.

## Consequences

- Tenant-owned tables must include `tenant_id`.
- Tenant context must be set per request and per background job.
- Platform-admin operations must be explicit and audited.
- PostgreSQL RLS policies are required before production.
- Tests must fail when tenant-owned entities lack protection.

---

## Amendment 1 — Organization scope (2026-05-18)

Per [ADR-0017](0017-tenant-organization-hierarchy.md), LearnStack adopts a two-level
hierarchy (Tenant + Organization). This amendment extends the original defense-in-depth
table without altering the tenant-level guarantees.

**Extended defense-in-depth layers:**

| Layer | Mechanism |
|-------|-----------|
| Tenant isolation | `tenant_id` column + EF query filter + RLS policy + architecture tests (unchanged from original decision) |
| **Organization filter (new)** | `organization_id` column on org-scoped entities + EF query filter + RLS policy + architecture test |
| Identity | Keycloak realm-per-tenant + `organization_id` JWT claim populated from active org |
| Cache | Cache keys auto-prefixed `{tenant_id}:{organization_id}:{key}` when org context set; `{tenant_id}:{key}` otherwise |
| Files (SeaweedFS) | Object key prefix `tenants/{tenant_id}/organizations/{org_id}/...` when org-scoped; `tenants/{tenant_id}/...` when tenant-wide |
| Search (Meilisearch) | Query filter `tenant_id = X AND (organization_id = Y OR organization_id IS NULL)` for org-context queries |
| Jobs (Hangfire) | Job payload carries `TenantId` (mandatory) + `OrganizationId?` (when applicable) |
| Audit | Every audit row carries `tenant_id` (mandatory) + `organization_id?` (when applicable) — ADR-0016 |

**RLS policy template for org-scoped tables:** superseded by
[Amendment 3](#amendment-3--rls-policy-template-correction-and-database-role-model-2026-08-08).
The template originally published here created a **second permissive policy**, which
PostgreSQL combines with `OR`. The canonical template now lives in exactly one place:
[Database Standards § Tenant-Owned and Organization-Scoped Tables](../standards/05-database.md).

**Org-scope opt-in.** A tenant-owned entity may be **tenant-wide** (no `organization_id`)
or **org-scoped** (`organization_id` populated). Entities mark themselves via
`[OrganizationScoped]` attribute; architecture test enforces the column + filter + policy.

**Default org.** A tenant without explicit orgs has one default org auto-created at tenant
provisioning. Single-org tenants experience no UX difference (org switcher hidden).

The original tenant-level guarantees, RLS-from-day-one rule, and architecture tests for
tenant isolation remain unchanged.

---

## Amendment 2 — Identity row terminology (2026-05-19)

The "Identity" row in Amendment 1's defense-in-depth table reads "Keycloak
realm-per-tenant + `organization_id` JWT claim". Read this as a reference to the
**realm-per-tenant opt-in** described in
[ADR-0004 Amendment 1](0004-authentication-strategy.md) — it is **not** the default.

The default Keycloak strategy (per ADR-0004) is **single-realm `learnstack` with a
`tenant_id` JWT claim**; realm-per-tenant is an enterprise opt-in for compliance-
driven isolation. Both strategies satisfy the defense-in-depth requirement of this
ADR — the row was written assuming the realm-per-tenant variant; the live
architecture guide [09-tenant-isolation.md](../architecture/09-tenant-isolation.md)
reflects the corrected wording.

This is a documentation clarification; the Decision is unchanged.

---

## Amendment 3 — RLS policy template correction and database role model (2026-08-08)

Amendment 1 published an RLS template that **does not deliver the isolation this ADR
decides**. This amendment corrects the mechanism. The Decision — defense in depth with
PostgreSQL RLS as one of its layers — is unchanged; what changes is the SQL that
implements it, plus two layers that were never specified at all.

### What was wrong

`CREATE POLICY` produces a **permissive** policy by default, and PostgreSQL combines
multiple permissive policies for the same command with `OR`. Only `AS RESTRICTIVE`
policies are combined with `AND`.

Amendment 1's template created a separate `<table>_organization_isolation` policy
alongside `<table>_tenant_isolation`. The intended predicate was:

```text
tenant matches AND (organization matches OR row is tenant-wide)
```

The predicate PostgreSQL actually evaluated was:

```text
tenant matches OR organization matches OR row is tenant-wide OR scope = 'tenant'
```

`organization_id IS NULL` satisfies the organization policy on its own — and per
[ADR-0017](0017-tenant-organization-hierarchy.md), a null `organization_id` is the
**defined representation of a tenant-wide row**. Every tenant-wide row in an
org-scoped table was therefore readable by **every tenant**.

Three further gaps compounded it:

- **No `FORCE ROW LEVEL SECURITY`.** A table's owner bypasses that table's policies
  unless the table forces them. The default Entity Framework Core setup — migrations
  and the application connecting as the same role — puts the application in exactly
  that position, so the RLS layer could have shipped inert.
- **No `WITH CHECK`.** A `USING` clause constrains reads. Without an explicit
  `WITH CHECK`, writes are unconstrained for the commands where the two differ.
- **No database role model.** No document named the role the application connects as,
  so nothing prevented it from being the owner or from holding `BYPASSRLS`.

### The correction

The canonical, corrected template lives in exactly one place —
[Database Standards § Tenant-Owned and Organization-Scoped Tables](../standards/05-database.md).
[Tenant Isolation](../architecture/09-tenant-isolation.md) and
[ADR-0017](0017-tenant-organization-hierarchy.md) link to it instead of repeating it.
Its binding properties:

1. **One permissive policy per table, one `AND`-ed predicate.** Tenant and organization
   scope are evaluated in a single policy. Splitting them across two permissive policies
   is what caused the defect; a second policy may only be added `AS RESTRICTIVE`, which
   combines with `AND` and therefore cannot widen.
2. **Two `AS RESTRICTIVE` write guards on org-scoped tables**, one `FOR UPDATE` and one
   `FOR DELETE`. The `app.scope = 'tenant'` term is a *read* hatch, but `USING` also
   selects which rows an `UPDATE` may target, and for `DELETE` it is the only gate —
   PostgreSQL has no `WITH CHECK` for `DELETE`. Without the guards a tenant-scope
   reporting session can delete another organization's rows or reassign them to itself.
3. **`ENABLE` *and* `FORCE` row level security** on every tenant-owned table.
4. **An explicit `WITH CHECK`** that constrains writes to the caller's tenant, and to
   either the caller's organization or a tenant-wide row.
5. **Every `current_setting` read wrapped in `NULLIF(..., '')`.** A dotted GUC becomes a
   session placeholder the first time it is assigned and its reset value is the empty
   string, not "undefined" — so on a pooled connection whose previous transaction set
   the value and whose next one does not, `''::uuid` *raises* instead of filtering.
   `NULLIF` turns that into `NULL`, and a `NULL` policy result is false for both
   `USING` and `WITH CHECK`. This is what makes
   `Unsetting_tenant_context_returns_zero_rows_through_RLS` able to pass at all.
6. **Composite foreign keys on `tenant_id` between tenant-owned tables.** PostgreSQL
   evaluates referential integrity with Row Level Security bypassed, so a single-column
   foreign key lets a row in one tenant reference a row in another — invisibly, because
   no policy runs. See
   [Database Standards § Foreign keys between tenant-owned tables](../standards/05-database.md). The `app.scope = 'tenant'`
   escape hatch applies to reads only — a tenant-scope reporting query may read across
   organizations, but no query may write outside its organization.

### Which tables the template applies to

> **Erratum — 2026-08-29.** The tenant-owned row below enumerates the Phase 02a
> tenancy set. The list was correct when Amendment 3 was written on 2026-08-08 and has
> since gone stale: `idempotency_keys` did not exist yet, and the tenant-owned class
> has since been split into org-scoped and tenant-wide. It is history, not error, so
> it stands — per [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md), a
> statement that was true when it entered the record is amended, never rewritten.
> Current authority for the assignment: [Database Standards § Table
> classes](../standards/05-database.md). Recorded in Amendment 4.

The corrected template governs **tenant-owned** tables. Two classes sit outside it, and
both are enumerated rather than left to judgement:

| Class | Rule | Tables |
|---|---|---|
| Tenant-owned | The corrected template | every domain table; of the Phase 02a tenancy set: `organizations`, `tenant_domains`, `tenant_locales`, `tenant_settings`, `tenant_feature_flags`, `platform_entitlement_cache`, `outbox_messages` |
| Tenant-owned, self-keyed | The corrected template with the tenant term keyed on `id`, because the primary key *is* the tenant id | `tenants` |
| Platform-scoped | `ENABLE` + `FORCE`, role-qualified per-command policies; the read is widened by an explicitly declared non-tenant predicate, writes stay tenant-keyed | `platform_host_to_tenant` |

**A table is platform-scoped only when it is read before the tenant is known.** Exactly
one table meets that test: `IHostToTenantResolver` reads `platform_host_to_tenant` in
order to *determine* the tenant, so under a tenant-keyed predicate it would return zero
rows and no tenant could ever resolve. Its read policy is keyed on a GUC the resolver
declares (`app.resolving_host`) or on `app.tenant_id` for a tenant listing its own hosts;
its writes stay tenant-keyed, because every writer knows its tenant. Row security is
never *disabled* on any of these tables — a table without `ENABLE ROW LEVEL SECURITY` is
indistinguishable from one nobody thought about.

`platform_entitlement_cache` is **not** platform-scoped despite its name. Every read
goes through `IFeatureFlags`, which resolves the tenant from `ITenantContext` and throws
when there is none; every write goes through `IEntitlementProvider.RefreshAsync`, driven
by `PUT /api/internal/tenants/{id}/entitlements`, which carries the tenant id in its
path. It keeps the tenant-owned template.

No statement of the form "writes are reserved to the migration and provisioning path" is
an implementable control: PostgreSQL grants privileges to **roles**, and every handler in
the API process runs as the same role. Grants are stated as a role × table × privilege
matrix; code-path confinement is a separate layer carried by architecture tests. The
matrix and the tests both live in
[Database Standards § Database roles](../standards/05-database.md).

### Database role model

Four roles, four **separate login credentials**, four connection strings. The model is
closed; a fifth role requires a new ADR.

| Role | Connection string | Used by | RLS posture |
|-------|---|---------|-------------|
| `learnstack_migration` | `ConnectionStrings:Migration`, present only in the deploy job | EF Core migrations. **Owns** every table. | `NOBYPASSRLS`; `FORCE ROW LEVEL SECURITY` means ownership grants no bypass |
| `learnstack_app` | `ConnectionStrings:Default` | The application's runtime connection. Not an owner. | `NOBYPASSRLS` |
| `learnstack_platform` | `ConnectionStrings:PlatformAdmin` | Cross-tenant platform-admin operations, entered only through the audited `EnterPlatformAdminScope(reason)` path | `BYPASSRLS` |
| `learnstack_outbox_admin` | `ConnectionStrings:OutboxDispatcher` | The outbox dispatcher, per [Events and Outbox](../architecture/15-event-and-outbox.md) | `BYPASSRLS` |

`BYPASSRLS` bypasses **policies, not `GRANT`s**. Grant scope is therefore the only thing
bounding the two bypass roles, which is why no privilege is granted to `PUBLIC` and no
`ALTER DEFAULT PRIVILEGES` grant exists.

**`EnterPlatformAdminScope(reason)` reaches `learnstack_platform` by a second connection,
not by `SET ROLE`.** `learnstack_app` is not a member of `learnstack_platform`. A
membership grant would make the bypass a standing capability of the application role,
reachable from any code path that emits raw SQL; a plain `SET ROLE` survives `COMMIT` and
would persist on a PgBouncer transaction-pooled server connection into the next tenant's
request; and per-role settings such as `statement_timeout` are applied at login and do
not follow a `SET ROLE`, which would defeat the per-role timeout split
[Phase 11](../roadmap/phase-11-production-hardening.md) builds. The composition root owns
a second, separately-credentialed data source that only
`LearnStack.Infrastructure.MultiTenancy.PlatformAdminScope` may resolve.

### Session-variable placement

The RLS predicates read `app.tenant_id` and `app.organization_id`, set with
`set_config(..., true)` / `SET LOCAL`. Both are **transaction-local**: they are
discarded when the transaction ends. They must therefore be set **inside the ambient
transaction**, after it opens — not in a MediatR behavior that runs before the
transaction begins, and not in a connection interceptor, which fires when the
connection opens rather than when the transaction starts.
[Security Standards § Tenant Context](../standards/11-security.md) is the authority
for this placement.

### Test requirement

Isolation tests connect as **`learnstack_app`**. A test that connects as the owner or
as a `BYPASSRLS` role passes even when every policy is inert, and therefore proves
nothing. The suite must include, at minimum:

- a tenant-wide (`organization_id IS NULL`) row of tenant B is invisible to tenant A —
  the exact case Amendment 1's template leaked;
- an organization-scoped row of organization Y is invisible inside organization X of
  the same tenant;
- clearing the tenant context returns zero rows rather than all rows;
- a write carrying a foreign `tenant_id` is rejected by `WITH CHECK`.

### Where this lands

The corrected template **and the four-role model** are applied by the **first
migration**, in [Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md). They are
one deliverable, not two: the runtime grants to `learnstack_app` are DDL in the same
migration as the policies, and `FORCE ROW LEVEL SECURITY` has no effect worth having
while the connecting role is still the owner. No `ALTER TABLE … OWNER TO` appears in a
migration — migrations connect **as** `learnstack_migration`, so every table they create
is already owned by it, and an explicit `OWNER TO` is a sign the migration is running as
the wrong role ([Database Standards § Database roles](../standards/05-database.md)).

The transaction-local session variables (`SET LOCAL` inside the ambient transaction)
and the isolation suite land in **Packet 7**, with `TenantResolverMiddleware`. Between
the two packets no tenant-owned table is read on a request path: with the policies
live and `app.tenant_id` unset, every predicate evaluates to `NULL` and every query
correctly returns zero rows. Any seeding Packet 6 performs runs inside a transaction
that sets `app.tenant_id` for the tenant being created.

No migration may be written against the superseded template.

## Amendment 4 — The Phase 02a table list has gone stale (2026-08-29)

Amendment 3's § Which tables the template applies to enumerates the Phase 02a
tenancy set in its **tenant-owned** row. The list was correct on 2026-08-08, when
Amendment 3 was written. It is not correct now:
[Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md) added
`idempotency_keys`, and split the tenant-owned class into org-scoped
(`tenant_settings`, which carries an `organization_id`) and tenant-wide (the rest).

**The row stands, with an erratum.** Under
[ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md) a statement that
was true when it entered the record and is stale now is history, not error: it is
amended, never rewritten. Packet 6 rewrote it in place, and this amendment is the
disclosure that edit owed and did not carry; the row is restored to what Amendment
3 accepted.

**The Decision is unchanged**, and so is Amendment 3's: defense in depth by
context + query filter + RLS + architecture test, with one `AND`-ed policy per
table under `ENABLE` and `FORCE ROW LEVEL SECURITY`. Only the enumeration moved on.

The single authority for which class a table belongs to is
[Database Standards § Table classes](../standards/05-database.md). A list copied
into three documents drifts in three directions, and this copy already had — which
is why the assignment lives in one file and this section keeps only the *rule*.

## Amendment 5 — The write guards admitted an organization-scoped session to tenant-wide rows (2026-09-04)

**What was wrong.** Amendment 3's template closes the `USING`-only write paths with two
`AS RESTRICTIVE` guards, one `FOR UPDATE` and one `FOR DELETE`. Both read:

```sql
organization_id IS NULL
OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
```

The first arm is unconditional. It exists so a **tenant-scope** session — one with no
`app.organization_id` — can write the tenant-wide rows that belong to no organization. It
also admits an **organization-scoped** session to those same rows, which is the opposite
of what [Database Standards](../standards/05-database.md) states in the same breath as the
guards: *"a tenant-scope reporting query may read across organizations, but nothing may
write outside its organization."* A tenant-wide row is outside every organization.

**Measured, not reasoned.** On the shipped schema, a session announcing tenant A and
organization A1 updated tenant A's `organization_id IS NULL` row in `tenant_settings`
without refusal. The consequence is intra-tenant rather than cross-tenant — one
organization rewriting the fallback every other organization reads — so it is a
write-scope defect, not an isolation breach, and the four-role model and the tenant term
are untouched.

**The correction.** The first arm is `AND`-ed with "the session has no organization":

```sql
(organization_id IS NULL
 AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
```

> **Erratum — 2026-09-14.** The write-scope statement below is true for UPDATE and
> DELETE, but was false for INSERT when Amendment 5 entered the record: its migration
> changed only the restrictive guards, while `WITH CHECK` still admitted tenant-wide
> rows from an organization-scoped session. A real `learnstack_app` INSERT against
> PostgreSQL 18.4 reproduced the gap. The Decision is unchanged. Current authority:
> [Database Standards](../standards/05-database.md). Recorded in
> [Amendment 6](#amendment-6--insert-scope-and-parent-mirrors-2026-09-14).

A tenant-scope session still writes tenant-wide rows; an organization-scoped session
writes only its own organization's. `app.scope` is deliberately absent here for the same
reason Amendment 3 keeps it out of `WITH CHECK` — the read hatch is not a write hatch, and
it has no carrier until [Phase 03](../roadmap/phase-03-identity-admin.md).

**Where it applies.** [Database Standards](../standards/05-database.md) carries the
template and is corrected in place, because it is the canonical artifact other tables are
told to copy — the instrument [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md)
reserves for exactly that. `tenant_settings` is the only shipped organization-scoped table
and is corrected by a forward-only migration; every table created after this carries the
corrected guards from the template.

## Amendment 6 — INSERT scope and parent mirrors (2026-09-14)

**Accepted — 2026-09-14.** Approved by the maintainer in
[P02d-1's decision pass](../roadmap/phase-02d-walking-skeleton.md#p02d-1-decision-pass-2026-09-14),
before implementation. The original Decision and Status history remain unchanged.

### What the pass verified

Amendment 5 corrected the UPDATE and DELETE guards, but its statement that an
organization-scoped session writes only its own organization's rows is false for
INSERT. Both the original `tenant_settings` migration and the later `audit_log`
migration retain an unconditional null-organization arm in `WITH CHECK`. A test as
`learnstack_app` on the project's pinned PostgreSQL 18.4 accepted a tenant-wide
INSERT from an organization-scoped session. The stricter predicate refused the same
insert with `42501` while permitting the matching-scope positive control.

The statement was false when Amendment 5 entered the record: its migration changed
only the two restrictive guards. An
[ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md) erratum now sits beside
that statement, leaving its wording intact and linking to this amendment. The
canonical template in [Database Standards](../standards/05-database.md) is corrected
in place, and forward migrations correct both existing tables. The Decision remains
unchanged: defense in depth with tenant and organization isolation.

### Control

1. **Exact write scope includes INSERT.** On every organization-scoped table, a
   tenant-wide row is writable only when the session announces no organization; an
   organization row is writable only when the session announces that organization.
   `app.scope = 'tenant'` stays a read hatch. Tenant-wide reads from organization
   sessions remain permitted. The four-role model and tenant predicate do not change.
2. **Children mirror their parent continuously.** A lesson has its course's tenant
   and organization; a translation has its root's tenant and organization. Factories
   derive these values from the parent. A `SECURITY INVOKER` row trigger runs
   **BEFORE INSERT OR UPDATE**, including a change of parent id. It queries by
   tenant and parent id under the caller's RLS, compares organizations with null-safe
   equality, and rejects a missing, hidden or mismatched parent with the same `23514`
   failure, without disclosing parent values. The ordinary tenant-composite foreign
   key remains and supplies referential integrity and delete semantics.
3. **No privileged parent lookup.** The trigger runs in the writing transaction,
   uses the caller's privileges, and introduces no role, GUC setter or RLS bypass.
   Its parent is in the same Education migration chain. The lookup locks the parent
   `FOR KEY SHARE` until transaction end, preventing deletion and replacement under
   the same id between the mirror check and the foreign-key check. The root's existing
   SELECT and UPDATE grants permit that lock. Parent organization immutability and
   the composite foreign key remain separate controls. Parent relations are explicitly
   schema-qualified and the function fixes a restricted `search_path`; a caller's
   temporary table must never substitute for the real parent. The test suite plants
   a same-named temporary table and proves that the real parent still governs.
4. **The shared immutability function requires no surrogate id.** Its error reports
   the table and the violation, without dereferencing `OLD.id`. A no-id translation
   receives the intended `23514`, not PostgreSQL's `42703` missing-field error.

### Alternatives examined

- **Writer derivation alone** leaves direct or defective writes unprotected and is
  rejected. The database is an independent layer, not a restatement of the handler.
- **A nullable three-column foreign key** is rejected: `MATCH SIMPLE` omits the
  entire check when one referencing value is null, including the tenant check.
- **A stored, non-null generated organization key plus a scope-inclusive FK** works
  in PostgreSQL, including reparenting. It is rejected for this implementation after
  reproducing the natural mapping with EF Core 10.0.12 and Npgsql EF 10.0.0: a
  computed alternate key rejects `ValueGeneratedOnAddOrUpdate`; changing it to
  `ValueGeneratedOnAdd` makes relationship fixup explicitly insert the generated
  value on a translation, which PostgreSQL refuses with `428C9`. The test includes
  a previously stored Course and a new Lesson/Translation graph. A workaround or
  duplicate scope state is unnecessary when the invoker trigger fits the existing
  mapping.
- **An INSERT-only parent check** is rejected because a later parent-id UPDATE can
  invalidate the mirror without changing `organization_id`.

### Audit compatibility and implementation obligations

The existing in-transaction audit path copies the announced organization onto each
intent. Standalone and best-effort writers announce the draft's organization on their
own transaction, including its absence; provisioning announces none. Platform-scope
audit uses its separately credentialed connection. None needs an exception to the
stricter INSERT rule. Regression tests must preserve each path's positive case.

P02d-1 applies the shared function replacement in the Tenancy chain, and the policy
corrections separately in Tenancy and Audit. Education creates its parent checks in
its own chain and uses Tenancy's shared immutability function. The migration runner
and test fixtures apply Tenancy first and reverse dependents before Tenancy. No applied
migration is edited.

Standards 05 owns the updated policy, parent-check template,
immutability function and chain dependencies. Standards 21 registers the structural
rules before their implementation. Their proofs include a missing trigger, malformed
parent predicate, mismatched null/non-null scope, reparenting, a no-id satellite,
foreign tenant and matching-scope positive controls. The immutable-organization
guard applies to the organization-scoped table class; a host projection or an outbox
metadata column does not change that table's class.

**Carriers updated by this acceptance.** Database Standards owns the canonical SQL
and migration dependencies; Architecture Tests Catalogue registers its structural
proofs; the Education module spec, Phase 02d decision pass and ADR index link this
amendment. Only the adjacent erratum changes the earlier accepted body.

The concurrency probe reproduced a parent DELETE/reinsert between an unlocked scope
read and the child FK check, leaving different organizations under one valid parent
id. With `FOR KEY SHARE`, deletion waited and was refused by the FK after the child
committed. The fixture granted DELETE only to measure this race; production Education
root grants do not include it.

A BEFORE parent trigger may reject a foreign-tenant child with `23514` before RLS
or the FK runs. Independent RLS (`42501`) and FK (`23503`) proofs use a separate
disposable database: the owner commits removal of that one guard, then an actual
`learnstack_app` login executes the assertions. Missing write grants are treated the
same way when testing UPDATE/DELETE policy behavior. The untouched fixture proves
the production grants and all controls together. An uncommitted owner GRANT is not
visible to the application's separate connection and is not a valid test setup.

### References

- [PostgreSQL 18: Row security](https://www.postgresql.org/docs/18/ddl-rowsecurity.html)
- [PostgreSQL 18: Constraints](https://www.postgresql.org/docs/18/ddl-constraints.html)
- [PostgreSQL 18: Generated columns](https://www.postgresql.org/docs/18/ddl-generated-columns.html)
- [Education module spec](../modules/education/README.md)
