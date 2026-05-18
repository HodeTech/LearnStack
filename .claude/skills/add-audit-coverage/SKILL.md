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
([ADR-0016](../../../docs/decisions/0016-audit-log-subsystem.md),
[31-audit-subsystem.md](../../../docs/architecture/31-audit-subsystem.md),
[18-audit-coverage.md](../../../docs/standards/18-audit-coverage.md)) by extending
the module's matrix and the audit catalogue. Modules never write `audit_log`
directly; the catalogue + the `AuditLogBehavior` MediatR behaviour do.

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
  - `Module_<Name>_HasAuditMatrix` (the module's `audit.md` exists).
  - `Modules_Do_Not_Write_AuditLog_Directly` (no `IAuditStore.WriteAsync` call from
    outside the audit infrastructure).
  - `Every_MustAudit_Operation_HasMatrixEntry`.
- An integration test demonstrates the new entry appears in `audit_log` with the
  right `operation`, `actor`, `before`, `after`, and any `[PiiSensitive]` fields
  redacted.
- The module's `audit.md` matrix lists the operation with the right classification.

## Common pitfalls

- **Calling `IAuditStore` directly from the handler.** Forbidden. The
  `AuditLogBehavior` does this once; a second write produces a duplicate row.
- **Truncating snapshots silently.** If a `before/after` JSON is too large, store
  an external pointer (`audit_blob_id`); never an empty object.
- **Skipping the matrix update.** `Module_<Name>_HasAuditMatrix` will fail; CI
  rejects.
- **Auditing a `read` for noise.** `read-sensitive` is the only read class that
  should be audited; broad read auditing creates noise that hides real signals.
- **Tenants relaxing MUST.** Forbidden by the catalogue API. Calling
  `MustAudit<T>` registers a floor that tenants cannot demote.
- **Domain-specific operation names.** Use `enrollment.create`, not
  `english.lesson.enrolled`. Domain-flavoured operation names break the
  cross-tenant audit query.
