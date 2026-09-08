# ADR-0044: The Audit Write Path — Identity, Multiplicity, Capture and Classification

## Status

Accepted (**Amendment 1: 2026-09-08** — `audit_config` is tenant-**wide**, not
org-scoped; it has no `organization_id` column and § 9's "both" was wrong when written.
`audit_log` is unchanged. **Amendment 2: 2026-09-08** — the redaction sentinel is
`SensitiveTokenCatalog.RedactedValue`, not a second literal; and `FORCE ROW LEVEL
SECURITY` *does* constrain the owner, which changes the trigger's reason and not its
necessity. Errata sit beside both statements; both amendments are at the bottom.)

**Date:** 2026-09-07
**Deciders:** @platform
**Accepted:** 2026-09-07

## Decision Drivers

- **[ADR-0033](0033-audit-durability-model.md) predates the code it governs.** It was
  accepted 2026-08-08, before [ADR-0040](0040-ambient-unit-of-work.md) (2026-08-27)
  defined the ambient unit of work and its nested frames, before Packet 6 shipped
  `TransactionBehavior`'s body, and before Packets 7 and 8 shipped the seven commands
  it will audit. Its shape survives contact with that code; four of its clauses do
  not. This ADR decides what those clauses left open — it does not reopen the
  durability model, which stands.
- **`audit_log.tenant_id` is `NOT NULL` under a policy of `tenant_id = app.tenant_id`,
  so every row needs a tenant — including the rows no tenant owns.**
  [TenantId.cs](../../backend/src/LearnStack.SharedKernel/Identifiers/TenantId.cs) and
  [Phase 02a](../roadmap/phase-02a-kernel-tenancy.md) both say Packet 9 chooses that
  value because `audit_log` is its irreversible consumer;
  [Audit Subsystem § 7](../architecture/31-audit-subsystem.md) has already written the
  nil UUID. Measured: the nil UUID is refused by three shipped mechanisms that read
  all-zero as *no tenant* — `NpgsqlUnitOfWork.SetTenantContextAsync` maps it to the
  empty string, `SetProvisioningTenantContextAsync` throws `ArgumentException` on it,
  and `TenantOwnership.EnsureRealTenant` rejects it in every aggregate factory.
- **The row's tenant is a policy predicate, not a field.** Under
  [ADR-0003 Amendment 3](0003-tenant-isolation-defense-in-depth.md)'s corrected
  template the insert is checked against the GUC the transaction announced. For a
  provisioning command that GUC is the **new** tenant's id, so any other value fails
  `WITH CHECK`, raises `42501`, and — under ADR-0033's fail-closed rule — rolls the
  provisioning back. Measured against the shipped policy shape: an insert whose
  `tenant_id` disagrees with `app.tenant_id` raises *new row violates row-level
  security policy*, and `make seed` drives `ProvisionTenantCommand` through that path.
- **Two shipped commands already write two audited resources on one transaction.**
  `ProvisionTenantCommand` writes `Tenant` and `Organization` under
  [ADR-0042](0042-tenant-provisioning-cross-aggregate-transaction.md), and
  [the Tenancy matrix](../modules/tenancy/audit.md) classifies both **MUST**.
  ADR-0033's "exactly one intent per request" cannot represent that.
- **Nested dispatch is sanctioned and silently loses the row.** ADR-0040 § Nesting
  makes a joiner frame reachable, and `IUnitOfWorkScope.CompleteAsync` is a documented
  no-op on one. A joiner that reports `Committed` claims durability for a row nothing
  has committed; if the owning transaction then rolls back, the MUST row is gone —
  the exact failure ADR-0033 exists to prevent, reintroduced through a door it never
  looked at.
- **Classification is functional under ADR-0033 and nothing in code carries its key.**
  The catalogue is keyed by `(module, operation)`, no document defines that string's
  form, and no interface distinguishes a command from a query. Two architecture tests
  are registered against a join key that does not exist.
- **The capture predicate is blind to the row the corpus calls the most important
  one.** [Audit Subsystem § 3](../architecture/31-audit-subsystem.md) captures
  `AuditableEntity<>` descendants only. `PlatformHostMapping` — "the row that decides
  whose data an anonymous request sees", **MUST** in the Tenancy matrix — is a plain
  class, as are `TenantLocale`, `TenantFeatureFlag`, `PlatformEntitlement`,
  `CustomizationGeneration` and `TenantLevelTaxonomyItem`. Their audit rows would
  carry empty snapshots.
- **`audit_log` carries `organization_id`, and the writer contract predates that.**
  ADR-0033 § Implementation Notes gives both standalone writers as `BEGIN; SET LOCAL
  app.tenant_id; INSERT; COMMIT`, which was sufficient while the table was assumed
  tenant-wide. Under the class the column puts it in, a row whose `organization_id` is
  non-null while `app.organization_id` is unset fails `WITH CHECK` — measured on the
  shipped policy shape: with the organization GUC set the insert returns `INSERT 0 1`,
  without it the row is refused. Every `denied` row for an org-scoped resource travels
  that path.
- **The fail-closed response is a 500 today.** Neither `audit_unavailable` nor
  `audit_unclassified_operation` appears in
  [Error Handling § error codes](../standards/09-error-handling.md) or in
  `HttpStatusMap`, so both fall to `_ => InternalServerError` — a body whose `code`
  reads `audit_unavailable` under a status that says otherwise, which `HttpStatusMap`'s
  own contract calls the thing that must never happen.

## Considered Options

1. **One decision record for the write path** (chosen). Eleven decisions that are one
   subsystem's write path, decided together, with dated Amendments in each Accepted
   ADR the diff touches.
2. **Scatter them as errata and amendments across the six carriers** (rejected). Most
   of these are not corrections of false statements and not stale text — they are
   questions nobody answered, and [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md)'s
   two mechanisms do not cover "undecided". Spreading eleven interlocking answers over
   ADR-0033, ADR-0023, ADR-0028, Standards 05/09/18/21 and two architecture documents
   would leave no single place where the shape is legible, which is how the corpus
   arrived at four incompatible descriptions of one table.
3. **Decide in code and record afterwards in the delivery record** (rejected). Four of
   the eleven are one-way doors written into rows (`tenant_id`, `outcome`, `timestamp`,
   row multiplicity) and one is a cross-repository contract. A delivery record explains
   what happened; it does not let the Hub repository agree in advance.

## Decision

### 1. The platform sentinel tenant id

The reserved platform tenant id is **`00000000-0000-7000-8000-000000000002`** — a
UUIDv7-shaped constant on the precedent `UserId.SystemActor` already sets, exposed as
`TenantId.PlatformSentinel`. It is **not** the nil UUID.

`tenants` carries `CONSTRAINT ck_tenants_not_platform_sentinel CHECK (id <> '…0002')`,
so no tenant can ever be provisioned under it and the sentinel's rows are invisible to
every tenant policy.

It is carried by exactly one class of row: a platform-scope operation with no resolvable
tenant, written standalone. Today that is entry into `EnterPlatformAdminScope(reason)`
and the operations performed inside it — `tenancy.killswitch.toggle` is the first
([ADR-0045](0045-entitlement-and-feature-flag-socket.md) § 5). It is never written by a
tenant request path, never announced by `SetTenantContextAsync`, and — per
[ADR-0036](0036-tenant-resolution-trusted-inputs.md) — never carries a rejected tenant
assertion.

### 2. The tenant a MUST-class row carries

The row's `tenant_id` is **the tenant the ambient transaction announced**, because
that is the only value its own `WITH CHECK` accepts:

| Request shape | `tenant_id` on the row |
|---|---|
| Resolved context | `ITenantContext.TenantId` |
| `IProvisionsTenant` under an unresolved context | `IProvisionsTenant.ProvisioningTenantId` — the value `TransactionBehavior` passed to `SetProvisioningTenantContextAsync` |
| No transaction and no resolvable tenant (`EnterPlatformAdminScope`) | `TenantId.PlatformSentinel` |
| Unresolved context, not provisioning | no row — there is no tenant whose admin could read it, and ADR-0036 forbids inventing one |

It never comes from the request payload. For the provisioning case the value *is* on
the request, and that is safe for the reason ADR-0036 gives about the asserted tenant
and not in spite of it: the same value already drives the GUC, so the database refuses
the write when it is wrong.

### 3. Intent multiplicity

`IAuditStateCapture` holds an **ordered list of intents**, one per `(resource,
operation)` the request audits — not one per request. `ProvisionTenantCommand`
declares two: `tenancy.tenant.create` and `tenancy.organization.create`.

ADR-0033's "exactly one intent per request" is retired by
[its Amendment 2](0033-audit-durability-model.md). `WritePendingAsync` composes and
inserts **all** pending intents on the ambient transaction in one round trip; the
reconcile step re-writes standalone whichever ones the commit did not carry.

### 4. Nested frames

Only the **owning** frame acts. Gated on `IUnitOfWorkScope.IsOwner`:

- only the owner calls `IAuditStore.WritePendingAsync`, and it drains every intent in
  the scope, not only its own;
- only the owner calls `MarkCommitted` / `MarkRolledBack` / `MarkIndeterminate`;
- a joiner's `AuditLogBehavior` neither reconciles nor calls `Clear()`.

`Clear()` is called once, by the outermost behavior, in its `finally`. A joiner that
cleared would erase the outer request's intents and every snapshot before the owner
committed.

### 5. `outcome` and `timestamp`

`audit_log.outcome` is `text` with a **four**-value closed set —
`success | denied | failed | indeterminate` — under
`CHECK (outcome IN ('success','denied','failed','indeterminate'))`. ADR-0016's
`is_success boolean` is superseded; a boolean cannot carry `denied`, which
[Audit Coverage](../standards/18-audit-coverage.md) requires in order to detect
probing.

`timestamp` is **always supplied by `PostgresAuditStore` from `IClock`** — the intent's
`DeclaredAt` for the in-transaction row, a **fresh** `clock.UtcNow` for a standalone
re-write. `DEFAULT now()` stays on the column as a backstop for a row inserted by
something other than the store. This is what keeps ADR-0033's deliberate
`Indeterminate` pair — two rows, one `AuditEntryId` — legal under the composite
primary key `(id, timestamp)` instead of a `23505`.

A `23505` on the standalone re-write is **positive evidence that the business `COMMIT`
landed**: the in-transaction row is durable. It is logged at `Warning`, counted, and
swallowed; it is not an audit failure and must not produce `audit_unavailable`.

### 6. The classification key, and what a request must declare

The catalogue key is `(module, operation)` where `operation` is the dotted slug
**`{module}.{resource}.{verb}`**. It borrows the *shape* of a permission key from
[Permission Standards](../standards/19-permissions.md) — three segments, lowercase,
singular resource, snake_case where a segment is multi-word — and shares its first two
segments with the permission that gates the same resource. `tenancy.tenant.create`,
`customization.content_type.publish`.

**The third segment is not the permission action set, and that is deliberate.**
Standards 19 closes actions at `read | write | delete | admin` and names `publish`
explicitly as forbidden. An audit vocabulary bounded the same way would have to record
`customization.content_type.write` for both a rename and a publication — and publication
is the highest-blast-radius act in that module, which the matrix classifies MUST for
exactly that reason. The two vocabularies answer different questions: a permission
bounds **what a principal may do**, and a closed set is what keeps role grants
comprehensible; an audit operation records **what happened**, and a closed set there
would either make the record lie or force the permission set open. The audit verb
therefore comes from the module's own coverage matrix, and
`Every_TenantOwned_Command_HasAuditCoverage` is what keeps it honest — a verb in the
catalogue with no matrix row fails, and a matrix row with no catalogue entry fails.
Only the `{module}` and `{resource}` segments are required to match the permission key
for the same resource, which is the part a cross-reference actually needs.

The key is declared **in code**, not parsed from Markdown. Each module registers its
defaults through `IAuditCatalogSource.Describe(IAuditCatalogBuilder)`, discovered from
DI; the builder maps a **request type** to one or more `(operation, OperationType,
OperationClass)` triples. `docs/modules/<module>/audit.md` gains an `Operation` column
carrying the same slugs, and `Every_TenantOwned_Command_HasAuditCoverage` asserts the
two agree — the matrix stays the human-readable artifact and the catalogue stays the
executable one, and neither is derived by parsing the other.

**There is no `RequestKind.Other`.** Every `IRequest<Result<T>>` that reaches step 3
must be classified, `Off` included; an unregistered request is rejected with
`audit_unclassified_operation`. The command/query discriminator ADR-0033's sketch
assumed does not exist in this codebase and is not introduced: the catalogue supplies
the `OperationType`, which is the fact the row needs. Test-only request types register
through the same builder in their fixture.

### 7. What the interceptor captures

`AuditChangeTrackerInterceptor` captures **every** entity in the `ChangeTracker` whose
state is `Added`, `Modified` or `Deleted`, minus a named exclusion list — the
`AuditableEntity<>` predicate is withdrawn. Five shipped entities the two module
matrices classify MUST carry no such base class, and `PlatformHostMapping` is the one
both matrices single out as mattering most.

Excluded, by name and with a reason each: `OutboxMessage` (its payload is the audited
event, and the row is machinery), `IdempotencyKey` (request plumbing), and the audit
tables themselves (`AuditEntry`, `AuditConfig` — which cannot arise anyway, since the
store writes parameterised SQL and never a `DbContext`, so the interceptor has no
re-entrancy path).

The interceptor **captures only**: it constructs no row, issues no SQL, and returns
the unmodified `InterceptionResult`. It reaches every module `DbContext` through
`AddModuleDbContext`, which resolves `IEnumerable<ISaveChangesInterceptor>` from the
provider beside the shipped `TenantContextGuardInterceptor` — measured: registering an
`ISaveChangesInterceptor` in DI alone does **not** attach it under this repository's
hand-built options shape.

`changes` serialises as a JSON **array** of `{ path, before, after }` with an
entity-qualified RFC 6901 pointer in `path`, never the polymorphic
object-or-array shape ADR-0016 described.

### 8. Redaction and size

Two gates run inside the capture, before anything reaches `IAuditStateCapture`:

- **`[PiiSensitive]`**, a new SharedKernel property attribute, plus the shipped
  `SensitiveTokenCatalog` name-token list. A marked or name-matched property's value is
  replaced with **`SensitiveTokenCatalog.RedactedValue`** in `before`, `after` and
  `changes` — the property is not dropped, so the diff still records *that* it changed.
  > **Erratum (2026-09-08).** This clause first wrote the sentinel as the literal
  > `"[REDACTED]"`, which was wrong when written: the constant it cites in the same
  > sentence is `"***REDACTED***"`, and three shipped components already emit that one.
  > Naming the constant rather than a literal is the correction; see
  > [Amendment 2](#amendment-2--two-statements-that-were-wrong-when-written-2026-09-08).
- **Size.** Each of `before_state`, `after_state` and `changes` is capped by Packet 8's
  `JsonValue.MaxRowBytes` (256 KiB). Above the cap the value is replaced by an explicit
  elision record — `{"_elided": true, "bytes": <n>, "sha256": "<hex>"}` — never an
  empty object and never a silent truncation.

  **The elision preserves the column's JSON type.** `changes` is an array on both sides
  of the cap, so an elided `changes` is `[{"_elided": …}]` and not a bare object;
  `before_state` and `after_state` are objects on both sides, so theirs is the object
  form. A reader that has to branch on the shape before it can tell whether it is
  looking at a diff is a reader that will get it wrong once, and the Phase 06 diff
  viewer and the per-module `IUserReferenceLocator` both parse this column. The
  binding cases are the two immediately either side of the cap: a payload just under it
  and a payload just over it must deserialise through the same contract.

`audit_blob_id` is **struck** from Audit Coverage Standards. It is named in one
sentence of one standard, exists in no DDL anywhere in the corpus, and names no blob
store; the elision record is what that sentence was reaching for.

### 9. `audit_log` and `audit_config` are tenant-owned, org-scoped

Both join the **tenant-owned, org-scoped** class of
[Database Standards § Table classes](../standards/05-database.md) and take the
canonical template unmodified — one `AND`-ed permissive policy, `ENABLE` **and**
`FORCE`, explicit `WITH CHECK`, and both `AS RESTRICTIVE` write guards. `audit_log`
carries `organization_id`, and the class follows the column. The hand-written policy in
Audit Subsystem § 7 is deleted, not corrected: the template lives in one file.

> **Erratum (2026-09-08).** "Both" was wrong when it was written. `audit_config` has no
> `organization_id` column — not in Audit Subsystem § 7's DDL, not anywhere in the
> corpus — so it cannot take a template whose predicate `AND`s an organization term.
> **`audit_log` is org-scoped; `audit_config` is tenant-owned, tenant-wide**, and
> therefore carries no organization term and no restrictive write guards. See
> [Amendment 1](#amendment-1--audit_config-is-tenant-wide-2026-09-08). The rest of this
> section stands, `audit_config`'s foreign key included.

They ship in a **fourth migration chain**, owned by
`LearnStack.Modules.Audit.Infrastructure`'s `AuditDbContext`, on the Packet 8 pattern.

**Two consequences of the org-scoped class, both binding.**

*The standalone writers announce both GUCs.* ADR-0033 § Implementation Notes describes
`WriteStandaloneAsync` and `WriteBestEffortAsync` as `BEGIN; SET LOCAL app.tenant_id;
INSERT; COMMIT` — a shape written when `audit_log` was assumed tenant-wide. Under the
org-scoped template a row whose `organization_id` is non-null while `app.organization_id`
is unset fails `WITH CHECK`: the GUC reads as the empty string, `NULLIF` turns it into
`NULL`, and `organization_id = NULL` is `NULL`, which is false. Every `denied` row for an
org-scoped resource and every rollback re-write of one would be refused — and refused on
the path whose whole job is to make sure the record survives. Both standalone writers
therefore issue `set_config('app.tenant_id', …, true)` **and**
`set_config('app.organization_id', …, true)` from the draft, as their first statements,
exactly as `NpgsqlUnitOfWork.SetTenantContextAsync` already issues the pair for the
ambient transaction. This is named in ADR-0033's Amendment 2.

*`audit_log` carries no foreign key to `tenants`.* Every other tenant-owned table in the
schema has one; this one must not, for two independent reasons. The platform-scope row
carries `TenantId.PlatformSentinel`, which by § 1 has no `tenants` row by construction,
so an FK would refuse the one row the sentinel exists for — and a foreign key admits no
exception, not for `learnstack_platform` and not under `BYPASSRLS`, because it is a
constraint rather than a policy. And an audit log that cascades or restricts on tenant
deletion is not an audit log: the record of what happened to a tenant has to outlive the
tenant, which is the case a regulator asks about most often. `audit_config` keeps its
FK — it is live configuration, not history, and its rows are meaningless without the
tenant they configure.

GRANT matrix rows:

| Table | `learnstack_app` | `learnstack_platform` | `learnstack_outbox_admin` |
|---|---|---|---|
| `audit_log` | `SELECT, INSERT` | `SELECT, INSERT, DELETE`, `UPDATE (actor_email, ip_address, user_agent, before_state, after_state, changes)` | — |
| `audit_config` | `SELECT` | `SELECT` | — |

Append-only is enforced in three layers and each stops a different actor — measured on
PostgreSQL 18.6: `learnstack_app` is stopped by the absent privilege (`42501`); a
`learnstack_platform` `UPDATE` touching any other column is stopped by the
**column-level GRANT**, before the trigger runs; and the table **owner**, who holds
every privilege implicitly, is stopped only by `audit_log_append_only_guard`. The
trigger is not redundant with the grant — it is the only layer that binds
`learnstack_migration`.

> **Erratum (2026-09-08).** "Whom row security never constrains" was wrong when
> written. Under `FORCE ROW LEVEL SECURITY` the policy applies to the owner like anyone
> else — measured: a `learnstack_migration`-shaped owner's `UPDATE` with no
> `app.tenant_id` announced returns `UPDATE 0`. What the policy constrains it **by** is
> the tenant, not immutability: with the tenant announced the same `UPDATE` returns
> `UPDATE 1`. So the conclusion stands and its reason changes — the owner is the one
> actor no grant and no tenant predicate can stop from rewriting a row it is entitled to
> see, and the trigger is the only layer that does. See
> [Amendment 2](#amendment-2--two-statements-that-were-wrong-when-written-2026-09-08).

### 10. The fourth write path

`IAuditStore` gains a fourth method, `WritePlatformScopeAsync(AuditEntryDraft, DbConnection, DbTransaction, CancellationToken)`,
used by exactly one caller: `EnterPlatformAdminScope(reason)`, which writes its
`security-event` row on **its own platform-role connection, before the operation runs**,
so an operation that later fails is still recorded. It is a fourth *write* method and
not an update method; ADR-0033's "no update method" is untouched.

### 11. Error codes, statuses, and where the code lives

| Code | Status | Meaning |
|---|---|---|
| `audit_unavailable` | **503** | A MUST-class row could not be written durably for an operation that would otherwise have succeeded |
| `audit_unclassified_operation` | **500** | The operation is absent from the catalogue — a deployment defect the caller cannot act on |

Both are added to Error Handling Standards' table, to `HttpStatusMap.For(string)`
explicitly rather than by fallthrough, and to the localization catalogue as
`lockey_audit_unavailable` / `lockey_audit_unclassified_operation`.

`audit_unavailable` reaches the client as an exception, not a `Result`:
`IAuditStore.WritePendingAsync` throws `AuditWriteFailedException : InfrastructureException`
carrying that `Error`, which `TransactionBehavior`'s catch rolls back and
`HttpStatusMap.For(Exception)`'s existing `LearnStackException known => For(known.Error)`
branch maps. `AuditLogBehavior`'s shipped catch-and-rethrow-through-`ExceptionDispatchInfo`
contract, which [ADR-0032](0032-exception-handling-logging-and-observability.md) binds,
is not touched.

**Assembly placement, settled once.** `AuditLogBehavior` **stays in
`LearnStack.Application/Pipeline`**. The two other homes the corpus names —
`LearnStack.Infrastructure.Audit` and `LearnStack.Infrastructure.Behaviors` — would
each invert the Application → Infrastructure dependency that
`MediatRPipelineRegistration.CanonicalBehaviorOrder` and its architecture test depend
on, and the first is forbidden by that project's own csproj. Ports
(`IAuditStore`, `IAuditStateCapture`, `AuditEntryDraft`, `AuditIntent`,
`AuditEntryId`) live in **`LearnStack.SharedKernel.Audit`** — not
`SharedKernel.Abstractions.Audit`, which names a namespace segment no other
SharedKernel surface uses. `PostgresAuditStore`, `AuditStateCapture` and
`AuditChangeTrackerInterceptor` live in `LearnStack.Infrastructure.Audit`;
`AuditEntry`, `AuditConfig` and `AuditDbContext` in the Audit module.

## Context

### Why the sentinel is not the nil UUID

The nil UUID is not merely awkward here, it is currently unwritable. Three shipped
mechanisms read all-zero as *the absence of a tenant*, and a fourth reports it as *not
assigned*: `StronglyTypedId.IsAssigned` returns `false` for it, which every module
validator calls. Choosing it would mean the same value means "no tenant" to the filter
layer, the unit of work and eight aggregate factories, and "a real tenant" to the one
component that writes audit rows — and each of those four sites would need an
explicit, permanent exemption. A reserved non-nil constant needs none of them, and the
`tenants` CHECK gives back the only property the nil UUID had for free.

`UserId.SystemActor` already made this choice for the actor column, for the same reason
and in the same shape. Making the tenant sentinel look different from the actor
sentinel would be the surprising outcome.

### Why the row count follows the resource and not the request

The alternative reads better in ADR-0033's prose and produces a matrix that lies. Both
shipped module matrices classify per resource; `ProvisionTenantCommand` writes two of
them, and the `Organization | create` row the Tenancy matrix marks **MUST** would
simply never exist. The multiplicity is also forced from the other end: nested dispatch
puts a second request's intent in the same scoped buffer whatever we decide about
provisioning, so the buffer holds a list either way. Given that, one intent per request
is not the simpler model — it is the same model with a defect.

The cost is real and is accepted: the catalogued assertion
`MustClass_Audit_Writes_Share_The_Business_Transaction` currently says "exactly one
`audit_log` row" and becomes "exactly one row per declared intent, all on the business
transaction".

### Why classification is in code and the matrix is prose

Two registered tests were specified to parse `docs/modules/<module>/audit.md`, and the
two shipped matrices do not share a grammar — one carries `Resource | Operation | Class
| Why`, the standard's own template carries a resource row with one column per
`OperationType`. Fixing that by legislating a Markdown grammar puts a parser in the
architecture suite and makes every matrix edit a potential build break, for the sake of
deriving a fact the code already has to state.

The inversion is cheaper and stronger: the catalogue is code, the matrix is the
document, and one test asserts they agree. A matrix row with no catalogue entry fails;
a catalogue entry with no matrix row fails. Neither is derived from the other, so
neither can drift silently.

### What we explicitly punted on

- **Retention.** No column, no job, no per-tenant override. `audit_config` carries
  classification overrides only. Retention lands with the purge job in
  [Phase 11](../roadmap/phase-11-production-hardening.md) per
  [ADR-0028](0028-audit-log-partition-management.md) and
  [ADR-0035](0035-demand-gated-infrastructure.md); the three documents that promise a
  per-tenant retention override say which phase owns it.
- **The audit read API and its permissions.** `docs/modules/audit/permissions.md`
  forward-declares `audit.event.read`, `audit.event_export.write`,
  `audit.event_export.read` and `platform.audit.read` in Standards 19's three-part,
  singular, closed-set form; the registry and the endpoints land in
  [Phase 03](../roadmap/phase-03-identity-admin.md).
- **GDPR redaction.** The trigger and the column-restricted grant that make it
  possible ship here; `UserGdprDeletedIntegrationEventHandler` and
  `IUserReferenceLocator` land in Phase 03 with the `users` table they need. The Packet
  7 delivery record's remark that "Packet 9's GDPR handler is the first" caller of
  `PlatformAdminScope` is answered instead by § 10's platform-scope audit row, which is
  a Packet 9 caller of that scope.
- **Fan-out.** Audit export to external sinks rides the outbox and is unchanged.

## Consequences

### Positive

- Provisioning can commit. The MUST row for `ProvisionTenantCommand` satisfies the
  policy by construction rather than by exemption, and `make seed` keeps working.
- The two shipped matrices become true. Every row they classify MUST is a row the
  runtime writes.
- The nested-frame hole closes before any code can fall into it, and closes through
  the seam ADR-0040 already ships (`IsOwner`) rather than a new one.
- The five entities the old predicate missed get snapshots, including the host mapping.
- A MUST-class audit failure answers `503` with a matching body, and a commit-in-doubt
  is a row a compliance reviewer can find rather than a `23505` the caller sees as
  `audit_unavailable`.

### Negative

- **Packet 9 grows.** Eleven decisions, four of them with their own tests, on top of
  the scope the roadmap paragraph describes. The alternative was to discover each one
  mid-implementation, which is the same work at a worse time.
- **A fourth `IAuditStore` method.** ADR-0033 said "exactly three". The platform-scope
  row cannot use any of them: it runs on a connection the request path does not own,
  as a role the other three never use.
- **Every request type must be classified, test types included.** Eight test-only
  request types across four suites need catalogue registrations they did not need
  before. This is the fail-closed rule working as specified, and the cost is visible
  rather than deferred to the first unclassified production query.
- **`[PiiSensitive]` and the elision record are new surface** with, today, no property
  marked and no snapshot large enough to elide. Both are one-way doors once rows exist,
  which is why they ship with the capture rather than after it.

### Neutral

- ADR-0033's durability model, its pipeline position, and ADR-0032's canonical order
  are untouched. This ADR decides what happens *inside* the steps ADR-0033 named.

## Implementation Notes

- **Dated Amendments owed by this ADR**, per
  [Documentation Standards § Correcting and Amending ADRs](../standards/13-documentation.md):
  - [ADR-0033](0033-audit-durability-model.md) **Amendment 2** — intent multiplicity,
    the nesting rule, the `outcome` domain, the row's tenant source, the fourth write
    method, the standalone writers' second GUC (§ 9), and the correction of "a
    read-sensitive query never reaches step 6" (stale since Packet 6:
    `TransactionBehavior` has no request-kind gate and opens the transaction for every
    request that reaches step 6).
  - [ADR-0023](0023-strongly-typed-id-source-generator.md) **Amendment 9** —
    `AuditEntryId` becomes the fourth SharedKernel cross-cutting identifier, because
    `AuditEntryDraft` and `AuditIntent` are SharedKernel records that name it and a
    module-local id would be a project cycle.
  - [ADR-0028](0028-audit-log-partition-management.md) **Amendment 2** — the
    instruction to create `audit_log` as a partitioned parent with two seed partitions
    in its first migration is withdrawn for Phase 02a;
    `Partition_Manager_Job_Is_Registered_AtStartup` belongs to Phase 11 and is
    registered in the catalogue there.
- **Architecture and integration tests** (canonical names registered in
  [the catalogue](../standards/21-architecture-tests-catalogue.md); the catalogue is
  authoritative for each rule's assembly and kind, and three of them are Testcontainers
  integration tests that cannot live in `LearnStack.Tests.Architecture`):
  `AuditEntry_Inherits_Entity_Not_AuditableEntity`,
  `Every_TenantOwned_Command_HasAuditCoverage`,
  `Every_Module_Has_An_AuditCoverage_Matrix`,
  `Modules_Do_Not_Write_AuditLog_Directly`,
  `AuditEntry_Is_AppendOnly`,
  `OperationType_Enum_Matches_Catalog`,
  `AuditStateCapture_ClearedPerRequest`,
  `MustClass_Audit_Writes_Share_The_Business_Transaction`,
  `Audit_Survives_Transaction_Rollback`,
  `Audit_Classification_Does_Not_Read_The_Database_On_The_Request_Path`,
  `AuditLog_Update_Is_Column_Restricted`.
- **Binding runtime cases this ADR adds**, all as `learnstack_app` against a real
  database: an authorization denial on an **org-scoped** resource writes its standalone
  `denied` row (the case that fails if the second GUC of § 9 is missing); a rolled-back
  org-scoped MUST operation leaves zero business rows and exactly one `failed` audit
  row; and a `changes` payload immediately under and immediately over
  `JsonValue.MaxRowBytes` deserialises through the same array contract (§ 8).
- **Carriers this decision changes**, all editable and all updated in the same change:
  [Audit Subsystem](../architecture/31-audit-subsystem.md) (§ 3, § 4, § 5, § 6, § 7,
  § 11, § 13, § 14), [Audit Coverage Standards](../standards/18-audit-coverage.md),
  [Database Standards](../standards/05-database.md) (§ Table classes, § GRANT matrix),
  [Error Handling Standards](../standards/09-error-handling.md),
  [the architecture-test catalogue](../standards/21-architecture-tests-catalogue.md),
  [the glossary](../glossary.md), [CLAUDE.md](../../CLAUDE.md) and
  [Phase 02a](../roadmap/phase-02a-kernel-tenancy.md).


## Amendment 1 — `audit_config` is tenant-wide (2026-09-08)

**Status: Accepted.** Raised by the verification round over the carrier documents this
ADR changes, one day after acceptance.

### What was wrong

§ 9's first sentence puts **both** `audit_log` and `audit_config` in the tenant-owned,
**org-scoped** class. That is false for `audit_config` and was false when it entered the
record.

### How it was shown wrong

`audit_config`'s DDL — [Audit Subsystem § 7](../architecture/31-audit-subsystem.md), the
only declaration of the table in the corpus — is
`(id, tenant_id, module, operation, is_enabled, created_at, updated_at)` under
`UNIQUE (tenant_id, module, operation)`. There is no `organization_id`, and a grep of
`docs/` returns no statement anywhere that an audit classification override is scoped to
an organization. The org-scoped template's predicate `AND`s
`organization_id IS NULL OR organization_id = …`, so applying it to this table names a
column that does not exist and the migration would not run.

### How it should be read

- **`audit_log`** is tenant-owned, **org-scoped**: it carries `organization_id`, and the
  class follows the column, exactly as § 9 says. Unchanged.
- **`audit_config`** is tenant-owned, **tenant-wide**: the tenant term only, no
  organization term, and therefore no `AS RESTRICTIVE` write guards — the class that
  carries none, for the reason Database Standards gives, which is that there is no
  organization to guard.

Adding an organization dimension instead was considered and rejected: nothing in the
corpus asks a tenant to classify one organization's operations differently from
another's, and inventing the column to satisfy a sentence would be the more expensive
error of the two — it would reach the migration, the aggregate and the cache key.

### Carriers changed

[Audit Subsystem § 7](../architecture/31-audit-subsystem.md),
[Database Standards § Table classes and § GRANT matrix](../standards/05-database.md),
[Security Standards](../standards/11-security.md) and [the glossary](../glossary.md) —
each of which had taken the claim from this ADR before it was corrected.


## Amendment 2 — Two statements that were wrong when written (2026-09-08)

**Status: Accepted.** Both raised by the verification round over this ADR's carrier
documents. Neither changes a decision; both correct a reason or a literal that would have
reached code.

### 1. The redaction sentinel is a constant, not a literal

§ 8 wrote the replacement value as `"[REDACTED]"` while, in the same sentence, telling the
implementer to use the shipped `SensitiveTokenCatalog`. That catalogue's constant is
`RedactedValue = "***REDACTED***"`, and three shipped components already emit it —
`RedactSensitiveFieldsEnricher`, `SentryErrorTracker` and `LocalFileErrorTracker`. An
implementer following § 8 literally would have introduced a second sentinel for one
purpose, so a log line and an audit snapshot would disagree about what a redacted value
looks like.

**Read as:** the value is `SensitiveTokenCatalog.RedactedValue`. The corpus names the
constant and never the string, which is what keeps one answer. Phase 03's GDPR redaction
`UPDATE` writes the same constant.

### 2. `FORCE ROW LEVEL SECURITY` does constrain the owner

§ 9 justified the trigger by saying the table owner is one "whom row security never
constrains". That is false, and `FORCE` exists precisely to make it false.

**Measured** on PostgreSQL 18.6, with a `NOSUPERUSER NOBYPASSRLS` role owning a table
under `ENABLE` + `FORCE` and the canonical tenant policy: an owner `UPDATE` with no
`app.tenant_id` announced returns **`UPDATE 0`**; the same `UPDATE` inside a transaction
that announces the tenant returns **`UPDATE 1`** and the row changes. (An earlier probe
appeared to show the opposite because it ran as a **superuser**, which bypasses row
security whatever `FORCE` says — the same trap
[the roles script](../../infra/compose/postgres-init/02-create-roles.sql) documents for
`learnstack_app`.)

**Read as:** the policy constrains the owner **by tenant**, not by immutability. Announce
a tenant and the owner satisfies the policy; hold the table and it satisfies every
privilege check implicitly. Nothing in the grant layer or the policy layer then stands
between `learnstack_migration` and a rewritten audit row — which is the whole of the
trigger's job, and a sharper reason for it than the one first given.

**§ 9's conclusion is unchanged**: three layers, each stopping a different actor, and the
trigger is the only one that binds the owner.

### Carriers changed

[Audit Subsystem § 7](../architecture/31-audit-subsystem.md),
[Database Standards](../standards/05-database.md),
[Security Standards](../standards/11-security.md),
[Audit Coverage Standards](../standards/18-audit-coverage.md),
[the architecture-test catalogue](../standards/21-architecture-tests-catalogue.md) and
[the glossary](../glossary.md).

## Amendment 3 — What the join binds to, and the types the ports carry (2026-09-08)

**Status: Accepted.** Raised by the cross-corpus review of the carrier repair. Four
questions § 6 and § 11 left open, each of which an implementer would otherwise answer by
improvising. **§ Decision is unchanged.**

### 1. The join has two directions and they have different domains

§ 6 says "a verb in the catalogue with no matrix row fails, and a matrix row with no
catalogue entry fails", and § 6 also keys the catalogue by **request type**. Both are
right; together, as written, they are unsatisfiable. Measured: `backend/src/Modules`
contains **seven** request types, and the two shipped matrices classify **thirty**
operations — because both were deliberately written ahead of code and both say so in
their second paragraph. Twenty-three rows therefore have no request type for a catalogue
entry to be keyed on, and `Every_TenantOwned_Command_HasAuditCoverage` would be red on
its first run against classification the corpus asked for.

Read as two rules with two subjects:

- **Catalogue → matrix, total.** Every entry a *module's* `IAuditCatalogSource`
  registers has a row in that module's matrix carrying the same slug. No exemption. The
  test-only request types of § Consequences register in their fixtures, not in a module
  source, and are outside this direction.
- **Matrix → catalogue, scoped to what exists.** A matrix row fails only when a request
  type that raises it **exists** and no catalogue entry names it. A row classified ahead
  of its command is not drift; it is the classification the standard requires *before*
  the command ships, which is the whole reason § Consequences calls MUST/SHOULD/MAY "a
  functional distinction, not a documentation one".
- **Anti-rot, which is what stops the scoping from becoming a hole.** A row marked
  `(planned)` whose command has since shipped **fails**. Marking is not an escape: it is
  a claim the rule re-checks on every run.
- **Off the request path entirely.** Some audited operations are not MediatR requests at
  all — `platform.admin_scope.enter`, `tenancy.killswitch.toggle`,
  `tenancy.entitlement.refresh`, and the two tenant-assertion keys ADR-0036 parks on this
  packet. Their rows carry `(off-path)` and are outside the request-type join in both
  directions; their catalogue entries are registered by slug rather than by type.

`docs/modules/<module>/audit.md`'s `Operation` cell carries the marker. Both shipped
matrices are corrected in the same change; the Tenancy matrix's claim that
`platform.admin_scope.enter` is "the single row outside that join" was wrong when
written and is corrected with them.

### 2. `AuditIntent` carries the tenant

§ 2 decides which tenant a row carries and § 11 places the ports, but nothing gives
`PostgresAuditStore` the value on the in-transaction path: `WritePendingAsync(IUnitOfWork,
CancellationToken)` takes only the unit of work, `IUnitOfWork` exposes no tenant, and
`ITenantContextAccessor` — the source § 5 of the architecture document names — **throws**
for the provisioning case, which is the one case § 2 exists to answer.

`AuditIntent` therefore carries `TenantId TenantId` and `OrganizationId? OrganizationId`,
resolved by `AuditLogBehavior` at step 3, which is the only place all four of § 2's cases
are decidable and is where the intent is already minted. The store composes the row from
the intent and never resolves a tenant itself. `WritePendingAsync`'s signature is
untouched.

### 3. `LearnStack.SharedKernel.Audit` holds the value types, not only the ports

§ 11 enumerates the five ports and is silent on the types those ports carry. That silence
is a project cycle: `AuditIntent` and `AuditEntryDraft` are SharedKernel records that name
`OperationType`, `OperationClass` and the outcome, and every module's
`IAuditCatalogSource` names the first two — while the only declaration of all three sits
in `LearnStack.Modules.Audit.Domain`, which already references SharedKernel. It is the
same cycle [ADR-0023 Amendment 9](0023-strongly-typed-id-source-generator.md) resolves for
`AuditEntryId`, and it resolves the same way.

`LearnStack.SharedKernel.Audit` additionally holds `OperationType`, `OperationClass`,
`AuditOutcome`, `AuditClassification`, `AuditIntentState` and `CapturedEntityChange`. The
Audit module's `AuditEntry` consumes them; it does not declare them.

### 4. `AuditClassification` is the effective answer, `OperationClass` the declared tier

§ 6 requires every request to be classified, "`Off` included", and no enum in the corpus
has an `Off`. Two types, one distinction:

- **`OperationClass { Must, Should, May }`** — what the catalogue and the matrix
  *declare*.
- **`AuditClassification { Off, May, Should, Must, Unclassified }`** — what
  `IAuditConfigService.ClassifyAsync` *returns*, after the tenant's `audit_config`
  override and the MUST floor. `Off` is how a request that writes no row is registered —
  the eight test-only types among them — and `Unclassified` is the rejection.

`IAuditCatalogBuilder` gains the registration that expresses it, so "register `Off`" is a
call and not a convention.

### 5. The sentinel invariant gets an enforcer

§ 1 states that the sentinel "is never written by a tenant request path, never announced
by `SetTenantContextAsync`" as though something enforced it. Nothing does, and the one
announcement path that takes a caller-supplied id — `SetProvisioningTenantContextAsync` —
is not the one § 1 names. Packet 9 adds the guard where the value enters:
`SetProvisioningTenantContextAsync` refuses `TenantId.PlatformSentinel` exactly as it
already refuses `Guid.Empty`, and `Tenant.Create` refuses it in the factory. The `tenants`
CHECK is the backstop, not the control — a constraint cannot stop a GUC from being
announced.

### 6. The slug grammar and ADR-0036's two keys

§ 6 fixes the slug as `{module}.{resource}.{verb}`, lowercase, singular resource,
snake_case within a segment. [ADR-0036](0036-tenant-resolution-trusted-inputs.md) names
two operations this packet must emit — `tenancy.tenant-assertion.reject` and
`tenancy.tenant-assertion.anonymous-burst` — which carry hyphens inside a segment and so
do not parse under it. They are recorded in their snake_case form,
`tenancy.tenant_assertion.reject` and `tenancy.tenant_assertion.anonymous_burst`, and
ADR-0036 carries a dated amendment saying so. Renaming the key rather than widening the
grammar keeps one parser for every audit slug and every permission key.

### Carriers changed

[Audit Subsystem](../architecture/31-audit-subsystem.md) §§ 4, 5, 7, 13,
[Audit Coverage Standards](../standards/18-audit-coverage.md),
[the architecture-test catalogue](../standards/21-architecture-tests-catalogue.md),
[the glossary](../glossary.md), both module matrices,
[ADR-0036](0036-tenant-resolution-trusted-inputs.md) (its own dated amendment) and
`.claude/skills/add-audit-coverage/SKILL.md`.

## References

- [ADR-0033 Audit Durability Model](0033-audit-durability-model.md) — the durability
  model this ADR implements and amends
- [ADR-0016 Audit Log Subsystem](0016-audit-log-subsystem.md) (superseded by ADR-0033)
- [ADR-0003 Tenant Isolation Defense in Depth](0003-tenant-isolation-defense-in-depth.md)
  (Amendment 3 — the corrected RLS template and the four-role model)
- [ADR-0023 Strongly Typed Identifiers](0023-strongly-typed-id-source-generator.md)
- [ADR-0028 Audit Log Partition Management](0028-audit-log-partition-management.md)
- [ADR-0032 Exception Handling, Logging, and Observability](0032-exception-handling-logging-and-observability.md)
- [ADR-0035 Demand-Gated Infrastructure](0035-demand-gated-infrastructure.md)
- [ADR-0036 Trusted Inputs for Tenant and Organization Resolution](0036-tenant-resolution-trusted-inputs.md)
- [ADR-0040 Ambient Unit of Work](0040-ambient-unit-of-work.md)
- [ADR-0042 Tenant Provisioning Cross-Aggregate Transaction](0042-tenant-provisioning-cross-aggregate-transaction.md)
- [Audit Coverage Standards](../standards/18-audit-coverage.md)
- [Audit Subsystem](../architecture/31-audit-subsystem.md)
- [Database Standards](../standards/05-database.md)
