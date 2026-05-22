# 21 — Architecture Tests + Analyzers Catalogue

**Status:** Active
**Derives from:** [ADR-0032 Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md)
(ships the first batch of catalogue entries). The catalogue grows as
subsequent ADRs and phases land their tests; per-test ownership stays with
the originating ADR / standard.

The single source of truth for the **identifier**, the **assertion**, the
**source ADR / standard**, and the **scope** of every non-skippable rule
LearnStack enforces at build time — whether the rule lives in the
`LearnStack.Tests.Architecture` assembly (xUnit / NetArchTest) or in a
compile-time Roslyn analyzer under `backend/analyzers/`.

## Why a catalogue

Identifier names propagate across ADRs, standards, roadmap deliverables,
glossary entries, and SKILL.md files. A rename or relocation forces an edit
to every cross-link site. Centralising the registry keeps **one** name
canonical; other documents cite the catalogue entry by anchor link
(`21-architecture-tests-catalogue.md#<test-name>`) so the next rename touches
exactly one line.

The catalogue is **not** a substitute for the originating ADR / standard —
the rule still lives there. The catalogue only owns the **name**, the
**short assertion**, and the **pointer back**.

## How to add an entry

When a new test or analyzer lands:

1. Pick a name. Convention: `Subject_Constraint`
   (e.g. `Modules_Do_Not_Reference_DeploymentMode`). Don't bake an ADR
   number into the identifier (architecture tests are read by humans years
   after the ADR is superseded; the test name should age well). Cite the
   ADR in the test's `[Description]` / `[FactDescription]` attribute, not
   in the type name.
2. Add a row to the right section table below.
3. Cite the catalogue entry from the originating doc:
   `[name](../standards/21-architecture-tests-catalogue.md#name-lowercased-with-dashes)`.

When a test is renamed:

1. Edit the catalogue row first.
2. `git grep` the old name across `docs/`, `.claude/`, `CLAUDE.md` and
   replace; the count should be small because everything points back here.
3. Update the test code last.

When a test is retired:

1. Move the row to the "Retired" section at the bottom with a one-line note
   on why and which commit.
2. Leave the anchor in place so old links don't 404; the row's body says
   "retired — see <new test name>" or "obsolete — replaced by …".

## Naming convention

| Convention | Example |
|---|---|
| Architecture test class / fact | `Subject_Constraint`: `Modules_Do_Not_Reference_DeploymentMode`, `Every_TenantOwned_Command_HasAuditCoverage`, `MediatR_Pipeline_Order_Matches_Canonical_Sequence` |
| Roslyn analyzer **rule name** | `LearnStackException-<Topic>`: `LearnStackException-DomainExceptionThrow` |
| Roslyn analyzer **diagnostic id** | `LS####` (valid C# identifier): `LS0001` |

A Roslyn diagnostic id **must be a valid identifier** (letters/digits, no
hyphens) — Roslyn raises `AD0001` at report time otherwise (see ADR-0032
Amendment 1). The hyphenated `LearnStackException-<Topic>` form is the
human-readable **rule name** carried in the analyzer title / help text; the
wire-level **diagnostic id** is the `LS####` form. The architecture-test
fact namespace and both analyzer namespaces are disjoint, so identifiers
never collide.

## Catalogue

### Cross-cutting: error handling, logging, observability

Source: [ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md).
Ships in [Phase 02a](../roadmap/phase-02a-kernel-tenancy.md) (unless noted
otherwise).

#### `IExceptionHandler_Registered_AtStartup`

- **Asserts:** every backend host registers a single
  `IExceptionHandler` implementation (`LearnStackExceptionHandler`); the
  legacy `app.UseExceptionHandler(lambda)` and inline `app.Use((ctx, next)
  => {...})` patterns are absent.
- **Source:** ADR-0032 § Sub-decision 1.
- **Type:** xUnit + service-collection inspection.
- **Phase:** 02a.

#### `MediatR_Pipeline_Order_Matches_Canonical_Sequence`

- **Asserts:** the MediatR DI registration order at startup is exactly
  `Validation → Logging → AuditLog → TenantContext → Authorization →
  Transaction → OutboxFlush → Handler`. No `ExceptionHandlingBehavior` is
  registered; no extra behaviors are inserted between the eight canonical
  steps.
- **Source:** ADR-0032 § Sub-decision 2;
  [02-backend-coding.md § Pipeline Behaviors](02-backend-coding.md).
- **Type:** xUnit + reflection over `IServiceCollection`.
- **Phase:** 02a.

#### `ValidationBehavior_DoesNotThrow_ValidationException`

- **Asserts:** triggering a validation failure end-to-end through the
  MediatR pipeline produces a `Result.Fail(validation_failed, errors)`
  outcome; a `FluentValidation.ValidationException` never escapes the
  behavior into the handler scope or up to L1.
- **Source:** ADR-0032 § Sub-decision 3.
- **Type:** unit (`LearnStack.Tests.Unit` —
  `ValidationBehaviorTests.Never_Throws_ValidationException`) **+**
  HTTP-level integration (`LearnStack.Tests.Integration` —
  `CrossCuttingFoundationHttpTests.ValidationBehavior_Returns_400_ProblemDetails_For_Invalid_Command`,
  via `WebApplicationFactory<Program>` and the
  `CrossCuttingTestController.validate` endpoint). The integration
  variant lights up the full controller → MediatR pipeline → Problem
  Details body shape so a regression at any layer surfaces.
- **Phase:** 02a (Packet 3 — both variants shipped).

#### `Domain_Methods_Do_Not_Throw_For_Expected_Cases`

- **Asserts:** the Roslyn analyzer `LearnStackException-DomainExceptionThrow`
  (diagnostic id `LS0001`) produces zero Warnings inside `Domain` +
  `Application` projects of every module. Walks `Result<T>`-returning
  methods and asserts the analyzer report is empty for the module.
- **Source:** ADR-0032 § Sub-decision 4;
  [09-error-handling.md § Domain Exceptions](09-error-handling.md).
- **Type:** xUnit + Roslyn analyzer report inspection.
- **Status:** **Deferred** — not yet implemented as a discrete architecture
  test. The enforcement it represents is already live: the `LS0001`
  analyzer runs in every module's `Domain` + `Application` build and the
  `DomainExceptionThrowAnalyzerTests` unit tests lock its behaviour. The
  report-walking architecture test lands when module domain code exists to
  walk (Packet 6+). Until then this row documents the intent, not a shipped
  test.
- **Phase:** target 02a (Warning); escalates to Error after Phase 03 exit.

#### `LearnStackException-DomainExceptionThrow` (Roslyn analyzer)

- **Rule name:** `LearnStackException-DomainExceptionThrow`.
- **Diagnostic id:** `LS0001` (Roslyn ids must be valid identifiers; the
  hyphenated rule name is the human-readable title — see ADR-0032
  Amendment 1).
- **Asserts:** every `throw new DomainException(...)` is flagged so the
  "DomainException = programmer error" discipline is mechanical, not
  reviewer-dependent. Genuine aggregate-invariant throws (the sanctioned
  use) are the rare sites that suppress with justification
  (`#pragma warning disable LS0001`). The analyzer ships in
  `backend/analyzers/LearnStack.Analyzers` and is referenced by every
  module's `Domain` + `Application` project (and the core `LearnStack.Domain`
  / `LearnStack.Application`) via
  `<ProjectReference ... OutputItemType="Analyzer" ReferenceOutputAssembly="false" />`
  (NOT a PackageReference — the analyzer is built in-tree, not packed).
- **Severity:** Warning in Phase 02a; listed in `WarningsNotAsErrors`
  (Directory.Build.props) so it does not break CI under
  `TreatWarningsAsErrors` before the documented escalation. Flipped to Error
  (and removed from `WarningsNotAsErrors`) after the Phase 03 exit gate is
  green across all modules.
- **Tests:** `DomainExceptionThrowAnalyzerTests`
  (`LearnStack.Tests.Unit`) runs the analyzer over synthetic compilations
  and asserts `LS0001` is reported (and no `AD0001` crash).
- **Source:** ADR-0032 § Sub-decision 4 + Amendment 1;
  [09-error-handling.md § Domain Exceptions](09-error-handling.md).
- **Phase:** 02a.

#### `Handlers_Return_Result`

- **Asserts:** every `IRequestHandler<TRequest, TResponse>` implementation
  in a `*.Application` assembly has `TResponse : IResultBase`. A handler
  that returns a raw DTO would satisfy none of the
  `where TResponse : IResultBase`-constrained pipeline behaviors and so
  would silently bypass validation / audit / tenant-context + RLS.
- **Source:** ADR-0032 § Sub-decision 2;
  [02-backend-coding.md § MediatR Use Cases](02-backend-coding.md).
- **Type:** xUnit + reflection over `IRequestHandler<,>` implementations.
- **Phase:** 02a (Packet 3 — lands now while the pipeline contract is
  fresh; vacuous until handlers exist, active the moment they land).

#### `Adapters_Wrap_Provider_Exceptions`

- **Asserts:** provider SDK exception types (`LiveKit.NET.LiveKitException`,
  `Stripe.StripeException`, `Meilisearch.MeilisearchApiError`,
  `SeaweedFS.S3Exception`, …) appear only inside
  `LearnStack.Infrastructure.<Adapter>` namespaces. They never escape into
  `Application`, `Domain`, or another adapter's namespace.
- **Source:** ADR-0032 § Sub-decision 5;
  [09-error-handling.md § Provider Failures](09-error-handling.md).
- **Type:** xUnit + NetArchTest.
- **Phase:** 02a.

#### `Modules_Do_Not_Reference_Sentry_SDK_Directly`

- **Asserts:** no module assembly (`LearnStack.Modules.*.{Domain,Application,Infrastructure}`)
  has a transitive dependency on `Sentry.*` packages. Only
  `LearnStack.Infrastructure.ErrorTracking` may reference the Sentry SDK.
- **Source:** ADR-0032 § Sub-decision 9;
  [09-error-handling.md § L1 Exception Handler](09-error-handling.md);
  [20-infrastructure-stack.md § Forbidden](20-infrastructure-stack.md).
- **Type:** xUnit + assembly-dependency walk.
- **Phase:** 02a.

#### `Logging_Goes_Through_Microsoft_Extensions_Logging`

- **Asserts:** no module assembly imports `Serilog.ILogger` or
  `Serilog.Log.*`. Module code logs through
  `Microsoft.Extensions.Logging.ILogger<T>` (injected); Serilog is the
  implementation wired once at the composition root.
- **Source:** ADR-0032 § Sub-decision 8;
  [10-observability.md § Stack](10-observability.md).
- **Type:** xUnit + NetArchTest.
- **Phase:** 02a.

#### `Modules_Do_Not_Reference_DeploymentMode`

- **Asserts:** no module assembly references
  `LearnStack.SharedKernel.Hosting` (the namespace that owns
  `DeploymentMode`). The composition root is the only sanctioned read
  site per Standards 20 § Composition Root and Deployment Mode.
- **Source:** ADR-0020;
  [20-infrastructure-stack.md § Composition Root and Deployment Mode](20-infrastructure-stack.md).
- **Type:** xUnit + NetArchTest.
- **Phase:** 02a (Packet 3 — landed alongside the cross-cutting tests).

#### `OTel_Pipeline_Includes_TenantContextSpanProcessor`

- **Asserts:** the registered OpenTelemetry tracing pipeline includes the
  `TenantContextSpanProcessor`. Fails if a future composition-root edit
  removes the processor.
- **Source:** ADR-0032 § Sub-decision 10.
- **Type:** xUnit + service-collection inspection of
  `IOptions<OpenTelemetryTracerOptions>`.
- **Phase:** 02a.

#### `TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing`

- **Asserts:** `TenantContextSpanProcessor.OnStart(activity)` does not
  throw when `ITenantContextAccessor.Current` is `null` (warm-up
  `Activity` instances created during startup, background tasks before
  any scope populated the accessor).
- **Source:** ADR-0032 § Sub-decision 10.
- **Type:** xUnit unit test.
- **Phase:** 02a.

#### `Outbox_Row_Carries_Correlation_Context`

- **Asserts:** every persisted `outbox_messages` row has non-null
  `tenant_id` and `correlation_id` columns. Integration test that writes
  through `IOutbox.EnqueueAsync` and inspects the row.
- **Source:** ADR-0032 § Sub-decision 12;
  [ADR-0006](../decisions/0006-events-and-outbox.md) Amendment 1.
- **Type:** integration test (Testcontainers).
- **Phase:** 02b.

#### `Hangfire_Job_Payloads_Include_TenantId`

- **Asserts:** Hangfire enqueue rejects job payloads missing `tenant_id`
  or `correlation_id`. Per the `JobActivator` contract the enqueue path
  fails at submission, not at activation, so the failure mode is loud.
- **Source:** ADR-0032 § Sub-decision 12; Phase 02b deliverable.
- **Type:** xUnit + Hangfire enqueue interceptor test.
- **Phase:** 02b.

#### `Integration_Event_Handler_Restores_Tenant_Context`

- **Asserts:** when an outbox consumer dispatches an integration event,
  the inner handler scope has `ITenantContext.IsResolved == true` before
  business code runs. Verifies the envelope-to-context restoration.
- **Source:** ADR-0032 § Sub-decision 12; Phase 02b deliverable.
- **Type:** integration test (Testcontainers + Dapr sidecar).
- **Phase:** 02b.

### Earlier ADRs (to be backfilled)

Existing architecture tests already cited from other ADRs / standards live
in their respective docs. They will be migrated into this catalogue in
follow-up PRs as their text is touched (no rewrite-for-rewrite churn);
until then the originating doc remains the single reference. Known
identifiers awaiting migration:

- ADR-0003 / ADR-0017 — tenant + organization isolation:
  `Every_TenantOwned_Command_HasAuditCoverage`,
  `Every_OrgScoped_Entity_HasOrgIdAndFilter`.
- ADR-0014 — Dapr building blocks:
  `Dapr_SDK_Types_NotImportedOutsideInfrastructure`,
  `Modules_DoNotReference_DaprPackage`,
  `ICacheService_Is_OnlyCacheAbstraction`,
  `Dapr_PubSub_TopicNames_FollowConvention`.
- ADR-0016 — audit subsystem:
  `AuditEntry_Inherits_Entity_Not_AuditableEntity`,
  `AuditEntry_Is_AppendOnly`,
  `AuditLogBehavior_NeverBlocks_BusinessWrites`,
  `Modules_Do_Not_Write_AuditLog_Directly`,
  `OperationType_Enum_Matches_Catalog`.
- ADR-0018 — domain-specific names forbidden:
  `Core_Modules_HaveNo_DomainSpecific_Names`,
  `No_Source_Folder_Named_Verticals`.
- ADR-0019 — Hub HTTPS contract:
  `LearnStack_Modules_DoNotReference_Hub`.
- ADR-0020 — entitlement providers:
  `IEntitlementProvider_Implementations_Are_Three`,
  `NullEntitlementProvider_NotRegistered_OutsideDevelopment`,
  `LicenseKey_Validation_Is_Pinned_RSA2048`.
- Standards 20 — composition-root + direct-injection bans:
  `Modules_Do_Not_Inject_Valkey_Directly`,
  `Modules_Do_Not_Read_Entitlement_Cache_Directly`.
  (`Modules_Do_Not_Reference_DeploymentMode` migrated to the main
  catalogue below in Phase 02a Packet 3 — see the dedicated entry.)

The next PR that edits any of these source documents folds the
corresponding row in here.

### Retired

(none yet)

## References

- [ADR-0032 Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md)
- [02-backend-coding.md § Pipeline Behaviors](02-backend-coding.md)
- [09-error-handling.md](09-error-handling.md)
- [10-observability.md](10-observability.md)
- [20-infrastructure-stack.md](20-infrastructure-stack.md)
- [Phase 02a Roadmap § Architecture Tests](../roadmap/phase-02a-kernel-tenancy.md)
- [Phase 02b Roadmap § Architecture Tests](../roadmap/phase-02b-events-auth.md)
- [add-architecture-test skill](../../.claude/skills/add-architecture-test/SKILL.md)
