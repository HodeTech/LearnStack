# Module Spec — Audit

**Status:** Design stable, write path implemented. Shipped today: the two tables and
their isolation, the three append-only layers, the SharedKernel ports and value types,
`AuditDbContext`, and — in `LearnStack.Infrastructure.Audit` — `AuditStateCapture`,
`AuditChangeTrackerInterceptor`, `PostgresAuditStore` with eight write methods, the
merged `AuditCatalog` and `AuditConfigService`, registered at both composition roots and
attached to every module's `DbContext`. The pipeline is lit: `AuditLogBehavior`
classifies and parks intents at step 3 and reconciles in its `finally`,
`TransactionBehavior` flushes the MUST-class rows on the owning frame immediately before
`COMMIT`, and `PlatformAdminScope.EnterAsync` writes its own `security-event` row on the
scope's platform-role connection before the operation runs. The observable half of the
fail-closed rule ships with it — the `audit` health check, the two counters, and the
`Critical` line ([ADR-0033 Amendment 3](../../decisions/0033-audit-durability-model.md)).
`AuditingTenantAssertionRecorder` ships with them, so both
`tenancy.tenant_assertion.*` slugs are written rather than declared.
Accepted [ADR-0072](../../decisions/0072-application-owned-identity-bootstrap.md)
adds two confined global-identity seams, implemented in P03-1 Step 3 with the
common prepared owner, exact evidence and real app-role/COMMIT fault controls.
No HTTP creation or global audit-read endpoint is activated.
The entitlement socket of
[ADR-0045](../../decisions/0045-entitlement-and-feature-flag-socket.md) shipped in the same
packet. Rows marked
`(planned)` in [the coverage matrix](audit.md) belong to the phase their cell names.
The **read** side — the query API of
[§ Querying](../../architecture/31-audit-subsystem.md), its export job and its permission
registry — lands with Identity in
[Phase 03](../../roadmap/phase-03-identity-admin.md). The GDPR redaction path is
delivered: an operator act on the platform scope with one Audit-owned executor behind
`IAuditRedactor` and each module's `IUserReferenceLocator` declarations
([ADR-0108](../../decisions/0108-privileged-audit-redaction.md))
([Audit Subsystem § 10](../../architecture/31-audit-subsystem.md)). Partitioning and the
retention purge land in [Phase 11](../../roadmap/phase-11-production-hardening.md) against
written triggers ([ADR-0035](../../decisions/0035-demand-gated-infrastructure.md)).

**ADR-0046 fifth writer implemented in P02b-2 Step 2 — 2026-10-12; both review
rounds complete.** The existing
four writes remain implemented. `WriteEventTerminalAsync` adds INSERT-only access
for the confined outbox role and validates the audit draft against a restricted
witness from the actual fenced terminal UPDATE on the caller's transaction.
Terminal state and MUST audit are atomic; this is not a standalone or platform-
scope write. Tenancy owns the producer/subscriber operation declarations in
[its matrix](../tenancy/audit.md); P02b-3 Step 4 implements the subscriber writer.

The third module spec in the repository, per
[Documentation Standards § Per-Module Specifications](../../standards/13-documentation.md).

P03-6 adds the required provider-history subscriber and its catalogue source.
`ProviderEventObservationHandler` loads only the exact enqueued tenant target
through Identity's application contract, then attaches
`ProviderEventAuditEvidence` to the central MUST owner. It does not write Audit
SQL or derive tenant authority from provider content. The
[coverage matrix](audit.md) and [typed-evidence standard](../../standards/18-audit-coverage.md#retained-provider-event-observations)
own operation semantics; the phase Status owns qualification.

## Overview

Audit owns **the record of what happened**, and nothing about deciding what should.

Its subject matter is one table other modules never write directly and one that
configures how much of it they fill. The conceptual description — the pipeline, the
capture, the classification, the two durability classes — is
[Audit Subsystem](../../architecture/31-audit-subsystem.md); the deciding records are
[ADR-0033](../../decisions/0033-audit-durability-model.md) for durability and
[ADR-0044](../../decisions/0044-audit-write-path.md) for the write path. This file is
the module's own contract: what it owns, what it exposes, and what a reader has to
know before touching it.

**It owns:**

- **`audit_log`** — one row per audited operation, append-only, tenant-owned and
  organization-scoped. `AuditEntry` maps it; nothing constructs that type. Rows arrive as
  `PostgresAuditStore`'s parameterised `INSERT`, and the aggregate exists for the model
  and for the Phase 03 read API.
- **`audit_config`** — a tenant's per-`(module, operation)` override, tenant-owned and
  tenant-wide. It can narrow a SHOULD or a MAY and can never remove a MUST.
- **`AuditDbContext`** — the model those two tables are mapped by, and from
  [Phase 03](../../roadmap/phase-03-identity-admin.md) the read side of the audit admin
  API. Not a write path: nothing saves through it.

**It does not own:**

- **Deciding what is audited.** Each module declares its own coverage in code, through
  `IAuditCatalogSource.Describe(IAuditCatalogBuilder)`, and states it for a human in
  its own `audit.md`. This module holds the registry those declarations land in, not
  the declarations
  ([ADR-0044 § 6](../../decisions/0044-audit-write-path.md)).
- **Writing the row.** `AuditLogBehavior` — a cross-cutting behavior in
  `LearnStack.Application`, not a handler here — classifies and parks the intent, and
  `TransactionBehavior` flushes it immediately before `COMMIT`. A module that called
  `IAuditStore` from a handler would be writing outside the guarantee
  [ADR-0033](../../decisions/0033-audit-durability-model.md) makes.
- **The subscriber inbox or delivery ledger.** The shared transport claims its
  inbox before constructing `ProviderEventObservationHandler` and completes it
  on the same transaction as the central MUST write. The handler attaches typed
  evidence and writes neither transport storage nor Audit SQL
  ([ADR-0097](../../decisions/0097-retained-provider-events-and-tenant-audit-mirror.md#audit-subscription-and-retained-copies)).
- **The store, the capture and the catalogue.** `PostgresAuditStore`,
  `AuditStateCapture` and `AuditChangeTrackerInterceptor` live in
  `LearnStack.Infrastructure.Audit`, not here
  ([ADR-0044 § 11](../../decisions/0044-audit-write-path.md)), and the merged
  `IAuditCatalog` is built at the composition root from every module's
  `IAuditCatalogSource`. The reason is a reference edge rather than taste: the
  interceptor is attached to **every** module's `DbContext` by `AddModuleDbContext`, so
  a home for it inside this module would make every module reference the Audit module.
  `LearnStack.Infrastructure.Audit`'s csproj references `LearnStack.SharedKernel` and
  nothing else, which is what keeps that impossible. The **ports** those types implement
  live in `LearnStack.SharedKernel.Audit`, beside the value types every module's
  catalogue source names.
- **The log's own retention or redaction.** Redaction is the privileged erasure step
  and the purge is Phase 11's; both run as `learnstack_platform` through the audited
  platform scope, and both are bounded by what this packet's migration grants: a
  `DELETE`, and an `UPDATE` restricted to six columns. The redaction's one executor
  ships; the purge has no caller yet.
- **Anything a reader would call a metric.** Counters, latencies and error rates are
  [ADR-0032](../../decisions/0032-exception-handling-logging-and-observability.md)'s,
  and an audit row is not a log line: it is a record a regulator reads and a tenant
  admin can be shown.

**Audit rows cannot be corrected.** `learnstack_app` holds no `UPDATE` and no
`DELETE`; `learnstack_platform`'s `UPDATE` names six columns and no more; and the table
owner — which no grant bounds — is stopped by
`audit_log_append_only_guard`. So a wrong `tenant_id`, a wrong `operation` or a wrong
`outcome` is permanent. That is why every one of those values is decided at pipeline
step 3 from the tenant context and the catalogue, and never from the payload the row
describes.

## Entity-relationship diagram

`AuditEntry` references a tenant, an organization and a user **by id and by nothing
else** — no navigation, no foreign key on the first, and a deliberately orphanable
`actor_user_id`. The dashed edges below are id references across a module boundary,
which is the only kind this schema has.

```mermaid
erDiagram
    AUDIT_LOG {
        uuid id PK "app-side uuidv7, DEFAULT as backstop"
        timestamptz timestamp PK "from IClock; composite key with id"
        uuid tenant_id "no FK — outlives its tenant"
        uuid organization_id "null = tenant-wide"
        uuid actor_user_id "never redacted"
        text module
        text operation "{module}.{resource}.{verb}"
        text operation_type "closed set"
        text operation_class "Must | Should | May"
        text outcome "success|denied|failed|indeterminate"
        jsonb before_state
        jsonb after_state
        jsonb changes "always an array"
        inet ip_address "redactable"
    }

    AUDIT_CONFIG {
        uuid id PK
        uuid tenant_id FK "RESTRICT — the schema's only cross-chain FK"
        text module
        text operation
        boolean is_enabled "narrows SHOULD/MAY, never a MUST"
    }

    TENANTS {
        uuid id PK "ck_tenants_not_platform_sentinel"
    }

    ORGANIZATIONS {
        uuid id PK
    }

    USERS {
        uuid id PK "Phase 03"
    }

    TENANTS ||--o{ AUDIT_CONFIG : "configures"
    TENANTS ||..o{ AUDIT_LOG : "by id only"
    ORGANIZATIONS ||..o{ AUDIT_LOG : "by id only"
    USERS ||..o{ AUDIT_LOG : "by id, orphanable"
```

Text fallback, for a renderer without Mermaid:

- `audit_log` — the append-only record. Composite primary key `(id, timestamp)`. Carries
  `tenant_id` with **no** foreign key, `organization_id` nullable, `actor_user_id`
  nullable and never redacted.
- `audit_config` — per-tenant overrides, keyed `(tenant_id, module, operation)`. Its
  foreign key to `tenants` is the schema's only one crossing two migration chains, which
  is why `make migrate` applies Tenancy first.
- `tenants`, `organizations`, `users` — other modules' tables. Only `audit_config` has a
  real edge; the rest are id references a query joins on and a constraint does not.

**Why `audit_log` has no foreign key**, restated here because a reader of an ER diagram
looks for one: the platform-scope row carries `TenantId.PlatformSentinel`, which by
[ADR-0044 § 1](../../decisions/0044-audit-write-path.md) has no `tenants` row by
construction, and a constraint admits no exception — not for `learnstack_platform` and
not under `BYPASSRLS`. Independently, the record of what happened to a tenant has to
outlive the tenant.

## State diagrams

Neither table's rows have a lifecycle: an `audit_log` row is written once and never
changes state, and an `audit_config` row is a boolean. What does have one is the
**intent** — the parked declaration `AuditLogBehavior` creates at step 3 and
`TransactionBehavior` resolves, which is the mechanism
[ADR-0033](../../decisions/0033-audit-durability-model.md) exists to define.

```mermaid
stateDiagram-v2
    [*] --> Declared: AuditLogBehavior classifies (step 3)
    Declared --> WrittenInTransaction: store INSERTs before COMMIT
    Declared --> Skipped: no transaction, nothing to join
    WrittenInTransaction --> Committed: COMMIT returned
    WrittenInTransaction --> RolledBack: transaction rolled back
    WrittenInTransaction --> Indeterminate: COMMIT outcome unknown
    RolledBack --> Rewritten: standalone write, fresh timestamp
    Indeterminate --> Rewritten: standalone write, outcome 'indeterminate'
    Committed --> [*]
    Skipped --> [*]
    Rewritten --> [*]
```

Text fallback:

1. **Declared** — an intent is parked per audited `(resource, operation)`, not per
   request, so one command can declare two ([ADR-0044 § 3](../../decisions/0044-audit-write-path.md)).
2. **WrittenInTransaction** — the row is inserted on the business transaction, immediately
   before `COMMIT`, while `app.tenant_id` is set and the policy accepts it.
3. **Committed** — the ordinary end. The row and the state change are one transaction.
4. **RolledBack** — the business write failed. The row is re-written standalone, so the
   attempt survives; the re-write takes a fresh clock reading.
5. **Indeterminate** — `COMMIT` threw in a way that leaves the server-side outcome
   unknown. A second row is written with `outcome = 'indeterminate'` and the **same**
   `AuditEntryId`; the composite primary key makes the pair legal, and a `23505` on the
   re-write is positive evidence the first row is durable
   ([ADR-0044 § 5](../../decisions/0044-audit-write-path.md)).
6. **Skipped** — no transaction was opened, so there is nothing to join. The standalone
   path writes the row itself.

Only the **owning** unit-of-work frame moves an intent out of `Declared`. A joiner that
called `MarkCommitted` would mark a row nothing has committed, and if the outer
transaction then rolled back the reconcile step would do nothing and the MUST row would
be lost — which is the failure ADR-0033 exists to prevent.

## Sequence diagrams

**The primary write use case**: a MUST-class operation on the ordinary request path.
The row is written by the pipeline, never by the handler, and it commits with the state
change or not at all.

```mermaid
sequenceDiagram
    participant C as Client
    participant P as MediatR pipeline
    participant A as AuditLogBehavior
    participant T as TransactionBehavior
    participant H as Handler
    participant D as PostgreSQL

    C->>P: command
    P->>A: step 3 — classify
    A->>A: catalogue + tenant AuditConfig override, MUST floor re-applied
    A->>A: park AuditIntent (id, tenant, organization, declared at)
    A->>T: step 6 — open transaction
    T->>D: BEGIN, then SET LOCAL app.tenant_id + app.organization_id
    T->>H: handle
    H->>D: business write
    H-->>T: Result
    T->>D: INSERT INTO audit_log  (still inside the transaction)
    T->>D: COMMIT
    T-->>A: Committed
    A-->>C: Result
```

Text fallback: classify and park at step 3; open the transaction and announce the tenant
at step 6; the handler writes; the audit row is inserted on the same transaction
immediately before `COMMIT`; the commit makes both durable together. If `COMMIT` fails
the intent moves to `RolledBack` or `Indeterminate` and the standalone writer re-writes
the row on its own short transaction, announcing both session variables from the draft.

**The module publishes no integration event**, so there is no second diagram here. See
[§ Integration-event catalogue](#integration-event-catalogue) for its required
provider-history subscription.

## Component diagram

```mermaid
graph TD
    subgraph SharedKernel
        PORTS["LearnStack.SharedKernel.Audit<br/>IAuditStore · IAuditCatalog · IAuditStateCapture<br/>AuditEntryDraft · AuditIntent"]
    end

    subgraph Application
        BEH["AuditLogBehavior (step 3)"]
        TX["TransactionBehavior (step 6)"]
    end

    subgraph InfraAudit["LearnStack.Infrastructure.Audit"]
        STORE["PostgresAuditStore"]
        CAP["AuditChangeTrackerInterceptor"]
        CAT["AuditCatalog (merged at the composition root)"]
    end

    subgraph AuditModule["Modules.Audit"]
        AGG["AuditEntry · AuditConfig"]
        CTX["AuditDbContext (model + Phase 03 reads)"]
    end

    subgraph OtherModules["Every other module"]
        SRC["IAuditCatalogSource.Describe"]
    end

    DB[("PostgreSQL<br/>audit_log · audit_config")]
    SCOPE["EnterPlatformAdminScope(reason)"]

    BEH --> PORTS
    TX --> PORTS
    SRC --> PORTS
    STORE --> PORTS
    CAP --> PORTS
    CAT --> PORTS
    STORE --> DB
    CTX --> DB
    CTX --> AGG
    SCOPE --> PORTS
```

Text fallback: every arrow points at the ports in `LearnStack.SharedKernel.Audit`, and
no arrow points at this module's assembly. That is the whole shape.
`AuditLogBehavior` and `TransactionBehavior` live in `LearnStack.Application` and reach
the store through `IAuditStore`; each module's `IAuditCatalogSource` is discovered from
DI; `PostgresAuditStore`, `AuditChangeTrackerInterceptor` and the merged catalogue live in
`LearnStack.Infrastructure.Audit`, whose csproj references `LearnStack.SharedKernel` and
nothing else ([ADR-0044 § 11](../../decisions/0044-audit-write-path.md)). This **module**
holds the two aggregates and the context that maps them, and that split is not
bookkeeping: the interceptor is attached to every module's `DbContext`, so the assembly
holding it must be one every module may reference. Mapping `AuditEntry` into every
module's context would need SharedKernel to reference the Audit module, which already
references SharedKernel — the circular reference ADR-0033 rejects.

**The fourth external caller is not a behavior.** `EnterPlatformAdminScope(reason)`
writes its own row through `IAuditStore.WritePlatformScopeAsync` on the scope's own
platform-role connection, before the operation runs
([ADR-0044 § 10](../../decisions/0044-audit-write-path.md)). It is a service method
rather than a MediatR request, so it has no request type to key a catalogue entry on and
its entry is registered by slug.

## Integration-event catalogue

**This module publishes none, and that is a decision rather than a gap.**

An audit row that arrived at a consumer as an integration event would be a second copy
of the record with its own delivery semantics — retried, reordered, and durable
independently of the transaction that produced it. The guarantee
[ADR-0033](../../decisions/0033-audit-durability-model.md) makes is about one row in one
transaction; a published mirror of it would be a second answer to "what happened", and
the two would disagree the first time a delivery failed.

The Hub-side stream that
[Audit Subsystem § 12](../../architecture/31-audit-subsystem.md) describes is **not** this
table and not a read of it. The Hub keeps its own record of its operators' actions, in its
own database (`hub_audit_log`); the two streams share nothing but `correlation_id`, which is
how an inquiry spanning both joins them
([Phase 09](../../roadmap/phase-09-billing-integrations-analytics.md) builds that query).
Nothing in [ADR-0034](../../decisions/0034-hub-contract-surface-invariant.md)'s enumerated
surface reads `audit_log` from the operator plane, and one that did would be a new crossing
— decided by ADR in both repositories, like every other.

This module consumes the retained provider-history notification:

| Event | Topic | Subscription | Handler |
|---|---|---|---|
| `UserProviderEventObservedIntegrationEventV1` | `learnstack.identity.user` | `audit.provider-event-mirror.v1` (required Active) | `ProviderEventObservationHandler` |

The handler reads the exact retained target through Identity's application
contract and attaches [`ProviderEventAuditEvidence`](../../glossary.md#audit).
The successful mirror's MUST row and transport-owned completed inbox commit
together. Audit owns no inbox table or read-model projection. Source occurrence
and result remain separate from the mirror's processing time and outcome
([ADR-0097](../../decisions/0097-retained-provider-events-and-tenant-audit-mirror.md#audit-subscription-and-retained-copies)).

Accepted [ADR-0057](../../decisions/0057-transactional-event-production-and-completion.md)
uses completed provisioning to initialize Customization defaults; Audit is not
its mirror consumer. Existing source MUST rows remain on the source transaction.
The reusable execution coordinator supplies non-MediatR classification/entry,
owner write/boundary reporting and outermost reconciliation under ADR-0044/0033.
P02b-1 proves a controlled direct owner; P02b-3 Steps 3–5 implement production
delivery and per-effect audit under ADR-0046's accepted G1 contract. Business audit
retains
the existing coordinator and durability model; explicit terminal audit uses the
separate fifth seam described above.

## Permission matrix

[permissions.md](permissions.md) — seven keys, six Tenant-scope and one Platform-scope, all forward declarations. The registry
that makes them enforceable lands with Identity in
[Phase 03](../../roadmap/phase-03-identity-admin.md).

## Audit coverage matrix

[audit.md](audit.md). The module that owns the log is audited like any other: reading
someone's audit trail is itself a `ReadSensitive` operation, and redacting or purging a
row is a `security-event` — the class
[Audit Coverage § Required Behaviours](../../standards/18-audit-coverage.md) puts every
platform-bypass code path on, and the one a compliance query filters for.

## Performance budget

The budget that matters here is on the **write** path, because it is paid by every
audited request rather than by a reader.

| Operation | Target | Why that number |
|---|---|---|
| Classify (step 3) | < 0.1 ms | An in-memory dictionary lookup plus a cached override. Never a database read: at step 3 no transaction is open and `app.tenant_id` is unset, so a lookup against `audit_config` would return **zero rows silently** — which reads exactly like "this tenant has no overrides". |
| Capture (interceptor) | < 1 ms per `SaveChanges` | Walks `ChangeTracker` entries already materialised for the save. Bounded by the size cap on the three `jsonb` columns, not by entity count. |
| In-transaction `INSERT` | < 1 ms | One parameterised statement on a connection that is already open and already has its tenant announced. It adds no round trip of its own. |
| Standalone write | < 5 ms | Its own connection, its own `BEGIN`/`COMMIT`, and two `set_config` calls. Off the happy path — it runs for `denied`, for a rolled-back business write, and for the commit-in-doubt re-write. |
| Read, tenant's own log, newest first | < 50 ms p95 | `ix_audit_log_tenant_id_timestamp` answers it as an index scan. The budget is Phase 03's to hold; it is here because the index that serves it ships now. |
| Data-request selection, count and actor/entity counts (P03-10) | Bounded by the subject's own rows, under a 15 s statement budget | Unions of equality branches on `ix_audit_log_actor_user_id_timestamp` and `ix_audit_log_tenant_id_entity_type_entity_id_timestamp`. Measured at 1.9 million rows of one tenant: about 2 ms each, against 140–235 ms for the single `OR` predicate they replaced, whose parallel scan grows with the tenant. |
| Data-request metadata count (P03-10) | Bounded by `MetadataScanWindow` (500,000 keyed rows) per branch of the reach | The causal-actor and affected-user references cannot be index conditions under the row policy (JSON operators are not leakproof), so each branch of the reach, the group's organization and the tenant-level rows, is a newest-first seek cut at the window, on `ix_audit_log_tenant_id_organization_id_person_reference` and `ix_audit_log_tenant_id_tenant_level_person_reference`; `audit_metadata_reference` is a lower bound past a window. Measured at 2.3 million rows, 2,000,000 of them keyed rows of one organization: about 1 ms for an organization with no keyed rows of its own, against 2.2–3.4 s (2,000,000 rows read and removed by the row policy) for the single tenant-keyed window it replaced; 1.2–1.6 s for the organization whose branch fills a window, as before; about 1 s for a tenant-level branch of 601,000 keyed rows, stopping at the window with no sort. |
| Redaction selection (P03-10, [ADR-0108](../../decisions/0108-privileged-audit-redaction.md)) | One statement per batch of at most 100,000 of the tenant's rows or 64 MiB of decompressed payload in the reach, newest first, each under a 30 s statement budget; the act's time grows with the tenant's decompressed payload | The actor and entity branches are index-served (`ix_audit_log_actor_user_id_timestamp`; `ix_audit_log_tenant_id_entity_type_entity_id_timestamp` by a skip scan over the entity types). A payload or metadata reference has no index, so each batch is a range of the key `("timestamp", id)` along `ix_audit_log_tenant_id_timestamp`, whose payload columns are walked once for the identifiers and rewritten only when that filter keeps the row. A boundary statement ends the batch at the row cap or where the decompressed size of the payload columns of its rows in the reach reaches the byte budget, because the selection and the rewrite work in proportion to decompressed bytes, which a compressible document can make tens of times its stored size: an uncompressed value is sized by `pg_column_size` without detoasting, a compressed one is decompressed once to be measured, and a row outside the reach counts nothing. Measured on PostgreSQL 18.4 with 1 GB of shared buffers, before and after the budget counted decompressed bytes. At 1.5 million rows of one tenant (1.5 GB stored, 9.8 GB decompressed): 155 batches instead of 24, every statement at most 0.34 s; 12.1–12.9 s for a whole-tenant act instead of 7.1–8.5 s, 3.2 s for an organization group's instead of 1.3–1.5 s, 4.2–5.2 s for the tenant-wide group's instead of 2.7–3.3 s; the difference is the boundary's decompression. At 30,000 rows of 250 KB documents that compress to 3 KB (88 MB stored, 7.2 GB decompressed): 112 batches, every statement at most 0.04 s, 3.0–4.0 s for a whole-tenant act; counted by stored size, two batches of 3.7 GB each took up to 1.15 s per statement, unbounded on a slower host. At 10,000 documents of 2,000 keys (78 MB stored, 660 MB decompressed): 10 batches of at most 0.12 s instead of 2 of 0.9 s. At 150,000 rows each carrying 100 KiB of incompressible payload (14 GB stored, which is also its decompressed size, so its batches did not change): 230 batches of about 650 rows, every statement at most 2.0 s, 215–243 s for a whole-tenant act; batches of 100,000 rows put about 10 GB in the first statement, which ran past its 30 s budget on every attempt. JIT is off for these statements: with it a batch took 1.6–3.1 s and a whole-tenant act 41–42 s. |
| Redaction rewrite (P03-10) | At most 1,000 selected rows of one batch per statement | Each statement locks and rewrites its rows by primary key, touching only the six granted columns, and keeps the stored value of a payload column the redaction leaves equal, so an unchanged large payload is not recompressed and logged again: 277 rows in 31–35 ms, the re-run rewrote nothing in under 10 ms, no statement took more than 27 ms on the 14 GB tenant, and decompressed payload was rewritten at 17–165 MB a second. |

Two budget-shaped facts that are not latency:

- **`audit_log` grows without bound until Phase 11.** There is no partitioning and no
  retention purge in this packet, deliberately — audit correctness cannot be added later
  and audit scale can, and the platform has no rows yet to scale
  ([ADR-0028's 2026-09-07 amendment](../../decisions/0028-audit-log-partition-management.md)).
- **Seven indexes, still not the template's `(tenant_id, organization_id)`.** The
  canonical organization-scoped template carries an index on
  `(tenant_id, organization_id)`; this table does not take it, because it is a write
  cost paid on every row for a predicate arm the shipped readers do not take. The
  reasoning is in [Audit Subsystem § 7](../../architecture/31-audit-subsystem.md), and
  Phase 03's admin API is where to revisit it against a measured plan. P03-10 added the
  three data-request indexes measured above: the entity index takes an entry for every
  row, and a row carrying a causal-actor or affected-user key takes one entry in one of
  the two person-reference indexes, split by whether it has an organization.

## Risks and open questions

- **A wrong row is permanent.** Append-only has no correction path by design, and the
  six redactable columns are for erasure rather than for repair. The mitigation is
  entirely upstream: `tenant_id`, `organization_id`, `operation` and `outcome` are
  decided at step 3 from the tenant context and the catalogue, never from the request
  payload, and the isolation suite writes each of them under a foreign tenant to prove
  the policy refuses it.
- **The owner can disable a trigger.** `ALTER TABLE audit_log DISABLE TRIGGER` is not
  stopped by anything inside the database. The bound is operational: `learnstack_migration`
  is a migration credential, and nothing the platform runs day to day holds it. Stated
  rather than mitigated, because a mitigation that does not exist is worse than a
  documented limit.
- **`audit_config` ships with no writer.** Both runtime roles hold `SELECT` and nothing
  more, so the table is empty until Phase 06's Studio editor lands with the permission
  Phase 03 registers. An absent override reads as "no overrides", which is the safe
  answer, so nothing is lost by the gap — but the override branch is exercised by a test
  that seeds the row as the migration role rather than by any shipped caller.
- **Retention is unowned until Phase 11.** The purge and the partition-management job
  sit behind the same trigger — measured `audit_log` growth. GDPR redaction is not on it:
  an erasure triggers it, and its executor ships with Phase 03's erasure. The grants all
  three need are already written, which is the part that could not be added later without
  a migration on a table nobody may rewrite.
- **The read API's shape is Phase 03's to settle.** The four permission keys and the
  endpoint list in [Audit Subsystem § 11](../../architecture/31-audit-subsystem.md) are
  forward declarations; the export job's storage, its expiry and its own audit row are
  open.
