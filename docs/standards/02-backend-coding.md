# 02 — Backend Coding Standards

**Status:** Active
**Derives from:** [ADR 0002 — Initial Architecture](../decisions/0002-initial-architecture.md), [ADR 0006 — Events and Outbox](../decisions/0006-events-and-outbox.md), [ADR 0023 — Strongly-Typed ID Source Generator](../decisions/0023-strongly-typed-id-source-generator.md), [ADR 0031 — PostgreSQL Major Version](../decisions/0031-postgresql-major-version.md), [ADR 0036 — Trusted Inputs for Tenant and Organization Resolution](../decisions/0036-tenant-resolution-trusted-inputs.md).

C# / .NET conventions for LearnStack backend code.

## Language Settings

- Target framework: `net10.0`.
- C# language version: latest stable.
- `Nullable` enabled on every project (`<Nullable>enable</Nullable>`).
- `TreatWarningsAsErrors` set to `true` in CI.
- `ImplicitUsings` enabled in modern projects.
- `LangVersion` = `latest`.
- File-scoped namespaces everywhere.

## Naming

| Element | Convention |
|---------|------------|
| Namespaces | `LearnStack.Modules.Education.Application` |
| Classes / records / structs | `PascalCase` |
| Interfaces | `PascalCase` prefixed with `I` (`ICourseRepository`) |
| Methods | `PascalCase` |
| Private fields | `_camelCase` |
| Parameters / locals | `camelCase` |
| Constants | `PascalCase` |
| Enums | `PascalCase`; members `PascalCase`, no `_` prefix |
| Async methods | `PascalCase` ending in `Async` |
| Test classes | `<TargetClassName>Tests` |
| Test methods | `Method_Scenario_ExpectedOutcome` |

## Types

- **Records** for immutable value-like data: DTOs, integration events, configuration options.
- **Sealed classes** by default; open inheritance is the exception.
- **Structs** only for small, immutable, frequently-allocated values (≤ 16 bytes).
- **Strongly-typed ids** (`partial record struct CourseId : IStronglyTypedId<Guid>;` per the [Vogen pattern below](#strongly-typed-identifiers)) for all entity identifiers. Never expose raw `Guid` on the public surface.
- **Value objects** for domain concepts with invariants (e.g. `Email`, `Slug`, `LocaleCode`).

## Strongly-Typed Identifiers

Per [ADR-0023](../decisions/0023-strongly-typed-id-source-generator.md), the
shared source generator is **[Vogen](https://github.com/SteveDunn/Vogen)**. The
canonical declaration uses Vogen's `[ValueObject<Guid>(...)]` annotation on a
partial `record struct`:

```csharp
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct CourseId : IStronglyTypedId<Guid>;
```

Vogen emits per ID:
- EF Core value converter.
- `JsonConverter` (System.Text.Json).
- TypeConverter (carries ASP.NET Core minimal-API + MVC route-parameter binding).
- OpenAPI schema mapping (wired centrally in Packet 4 per ADR-0023 § Implementation
  Notes).

Construction:
- New IDs in aggregate methods mint via the injected `IGuidFactory`:
  `CourseId.From(guidFactory.NewUuidV7())`. **Never call `Guid.CreateVersion7()` /
  `Guid.NewGuid()` directly in `Domain` / `Application` code** — Standards 02
  § Time bans the symmetric `DateTime.UtcNow` for the same reason (deterministic
  tests). High-volume append-only tables (`audit_log`, `outbox_messages`) prefer
  DB-side `uuidv7()` (per [ADR-0031](../decisions/0031-postgresql-major-version.md)).
- ID types do **not** expose a `New()` static — explicit `From(guidFactory.NewUuidV7())`
  at the call site keeps the dependency surface honest.

The same annotation covers richer value objects (`Email`, `Slug`, `LocaleCode`,
`Money`) — the emitter shape is identical for IDs and value objects, with the
value-object's invariant captured in a `Validate` static method.

## Nullability

- `Nullable` is on. Treat warnings as errors.
- Reference types are non-nullable unless declared `T?`.
- Never use `!` (null-forgiving operator) without a comment explaining why.
- Prefer `ArgumentNullException.ThrowIfNull(param)` at public boundaries.
- Return `Result<T>` for expected absences — `Result.Fail` with a `not_found` code, per
  [Standards 09](09-error-handling.md). There is no `Maybe<T>` / `Option<T>` in the
  kernel and none is planned; a second absence type would compete with `Result<T>` for
  the same job. Reserve null for true uninitialized state.

## Async

- Public methods that perform I/O end in `Async` and accept `CancellationToken ct`.
- Always pass `ct` down.
- Never `Task.Wait()` or `.Result` in production code. Use `await` end to end.
- `ValueTask<T>` only when profiling shows allocation pressure.
- Avoid `async void` except in event handlers framed by frameworks.

## Result and Error Modeling

Two patterns coexist:

- **Exceptions** for *unexpected* failures (bug, transient infra, programming error).
- **`Result<T>`** for *expected* outcomes (validation failure, not found, conflict).

```csharp
public sealed record Result<T> : IResultBase
{
    internal Result(bool isSuccess, T? value, Error? error, LocalizedMessage? successMessage = null) { ... }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public T? Value { get; }
    public Error? Error { get; }
    public LocalizedMessage? SuccessMessage { get; }

    // Throws when value is null — Standards 09 § Forbidden bans
    // IsSuccess = true with Value = null. For payload-less success use
    // Result<None>.
    public static Result<T> Ok(T value, LocalizedMessage? message = null);
    public static Result<T> Fail(Error error);
}

public sealed record Error(
    LocalizedMessage Message,
    IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>>? Details = null)
{
    // Stable machine-readable identifier — Standards 04 § Problem Details
    // "code". Derived from Message.Key by stripping the lockey_ prefix so
    // the code never drifts from the localization key by construction.
    public string Code => Message.Key[LocalizedMessage.RequiredPrefix.Length..];
}
```

`LocalizedMessage`'s constructor enforces the `lockey_` key prefix; the
constructor of `Result<T>` is `internal` so callers cannot bypass the
`Ok` / `Fail` factory invariants via positional record syntax. See
[09-error-handling.md § Result Type](09-error-handling.md) and
[Phase 02a Packet 2](../roadmap/phase-02a-kernel-tenancy.md).

Use cases for `Result<T>`:
- Validation outcomes.
- Optimistic concurrency conflicts.
- Domain rule violations expected to be common.

Exceptions stay for things like "database is down" or "the program is in a bug state."

## MediatR Use Cases

Each use case is a command or query:

```csharp
public sealed record PublishCourseCommand(CourseId CourseId, UserId ActorId) : IRequest<Result<CourseVersionId>>;

public sealed class PublishCourseHandler : IRequestHandler<PublishCourseCommand, Result<CourseVersionId>>
{
    public async Task<Result<CourseVersionId>> Handle(PublishCourseCommand command, CancellationToken ct)
    {
        // ...
    }
}
```

Rules:
- Handlers are thin; orchestrate domain methods and persistence.
- One transaction per handler.
- Validation lives in FluentValidation validators; pipeline behavior short-circuits invalid commands.
- Logging, tracing, and metrics live in pipeline behaviors, not in handlers.

## Validation

- `FluentValidation` for command and DTO validation.
- Domain invariants enforced in domain methods, not duplicated in validators.
- Validation failures return `Result<T>` with a `validation_failed` error code and field-level details.

## EF Core

- One `DbContext` per module (no monolithic context).
- Entity configurations in dedicated `*Configuration : IEntityTypeConfiguration<T>` classes; never inline in `OnModelCreating` body.
- Global query filters configured via a base configuration method for tenant-owned entities.
- Migrations generated per module; CI checks that the migration is included when a config changes.
- No lazy loading. Explicit `.Include()` only when needed; prefer projection (`Select(...)`).
- Avoid `Tracking` for read-only queries: use `AsNoTracking()`.
- Avoid `string` interpolated SQL. Use parameterized queries.

## Domain Modeling

- Aggregates are the only entry points for state changes.
- Aggregate methods enforce invariants; setters are private.
- Domain events raised from aggregate methods; collected by the unit-of-work and dispatched on commit.
- Avoid anemic models (data + getters/setters with logic outside).
- Avoid primitive obsession; use value objects.
- **Entity equality is identity equality, and it is defined once.** `Entity<TId>`
  implements `IEquatable<Entity<TId>>` and overloads `==` / `!=`; both, plus
  `Equals(object?)`, delegate to the single typed `Equals(Entity<TId>?)`. Aggregates
  do not redefine any of them. Three guards live in that one body and must not be
  bypassed: an entity whose `Id` is uninitialized is equal only to itself by
  reference, two entities of different runtime types are never equal even when their
  `Id` matches, and `GetHashCode` partitions uninitialized instances apart.
- **Ask `IStronglyTypedId.IsInitialized()`, never `id.Equals(default(TId))`**
  ([ADR-0023 Amendment 3](../decisions/0023-strongly-typed-id-source-generator.md))**.** A
  Vogen `[ValueObject]` returns `false` from `Equals` when either side is
  uninitialized, so the `default` comparison answers `false` for exactly the case it
  is meant to catch, and the guard behind it silently never runs.
- **Constrain aggregate id parameters to `IEquatable<TId>`.** Without it
  `id.Equals(other.Id)` binds to `ValueType.Equals(object)` and boxes — 40 bytes per
  boxed call, measured. `Entity<TId>` used to box three times per comparison (120 B)
  because the dead `default(TId)` guard above added two more; the constraint removes
  the remaining one, taking every equality path and `GetHashCode` to 0 B.
- **`Equals(object?)` and `GetHashCode()` on `Entity<TId>` are `sealed override`.**
  A derived aggregate that overrode them could also declare its own `operator ==`;
  sealed, it cannot silence CS0660 / CS0661 and the build fails instead. Aggregates
  never redefine equality — enforced from Packet 10 by
  [`Aggregates_Do_Not_Redeclare_Entity_Equality`](21-architecture-tests-catalogue.md#aggregates_do_not_redeclare_entity_equality),
  which catches the one case the compiler cannot: a derived `Equals(TSelf?)`
  **overload**, which is a new method rather than an override.
- **Do not enable EF Core lazy-loading proxies.** The cross-type guard compares
  `GetType()`, and a proxy's runtime type is `Castle.Proxies.<Name>Proxy`, so a
  proxied instance would never equal the entity it proxies. Lazy loading is already
  barred under [EF Core](#ef-core); this is the second reason.

## Pipeline Behaviors

Standard MediatR pipeline (in order; outermost first, innermost last). Bound by
[ADR-0032 § Sub-decision 2](../decisions/0032-exception-handling-logging-and-observability.md)
and consistent with [ADR-0033](../decisions/0033-audit-durability-model.md), which keeps
this order and changes only the durability contract of what step 3 records
(ADR-0033 supersedes ADR-0016):

1. **`ValidationBehavior`** — FluentValidation. Invalid input → returns
   `Result.Fail(validation_failed, errors)`; never throws
   `ValidationException`. Short-circuits the request before any DB / audit /
   business code runs.
2. **`LoggingBehavior`** — Opens the `ILogger.BeginScope` carrying the eight
   correlation fields ([10-observability.md § Correlation](10-observability.md)),
   starts the manual `<module>.<operation>` `Activity`, and measures handler
   latency for the histogram metric.
3. **`AuditLogBehavior`** — Wraps the inner pipeline with `try / catch`. On
   exception it records the failure outcome and rethrows via
   `ExceptionDispatchInfo` to preserve the original stack.

   Per [ADR-0033](../decisions/0033-audit-durability-model.md) this behavior
   keeps its position and **decides**; it does not own the durable write. On
   the way in it classifies `(module, operation)` from the in-process audit
   catalogue plus the tenant's cached `audit_config` overrides — it issues no
   query, because at step 3 no transaction is open, `app.tenant_id` is unset,
   and `audit_config` is RLS-protected, so a read there would return zero rows
   silently. For MUST it mints the audit id and parks a pending intent in the
   scoped `IAuditStateCapture`, touching no `DbContext`.

   On the way out it **reconciles**: if the intent's state is anything other
   than `Committed` — never written, rolled back, or a commit whose outcome is
   unknown — it writes the row standalone with the real outcome, in its own
   short transaction. "Written" is not "committed", and a per-request flag
   cannot observe a rollback. A MUST-class audit that cannot be written at all
   **fails closed**: the caller receives `503 audit_unavailable`.
   **SHOULD/MAY-class** entries stay best-effort — written on the same outbound
   pass, logged on failure, never blocking the business operation.
4. **`TenantContextBehavior`** — Asserts `ITenantContext.IsResolved` (the
   `TenantResolverMiddleware` populated it from the inbound HTTP request,
   the Hangfire `JobActivator` populated it from the job payload, or the
   integration-event handler scope populated it from the event envelope)
   and carries the resolved tenant + organization forward for the rest of
   the pipeline. Unresolved context short-circuits with
   `Result.Fail(tenant_mismatch)` unless the request carries
   `[AllowsUnresolvedTenantContext]`. The behavior also rejects a request whose
   `TenantContextOrigin` exceeds what the request type permits — a `HostOnly`
   context reaches only `[PublicSurface]` request types, enumerated in
   [04-api-design.md § Public surface](04-api-design.md) and bounded by
   [ADR-0036 § The reconciliation matrix](../decisions/0036-tenant-resolution-trusted-inputs.md).

   This behavior does **not** set the PostgreSQL session variables. It runs
   at step 4; the transaction opens at step 6; and
   `set_config('app.tenant_id', …, true)` / `SET LOCAL` are
   **transaction-local**, so a value set here is discarded before the
   transaction that needs it ever begins. The same objection rules out a
   `DbConnectionInterceptor`, which fires at connection open rather than at
   transaction start.
   [Security Standards § Tenant Context](11-security.md) is the single
   authority for where the session variables are set; the canonical policy
   template that reads them lives in
   [Database Standards § Tenant-Owned and Organization-Scoped Tables](05-database.md).
5. **`AuthorizationBehavior`** — `IAuthorizationService.AuthorizeAsync`
   against the command's resource. Denial returns
   `Result.Fail(forbidden)`; no exception.
6. **`TransactionBehavior`** — Opens the ambient transaction through
   `IUnitOfWork` and, as its **first statement inside that transaction**,
   issues `SET LOCAL app.tenant_id` / `app.organization_id` from the
   `ITenantContext` step 4 asserted, so Row Level Security evaluates every
   subsequent statement — including the MUST-class audit insert — against
   the right values. Commits on a success-`Result`; rolls back on a
   fail-`Result` or any exception that bubbles through. No transaction for
   forbidden or validation-failed requests because those short-circuit
   upstream.

   This behavior owns the **commit boundary**, and therefore owns two further
   responsibilities per
   [ADR-0033](../decisions/0033-audit-durability-model.md). First, immediately
   before `COMMIT` it calls `IAuditStore.WritePendingAsync`, which inserts the
   complete MUST-class audit row on this transaction — a no-op when no intent
   is pending, and a rollback plus `audit_unavailable` when it fails. Placing
   the write here rather than in the EF interceptor is deliberate: at
   pre-commit every flush has happened, so the row's snapshots are complete
   however many times the handler saved. Second, it records the outcome on
   `IAuditStateCapture` — `Committed` once `CommitAsync` returns, `RolledBack`
   after a rollback, `Indeterminate` when `CommitAsync` faults and the
   server-side result is genuinely unknown. That signal is the only thing step
   3's reconcile pass trusts.
7. **`OutboxFlushBehavior`** — Per
   [15-event-and-outbox.md](../architecture/15-event-and-outbox.md), enrols
   `IOutbox` messages in the current transaction; the dispatcher ships them
   through the registered `IEventBus` after commit. The registered
   implementation is `InProcessEventBus` until the Dapr adapter's trigger
   fires ([ADR-0035](../decisions/0035-demand-gated-infrastructure.md));
   handler code is identical either way, which is the point of the port.
8. **Handler** — domain logic; returns `Result<T>`. **No** `throw new
   DomainException` for expected business-rule violations — use
   `Result.Fail(business_rule_violation, ...)`. The
   `LearnStackException-DomainExceptionThrow` Roslyn analyzer
   ([ADR-0032 § Sub-decision 4](../decisions/0032-exception-handling-logging-and-observability.md))
   flags violations.

The pipeline does **not** include a separate `ExceptionHandlingBehavior`.
`AuditLogBehavior`'s catch-and-rethrow + the L1 `IExceptionHandler`
([ADR-0032 § Sub-decision 1](../decisions/0032-exception-handling-logging-and-observability.md))
together cover every exception path; a third behavior would duplicate the
responsibility.

Architecture test
[`MediatR_Pipeline_Order_Matches_Canonical_Sequence`](21-architecture-tests-catalogue.md#mediatr_pipeline_order_matches_canonical_sequence)
asserts the DI registration order at startup; the test fails the build if
any behavior is missing, reordered, or duplicated. The catalogue entry in
[21-architecture-tests-catalogue.md](21-architecture-tests-catalogue.md) is
the canonical reference for this identifier.

## Time

- Use `IClock` (or `TimeProvider` from .NET 8+) — never `DateTime.Now` / `DateTimeOffset.UtcNow` in domain or application code.
- Persist times in UTC.
- Convert to user / tenant timezone only at presentation boundaries.

## Configuration

- Strongly-typed options bound via `IOptions<TOptions>`.
- Options classes annotated with `[OptionsValidator]` and validators.
- Configuration sources, in order: environment variables, secret manager, `appsettings.{env}.json`, `appsettings.json`.
- No secrets in code, no secrets in git.

## Logging

- Use `ILogger<T>` with structured logging.
- Never log secrets, passwords, tokens, or full payment payloads.
- See [Observability Standards](10-observability.md) for tag conventions.

## Forbidden

- `dynamic` (except at provider-SDK boundaries with explicit justification).
- `Task.Run` to escape async context.
- `Thread.Sleep` outside of well-explained tests.
- `unsafe` code outside justified hot paths.
- Static mutable state.
- Service-locator pattern (`ServiceProvider.GetService<T>` outside composition root).
- Reflection at runtime in domain code.
- Public mutable properties on aggregates.

## File Organization

- One public type per file (records inside a file may share if related).
- Files match the type name.
- Test files mirror the structure of the source folder.

## Comments

- Comment only when the *why* is non-obvious.
- Don't restate the code in prose.
- Public APIs should have an XML doc comment when consumed across module boundaries.
- TODO comments include a date and an owner (`// TODO(YYYY-MM-DD, @owner): ...`).
