---
name: add-audit-coverage
description: >
  Extend a module's audit-coverage matrix (`docs/modules/<module>/audit.md`) and
  register operations in the catalogue so `AuditLogBehavior` writes the right
  entries automatically. USE FOR: a new MUST/SHOULD/MAY classification, a new
  operation that should be audited, a `before/after` snapshot rule for a sensitive
  column. DO NOT USE FOR: writing to `audit_log` directly (forbidden — use
  `IAuditStore` via the pipeline), bypassing audit for performance (use
  `AuditConfig` per-tenant overrides instead), or putting domain-specific terms in
  audit messages (forbidden).
---

# Adding audit coverage

## Purpose

Wire a new operation into LearnStack's central audit pipeline
([ADR-0033](../../../docs/decisions/0033-audit-durability-model.md) — the binding
durability contract; [ADR-0016](../../../docs/decisions/0016-audit-log-subsystem.md) —
superseded, read only for subsystem context;
[31-audit-subsystem.md](../../../docs/architecture/31-audit-subsystem.md),
[18-audit-coverage.md](../../../docs/standards/18-audit-coverage.md)) by extending the
module's matrix and the audit catalogue. Modules never write `audit_log` directly; the
catalogue plus the pipeline do.

The pipeline is **decide → write → reconcile**: `AuditLogBehavior` classifies at step 3
and parks an intent, `TransactionBehavior` writes the row on the business transaction
immediately before `COMMIT`, and `AuditLogBehavior` re-writes it standalone on the way out
if that transaction did not commit. You do not touch any of it — but the classification
you pick decides which of those paths a given operation takes.

## When to use

- A new command / query in the module should be MUST or SHOULD audited.
- A previously SHOULD-audited operation is being promoted to MUST.
- A column previously not snapshotted should now have `before` / `after` captured
  on update.
- A new module is shipping its first audit matrix.

## When not to use

- Adding an audit row from a custom code path (forbidden; the pipeline does it).
- "Audit everything." Operations of class `read` (non-sensitive) are MAY by default;
  blanket auditing creates noise.
- Domain-specific operation names. Use the generic verb (`enrollment.create`), not
  `english.placement.scored`.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Module | Yes | Owns the matrix file. |
| Resource | Yes | Aggregate or sub-resource name. |
| Operation class | Yes | `create` / `update` / `delete` / `read-sensitive` / `security-event` / `platform-admin`. |
| Classification | Yes | `MUST` / `SHOULD` / `MAY` / `–`. |
| Before/after snapshot? | If `update` on sensitive fields | Yes/no. |
| PII fields | If applicable | List the property names to redact. |

## Workflow

### Step 1: Open the matrix

`docs/modules/<module>/audit.md`. Each module owns one matrix. The default
template (from [18-audit-coverage.md](../../../docs/standards/18-audit-coverage.md)):

```markdown
| Resource | create | update | delete | read-sensitive | security-event |
|----------|:------:|:------:|:------:|:--------------:|:--------------:|
| Enrollment | MUST | MUST | MUST | – | – |
| Entitlement | MUST | MUST | MUST | – | – |
| Cohort | SHOULD | MUST | MUST | – | – |
```

Legend:

- **MUST** — every occurrence is audited.
- **SHOULD** — audited by default; opt-out requires a code comment + justification.
- **MAY** — allowed but not required.
- **–** — operation doesn't apply (e.g. read-sensitive on Cohort).

### Step 2: Pick the classification

Use the rules from
[18-audit-coverage.md § Operation Classes](../../../docs/standards/18-audit-coverage.md):

- **create**: SHOULD by default. MUST when permission, money, content-publication,
  or consent state changes.
- **update**: MUST when status, permission, money, content-publication, recording
  policy, or consent fields change.
- **delete**: MUST. Always.
- **read-sensitive**: MUST when reading another user's PII, financials, learner
  progress, recordings, consent state.
- **security-event**: MUST. Login bursts, MFA failures, admin override of any
  guard, mTLS/JWT/HMAC verification failure.
- **platform-admin**: MUST when an operator acts on a tenant they're not a member
  of. Required `reason` field.

If your operation falls outside this list, it probably belongs as a sub-resource
(see [add-permission § Closed Action Set](../add-permission/SKILL.md)).

### Step 2b: What a MUST classification now costs

Under [ADR-0033](../../../docs/decisions/0033-audit-durability-model.md) the class is
**load-bearing, not documentary**. Before you write MUST, know what you are buying:

- The row is inserted on the **same transaction** as the business write, immediately
  before `COMMIT`, while `app.tenant_id` is set — so it commits with the state change or
  not at all, and Row Level Security accepts it.
- If the transaction rolls back, the row is **re-written standalone** with outcome
  `failed`. A MUST-class operation is never left with no row, including on the ordinary
  path where a handler saves and then returns `Result.Fail(...)`.
- The operation **fails closed**. If the audit row cannot be written at all, the command
  is rejected — the caller gets `503 audit_unavailable`, never a partial success.
- MUST-class events with no business transaction — `denied` outcomes, read-sensitive
  queries, non-mutating security events — get a standalone row in a short transaction
  that sets its own tenant GUC. Classifying a *query* MUST is legitimate and costs a
  synchronous write before the result is returned.
- A tenant `AuditConfig` override can narrow SHOULD/MAY. It can never remove baseline
  MUST coverage; the catalogue re-applies the MUST floor after the override.
- SHOULD/MAY stays best-effort. Choosing it is choosing a **documented accepted loss** —
  write that loss into the module's matrix rather than leaving it implied.

So MUST is an availability trade as well as a compliance one. Classify deliberately.

### Step 3: Register in the catalogue

In the module's `RegisterAuditCoverage`:

```csharp
public void RegisterAuditCoverage(IAuditCatalog catalog)
{
    catalog.MustAudit<CreateEnrollmentCommand>(
        module: "enrollment",
        operation: "enrollment.create",
        operationType: OperationType.Command,
        operationClass: OperationClass.Create);

    catalog.MustAudit<SuspendEnrollmentCommand>(
        module: "enrollment",
        operation: "enrollment.suspend",
        operationType: OperationType.Command,
        operationClass: OperationClass.Update,
        capturesBeforeAfter: true,
        sensitiveFields: ["Status", "SuspendedReason"]);

    catalog.ShouldAudit<CancelEnrollmentCommand>(
        module: "enrollment",
        operation: "enrollment.cancel",
        operationType: OperationType.Command,
        operationClass: OperationClass.Update);

    catalog.MustAudit<DeleteCohortCommand>(
        module: "enrollment",
        operation: "cohort.delete",
        operationType: OperationType.Command,
        operationClass: OperationClass.Delete);
}
```

The catalogue entry tells `AuditLogBehavior` to:

- Write an entry on every invocation (`MustAudit` / `ShouldAudit`).
- Capture `before` and `after` JSON snapshots (`capturesBeforeAfter: true`).
- Redact named PII fields from snapshots (`sensitiveFields: [...]` — or mark the
  property with `[PiiSensitive]` and skip listing it here).

### Step 4: Sensitive-field redaction

For PII fields, mark them in the domain entity:

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

The `AuditChangeTrackerInterceptor` strips marked properties from `before` and
`after` snapshots, replacing the value with `"<redacted>"`. Architecture test
`Pii_Fields_AreRedacted_In_AuditSnapshots` confirms.

### Step 5: AuditConfig for opt-outs / opt-ins

Tenants can override MUST/SHOULD/MAY per `(module, operation)` via `AuditConfig`,
**but cannot relax MUST**. This is enforced at the catalogue level:

- A tenant can promote `SHOULD` → `MUST` (stricter).
- A tenant can promote `MAY` → `SHOULD` or `MUST`.
- A tenant cannot demote `MUST` → `SHOULD` / `MAY`. The catalogue method
  `MustAudit<T>` registers a floor.

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
            .Where(x => x.Operation == "enrollment.create")
            .SingleAsync();
        Assert.Equal("create", entry.OperationClass);
        Assert.Equal(actorId, entry.ActorUserId);
        Assert.NotNull(entry.After);
        Assert.Null(entry.Before);   // create has no prior state
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
    // ... assert "<redacted>" appears in the snapshot, raw value never ...
}
```

## Validation

- `dotnet build` and `dotnet test` pass.
- Architecture tests:
  - `Every_Module_Has_An_AuditCoverage_Matrix` (the module's `audit.md` exists) —
    Registered, backfilled in Packet 9.
  - `Modules_Do_Not_Write_AuditLog_Directly` (no `IAuditStore.WriteAsync` call from
    outside the audit infrastructure) — Registered, Packet 10.
  - `Every_TenantOwned_Command_HasAuditCoverage` — Registered, backfilled in
    Packet 9.
- An integration test demonstrates the new entry appears in `audit_log` with the
  right `operation`, `actor`, `before`, `after`, and any `[PiiSensitive]` fields
  redacted.
- The module's `audit.md` matrix lists the operation with the right classification.

## Common pitfalls

- **Calling `IAuditStore` directly from the handler.** Forbidden. The pipeline does this
  once per operation; a second write produces a duplicate row. `IAuditStore` is
  infrastructure, not a handler collaborator —
  `Modules_Do_Not_Write_AuditLog_Directly` enforces it.
- **Adding `AuditEntry` to a module's `DbContext`** so a handler can "enrol the row in
  its own `SaveChanges`". Forbidden and unnecessary: atomicity comes from the
  transaction, not from sharing a `SaveChanges` call, and mapping the Audit module's
  aggregate into another module's context inverts the dependency direction.
- **`UPDATE`ing an audit row to add detail after the fact.** There is no second phase.
  The row is composed complete at the commit boundary, `IAuditStore` has no update
  method, and `learnstack_app` holds no `UPDATE` privilege on `audit_log`.
- **Truncating snapshots silently.** If a `before/after` JSON is too large, store
  an external pointer (`audit_blob_id`); never an empty object.
- **Skipping the matrix update.** `Every_Module_Has_An_AuditCoverage_Matrix` will
  fail once Packet 9 backfills it; until then review is the only gate.
- **Auditing a `read` for noise.** `read-sensitive` is the only read class that
  should be audited; broad read auditing creates noise that hides real signals.
- **Tenants relaxing MUST.** Forbidden by the catalogue API. Calling
  `MustAudit<T>` registers a floor that tenants cannot demote.
- **Domain-specific operation names.** Use `enrollment.create`, not
  `english.lesson.enrolled`. Domain-flavoured operation names break the
  cross-tenant audit query.
