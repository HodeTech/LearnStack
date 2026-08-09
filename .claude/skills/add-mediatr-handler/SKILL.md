---
name: add-mediatr-handler
description: >
  Add a MediatR command or query handler that participates in the LearnStack
  pipeline — `Result<T>` return type, FluentValidation, audit capture via
  `AuditLogBehavior`, outbox-aware writes, idempotency where needed. USE FOR:
  any new write or read use case that lives in a module's `Application` layer.
  DO NOT USE FOR: pure domain logic (lives in `Domain`), provider adapter
  glue (lives in `Infrastructure`), or controllers (controllers are thin shells
  over MediatR; if you have controller logic, push it into a handler).
---

# Adding a MediatR handler

## Purpose

Write a command/query handler that participates correctly in the LearnStack MediatR
pipeline: `Validation → Logging → AuditLog → TenantContext → Authorization →
Transaction → OutboxFlush → Handler`. The pipeline is shared (per
[ADR-0032 § Sub-decision 2](../../../docs/decisions/0032-exception-handling-logging-and-observability.md)
and [Standards 02 § Pipeline Behaviors](../../../docs/standards/02-backend-coding.md)),
so the handler stays focused on its own business logic.

## When to use

- New write use case (`Create<Name>Command`, `Publish<Name>Command`,
  `Cancel<Name>Command`).
- New read use case (`Get<Name>ByIdQuery`, `List<Name>sQuery`) that needs
  application-layer logic beyond a single repository call.
- Promoting an existing controller method into a handler.

## When not to use

- Pure validation that fits in a domain method's invariants.
- Trivial repository pass-throughs in a query — emit them directly from the
  controller via a query handler if you want a stable contract, but don't write a
  handler just for ceremony.
- Provider adapter glue. That stays in `Infrastructure`.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Operation name | Yes | `<Verb><Aggregate><Command/Query>`, e.g. `CreateEnrollmentCommand`. |
| Owning module | Yes | Determines folder + DbContext. |
| Audit class | Yes | `create` / `update` / `delete` / `read-sensitive` / `security-event` / `platform-admin` per [18-audit-coverage.md](../../../docs/standards/18-audit-coverage.md). |
| Permission | Yes | `{module}.{resource}.{action}` from the closed action set. |
| Integration events | No | What it publishes (if any). |
| Idempotency key | No | Required only for write endpoints with external side effects. |

## Workflow

### Step 1: Define the command / query record

In `<Module>.Application.Contracts/<Aggregate>/<Verb><Aggregate>Command.cs`:

```csharp
public sealed record CreateEnrollmentCommand(
    UserId LearnerId,
    CourseVersionId CourseVersionId,
    CohortId? CohortId,
    EnrollmentSource Source)
    : ICommand<Result<EnrollmentDto>>;
```

Rules:

- Records, not classes.
- Strongly-typed ids; no raw `Guid` in the command surface.
- `: ICommand<Result<T>>` for writes; `: IQuery<Result<T>>` for reads.
- Live in `Application.Contracts` so other modules can subscribe to the typed contract
  (rare; usually they consume integration events instead).

### Step 2: FluentValidation validator

In `<Module>.Application/<Aggregate>/<Verb><Aggregate>CommandValidator.cs`:

```csharp
public sealed class CreateEnrollmentCommandValidator : AbstractValidator<CreateEnrollmentCommand>
{
    public CreateEnrollmentCommandValidator()
    {
        RuleFor(x => x.LearnerId).NotEmpty();
        RuleFor(x => x.CourseVersionId).NotEmpty();
        RuleFor(x => x.Source).IsInEnum();
    }
}
```

The `ValidationBehavior` in the pipeline runs the validator before the handler. A
validation failure returns
`Result.Fail(LocalizedMessage.Of("validation.<key>"))` automatically.

### Step 3: Handler

In `<Module>.Application/<Aggregate>/<Verb><Aggregate>CommandHandler.cs`:

```csharp
public sealed class CreateEnrollmentCommandHandler(
    EnrollmentDbContext db,
    ITenantContext tenantContext,
    IOutbox outbox,
    ILogger<CreateEnrollmentCommandHandler> logger)
    : ICommandHandler<CreateEnrollmentCommand, Result<EnrollmentDto>>
{
    public async Task<Result<EnrollmentDto>> Handle(
        CreateEnrollmentCommand cmd, CancellationToken ct)
    {
        // Domain check
        var existing = await db.Enrollments.AnyAsync(
            x => x.LearnerId == cmd.LearnerId && x.CourseVersionId == cmd.CourseVersionId,
            ct);

        if (existing)
            return Result.Fail<EnrollmentDto>(
                LocalizedMessage.Of("enrollment.already_exists"));

        var enrollment = Enrollment.Create(
            tenantContext.Current.TenantId,
            tenantContext.Current.OrganizationId,
            cmd.LearnerId,
            cmd.CourseVersionId,
            cmd.CohortId,
            cmd.Source);

        db.Enrollments.Add(enrollment);

        await outbox.EnqueueAsync(new EnrollmentCreatedIntegrationEvent
        {
            TenantId = tenantContext.Current.TenantId.Value,
            OrganizationId = tenantContext.Current.OrganizationId?.Value,
            EnrollmentId = enrollment.Id.Value,
            LearnerId = cmd.LearnerId.Value,
            CourseVersionId = cmd.CourseVersionId.Value,
            // OccurredAt is auto-populated by IntegrationEventBase — do not set
            // it manually. If you need it explicitly, inject IClock and use
            // clock.UtcNow per 02-backend-coding.md § Time.
        }, ct);

        await db.SaveChangesAsync(ct);   // atomic: aggregate + outbox row

        return Result.Success(
            MapToDto(enrollment),
            LocalizedMessage.Of("enrollment.created"));
    }
}
```

Rules:

- `Result<T>` everywhere. Throw only for *unexpected* failures (network, db down).
- `LocalizedMessage` keys carry the `lockey_` prefix invariant in serialization
  (handled by the result mapper).
- Outbox row written **inside** the same `DbContext` transaction. Never open a
  second transaction for the event publish.
- Read `TenantContext.Current` for tenant + org; don't accept them from the command
  body.

### Step 4: Audit catalogue entry

Open the module's `audit.md` (under `docs/modules/<name>/audit.md`). Add a row:

```markdown
| Enrollment | – | MUST | MUST | – | – |
```

…and register the operation in the catalogue:

```csharp
catalog.MustAudit<CreateEnrollmentCommand>(
    module: "enrollment",
    operation: "enrollment.create",
    operationType: OperationType.Command,
    operationClass: OperationClass.Create,
    capturesBeforeAfter: false);   // no prior state to capture for create
```

The `AuditLogBehavior` reads the catalogue and writes through `IAuditStore`
automatically. **You never call `IAuditStore` directly from the handler.**

See [add-audit-coverage](../add-audit-coverage/SKILL.md).

### Step 5: Permission policy

The endpoint that invokes this handler is guarded by
`[Authorize(Policy = "enrollment.enrollment.write")]`. Register the permission in
the module:

```csharp
registry.Tenant(
    key: "enrollment.enrollment.write",
    description: "Create or update enrollments",
    defaultGrants: [Roles.TenantAdmin, Roles.OrgAdmin]);
```

`Roles.*` references come from the **Built-in Roles** catalogue authoritative
at [19-permissions.md § Built-in Roles](../../../docs/standards/19-permissions.md).
Do not invent role names; pick from that table or extend it via PR.

See [add-permission](../add-permission/SKILL.md).

### Step 6: Idempotency (if applicable)

If the command triggers external side effects (LiveKit room creation, Stripe
charge, email send), the controller layer adds the `Idempotency-Key` header
requirement, and the handler:

1. Reads the idempotency key from the request context.
2. Checks the per-module `idempotency_keys` table for a prior result; returns it
   if found.
3. Otherwise runs the handler, writes the (key → result) row in the same
   transaction.

The pattern is detailed in [04-api-design.md § Idempotency](../../../docs/standards/04-api-design.md).

### Step 7: Endpoint shell

Endpoint controllers are thin:

```csharp
[ApiController]
[Route("v1/enrollments")]
public sealed class EnrollmentsController(ISender mediator) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "enrollment.enrollment.write")]
    public async Task<IActionResult> Create(
        [FromBody] CreateEnrollmentRequest body, CancellationToken ct)
    {
        var result = await mediator.Send(body.ToCommand(), ct);
        return result.ToActionResult();
    }
}
```

## Validation

- `dotnet build` and `dotnet test` pass.
- The validator runs (try a bad input and confirm `400 ProblemDetails` with the
  validation message keys).
- The audit pipeline writes an `AuditEntry` (check the `audit_log` table in an
  integration test).
- The outbox row is visible in `outbox_messages` after the command completes; the
  outbox processor dispatches it in dev (see
  [wire-dapr-pubsub](../wire-dapr-pubsub/SKILL.md)).
- The permission rejection path returns `403 ProblemDetails`.
- An integration test exercises the full pipeline including the audit + outbox.

## Common pitfalls

- **Throwing for expected failures.** Use `Result.Fail(...)`. Exceptions are for
  *unexpected* paths (DB unavailable, infrastructure faults, programmer error).
  `Result.Fail` values are converted to RFC 7807 Problem Details at the
  **controller/API boundary** when the endpoint calls
  `Result<T>.ToActionResult()` (Step 7 above) — the pipeline itself just
  propagates the `Result` unchanged; the explicit `.ToActionResult()` call is
  where the mapping happens. Per
  [ADR-0032 § Sub-decision 4](../../../docs/decisions/0032-exception-handling-logging-and-observability.md),
  `DomainException` is reserved for **bugs** — "expected business-rule
  violation" means `Result.Fail(business_rule_violation, …)`, not a throw. The
  Roslyn analyzer `LearnStackException-DomainExceptionThrow` flags violations.
- **Throwing `FluentValidation.ValidationException` from a validator.** The
  pipeline `ValidationBehavior` already produces
  `Result.Fail(validation_failed, errors)`. A throw from the validator is a
  bug; FluentValidation runs in collect-mode by default and pipeline behavior
  never raises a validation exception.
- **Calling `IAuditStore` directly.** The `AuditLogBehavior` does this for you. A
  direct call writes a duplicate row. The architecture test
  `Modules_Do_Not_Write_AuditLog_Directly` enforces it.
- **Two transactions for write + outbox.** The outbox row must be in the **same**
  `SaveChangesAsync` as the aggregate. Otherwise the system can publish without
  committing (or commit without publishing).
- **Trusting `TenantId` from the request body.** Always read from
  `ITenantContext`. The API edge sets it from the JWT + host; body input is not
  authoritative.
- **Missing permission registration.** The endpoint compiles but every request is
  rejected at runtime because the policy is unknown.
- **Using raw `Guid` in the command.** Loses type safety; the architecture test
  `Commands_Use_StronglyTypedIds` rejects it.
- **Logging `ILogger.LogError(ex, ...)` then rethrowing.** The L1
  `IExceptionHandler` already logs + records the OTel span error + captures
  to `IErrorTrackingProvider` per
  [ADR-0032 § Sub-decision 7](../../../docs/decisions/0032-exception-handling-logging-and-observability.md).
  Re-logging at the handler doubles the entry.
- **Per-call `Activity.Current?.SetTag("tenant.id", ...)`.** The
  `TenantContextSpanProcessor` enriches every span automatically. Per-call
  tagging is duplication and a maintenance burden.
