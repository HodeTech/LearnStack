# 18 — Audit Coverage Standards

**Status:** Active
**Derives from:** [ADR-0033 Audit Durability Model](../decisions/0033-audit-durability-model.md)
(supersedes [ADR-0016](../decisions/0016-audit-log-subsystem.md), which remains the
subsystem's context),
[ADR-0044 The Audit Write Path](../decisions/0044-audit-write-path.md),
[ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md),
[11-security.md](11-security.md) § Audit Log,
[01-architecture-standards.md](01-architecture-standards.md).

This standard defines **which operations must be audited**, **what an audit entry
contains**, **how long entries are retained**, and how each module signs up for its
own coverage. The `AuditEntry` aggregate itself is defined in
[02-domain-model.md](../architecture/02-domain-model.md) § Audit and the subsystem deep
dive is in [31-audit-subsystem.md](../architecture/31-audit-subsystem.md); this
document is the rule that prevents the audit story from drifting.

## Quick Rules

- A breach investigator or regulator should be able to answer "**who did what, to which
  resource, when, from where, with what outcome**" for every meaningful state change.
- If omitting an audit entry would embarrass a compliance officer, it **MUST** be
  audited.
- Audit entries are append-only, immutable and tenant-scoped. `audit_log` is org-scoped,
  and the row's organization is the one the ambient context announced: `AuditIntent`
  carries `TenantId` and `OrganizationId?`, both resolved by `AuditLogBehavior` at step 3
  ([ADR-0044 Amendment 3 § 2](../decisions/0044-audit-write-path.md)) — which is before
  the handler runs, so it is the request's organization and never one read back off the
  resource. Entries are queryable by tenant admins for their own tenant, and by org admins
  for their own organization
  ([ADR-0017](../decisions/0017-tenant-organization-hierarchy.md)).
- Every module owns a Resource × Operation classification table for its resources — the
  audit verb is not the permission action ([ADR-0044 § 6](../decisions/0044-audit-write-path.md)).
  The matrix is reviewed when the module is built and again when it is extended.
- Coverage is configured in the **catalog** (`AuditConfig` per-tenant override of the
  module/operation MUST/SHOULD/MAY mapping) and **enforced by the MediatR
  `AuditLogBehavior`**. Modules never call `IAuditStore` directly — the pipeline does
  it for them based on the catalog.

## Operation Types

This standard uses **OperationType** to mean *what kind of operation produced the
audit row* — distinct from `OperationClass` in [ADR-0016](../decisions/0016-audit-log-subsystem.md),
which carries the MUST/SHOULD/MAY audit-coverage tier. Both fields live on
`AuditEntry` (see [31-audit-subsystem.md](../architecture/31-audit-subsystem.md)).

| OperationType | Meaning | Default audit classification (OperationClass) |
|---------------|---------|------------------------------------------------|
| `create` | New aggregate row written | SHOULD |
| `update` | Existing aggregate mutated | MUST when status, permission, money, content-publication, or consent fields change |
| `delete` | Aggregate removed or soft-deleted | MUST |
| `read-sensitive` | Read of another user's PII, financial data, learner progress, recording, or consent state | MUST |
| `security-event` | Login, MFA challenge, role grant/revoke, permission change, tenant impersonation, token revocation, RLS bypass | MUST |
| `platform-admin` | Any operation performed by a platform admin against a tenant they are not a member of | MUST |
| `action` | A genuine in-tenant non-CRUD act that fits none of the rows above — recording start where no consent state changes is the example ADR-0016's 2026-05-19 amendment gives | SHOULD, unless § Baseline Coverage names the operation |

`platform-admin` does **not** subsume `action`.
[ADR-0016's 2026-05-19 amendment](../decisions/0016-audit-log-subsystem.md) adds `platform-admin`
**beside** `action` — "the `Action` value remains for genuine in-tenant non-CRUD
actions" — and calls the resulting seven-member enum binding. The table above is the
catalogue that enum is compared against by
[`OperationType_Enum_Matches_Catalog`](21-architecture-tests-catalogue.md#operationtype_enum_matches_catalog),
so a member missing from either side fails the build; adding an eighth type is a change
to this table first.

**The row labels above are documentation slugs, not the stored value.** What reaches
`audit_log.operation_type` — and what the audit query API accepts and returns — is the
C# enum member name: `ReadSensitive` for `read-sensitive`, `SecurityEvent` for
`security-event`. `operation_class` stores `Must` / `Should` / `May` the same way. That is
the relationship the shipped `ck_tenants_status` already has to `TenantStatus`, and it is
why neither column needs a custom value converter. `OperationType_Enum_Matches_Catalog`
compares members, not spellings. `outcome` is the one closed set that does not follow this
rule: [ADR-0044 § 5](../decisions/0044-audit-write-path.md) fixes its four values in
lowercase and puts them in the column's `CHECK`.

`read-sensitive` is not "any GET request" — it's the read paths a regulator would ask about. Examples: guardian viewing a student's grades; instructor exporting a class roster with PII; admin viewing learner email list; admin downloading a recording.

## Classification Matrix Template

Every module ships this table in its module spec under `docs/modules/<module>/audit.md` (or equivalent). The matrix is part of the module's PR; reviewers refuse merges without it. The `docs/modules/` directory was created with the first module spec — [Tenancy](../modules/tenancy/README.md), in Phase 02a Packet 6.

One row per audited resource — or one row per group of its operations that share a
class and a reason, the `Operation` cell then carrying each of their slugs separated by
`/`. That grammar is permitted, and the parser reads every slug in a cell; the three
shipped matrices — [Tenancy](../modules/tenancy/audit.md),
[Customization](../modules/customization/audit.md) and [Audit](../modules/audit/audit.md)
— use its one-slug-per-row form:

| Resource | Operation | Class | Why |
|---|---|:---:|---|
| `ResourceA` | `module.resource_a.create` | **MUST** | What the row lets a reader reconstruct |
| `ResourceA` | `module.resource_a.rename` | SHOULD | Presentational |
| `ResourceB` | `module.resource_b.archive` / `module.resource_b.soft_delete` | **MUST** | Deletes are always MUST, and two operations that share a class and a reason share a row |
| `ResourceC` | `module.resource_c.publish` `(planned)` | **MUST** | Classified ahead of the command that will raise it; the marker is what scopes the join |

Legend: **MUST** = audit entry required for every occurrence. **SHOULD** = audit by default; module may justify an opt-out per-operation. **MAY** = audit is allowed but not required. **–** = operation does not apply.

The `Operation` cell carries **catalogue slugs**, plus the `(planned)` and `(off-path)`
markers § The join below defines
([ADR-0044 § 6 and Amendment 3 § 1](../decisions/0044-audit-write-path.md)):

- The slug is `{module}.{resource}.{verb}` — `tenancy.tenant.create`,
  `customization.content_type.publish`. It borrows the *shape* of a permission key from
  [19-permissions.md](19-permissions.md) — three segments, lowercase, singular resource,
  snake_case inside a segment — and its first two segments match the permission key that
  gates the same resource. That is the part a cross-reference needs, and the only part
  required to match.
- The third segment is **not** the permission action set. Permissions close at
  `read | write | delete | admin`; audit verbs come from this matrix — `create`,
  `publish`, `revise`, `rename`, `soft_delete`, `toggle` — because a permission bounds
  what a principal *may do* and an audit operation records *what happened*. A closed set
  would record a rename and a publication as one `write`.
- No domain terms, in any segment
  ([ADR-0018](../decisions/0018-tenant-driven-customization-model.md)).

**The matrix is the prose artifact; the catalogue is the executable one.** Each module
registers its defaults in code through `IAuditCatalogSource.Describe(IAuditCatalogBuilder)`,
discovered from DI and keyed by request type, mapping one request type to one or more
`(operation, OperationType, OperationClass)` triples. Neither artifact is parsed from the
other, and
[`Every_TenantOwned_Command_HasAuditCoverage`](21-architecture-tests-catalogue.md#every_tenantowned_command_hasauditcoverage)
joins them catalogue → matrix and
[`Every_Matrix_Row_Whose_Command_Exists_Is_Registered`](21-architecture-tests-catalogue.md#every_matrix_row_whose_command_exists_is_registered)
matrix → catalogue, each reading a cell as the list of slugs it holds. Neither can see a
request type nobody registered when another request shares its slug, which is why
[`Every_Shipped_Request_Is_Registered`](21-architecture-tests-catalogue.md#every_shipped_request_is_registered)
asks the catalogue for every request type with a handler.

### The join

**It has two directions, and they have different domains**
([ADR-0044 Amendment 3 § 1](../decisions/0044-audit-write-path.md)). A single rule reading
"a matrix row with no catalogue entry fails, and a catalogue entry with no matrix row
fails" is unsatisfiable while a module classifies operations ahead of the commands that
will raise them — which this standard requires it to do.

- **Catalogue → matrix is total.** Every entry a module's `IAuditCatalogSource` registers
  has a row in that module's matrix carrying the same slug — that module's, and no other's.
  There is no exemption: shipped code auditing an operation no matrix classifies is the drift
  worth failing a build over, and adding the row always satisfies it. A `platform.*`
  operation has no module of its own, so its one row sits in the matrix of the module that
  writes it — Tenancy's, for `platform.admin_scope.enter`. Test-only request types register in their own
  fixtures rather than in a module source, and are outside this direction.
- **Matrix → catalogue binds to what exists.** A matrix row fails only when a request type
  that raises it **exists** and no catalogue entry names it. A row classified ahead of its
  command is not drift; it carries **`(planned)`** in the `Operation` cell beside its slug.
- **`(planned)` is re-checked, not an escape.** A row still marked `(planned)` whose
  command has since shipped **fails**. The marker is a claim the rule tests on every run,
  which is what stops the scoping from becoming a hole.
- **`(off-path)` is for operations that are not MediatR requests at all.** Seven rows
  carry it: Tenancy's `platform.admin_scope.enter`, `tenancy.killswitch.toggle`,
  `tenancy.entitlement.refresh` and the two tenant-assertion keys of § Baseline Coverage,
  and Audit's `audit.redaction.apply` and `audit.purge.apply`. They sit outside the
  request-type join in **both** directions and register by slug rather than by type — the
  three whose writer ships through `DeclareOffPath` today, the other four when their
  writer lands.
- **One operation, one row.** A slug is classified in exactly one row across every
  matrix. A copy is a second answer that passes every comparison the moment it agrees, so
  the matrix → catalogue direction refuses it, and the catalogue → matrix direction compares
  every row that carries a slug rather than the first.
- **A row can carry both markers**, and four do. `tenancy.killswitch.toggle` is
  `(off-path)` because every write runs inside `EnterPlatformAdminScope(reason)` rather
  than through the pipeline, and `(planned)` because Packet 9 ships the table, the policies
  and the read path but no writer —
  [Phase 03](../roadmap/phase-03-identity-admin.md) owns the toggle command, its
  Platform-scope permission and its runbook
  ([ADR-0045 Amendment 1 § 4](../decisions/0045-entitlement-and-feature-flag-socket.md)).

## Baseline Coverage (LearnStack Core Modules)

The following operations are MUST-audit across LearnStack regardless of which module owns them. Module matrices may add more rules but **cannot remove anything in this list**.

| Domain | MUST-audit operation |
|--------|----------------------|
| Identity | Membership created / removed (per `(user_id, tenant_id, organization_id)`); role assigned / revoked; permission set changed; invitation created / accepted / revoked; platform-admin tenant access; Hub operator access to a tenant resource. |
| Tenancy | Tenant created / suspended / deleted; organization created / archived; tenant setting changed; custom domain added / verified / removed; feature flag toggled; entitlement projection refresh (`tenancy.entitlement.refresh`); killswitch toggled (`tenancy.killswitch.toggle`). |
| Customization | `TenantContentType` / `TenantPageBlock` / `TenantLessonItemType` / `TenantLevelTaxonomy` / `TenantScoringRule` / `TenantCompletionRule` / `TenantCustomFieldDef` / `TenantTemplateLibrary` created / updated / deleted (schema changes are MUST; both `before` and `after` snapshots required). |
| Content / Pages | Page published / unpublished; content type schema changed; redirect created / changed. |
| Catalog / Learning | Course published / unpublished; CourseVersion published; lesson item replaced post-publish. |
| Enrollment | Enrollment created / suspended / cancelled / completed; entitlement granted / revoked. |
| Assessment | Attempt graded; score published; question added/removed from a published assessment. |
| Scheduling | LiveSession scheduled / rescheduled / cancelled; booking created / cancelled. |
| Classroom | Room opened / ended; participant joined / left (security-event); join token issued; consent state changed. |
| Recording | Recording started / stopped; recording downloaded; retention policy changed; legal hold applied / removed. |
| Billing | Order paid / refunded; subscription created / cancelled; payment provider account changed. |
| Hub contract | Inbound Hub command received (`tenancy.tenant.create_from_hub`, and the entitlement push, which is the Tenancy row's `tenancy.entitlement.refresh` — one operation, one slug); outbound usage report (`platform.usage.report`); license verification result. |
| Notifications | Template changed; outbound delivery to a recipient (SHOULD for non-PII channels; MUST for password reset / invitation / billing). |
| Integrations | External provider credential created / rotated / revoked; webhook secret rotated. |
| Security | Login failure burst beyond rate limit; MFA challenge failed; admin override of any guard; mTLS / signed-JWT / HMAC verification failure on `/api/internal/*`; cross-tenant assertion mismatch carrying a validated principal (`tenancy.tenant_assertion.reject`); anonymous assertion-mismatch burst beyond threshold (`tenancy.tenant_assertion.anonymous_burst`). |

There are **no vertical modules**. Tenant-specific extensions land as data via the
Customization aggregates and inherit the MUST-audit rules above for schema changes.

## Audit Entry Payload Contract

Every audit entry conforms to this shape; deviations require a one-line note in the module spec and a code-review approval.

```json
{
  "id": "01H...",
  "occurredAt": "2026-05-18T12:34:56.789Z",
  "tenantId": "ten_01H...",
  "organizationId": "org_01H...",
  "actor": {
    "userId": "usr_01J...",
    "membershipId": "mbr_01J...",
    "roles": ["instructor"],
    "platformAdmin": false,
    "hubOperator": false
  },
  "operation": {
    "key": "enrollment.enrollment.create",
    "module": "enrollment",
    "resource": "Enrollment",
    "resourceId": "enr_01K...",
    "operationType": "Create",
    "class": "Must"
  },
  "request": {
    "correlationId": "01H...",
    "sourceIp": "203.0.113.4",
    "userAgent": "Mozilla/5.0 ...",
    "route": "POST /v1/enrollments"
  },
  "outcome": "success",
  "before": null,
  "after": { "courseVersionId": "...", "userId": "...", "status": "active" },
  "changes": [
    { "path": "/Enrollment/status", "before": null, "after": "active" }
  ],
  "reason": null,
  "metadata": { "source": "tenant-admin-studio" }
}
```

Rules:

- `before` and `after` are **mandatory** for `update` on permission, money, content-publication, recording-policy, and consent fields. Snapshots are JSON, redacted for PII fields the module marks as `[PiiSensitive]`.
- `operation.key` is the catalogue slug of § Classification Matrix Template, and
  `operation.class` is the MUST / SHOULD / MAY tier — not a repeat of `operationType`,
  which carries one of the seven values of § Operation Types. Both render as the enum
  member name on the wire and in the column, which is why the example above reads `Create`
  and `Must` where the tables read `create` and **MUST**.
- `outcome` is one of `success`, `denied`, `failed`, `indeterminate`
  ([ADR-0044 § 5](../decisions/0044-audit-write-path.md); ADR-0016's `is_success boolean`
  is superseded, because a boolean cannot carry `denied`). `denied` is used when an
  authorisation check rejects the operation — these MUST be audited so we can detect
  probing. `indeterminate` is the commit whose server-side outcome is unknown: the
  standalone re-write carries the **same** `id` as the in-transaction attempt, and the
  pair is what a reviewer reads as one commit in doubt. `PostgresAuditStore` supplies
  `occurredAt` from `IClock` — the intent's declaration time in the transaction, a fresh
  reading for a standalone re-write — which is what keeps that pair legal under the
  composite primary key `(id, timestamp)`.
- `reason` is required when the operator is a platform admin acting on a tenant they are not a member of. It is `EnterPlatformAdminScope(reason)`'s short operator-authored slug naming the operation — never caller-supplied text — and it is surfaced in the tenant admin's audit view. A refusal's cause is not a `reason`: it is the refusal's message key, in the `error_key` column.
- `correlationId` matches the trace id in logs and the value in the Problem Details response for failures.
- `changes` is always a JSON **array** of `{ path, before, after }`, never the polymorphic
  object-or-array shape ADR-0016 described. A reader that has to branch on the shape gets
  it wrong once. `path` is **instance-qualified** — `/{EntityType}/{EntityId}` and then an
  RFC 6901 pointer into that instance's snapshot, `/TenantLevelTaxonomy/0190…/Items/b2/DisplayName`
  — because one row can carry two instances of a type: a publication records the successor
  it activated and the incumbent it retired
  ([ADR-0044 Amendment 6 § 5](../decisions/0044-audit-write-path.md)).
- **One row is about one instance.** `entity_id`, `before_state` and `after_state` describe
  the operation's subject. A handler that writes two instances of the aggregate its
  operation declares names the subject through `IAuditSubject.Designate`, and the other
  instance stays in `changes`; undesignated, the pair is refused rather than guessed
  between. An entity contained by its aggregate — a band in a taxonomy — is recorded in the
  aggregate's row, not in one of its own
  ([ADR-0044 Amendment 6 §§ 1, 4](../decisions/0044-audit-write-path.md)).
- A module that holds a PII column names it in that module's audit spec. The pipeline
  **redacts rather than strips**: a property marked `[PiiSensitive]`, or matched by the
  shipped `SensitiveTokenCatalog`, keeps its place in `before`, `after` and `changes` and
  loses its value to `SensitiveTokenCatalog.RedactedValue`, so the diff still records
  *that* it changed. Name the constant, never the string: the shipped value is
  `***REDACTED***`.
- **Not every field above is a column.** The `audit_log` DDL
  ([31-audit-subsystem.md § 7](../architecture/31-audit-subsystem.md)) carries
  `occurredAt` as `timestamp`, `resource` / `resourceId` as `entity_type` / `entity_id`,
  `sourceIp` as `ip_address`, and `operation.key` as `operation`. `actor.membershipId`,
  `actor.roles`, `actor.platformAdmin`, `actor.hubOperator` and `request.route` have no
  column of their own, and the shipped `PostgresAuditStore` writes none of them: nothing
  on the request path carries a membership, a role set or a route yet.
  [Phase 03](../roadmap/phase-03-identity-admin.md) settles where they go, with the
  identity and request-context work that gives them a source.

## Storage

- One global `audit_log` table. Phase 02a Packet 9 ships it **plain and
  unpartitioned**; monthly partitioning by `timestamp` — the column's name in
  ADR-0033's DDL — the retention job and the
  lifecycle policy of [ADR-0028](../decisions/0028-audit-log-partition-management.md)
  arrive in [Phase 11](../roadmap/phase-11-production-hardening.md). "Monthly from day
  one" was the earlier plan and [ADR-0033](../decisions/0033-audit-durability-model.md)
  changed it: PostgreSQL has no `ALTER TABLE … PARTITION BY`, so the conversion is a
  new table either way, and partitioning on day one would force every partition-key
  column into the primary key before the schema that shape has to serve exists. The
  policy shape is the same in both.
- **Table class and chain.** `audit_log` is **tenant-owned, org-scoped** and takes the
  canonical policy template from
  [Database Standards § Tenant-Owned and Organization-Scoped Tables](05-database.md)
  unmodified — one `AND`-ed policy, `ENABLE` **and** `FORCE`, explicit `WITH CHECK`, both
  `AS RESTRICTIVE` write guards. `audit_config` is **tenant-owned, tenant-wide**: it has
  no `organization_id`, so it takes the same shape with the organization half omitted and
  no restrictive guards — there is no organization to guard
  ([ADR-0044 Amendment 1](../decisions/0044-audit-write-path.md)). No document
  hand-writes either a second time. They ship in a
  fourth migration chain owned by `LearnStack.Modules.Audit.Infrastructure`'s
  `AuditDbContext`. `audit_log` carries **no** foreign key to `tenants`: the platform
  sentinel has no `tenants` row by construction, and the record of what happened to a
  tenant has to outlive the tenant. `audit_config` keeps its FK — it is live
  configuration, not history ([ADR-0044 § 9](../decisions/0044-audit-write-path.md)).
- Append-only, enforced in **three layers**, each stopping a different actor — measured
  on PostgreSQL 18.6 ([ADR-0044 § 9](../decisions/0044-audit-write-path.md)):
  - **The grant.** `learnstack_app` holds `SELECT, INSERT` on `audit_log` and nothing
    else, so an `UPDATE` or `DELETE` from the runtime role raises `42501` before any
    policy or trigger runs. On `audit_config` both roles hold `SELECT` only.
  - **The column-level grant.** `learnstack_platform` holds `SELECT, INSERT, DELETE` and
    `UPDATE (actor_email, ip_address, user_agent, before_state, after_state, changes)`,
    so an `UPDATE` touching any other column is refused by the grant, before the trigger
    runs.
  - **The trigger.** The table **owner** holds every privilege implicitly, and under
    `FORCE` the policy constrains it by **tenant** rather than by immutability —
    measured: an owner `UPDATE` returns `UPDATE 0` with no tenant announced and
    `UPDATE 1` with one. `audit_log_append_only_guard` is the only layer that binds
    `learnstack_migration`. It is not redundant with the grant — it is what stops the one
    actor the grants cannot.

  The aggregate carries its own half: `AuditEntry` inherits `Entity<TId>` **not**
  `AuditableEntity<T>` and exposes no mutators, and `IAuditStore` has no update method
  (its fourth *write* method is a different thing — see § Required Behaviours). Exactly
  **two** mutating paths exist, both owned by the Audit module, both running as
  `learnstack_platform` through the audited `EnterPlatformAdminScope(reason)` path:
  - **GDPR redaction** — an `UPDATE` that may change only `actor_email`, `ip_address`,
    `user_agent`, `before_state`, `after_state` and `changes`. The trigger rejects an
    `UPDATE` altering any other column, including `actor_user_id`, `operation`, `outcome`
    and `timestamp`.
  - **Retention purge** — a `DELETE` of rows past their tenant's and class's retention.
    It stays a row-level `DELETE` after Phase 11 partitioning: one monthly partition holds
    many tenants and several retention classes, so a partition cannot be dropped while any
    row in it is still inside its window. Dropping a whole partition is the separate
    partition-management job, and only past the platform's maximum retention
    ([ADR-0028](../decisions/0028-audit-log-partition-management.md)).

  A rule that forbade *every* `UPDATE` and `DELETE` would have made both shipped-by-design
  paths unimplementable. See
  [31-audit-subsystem.md § 7 Data model → Append-only enforcement](../architecture/31-audit-subsystem.md)
  for the trigger and § 10 for the redaction path.
- Tenant admins query their own tenant's entries through a paginated, indexed view.
  Org admins additionally filter by `organization_id`. Platform admins query across
  tenants through a separate read role. Hub operators are platform admins from this
  surface's perspective and their reads are themselves audited.
- The audit log is **never** the source of truth for application state; downstream
  consumers project from integration events, not from audit entries.
- **Snapshots are bounded, never silently truncated.** Each of `before_state`,
  `after_state` and `changes` is capped independently at `JsonValue.MaxRowBytes`
  (256 KiB). Above the cap the value becomes an explicit elision record —
  `{"_elided": true, "bytes": <n>, "sha256": "<hex>"}` — which **preserves the column's
  JSON type**: an elided `changes` is `[{"_elided": …}]`, an elided `before_state` or
  `after_state` is the object form, so a reader parses one contract either side of the
  cap. There is no external blob pointer
  ([ADR-0044 § 8](../decisions/0044-audit-write-path.md)).
- Capture pipeline — **decide → write → reconcile**
  ([ADR-0033](../decisions/0033-audit-durability-model.md),
  [ADR-0044](../decisions/0044-audit-write-path.md)): `AuditLogBehavior` (step 3)
  classifies and declares **one MUST-class intent per audited `(resource, operation)`** in
  `IAuditStateCapture`'s ordered list — `ProvisionTenantCommand` declares two;
  `AuditChangeTrackerInterceptor` snapshots each `SaveChanges` into the same buffer and
  writes nothing; `TransactionBehavior` (step 6) calls `IAuditStore.WritePendingAsync`
  immediately before `COMMIT`, inserting every pending intent as a complete row on the
  ambient transaction, and then records whether the commit succeeded; `AuditLogBehavior`
  reconciles on the way out, re-writing standalone whichever rows the transaction did not
  carry. Only the **owning** unit-of-work frame (`IUnitOfWorkScope.IsOwner`) writes,
  reports the commit boundary and clears the buffer; a joiner does none of the three,
  because a joiner that reported `Committed` would claim durability for a row nothing has
  committed and a joiner that cleared would erase the outer request's intents. Modules see
  none of this plumbing.
- The interceptor captures **every** `ChangeTracker` entry in state `Added`, `Modified` or
  `Deleted`, minus a named exclusion list — `OutboxMessage` and `IdempotencyKey`
  (machinery) and `AuditEntry` / `AuditConfig` (the audit tables themselves). The
  "`AuditableEntity<>` descendants only" predicate is **withdrawn**: it was blind to the
  seven plain classes the modules ship — `PlatformHostMapping`, `TenantLocale`,
  `TenantFeatureFlag`, `PlatformEntitlement` and `PlatformKillswitch` in Tenancy,
  `CustomizationGeneration` and `TenantLevelTaxonomyItem` in Customization — and could
  snapshot none of them. Five are MUST: the host mapping, the feature flag, the
  entitlement refresh and the killswitch toggle on rows of their own in the Tenancy
  matrix, and the taxonomy band inside its aggregate's rows. `TenantLocale` is SHOULD, and
  the generation counter is deliberately unaudited — bumped by one raw statement, so it is
  never a `ChangeTracker` entry at all. The host mapping is the one the Tenancy matrix
  singles out as mattering most. **This list is the canonical one**; a document that names
  these classes links here rather than counting them again. A module opts no entity in,
  and the interceptor constructs no row and issues no SQL.

## Retention

| Class | Default retention | Notes |
|-------|-------------------|-------|
| `security-event` | **7 years** | KVKK / GDPR / sector compliance baseline |
| `platform-admin` | **7 years** | Cross-tenant operations |
| Financial (`Billing.*`) | **7 years** | Tax and invoice retention |
| `read-sensitive` | **2 years** | Sufficient for typical investigation cycles |
| `create` / `update` / `delete` / `action` (other domains) | **2 years** | Tenant-configurable down to 6 months or up to 7 years |
| Recording-policy and consent changes | **7 years** | Lives with the recording itself if longer. **Note:** this is the retention of the *audit-log entry* about a recording policy or consent change — not the retention of the recording **file** itself. Recording files follow the per-tenant retention policy declared in [16-media-pipeline.md § Recordings](../architecture/16-media-pipeline.md) (default 30 days; tenant-configurable up to the platform cap). The two retentions are independent: the audit entry persists for compliance reconstruction even after the recording file is purged. |

A tenant cannot reduce retention below the platform-defined floor for `security-event`, `platform-admin`, or financial entries. Retention is enforced by a daily Hangfire job; deletions are batched, logged, and themselves audited (`security-event`).

**None of this schedule runs in Phase 02a.** Packet 9 ships `audit_log` with no retention
column, no purge job and no per-tenant override, and `audit_config` carries classification
overrides only. The daily `learnstack:audit:retention-purge` job, the per-tenant override
and the partition lifecycle are owned by
[Phase 11](../roadmap/phase-11-production-hardening.md)
([ADR-0044 § What we explicitly punted on](../decisions/0044-audit-write-path.md),
[ADR-0028 Amendment 2026-09-07](../decisions/0028-audit-log-partition-management.md)). The
table above is the policy those deliverables implement, not a description of today.

## Required Behaviours

- Every command that mutates a MUST-audit resource has its audit row written in the
  **same transaction** as the state change, per
  [ADR-0033](../decisions/0033-audit-durability-model.md). Atomicity comes from the
  transaction, not from sharing a `SaveChanges` call. If the row cannot be written, the
  business transaction **fails closed**: it rolls back and the caller receives
  `503 audit_unavailable` rather than committing unaudited. ADR-0016's "audit never blocks
  business logic" now applies to SHOULD/MAY-class entries only.
- **A committed audit row and a committed business row are the same event.** "Inserted"
  is not "committed": if the transaction rolls back — including on the ordinary path where
  a handler saves and then returns a failure `Result` — the audit row goes with it and is
  re-written standalone with outcome `failed`. A MUST-class operation is never left with
  no row.
- MUST-class events with **no committed business transaction** — a pipeline
  short-circuit at step 4 or 5, a non-mutating security event, a non-MediatR caller
  such as `TenantAssertionMiddleware`, and the reconcile re-write after a rollback or an
  indeterminate commit — are written standalone, in a short transaction on a connection
  outside any business transaction. That writer announces **both** session variables from
  the draft — `app.tenant_id` **and** `app.organization_id` — as its first statements:
  `audit_log` is org-scoped, so a row whose `organization_id` is non-null while the
  organization GUC is unset fails `WITH CHECK`
  ([ADR-0033 Amendment 2 § 5](../decisions/0033-audit-durability-model.md)). Three things
  are **not** on this path: a **granted** `read-sensitive` query, which rides the
  in-transaction path because `TransactionBehavior` has no request-kind gate (Amendment 2
  § 7); `EnterPlatformAdminScope`, whose row takes the fourth write method below; and a
  **validation refusal at step 1**, which writes no row at all — validation runs outside
  the audit step, so the request is refused before anything has classified it
  ([ADR-0033 Amendment 7](../decisions/0033-audit-durability-model.md)).
- **A standalone MUST-class write failure changes the response only when the operation
  would otherwise have succeeded**
  ([ADR-0033 Amendment 1](../decisions/0033-audit-durability-model.md)). A row recording
  an access being *granted* still rejects with `503 audit_unavailable`; a row recording an
  operation *already being refused* — a `denied` authorisation outcome, a rejected tenant
  assertion — keeps its own `403` / `404`, because the refusal is the security outcome and
  downgrading it hands an anonymous caller an availability signal it controls. Either way
  the failure logs at `Critical`, increments the standalone-write-failure counter and
  marks the audit health check unhealthy. **The in-transaction class is untouched:** its
  failure still rolls the operation back and answers `503`.
- Classification does not read the database on the request path. The MUST/SHOULD/MAY
  catalogue is in-process; per-tenant `audit_config` overrides are a cached projection
  whose loader sets its own tenant GUC. A failure to read a tenant override falls back to
  the catalogue — which carries the MUST floor, so nothing proceeds unaudited — and is
  logged at `Error`. It does not move the audit health check, which answers only whether a
  MUST-class row can be written ([ADR-0033 Amendment 6](../decisions/0033-audit-durability-model.md)).
  An operation the catalogue does not
  classify at all is **rejected** with `500 audit_unclassified_operation`
  ([09-error-handling.md](09-error-handling.md)): every `IRequest<Result<T>>` that reaches
  step 3 must be classified, `Off` included, there is no residual `RequestKind.Other`, and
  test-only request types register through the same builder in their fixture
  ([ADR-0044 § 6](../decisions/0044-audit-write-path.md)). A tenant override may narrow
  SHOULD/MAY coverage and may never remove baseline MUST coverage.
- **Two enums carry one distinction**
  ([ADR-0044 Amendment 3 § 4](../decisions/0044-audit-write-path.md)).
  `OperationClass { Must, Should, May }` is the tier the catalogue and the matrix
  *declare*; `AuditClassification { Off, May, Should, Must, Unclassified }` is what
  `IAuditConfigService.ClassifyAsync` *returns*, once the tenant's `audit_config` override
  and the MUST floor have been applied. `Off` is how a request that writes no row is
  registered — a builder call rather than a convention, which is what makes "classified,
  `Off` included" checkable — and `Unclassified` is the rejection above. `OperationClass`
  gains no fourth member: it is the persisted tier, and no row can hold a value meaning
  "no row".
- Fan-out to external sinks rides the outbox and is best-effort; the **local audit row**
  is not.
- Failed `denied` outcomes are audited even though no state changed.
- Background jobs that mutate MUST-audit resources receive the `actor` via job payload (operator id, or the seed of a system actor with a stable id) and write the entry under that identity.
- Integration event handlers that mutate state are treated as actors of type `system` and audited accordingly.
- Platform-bypass code paths (`IgnoreQueryFilters()` etc.) write a `security-event` entry on every invocation.
- Entering `EnterPlatformAdminScope(reason)` writes that row through
  `IAuditStore.WritePlatformScopeAsync` — a fourth **write** method, not an update
  method — on the scope's own platform-role connection and **before** the operation
  runs, so an operation that later fails is still recorded
  ([ADR-0044 § 10](../decisions/0044-audit-write-path.md)). The `DbTransaction` that
  method takes is the audit write's **own**, begun and committed on that connection before
  the scope's working transaction begins — never the handle's transaction, which a frame
  disposed without `CommitAsync` rolls back, taking the record of the bypass with it. That
  is the row [Database Standards § How `EnterPlatformAdminScope(reason)` reaches
  `learnstack_platform`](05-database.md) calls "committed on its own".
- **The row's tenant is the tenant the ambient transaction announced**, never a value off
  the request payload: `ITenantContext.TenantId` under a resolved context,
  `IProvisionsTenant.ProvisioningTenantId` for provisioning under an unresolved one, and
  `TenantId.PlatformSentinel` — the reserved `00000000-0000-7000-8000-000000000002`, not
  the nil UUID — for a platform-scope operation with no resolvable tenant. An unresolved,
  non-provisioning context writes **no row**: there is no tenant whose admin could read it,
  and [ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md) forbids inventing
  one ([ADR-0044 § 1, § 2](../decisions/0044-audit-write-path.md)). `AuditLogBehavior`
  decides it at step 3 and puts it on the `AuditIntent`, which is the only point at which
  all four cases are decidable; the store composes the row from the intent and resolves no
  tenant of its own
  ([ADR-0044 Amendment 3 § 2](../decisions/0044-audit-write-path.md)).
- **The sentinel has an enforcer, not only a constraint.** Packet 9 puts the guard where
  the value enters: `SetProvisioningTenantContextAsync` refuses
  `TenantId.PlatformSentinel` as it already refuses `Guid.Empty`, and `Tenant.Create`
  refuses it in the factory. The
  `tenants` CHECK is the backstop — a constraint cannot stop a GUC from being announced
  ([ADR-0044 Amendment 3 § 5](../decisions/0044-audit-write-path.md)).
- The outbox dispatcher attaches the actor and the correlation id to every event it
  dispatches.

## Architecture and Integration Tests

Canonical names and each rule's kind live in
[21-architecture-tests-catalogue.md § Audit](21-architecture-tests-catalogue.md), which is
authoritative for both. The two lists below split by assembly: a structural rule runs in
`LearnStack.Tests.Architecture`; the four rules that prove runtime behaviour against a
real database cannot, because that assembly has no database and a structural assertion
observes neither a rollback nor a `42501`.

**`LearnStack.Tests.Architecture` — structural, on every PR:**

- [`AuditEntry_Inherits_Entity_Not_AuditableEntity`](21-architecture-tests-catalogue.md#auditentry_inherits_entity_not_auditableentity)
  — the audit aggregate is append-only by inheritance.
- [`AuditEntry_Is_AppendOnly`](21-architecture-tests-catalogue.md#auditentry_is_appendonly)
  — no `UPDATE` or `DELETE` targets `audit_log` outside the three named sites, and
  `IAuditStore` exposes no update method.
- [`OperationType_Enum_Matches_Catalog`](21-architecture-tests-catalogue.md#operationtype_enum_matches_catalog)
  — the enum and § Operation Types carry the same seven members.
- [`Every_Shipped_Request_Is_Registered`](21-architecture-tests-catalogue.md#every_shipped_request_is_registered)
  — every request type with a handler in any backend assembly is in the catalogue, `Off`
  included.
- [`Every_TenantOwned_Command_HasAuditCoverage`](21-architecture-tests-catalogue.md#every_tenantowned_command_hasauditcoverage)
  — every catalogue entry has a matrix row with the same slug, class and type.
- [`Every_Matrix_Row_Whose_Command_Exists_Is_Registered`](21-architecture-tests-catalogue.md#every_matrix_row_whose_command_exists_is_registered)
  — every unmarked matrix row is registered, and no `(planned)` marker outlives its
  command.
- [`Every_Module_Has_An_AuditCoverage_Matrix`](21-architecture-tests-catalogue.md#every_module_has_an_auditcoverage_matrix)
  — every module spec ships `docs/modules/<module>/audit.md`, and
  [`Every_Module_With_An_Aggregate_Or_A_Request_Has_A_Matrix`](21-architecture-tests-catalogue.md#every_module_with_an_aggregate_or_a_request_has_a_matrix)
  — every module that ships an aggregate root or a request type has one.
- [`Modules_Do_Not_Write_AuditLog_Directly`](21-architecture-tests-catalogue.md#modules_do_not_write_auditlog_directly)
  — `IAuditStore` is the only sanctioned write path.
- [`No_Set_Based_Write_Bypasses_The_Audit_Capture`](21-architecture-tests-catalogue.md#no_set_based_write_bypasses_the_audit_capture)
  — no backend source writes through `ExecuteUpdate`, `ExecuteDelete` or `ExecuteSql*`,
  which leave the change tracker, and so the capture, without an entry.

**`LearnStack.Tests.Integration` — Testcontainers against a real PostgreSQL:**

- [`MustClass_Audit_Writes_Share_The_Business_Transaction`](21-architecture-tests-catalogue.md#mustclass_audit_writes_share_the_business_transaction)
  — exactly one row per declared intent, all on the business transaction.
- [`Audit_Survives_Transaction_Rollback`](21-architecture-tests-catalogue.md#audit_survives_transaction_rollback)
  — a rolled-back MUST-class command leaves zero business rows and exactly one row with
  outcome `failed`.
- [`Audit_Classification_Does_Not_Read_The_Database_On_The_Request_Path`](21-architecture-tests-catalogue.md#audit_classification_does_not_read_the_database_on_the_request_path)
  — an unreadable `audit_config` does not stop a MUST-class command, and an uncatalogued
  operation is rejected. Registered; lands in Packet 10.
- [`AuditLog_Update_Is_Column_Restricted`](21-architecture-tests-catalogue.md#auditlog_update_is_column_restricted)
  — `learnstack_app` gets `42501`; `learnstack_platform` gets the column-restricted
  redaction `UPDATE` and the purge `DELETE`, and nothing else; the table owner is stopped
  only by the trigger.

One further catalogue rule holds a claim made above and belongs to neither list:
[`AuditStateCapture_ClearedPerRequest`](21-architecture-tests-catalogue.md#auditstatecapture_clearedperrequest)
is behavioural — `Clear()` is called exactly once, by the outermost `AuditLogBehavior`,
and never by a joiner.

These drive the runtime path as `learnstack_app`, except where the privilege model is
itself the subject: `AuditLog_Update_Is_Column_Restricted` measures three layers that bind
three different roles, so it connects as `learnstack_app`, as `learnstack_platform` and as
the owner in turn. Everywhere else the rule of [05-database.md](05-database.md) holds — an
**isolation** assertion that connects as the owner or as a `BYPASSRLS` role passes even
when every policy is inert, so it proves nothing.

## Tenant-Admin Visibility

A tenant admin's audit view supports:

- Filter by actor user, module, resource, operation, operation type, outcome, date range.
- Detail view shows `before`, `after`, `request`, `reason`.
- CSV export limited by retention class (security-event entries do not export to non-admin roles).
- Search across `resource`, `route`, `correlationId`, `userId`.

Platform admins additionally see cross-tenant entries through a separate route guarded by platform-admin scope and rate-limited.

## Forbidden

- Writing a MUST-class audit entry **after** the controlling transaction commits, so that a commit failure can lose it. The standalone path is not this: it exists precisely for the cases where no business transaction committed, and it opens its own.
- Truncating `before` / `after` / `changes` snapshots silently, or writing an empty object
  in place of one that was too large. Over `JsonValue.MaxRowBytes` the value becomes the
  elision record of § Storage, in the column's own JSON type. There is **no**
  `audit_blob_id`: it exists in no DDL, names no blob store, and is struck from this
  standard ([ADR-0044 § 8](../decisions/0044-audit-write-path.md)).
- Dropping a PII property from a snapshot instead of redacting its value.
  `SensitiveTokenCatalog.RedactedValue`
  keeps the fact that the field changed; deletion loses it.
- Mutating an audit row in place from anywhere other than the two sanctioned paths (GDPR redaction, retention purge), or widening the trigger's redactable column set without an ADR. Granting `learnstack_app` `UPDATE` or `DELETE` on `audit_log`.
- Storing audit entries in the same module's read schema (the audit aggregate is global / cross-module).
- Reducing retention without an ADR.
- Skipping the matrix in a module spec.

## References

- [ADR-0044 The Audit Write Path](../decisions/0044-audit-write-path.md) — the platform
  sentinel tenant, intent multiplicity, the nesting rule, `outcome` and `timestamp`, the
  classification key, the capture predicate, redaction and size, the table class and its
  GRANTs, the fourth write method, and the two error codes.
- [ADR-0016 Audit Log Subsystem](../decisions/0016-audit-log-subsystem.md) — capture
  pipeline, retention policy, partitioning strategy; Amendment 1 fixes the seven-member
  `OperationType` enum.
- [ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md) —
  `organization_id` in audit entries.
- [ADR-0036 Tenant Resolution and Trusted Inputs](../decisions/0036-tenant-resolution-trusted-inputs.md) —
  the two tenant-assertion operations of § Baseline Coverage; Amendment 7 is where their
  snake_case spelling is decided.
- [31-audit-subsystem.md](../architecture/31-audit-subsystem.md) — subsystem deep dive
  (interceptor, state capture, MediatR behavior, retention job).
- [05-database.md](05-database.md) — the canonical RLS template, the table classes, the
  four database roles and the GRANT matrix.
- [09-error-handling.md](09-error-handling.md) — `audit_unavailable` (503) and
  `audit_unclassified_operation` (500) in the error-code table.
- [11-security.md](11-security.md) — security headers, authorization, tenant isolation,
  secrets.
- [02-domain-model.md](../architecture/02-domain-model.md) § Audit — `AuditEntry`
  aggregate definition.
- [13-identity-and-auth.md](../architecture/13-identity-and-auth.md) — Keycloak vs
  LearnStack audit split.
- [10-observability.md](10-observability.md) — correlation id propagation, redaction.
- [20-infrastructure-stack.md](20-infrastructure-stack.md) — `IAuditStore` rules.
