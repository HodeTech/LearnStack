# 02 — Backend Coding Standards

**Status:** Active
**Derives from:** [ADR 0002 — Initial Architecture](../decisions/0002-initial-architecture.md), [ADR 0006 — Events and Outbox](../decisions/0006-events-and-outbox.md).

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
- **Strongly-typed ids** (`record struct CourseId(Guid Value) : IStronglyTypedId`) for all entity identifiers. Never expose raw `Guid` on the public surface.
- **Value objects** for domain concepts with invariants (e.g. `Email`, `Slug`, `LocaleCode`).

## Strongly-Typed Identifiers

```csharp
public readonly record struct CourseId(Guid Value) : IStronglyTypedId<Guid>
{
    public static CourseId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}
```

A shared source generator (or analyzer pack) emits:
- EF Core value converter.
- `JsonConverter`.
- Minimal API model binder.
- OpenAPI schema mapping.

## Nullability

- `Nullable` is on. Treat warnings as errors.
- Reference types are non-nullable unless declared `T?`.
- Never use `!` (null-forgiving operator) without a comment explaining why.
- Prefer `ArgumentNullException.ThrowIfNull(param)` at public boundaries.
- Return `Result<T>` or `Maybe<T>` for expected absences; reserve null for true uninitialized state.

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
public sealed record Result<T>(bool IsSuccess, T? Value, Error? Error)
{
    public static Result<T> Ok(T value) => new(true, value, null);
    public static Result<T> Fail(Error error) => new(false, default, error);
}

public sealed record Error(string Code, string Message, IReadOnlyDictionary<string, string[]>? Details = null);
```

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

## Pipeline Behaviors

Standard MediatR pipeline (in order):

1. Logging / tracing / correlation propagation.
2. Validation (FluentValidation).
3. Tenant context check.
4. Authorization.
5. Transaction.
6. Outbox flush.
7. Handler.

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
