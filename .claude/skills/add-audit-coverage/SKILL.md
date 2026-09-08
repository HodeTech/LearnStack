---
name: add-audit-coverage
description: >
  Extend a module's audit-coverage matrix (`docs/modules/<module>/audit.md`) and
  register the same operations in the module's in-code catalogue
  (`IAuditCatalogSource`) so `AuditLogBehavior` writes the right entries
  automatically. USE FOR: a new MUST/SHOULD/MAY classification, a new operation that
  should be audited, a `before/after` snapshot rule for a sensitive column, a
  test-only request type that has to be classified to reach the handler. DO NOT USE
  FOR: writing to `audit_log` directly (forbidden — use `IAuditStore` via the
  pipeline), bypassing audit for performance (use `AuditConfig` per-tenant overrides
  instead), or putting domain-specific terms in audit messages (forbidden).
---

# Adding audit coverage

## Purpose

Wire a new operation into LearnStack's central audit pipeline
([ADR-0033](../../../docs/decisions/0033-audit-durability-model.md) — the binding
durability contract;
[ADR-0044](../../../docs/decisions/0044-audit-write-path.md) — the write path:
identity, multiplicity, capture and classification;
[ADR-0016](../../../docs/decisions/0016-audit-log-subsystem.md) — superseded, read only
for subsystem context;
[31-audit-subsystem.md](../../../docs/architecture/31-audit-subsystem.md),
[18-audit-coverage.md](../../../docs/standards/18-audit-coverage.md)) by extending the
module's matrix and the module's in-code catalogue. Modules never write `audit_log`
directly; the catalogue plus the pipeline do.

The pipeline is **decide → write → reconcile**: `AuditLogBehavior` classifies at step 3
and parks one intent per audited `(resource, operation)`, `TransactionBehavior` writes
every parked intent on the business transaction immediately before `COMMIT`, and
`AuditLogBehavior` re-writes standalone whichever ones that transaction did not carry.
You do not touch any of it — but the classification you pick decides which of those
paths a given operation takes.

**Intents are plural, and only the owning frame flushes them**
([ADR-0044 § 3–4](../../../docs/decisions/0044-audit-write-path.md)). One request may
declare several: `ProvisionTenantCommand` declares `tenancy.tenant.create` **and**
`tenancy.organization.create`, both MUST, both on one transaction. A nested `ISender`
dispatch joins the ambient unit of work rather than owning it, and a joiner writes
nothing, signals nothing, and never calls `Clear()` — the owning frame drains the whole
scope.

## When to use

- A new command / query in the module should be MUST or SHOULD audited.
- A previously SHOULD-audited operation is being promoted to MUST.
- A column previously not snapshotted should now have `before` / `after` captured
  on update.
- A new module is shipping its first audit matrix.
- A test-only request type needs a registration. Every `IRequest<Result<T>>` that
  reaches step 3 must be classified; an unregistered one is rejected with
  `audit_unclassified_operation` (500), fixtures included
  ([ADR-0044 § 6](../../../docs/decisions/0044-audit-write-path.md)).

## When not to use

- Adding an audit row from a custom code path (forbidden; the pipeline does it).
- "Audit everything." Operations of class `read` (non-sensitive) are MAY by default;
  blanket auditing creates noise.
- Domain-specific operation names. Use the generic slug
  (`enrollment.enrollment.create`), not `english.placement.scored`.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Module | Yes | Owns the matrix file and the `IAuditCatalogSource`. |
| Resource | Yes | Aggregate or sub-resource name. |
| Operation slug | Yes | `{module}.{resource}.{verb}` — the catalogue key and the matrix's `Operation` cell. |
| `OperationType` | Yes | `Create` / `Update` / `Delete` / `ReadSensitive` / `SecurityEvent` / `PlatformAdmin` / `Action`. |
| `OperationClass` | Yes | `Must` / `Should` / `May` — the tier the catalogue and the matrix **declare**. A request that writes no row is registered `Off` instead (Step 3). |
| Before/after snapshot? | If `Update` on sensitive fields | Yes/no. |
| PII fields | If applicable | The properties to mark `[PiiSensitive]`. |

## Workflow

### Step 1: Open the matrix

`docs/modules/<module>/audit.md`. Each module owns one matrix, and each row carries an
**`Operation` column holding the catalogue's slug**
([ADR-0044 § 6](../../../docs/decisions/0044-audit-write-path.md)). The matrix is the
human-readable artifact and the in-code catalogue is the executable one; neither is
parsed from the other, and `Every_TenantOwned_Command_HasAuditCoverage` joins them.

**The join has two directions and they have different domains**
([ADR-0044 Amendment 3 § 1](../../../docs/decisions/0044-audit-write-path.md#amendment-3--what-the-join-binds-to-and-the-types-the-ports-carry-2026-09-08)):

- **Catalogue → matrix, total.** Every entry a *module's* `IAuditCatalogSource`
  registers has a matrix row carrying the same slug. No exemption. Test-only request
  types register in their fixtures rather than in a module source and are outside this
  direction.
- **Matrix → catalogue, scoped to what exists.** A matrix row fails only when a request
  type that raises it **exists** and no catalogue entry names it. A row classified ahead
  of its command is the classification the standard asks for before the command ships,
  not drift.
- **`(planned)`, beside the slug in the `Operation` cell**, marks such a row — and the
  marker is a claim the rule re-checks on every run, not an escape: a `(planned)` row
  whose command has since shipped **fails**.
- **`(off-path)`** marks an operation that is not a MediatR request at all. Those rows
  sit outside the request-type join in both directions and their catalogue entries are
  registered by slug rather than by type. Today: `platform.admin_scope.enter`,
  `tenancy.killswitch.toggle`, `tenancy.entitlement.refresh`, and the two ADR-0036 parks
  on Packet 9 — `tenancy.tenant_assertion.reject` and
  `tenancy.tenant_assertion.anonymous_burst`.
- **A row carries both markers where both are true** — off the request path *and* ahead
  of the code that will write it.

[Audit Coverage § The join](../../../docs/standards/18-audit-coverage.md) is the standard
that carries this; read it before inventing a third marker.

The two shipped matrices are the model — [Tenancy](../../../docs/modules/tenancy/audit.md)
and [Customization](../../../docs/modules/customization/audit.md):

```markdown
| Resource | Operation | Class | Why |
|---|---|---|---|
| `Enrollment` | `enrollment.enrollment.create` | **MUST** | Grants access to paid content |
| `Enrollment` | `enrollment.enrollment.suspend` | **MUST** | Withdraws it again |
| `Enrollment` | `enrollment.enrollment.cancel` | **SHOULD** | Reversible, and the learner asked for it |
| `Cohort` | `enrollment.cohort.create` | **MUST** | Raised alongside the enrollment, on one transaction |
| `Cohort` | `enrollment.cohort.delete` | **MUST** | Deletes are always MUST |
| `Cohort` | `enrollment.cohort.archive` `(planned)` | **MUST** | No command raises it yet |
```

Every unmarked row above has a catalogue entry in Step 3's snippet, and the marked one
has none — which is the whole of the join.

Legend:

- **MUST** — every occurrence is audited.
- **SHOULD** — audited by default; opt-out requires a code comment + justification.
- **MAY** — allowed but not required.
- **–** — operation doesn't apply to the resource.
- **`(planned)`** — classification ahead of the command that will raise it. Drop the
  marker in the same commit that lands the command and its catalogue entry.
- **`(off-path)`** — not a MediatR request; catalogued by slug.

Keep the format [18-audit-coverage.md](../../../docs/standards/18-audit-coverage.md)
carries; whatever the column layout, the `Operation` cell holds the slug verbatim,
followed by its marker where it has one.

### Step 1b: Name the operation

The catalogue key is `(module, operation)`, and `operation` is the dotted slug
**`{module}.{resource}.{verb}`** — `tenancy.tenant.create`,
`customization.content_type.publish`, `enrollment.cohort.delete`.

- It borrows the **shape** of a permission key
  ([19-permissions.md](../../../docs/standards/19-permissions.md)): three segments,
  lowercase, singular resource, snake_case inside a segment.
- Its first two segments **match** the permission key that gates the same resource.
  That is the part a cross-reference needs, and the only part required to match.
- The third segment is **not** the permission action set, deliberately. Permissions
  close at `read | write | delete | admin`; audit verbs come from this matrix —
  `create`, `publish`, `revise`, `rename`, `soft_delete`, `toggle`. A closed action set
  would record a rename and a publication as one `write`, and publication is the row
  the Customization matrix classifies MUST for exactly that reason: a permission bounds
  what a principal *may do*, an audit operation records *what happened*.
- No domain terms, in any segment.

### Step 2: Pick the `OperationType` and the classification

Two independent fields, and the corpus keeps them apart:
`OperationType` is *what kind of operation produced the row*, `OperationClass` is the
MUST / SHOULD / MAY coverage tier. Use the defaults in
[18-audit-coverage.md § Operation Types](../../../docs/standards/18-audit-coverage.md):

- **`Create`**: SHOULD by default. MUST when permission, money, content-publication,
  or consent state changes.
- **`Update`**: MUST when status, permission, money, content-publication, recording
  policy, or consent fields change.
- **`Delete`**: MUST. Always.
- **`ReadSensitive`**: MUST when reading another user's PII, financials, learner
  progress, recordings, consent state.
- **`SecurityEvent`**: MUST. Login bursts, MFA failures, admin override of any
  guard, mTLS/JWT/HMAC verification failure.
- **`PlatformAdmin`**: MUST when an operator acts on a tenant they're not a member
  of. Required `reason` field. It does **not** subsume `Action`.
- **`Action`**: SHOULD, unless § Baseline Coverage names the operation. A genuine
  in-tenant non-CRUD act that fits none of the rows above — recording start where no
  consent state changes is the example
  [ADR-0016's 2026-05-19 amendment](../../../docs/decisions/0016-audit-log-subsystem.md) gives.

`OperationType_Enum_Matches_Catalog` asserts the seven-member enum and that standard's
list carry the same members, so an eighth type is a standards change first.

`Action` is the catch-all, so nothing falls outside the list — but reaching for it is a
signal. When it is tempting because the operation does two things at once, the
*resource* is usually what needs splitting — see
[add-permission § Step 1](../add-permission/SKILL.md).

**Two enums, one distinction** ([ADR-0044 Amendment 3
§ 4](../../../docs/decisions/0044-audit-write-path.md#amendment-3--what-the-join-binds-to-and-the-types-the-ports-carry-2026-09-08)).
`OperationClass { Must, Should, May }` is what the catalogue and the matrix *declare* —
the field this step picks. `AuditClassification { Off, May, Should, Must, Unclassified }`
is what `IAuditConfigService.ClassifyAsync` *returns*, after the tenant's `audit_config`
override and the MUST floor: `Off` is a request that writes no row, `Unclassified` is
the rejection. Do not reach for `Off` here — it is a registration (Step 3), not a tier.

### Step 2b: What a MUST classification now costs

Under [ADR-0033](../../../docs/decisions/0033-audit-durability-model.md) the class is
**load-bearing, not documentary**. Before you write MUST, know what you are buying:

- The row is inserted on the **same transaction** as the business write, immediately
  before `COMMIT`, while `app.tenant_id` is set — so it commits with the state change or
  not at all, and Row Level Security accepts it. Its `tenant_id` is the tenant the
  transaction **announced**, never a value off the request payload
  ([ADR-0044 § 2](../../../docs/decisions/0044-audit-write-path.md)).
  `AuditLogBehavior` resolves that tenant at step 3 and puts it on the intent —
  `AuditIntent` carries `TenantId` and `OrganizationId?` — so the store composes the row
  from the intent and never resolves a tenant itself
  ([ADR-0044 Amendment 3 § 2](../../../docs/decisions/0044-audit-write-path.md#amendment-3--what-the-join-binds-to-and-the-types-the-ports-carry-2026-09-08)).
- If the transaction rolls back, the row is **re-written standalone** with outcome
  `failed`. A MUST-class operation is never left with no row, including on the ordinary
  path where a handler saves and then returns `Result.Fail(...)`.
- The operation **fails closed — with one narrowing**. An in-transaction MUST row that
  cannot be written rejects the command with `503 audit_unavailable`, and so does a
  standalone row recording an access that is being **granted**. A standalone row
  recording an operation that is **already being refused** — a `denied` outcome, a
  rejected tenant assertion — keeps its own 403 / 404: it logs at `Critical`, counts,
  and marks the audit health check unhealthy, but it does not hand an anonymous caller a
  503 it can provoke
  ([ADR-0033 Amendment 1](../../../docs/decisions/0033-audit-durability-model.md)).
- A granted MUST-class **read-sensitive query rides the in-transaction path** like
  everything else. `TransactionBehavior` has no request-kind gate — a read needs the
  `SET LOCAL` as much as a write does — so classifying a query MUST costs a synchronous
  write before the result is returned, on the business transaction
  ([ADR-0033 Amendment 2 § 7](../../../docs/decisions/0033-audit-durability-model.md)).
  `WriteStandaloneAsync` is reached only by a short-circuit at step 1, 4 or 5, by a
  non-MediatR caller, and by the reconcile step.
- A tenant `AuditConfig` override can narrow SHOULD/MAY. It can never remove baseline
  MUST coverage; the catalogue re-applies the MUST floor after the override. A **failed
  read** of that override does not reject the request — classification falls back to the
  in-process catalogue, which carries the same MUST floor, so a cache outage does not
  deny every request platform-wide.
- SHOULD/MAY stays best-effort. Choosing it is choosing a **documented accepted loss** —
  write that loss into the module's matrix rather than leaving it implied.

So MUST is an availability trade as well as a compliance one. Classify deliberately.

### Step 3: Register in the module's catalogue — in code

**The catalogue is code, not parsed Markdown.** Each module registers its defaults
through `IAuditCatalogSource.Describe(IAuditCatalogBuilder)`, discovered from DI and
keyed by **request type**; the builder maps one request type to one or more
`(operation, OperationType, OperationClass)` triples
([ADR-0044 § 6](../../../docs/decisions/0044-audit-write-path.md)).

**The types the triple names live in `LearnStack.SharedKernel.Audit`**, beside the ports
— `OperationType`, `OperationClass`, `AuditOutcome`, `AuditClassification`,
`AuditIntentState` and `CapturedEntityChange`
([ADR-0044 Amendment 3 § 3](../../../docs/decisions/0044-audit-write-path.md#amendment-3--what-the-join-binds-to-and-the-types-the-ports-carry-2026-09-08)).
They are **not** declared in the Audit module's Domain: a module's `Application` project
references only SharedKernel, its own Domain and its own Contracts, and a SharedKernel
back-edge to a module is a project cycle. The Audit module's `AuditEntry` consumes them;
it does not declare them.

> **The ports exist; the behavior does not yet.** `IAuditCatalogSource`,
> `IAuditCatalogBuilder` and `IAuditStore` ship in `LearnStack.SharedKernel.Audit`, and
> the builder's method names are fixed — `MustAudit` / `ShouldAudit` / `MayAudit`,
> `Off` and `DeclareOffPath`. Write against them as spelled. What is still a Packet 3
> logging shell is `AuditLogBehavior`, which rejects nothing until Packet 9 lights it
> up, so a registration written today is correct and inert.

```csharp
// LearnStack.Modules.Enrollment.Application/EnrollmentAuditCatalogSource.cs
using LearnStack.SharedKernel.Audit;

public sealed class EnrollmentAuditCatalogSource : IAuditCatalogSource
{
    // Every slug this source declares through a request-keyed registration starts with
    // it, and it is the module whose docs/modules/<module>/audit.md the join reads.
    public string ModuleName => "enrollment";

    public void Describe(IAuditCatalogBuilder builder)
    {
        // The third argument is the aggregate the row is about. It is declared, never
        // derived from the slug: the matrices drop the module's own prefix, so
        // tenancy.feature_flag.write is TenantFeatureFlag — a slug-to-type rule is wrong
        // for two shipped entities on the day it is written (ADR-0044 Amendment 5 § 2).
        // It is what fills entity_type and entity_id.
        builder.MustAudit<CreateEnrollmentCommand>(
            operation: "enrollment.enrollment.create",
            operationType: OperationType.Create,
            entityType: typeof(Enrollment));

        builder.MustAudit<SuspendEnrollmentCommand>(
            operation: "enrollment.enrollment.suspend",
            operationType: OperationType.Update,
            entityType: typeof(Enrollment));

        builder.ShouldAudit<CancelEnrollmentCommand>(
            operation: "enrollment.enrollment.cancel",
            operationType: OperationType.Update,
            entityType: typeof(Enrollment));

        builder.MustAudit<DeleteCohortCommand>(
            operation: "enrollment.cohort.delete",
            operationType: OperationType.Delete,
            entityType: typeof(Cohort));

        // One request, two audited resources — the ProvisionTenantCommand shape.
        // Each registration becomes its own intent and its own row, and each names its
        // own aggregate, which is how the two rows say what they are about.
        builder.MustAudit<EnrollCohortCommand>(
            operation: "enrollment.cohort.create",
            operationType: OperationType.Create,
            entityType: typeof(Cohort));
        builder.MustAudit<EnrollCohortCommand>(
            operation: "enrollment.enrollment.create",
            operationType: OperationType.Create,
            entityType: typeof(Enrollment));

        // Registered, never audited: AuditClassification.Off. A call, not a
        // convention — an unregistered request is rejected, not silently skipped.
        builder.Off<GetEnrollmentCountQuery>();
    }
}
```

The registration tells `AuditLogBehavior` to:

- Mint an `AuditEntryId` and park one intent per registration at step 3.
- Fill `entity_type` / `entity_id` from the declared aggregate, merging every capture of
  that type: the **earliest** one's `before`, the **latest** one's `after`, their fields
  concatenated. There is **no** per-registration opt-in for snapshots — the interceptor
  captures every tracked entity unconditionally (ADR-0044 § 7), and a
  `capturesBeforeAfter:` argument an earlier draft of this skill showed does not exist.
- Apply the class — `MustAudit` registers a floor a tenant `AuditConfig` cannot demote.

**Every request type must be registered.** There is no `RequestKind.Other` and no
implicit "unaudited" default: an `IRequest<Result<T>>` that reaches step 3 without a
catalogue entry is rejected with `audit_unclassified_operation` (500), which is a
deployment defect rather than something the caller can act on.

Two registrations are easy to skip and neither is optional:

- **The `Off` cases.** A request that writes no row is registered `Off` through the
  builder's own call — "register `Off`" is a call, not a convention
  ([ADR-0044 Amendment 3 § 4](../../../docs/decisions/0044-audit-write-path.md#amendment-3--what-the-join-binds-to-and-the-types-the-ports-carry-2026-09-08)).
  `Off` is an `AuditClassification` and never an `OperationClass`, so it is not a tier
  the matrix can carry: the matrix legend closes at MUST / SHOULD / MAY / `–`.
- **Test-only request types, through the same builder in their fixture** — eight of them
  across the four suites already need it. They register in the fixture and not in a
  module's `IAuditCatalogSource`, which is what keeps them outside the catalogue → matrix
  direction of Step 1's join.

An operation that is **not** a MediatR request — the `(off-path)` rows of Step 1 — is
registered by slug rather than by request type, and earns no `builder.…<TRequest>()`
call at all.

### Step 4: Sensitive-field redaction

For PII fields, mark them in the domain entity with `[PiiSensitive]`, the SharedKernel
property attribute Packet 9 adds:

```csharp
public sealed class User : AuditableEntity<UserId>
{
    public string Email { get; private set; }   // not PII-sensitive in this context

    [PiiSensitive]
    public string? NationalId { get; private set; }

    [PiiSensitive]
    public string? PhoneNumber { get; private set; }
}
```

`AuditChangeTrackerInterceptor` replaces a marked value — or one whose property name
matches the shipped `SensitiveTokenCatalog`'s token list — with
**`SensitiveTokenCatalog.RedactedValue`** in
`before`, `after` and `changes`. **The property is not dropped**: the diff still records
*that* it changed, which is the fact an investigation needs
([ADR-0044 § 8](../../../docs/decisions/0044-audit-write-path.md)). Both gates run inside
the capture, before anything reaches `IAuditStateCapture`.

### Step 4b: What gets captured, and what happens when it is large

You do not opt an entity in. `AuditChangeTrackerInterceptor` captures **every**
`ChangeTracker` entry in state `Added`, `Modified` or `Deleted`, minus a named exclusion
list — `OutboxMessage` and `IdempotencyKey` (machinery), and `AuditEntry` / `AuditConfig`
(the audit tables themselves). The old "`AuditableEntity<>` descendants only" predicate is
**withdrawn**: it was blind to `PlatformHostMapping`, `TenantLocale`, `TenantFeatureFlag`,
`PlatformEntitlement`, `CustomizationGeneration` and `TenantLevelTaxonomyItem`, five of
which the shipped matrices classify MUST. A plain class is captured like any other.

The interceptor **captures only** — it builds no row and issues no SQL.

Two shapes are binding on anything that reads the columns:

- **`changes` is a JSON array** of `{ path, before, after }`, with an entity-qualified
  RFC 6901 pointer in `path`. Never the polymorphic object-or-array shape ADR-0016 drew.
- **Over 256 KiB (`JsonValue.MaxRowBytes`) the value is elided, not truncated.** Each of
  `before_state`, `after_state` and `changes` is capped independently, and above the cap
  the value becomes `{"_elided": true, "bytes": <n>, "sha256": "<hex>"}` —
  **preserving the column's JSON type**, so an elided `changes` is `[{"_elided": …}]` and
  not a bare object. There is no external blob pointer: `audit_blob_id` names no store
  and is struck from the corpus.

### Step 5: AuditConfig for opt-outs / opt-ins

Tenants can override MUST/SHOULD/MAY per `(module, operation)` via `AuditConfig`,
**but cannot relax MUST**. This is enforced at the catalogue level:

- A tenant can promote `SHOULD` → `MUST` (stricter).
- A tenant can promote `MAY` → `SHOULD` or `MUST`.
- A tenant cannot demote `MUST` → `SHOULD` / `MAY`. The catalogue method
  `MustAudit<T>` registers a floor.

A **failed read** of the override is not a rejection: classification falls back to the
in-process catalogue, which carries the same MUST floor, so nothing proceeds unaudited
and one tenant's config outage does not deny requests platform-wide.

### Step 6: Tests

Add an audit-side integration test:

```csharp
[Fact]
public async Task CreateEnrollment_writes_audit_entry()
{
    using var fixture = await TestFixture.CreateAsync();
    using (fixture.AsTenant(tenantId)) {
        await mediator.Send(new CreateEnrollmentCommand(...), ct);
    }

    using (fixture.AsTenant(tenantId)) {
        var entry = await fixture.Audit
            .Where(x => x.Operation == "enrollment.enrollment.create")
            .SingleAsync();
        Assert.Equal(OperationType.Create, entry.OperationType);
        // outcome is one of four: success | denied | failed | indeterminate.
        // It is not a boolean — ADR-0016's `is_success` is superseded — and the
        // enum's type name lands with Packet 9.
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Equal(actorId, entry.ActorUserId);
        Assert.NotNull(entry.AfterState);
        Assert.Null(entry.BeforeState);   // create has no prior state
    }
}

[Fact]
public async Task SuspendEnrollment_captures_before_and_after()
{
    // ... assert both snapshots, status visible in before AND after ...
}

[Fact]
public async Task User_NationalId_isRedacted_In_AuditSnapshot()
{
    // ... assert SensitiveTokenCatalog.RedactedValue appears in the snapshot, the raw
    // value never, and the
    // property is still present — redaction replaces the value, it does not drop
    // the key ...
}
```

Connect as `learnstack_app`, never as the owner: `audit_log` is tenant-owned and
org-scoped under the canonical template, so a test running as `learnstack_migration`
passes even when every policy is inert. See
[add-integration-test](../add-integration-test/SKILL.md).

## Validation

- `dotnet build` and `dotnet test` pass.
- Architecture tests, canonical names from
  [21-architecture-tests-catalogue.md](../../../docs/standards/21-architecture-tests-catalogue.md)
  — do not invent a second spelling:
  - `Every_Module_Has_An_AuditCoverage_Matrix` (the module's `audit.md` exists) —
    Registered, backfilled in Packet 9.
  - `Modules_Do_Not_Write_AuditLog_Directly` (no module assembly outside
    `LearnStack.Modules.Audit.*` names `audit_log` or `AuditEntry`; the
    `LearnStack.SharedKernel.Audit` ports are explicitly out of scope) — Registered,
    Packet 10.
  - `Every_TenantOwned_Command_HasAuditCoverage` — Registered, backfilled in Packet 9.
    This is the rule that holds the matrix and the catalogue together. Both directions
    run, on the two domains of Step 1: catalogue → matrix is total for a module's
    source, matrix → catalogue binds only where the request type exists, and a
    `(planned)` row whose command has shipped fails.
  - `OperationType_Enum_Matches_Catalog` — Registered, Packet 9, if you touched the
    `OperationType` list.
  - There is **no** registered PII-redaction rule. Redaction is proved by the
    integration test below, not by an architecture test.
- An integration test demonstrates the new entry appears in `audit_log` with the
  right `operation` slug, `actor`, `outcome`, `before`, `after`, and any
  `[PiiSensitive]` fields carrying `SensitiveTokenCatalog.RedactedValue`.
- The module's `audit.md` matrix lists the operation, with the same slug in its
  `Operation` cell and the right classification.

## Common pitfalls

- **Calling `IAuditStore` directly from the handler.** Forbidden. The pipeline does this
  once per operation; a second write produces a duplicate row. `IAuditStore` is
  infrastructure, not a handler collaborator. No architecture test catches it —
  `Modules_Do_Not_Write_AuditLog_Directly` puts the SharedKernel ports out of scope —
  so this one is on review.
- **Adding `AuditEntry` to a module's `DbContext`** so a handler can "enrol the row in
  its own `SaveChanges`". Forbidden and unnecessary: atomicity comes from the
  transaction, not from sharing a `SaveChanges` call, and mapping the Audit module's
  aggregate into another module's context inverts the dependency direction.
- **`UPDATE`ing an audit row to add detail after the fact.** There is no second phase.
  The row is composed complete at the commit boundary, `IAuditStore` has **no update
  method** — it has four *write* methods, `WritePendingAsync`, `WriteStandaloneAsync`,
  `WriteBestEffortAsync` and `WritePlatformScopeAsync`, and that is a different thing —
  and `learnstack_app` holds no `UPDATE` privilege on `audit_log`.
- **Truncating snapshots silently.** Over `JsonValue.MaxRowBytes` the value becomes the
  elision record of Step 4b, in the column's own JSON type. Never an empty object, never
  a silent cut, and never a pointer to a blob store that does not exist.
- **Skipping the matrix update.** `Every_TenantOwned_Command_HasAuditCoverage` fails a
  catalogue entry with no matrix row, and a matrix row whose request type exists with no
  catalogue entry, once Packet 9 backfills it; until then review is the only gate.
- **Leaving `(planned)` on a row whose command has landed.** The marker is anti-rot, and
  the rule re-checks it: the commit that adds the command adds the catalogue entry and
  drops the marker, or the build goes red.
- **Marking a row `(planned)` to get past a red build.** It buys nothing — the rule
  looks for the request type, not for the marker's promise — and it moves a live
  operation out of the join that exists to hold it.
- **Using a permission action for the verb.** `customization.content_type.write` for a
  publication is the failure mode the split exists to prevent. The verb comes from the
  matrix; only `{module}` and `{resource}` match the permission key.
- **Auditing a `read` for noise.** `ReadSensitive` is the only read type that should be
  audited; broad read auditing creates noise that hides real signals.
- **Tenants relaxing MUST.** Forbidden by the catalogue API. Calling
  `MustAudit<T>` registers a floor that tenants cannot demote.
- **Domain-specific operation names.** Use `enrollment.enrollment.create`, not
  `english.lesson.enrolled`. Domain-flavoured operation names break the
  cross-tenant audit query.
