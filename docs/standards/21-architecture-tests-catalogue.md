# 21 — Architecture Tests + Analyzers Catalogue

**Status:** Active
**Derives from:** [ADR-0032 Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md)
(ships the first batch of catalogue entries). The catalogue grows as
subsequent ADRs and phases land their tests; per-test ownership stays with
the originating ADR / standard.

The single source of truth for the **identifier**, the **assertion**, the
**source ADR / standard**, the **scope**, and the **implementation status** of every
non-skippable rule LearnStack enforces at build time — whether the rule lives in the
`LearnStack.Tests.Architecture` assembly (xUnit / NetArchTest), in a sibling test
assembly, or in a compile-time Roslyn analyzer under `backend/analyzers/`.

## Why a catalogue

Identifier names propagate across ADRs, standards, roadmap deliverables,
glossary entries, and SKILL.md files. A rename or relocation forces an edit
to every cross-link site. Centralising the registry keeps **one** name
canonical; other documents cite the catalogue entry by anchor link
(`21-architecture-tests-catalogue.md#<test-name>`) so the next rename touches
exactly one line.

The catalogue is **not** a substitute for the originating ADR / standard —
the rule still lives there. The catalogue only owns the **name**, the
**short assertion**, the **status**, and the **pointer back**.

That was the theory. In practice the drift this document exists to prevent had already
happened before a single named test was written: **six** competing spellings of the
tenant-isolation rule across eleven files, and **five** of the organization-scope rule.
§ Canonical names and superseded spellings reconciles them. Reconciling now is a
find-and-replace in Markdown; reconciling once the tests exist is a refactor across a
dozen files plus the test code.

## What a structural test proves — and what it does not

**A structural assertion that a policy *exists* is not a proof that it *isolates*.**

This is not a hypothetical distinction. The Row Level Security template that
[ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md)
supersedes satisfied every structural assertion in this catalogue — the table had RLS
enabled, it had a policy, the policy named `app.tenant_id`, the entity carried
`[TenantOwned]`, the EF filter was present — while making **every tenant-wide row
visible to every tenant**. It created two *permissive* policies, and PostgreSQL combines
permissive policies with `OR`, so the second policy widened access instead of narrowing
it. A test that asserts shape would have been green throughout.

A structure-shaped test that passes against a broken policy is worse than no test,
because it converts an open question into a false answer.

Two consequences, both binding:

- **Structural assertions stay.** They are cheap, they run on every build, and they
  catch the common failure — someone forgot the policy entirely. They are a *coverage*
  check, not a *correctness* check, and this catalogue labels them as such.
- **The binding proof of isolation is runtime.** Isolation is a property of what a
  query returns, and only a query can observe it. The proof lives in the
  [Phase 02a Packet 7](../roadmap/phase-02a-kernel-tenancy.md) integration suite, which
  connects as **`learnstack_app`** — a non-owning, `NOBYPASSRLS` role. A test that
  connects as the table owner or as a `BYPASSRLS` role passes even when every policy on
  every table is inert, and therefore proves nothing at all.

Rows in this catalogue carry a **Kind** of *structural*, *runtime*, or *compile-time* so
a reader can tell which question the test answers.

## Implementation status

Every row below carries a status:

| Status | Meaning |
|---|---|
| **Implemented** | The test exists, runs in CI, and can fail. The row names the file. |
| **Registered** | The name is reserved and the assertion is agreed; no code yet. The row names the owning phase or packet. A registered test is a commitment, not a claim. |
| **Awaiting backfill** | Decided and reserved like *Registered*, but blocked on something that does not exist yet rather than on someone writing it — usually the first code that could violate it. The row names what it waits for. |
| **Retired** | Moved to § Retired with the reason and the replacement. |

Each row also carries a **Kind**:

| Kind | Asserts |
|---|---|
| **structural** | A shape — types, references, attributes, configuration |
| **behavioural** | What the real thing does when started or exercised |
| **compile** | That the build fails, via an analyzer diagnostic |
| **runtime** | That the host refuses to start, via a composition-root guard |

A structural assertion that a rule *exists* is not a proof that it *holds*; see
§ What a structural test proves — and what it does not. `compile` and `runtime`
are the two kinds that fail *before* anything can be observed misbehaving, which
is why they are named separately rather than folded into `structural`.

Claiming a rule is "enforced by an architecture test" when the test is registered but
not implemented is the failure mode this column exists to prevent.

### Implemented today

Fifty-nine test methods exist in
[`backend/tests/LearnStack.Tests.Architecture`](../../backend/tests/LearnStack.Tests.Architecture),
shipped by [Phase 01](../roadmap/phase-01-repository-tooling.md),
[Phase 02a Packets 2–3](../roadmap/phase-02a-kernel-tenancy.md), Packet 4,
Packet 6 and Packet 7 — 77 cases once the theories expand. Counted from a run at
Packet 7's close; the previous figures were Packet 6's and were not updated when
Packet 7 added its rules. Methods are not rows: a `[Theory]`
is one row and many cases, and several rows pair a rule with the companion
assertion that stops it passing vacuously.

**Not every implemented rule lives in that assembly.** Packet 4 added eight
rules there — four API-convention ones
(`Live_Majors_Are_At_Most_Two_Adjacent`,
`Unversioned_Route_Prefixes_Are_Declared_Once`,
`Forwarded_Headers_Are_Not_Wired`, `Deployment_Mode_Is_Required_Configuration`)
and the four ADR-0036 tenancy-edge scans in `TenancyConventionTests` — plus
**six** behavioural rows in
[`backend/tests/LearnStack.Tests.Integration`](../../backend/tests/LearnStack.Tests.Integration),
all under § API conventions: `Every_Endpoint_Is_Under_Versioned_Route`, the four
startup guards (`An_Absolute_Controller_Route_Fails_At_Startup` and
`An_Absolute_Action_Route_Fails_At_Startup` share one row,
`A_Major_Outside_LiveMajors_Fails_At_Startup`,
`A_Bare_ControllerBase_Fails_At_Startup`, and
`A_Hand_Written_Prefix_That_Disagrees_With_The_Attribute_Fails_At_Startup`), and
`An_Absolute_Internal_Route_Is_Exempt_At_Both_Levels`, which is a guard's mirror
rather than a guard — it asserts a host that *does* start. Rows are not test
methods: `VersionedRouteEnforcementTests` carries ten, because several rows pair
a rule with the companion assertion that stops it passing vacuously. Both assemblies run in the same required `backend` CI check,
and a rule belongs where it can actually fail: the route-shape rule was
originally written as a reflection scan in the architecture assembly and passed
against a host serving unversioned endpoints.

| Test | File |
|---|---|
| `MediatR_Pipeline_Order_Matches_Canonical_Sequence` | `CrossCuttingFoundationTests.cs` |
| `IExceptionHandler_Registered_AtStartup` | `CrossCuttingFoundationTests.cs` |
| `OTel_Pipeline_Includes_TenantContextSpanProcessor` | `CrossCuttingFoundationTests.cs` |
| `Logging_Goes_Through_Microsoft_Extensions_Logging` | `CrossCuttingFoundationTests.cs` |
| `Modules_Do_Not_Reference_Sentry_SDK_Directly` | `CrossCuttingFoundationTests.cs` |
| `Adapters_Wrap_Provider_Exceptions` | `CrossCuttingFoundationTests.cs` |
| `Handlers_Return_Result` | `CrossCuttingFoundationTests.cs` |
| `Modules_Do_Not_Reference_DeploymentMode` | `CrossCuttingFoundationTests.cs` |
| `IErrorTrackingProvider_Is_Singleton` | `CrossCuttingFoundationTests.cs` |
| `Modules_Do_Not_Inject_IEventBus_Directly` | `CrossCuttingFoundationTests.cs` |
| `Integration_Event_TopicNames_FollowConvention` | `CrossCuttingFoundationTests.cs` |
| `ModuleDomain_DoesNotDependOn_OtherModuleDomain` (per-module theory) | `ModuleDependencyTests.cs` |
| `ModuleDomain_DoesNotDependOn_AnyApplicationOrInfrastructure` (per-module theory) | `ModuleDependencyTests.cs` |
| `Meta_NetArchTest_DetectsAPlantedViolation` | `ModuleDependencyTests.cs` |
| `Live_Majors_Are_At_Most_Two_Adjacent` | `ApiConventionTests.cs` |
| `Unversioned_Route_Prefixes_Are_Declared_Once` | `ApiConventionTests.cs` |
| `Forwarded_Headers_Are_Not_Wired` | `ApiConventionTests.cs` |
| `Deployment_Mode_Is_Required_Configuration` | `ApiConventionTests.cs` |
| `Effective_Host_Computed_In_One_Place` | `TenancyConventionTests.cs` |
| `Tenant_Headers_Are_Never_A_Resolution_Source` | `TenancyConventionTests.cs` |
| `Assertion_Recorder_Is_The_Only_Mismatch_Writer` | `TenancyConventionTests.cs` |
| `Assertion_Budget_Does_Not_Depend_On_ICacheService` | `TenancyConventionTests.cs` |
| `Organization_Aggregate_Declared_In_Tenancy_Domain` (per-type theory) | `TenancyConventionTests.cs` |
| `Aggregates_With_Optimistic_Concurrency_Map_RowVersion` | `PersistenceConventionTests.cs` |
| `Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork` | `PersistenceConventionTests.cs` |
| `The_registration_marker_does_not_vouch_across_containers` | `PersistenceConventionTests.cs` |
| `Every_Database_Test_Carries_The_Docker_Trait` | `PersistenceConventionTests.cs` |
| `Migrate_Target_Refuses_An_Aliased_Runtime_Credential` (per-alias theory) | `PersistenceConventionTests.cs` |
| `Migrate_Target_Redacts_A_Quoted_Value_Whole` (per-shape theory) | `PersistenceConventionTests.cs` |
| `Migrate_Target_Reads_The_Role_Through_A_Quoted_Value` | `PersistenceConventionTests.cs` |
| `Migrate_Target_Refuses_A_Uri_Without_Echoing_Its_Userinfo` | `PersistenceConventionTests.cs` |
| `TransactionBehavior_Does_Not_Reference_A_Module_Assembly` | `PersistenceConventionTests.cs` |
| `Migration_Startup_Project_References_EntityFrameworkCore_Design` | `PersistenceConventionTests.cs` |
| `Migrate_Target_Covers_Every_Migration_Chain` | `PersistenceConventionTests.cs` |
| `No_Source_Folder_Named_Verticals` | `RepositoryLayoutTests.cs` |
| `Frontend_Has_Only_The_Web_App` | `RepositoryLayoutTests.cs` |

Seven further rules in this catalogue are **implemented outside** that assembly and are
no less binding. Four of them could not live in it: a policy that is well-formed
and wrong, or a foreign key with no index, is only visible against an applied
schema.

| Rule | Where |
|---|---|
| `ValidationBehavior_DoesNotThrow_ValidationException` | `LearnStack.Tests.Unit` + `LearnStack.Tests.Integration` |
| `TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing` | `LearnStack.Tests.Unit` |
| `SoftDelete_Advances_The_Row_Version` | `LearnStack.Tests.Unit` (`AuditableEntityTests`) |
| `TenantWide_Row_Of_TenantB_Is_Invisible_To_TenantA` | `LearnStack.Tests.Integration` (`TenancySchemaTests`) |
| `Write_With_Foreign_TenantId_Is_Rejected_By_WithCheck` | `LearnStack.Tests.Integration` (`TenancySchemaTests`) |
| `Every_Foreign_Key_Has_A_Supporting_Index` | `LearnStack.Tests.Integration` (`TenancySchemaTests`) |
| `LearnStackException-DomainExceptionThrow` (`LS0001`) | `backend/analyzers/LearnStack.Analyzers` + `DomainExceptionThrowAnalyzerTests` |

`Meta_NetArchTest_DetectsAPlantedViolation` deserves its own note: it plants a forbidden
dependency and asserts NetArchTest **finds** it. If that meta-test ever passes in the
inverted sense — NetArchTest reporting the planted dependency as absent — every other
NetArchTest-based row in this catalogue is vacuously green. Keep it in perpetuity.

Every other rule in this document carries its own **Status** line, and that line —
not this section — is the authority. This index is a reader's orientation and goes
stale the moment a packet closes a row without updating it; the Status column is
what a reviewer checks.

## Canonical names and superseded spellings

One rule, one identifier. When a document, skill, or comment uses a superseded spelling,
the fix is to replace it with the canonical name — not to add a row here.

### Tenant isolation

**Canonical: `Every_TenantOwned_Entity_HasFilterAndRlsPolicy`** — one rule covering the
marker, the EF global query filter, and the table's RLS policy. Splitting it into an
entity-level and a table-level test is what produced half the drift; a `[TenantOwned]`
entity without a policy and a table with a policy but no marker are the same defect seen
from two sides.

| Superseded spelling | Where it appeared |
|---|---|
| `Every_TenantOwned_Entity_HasTenantId` | [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md), [Tenant Isolation](../architecture/09-tenant-isolation.md) |
| `Every_TenantOwned_Entity_Has_TenantId` | [Platform / Tenant / Organization](../architecture/28-platform-tenant-organization.md), `add-tenant-owned-entity`, `run-tests-locally` skills |
| `Every_TenantOwned_Entity_HasTenantIdAndFilter` | [Tenant Isolation](../architecture/09-tenant-isolation.md) |
| `Every_TenantOwned_Table_HasRlsPolicy` | [Tenant Isolation](../architecture/09-tenant-isolation.md) |
| `Every_TenantOwned_Table_HasRls_With_AppTenantId` | `add-ef-migration`, `add-tenant-owned-entity`, `add-architecture-test`, `run-tests-locally` skills |

### Organization scope

**Canonical: `Every_OrgScoped_Entity_HasOrgIdAndFilter`** — nullable `OrganizationId`
column, org-aware EF filter, and the organization term `AND`-ed into the table's single
policy.

| Superseded spelling | Where it appeared |
|---|---|
| `Every_OrgScoped_Entity_HasOrganizationId_Nullable` | [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md) |
| `Every_OrgScoped_Entity_HasOrgQueryFilter` | [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md) |
| `Every_OrgScoped_Table_HasOrganizationRlsPolicy` | [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md) |
| `Every_OrgScoped_Table_HasOrgRlsPolicy` | [Tenant Isolation](../architecture/09-tenant-isolation.md) |

### Domain genericity

**Canonical: `Core_Modules_HaveNo_DomainSpecific_Names`** for the name rule, and
**`No_Source_Folder_Named_Verticals`** for the folder rule. They are two rules, not one:
the folder check has been green since Phase 01 and is the weaker of the pair — renaming a
folder was never the failure mode anyone worried about, `CefrLevel` on an Education
aggregate is.

| Superseded spelling | Where it appeared | Canonical name |
|---|---|---|
| `No_DomainSpecific_Names_In_Modules` | [ADR-0018 § Architecture tests](../decisions/0018-tenant-driven-customization-model.md), [Extension Model](../architecture/06-extension-model.md) | `Core_Modules_HaveNo_DomainSpecific_Names` |
| `No_Per_Vertical_Folders` | [ADR-0018 § Architecture tests](../decisions/0018-tenant-driven-customization-model.md) | `No_Source_Folder_Named_Verticals` |

ADR-0018 is Accepted and is not rewritten; the mapping lives here for the same reason
ADR-0017's spellings do. The **mutable** carriers — this catalogue, the standards, the
skills — carry the canonical names.

ADR-0018's own body keeps the superseded spellings, and under
[ADR-0041](../decisions/0041-correcting-false-statements-in-accepted-adrs.md) it must:
those names were canonicalized *after* ADR-0018 was accepted, so they were true when
they entered the record and are stale now, which is history rather than error. If the
drift ever needs to be visible in ADR-0018 itself, the instrument is a dated Amendment
or an inline erratum — never a rewrite.

The reconciliation is a [Phase 02a Packet 10](../roadmap/phase-02a-kernel-tenancy.md)
deliverable: the canonical names go green in CI and the superseded spellings disappear
from the corpus in the same pass.

## How to add an entry

When a new test or analyzer lands:

1. Pick a name. Convention: `Subject_Constraint`
   (e.g. `Modules_Do_Not_Reference_DeploymentMode`). Don't bake an ADR
   number into the identifier (architecture tests are read by humans years
   after the ADR is superseded; the test name should age well). Cite the
   ADR in the test's `[Description]` / `[FactDescription]` attribute, not
   in the type name.
2. Check § Canonical names and superseded spellings first. If the rule already has a
   canonical identifier, use it rather than minting a near-synonym.
3. Add a row to the right section table below, with **Status**, **Kind**, and **Phase**.
4. Cite the catalogue entry from the originating doc:
   `[name](../standards/21-architecture-tests-catalogue.md#name-lowercased-with-dashes)`.

When a rule is agreed but not yet written, register it with **Status: Registered** and
the owning packet. Registering costs one row and makes the gap legible; the alternative
is a rule that lives only in an ADR's implementation notes.

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
- **Type:** xUnit + service-collection inspection. **Kind:** structural.
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`.
- **Phase:** 02a (Packet 3).

#### `MediatR_Pipeline_Order_Matches_Canonical_Sequence`

- **Asserts:** the MediatR DI registration order at startup is exactly
  `Validation → Logging → AuditLog → TenantContext → Authorization →
  Transaction → OutboxFlush → Handler`. No `ExceptionHandlingBehavior` is
  registered; no extra behaviors are inserted between the eight canonical
  steps. The test asserts a hardcoded sequence rather than reading
  `CanonicalBehaviorOrder`, so an accidental reorder of the production list
  cannot slip past.
- **Source:** ADR-0032 § Sub-decision 2;
  [02-backend-coding.md § Pipeline Behaviors](02-backend-coding.md).
- **Type:** xUnit + reflection over `IServiceCollection`. **Kind:** structural.
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`.
- **Phase:** 02a (Packet 3).

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
  Details body shape so a regression at any layer surfaces. **Kind:** runtime.
- **Status:** **Implemented** — both variants, outside the Architecture assembly.
- **Phase:** 02a (Packet 3).

#### `Domain_Methods_Do_Not_Throw_For_Expected_Cases`

- **Asserts:** the Roslyn analyzer `LearnStackException-DomainExceptionThrow`
  (diagnostic id `LS0001`) produces zero Warnings inside `Domain` +
  `Application` projects of every module. Walks `Result<T>`-returning
  methods and asserts the analyzer report is empty for the module.
- **Source:** ADR-0032 § Sub-decision 4;
  [09-error-handling.md § Domain Exceptions](09-error-handling.md).
- **Type:** xUnit + Roslyn analyzer report inspection. **Kind:** compile-time.
- **Status:** **Registered.** The enforcement it represents is already live — the
  `LS0001` analyzer runs in every module's `Domain` + `Application` build and
  `DomainExceptionThrowAnalyzerTests` locks its behaviour — but the report-walking
  architecture test needs module domain code to walk, and none exists yet.
- **Phase:** 02a (Packet 10); severity escalates Warning → Error after Phase 03 exit.

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
- **Kind:** compile-time.
- **Status:** **Implemented** — analyzer + unit tests.
- **Phase:** 02a (Packet 3).

#### `Handlers_Return_Result`

- **Asserts:** every `IRequestHandler<TRequest, TResponse>` implementation
  in a `*.Application` assembly has `TResponse : IResultBase`. A handler
  that returns a raw DTO would satisfy none of the
  `where TResponse : IResultBase`-constrained pipeline behaviors and so
  would silently bypass validation / audit / tenant-context + RLS.
- **Source:** ADR-0032 § Sub-decision 2;
  [02-backend-coding.md § MediatR Use Cases](02-backend-coding.md).
- **Type:** xUnit + reflection over `IRequestHandler<,>` implementations.
  **Kind:** structural.
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`. Vacuous until handlers
  exist; the first real handlers arrive in
  [Phase 02d](../roadmap/phase-02d-walking-skeleton.md).
- **Phase:** 02a (Packet 3).

#### `Adapters_Wrap_Provider_Exceptions`

- **Asserts:** provider SDK exception types (`LiveKit.NET.LiveKitException`,
  `Stripe.StripeException`, `Meilisearch.MeilisearchApiError`,
  `SeaweedFS.S3Exception`, …) appear only inside
  `LearnStack.Infrastructure.<Adapter>` namespaces. They never escape into
  `Application`, `Domain`, or another adapter's namespace.
- **Source:** ADR-0032 § Sub-decision 5;
  [09-error-handling.md § Provider Failures](09-error-handling.md).
- **Type:** xUnit + NetArchTest. **Kind:** structural.
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`.
- **Phase:** 02a (Packet 3).

#### `Modules_Do_Not_Reference_Sentry_SDK_Directly`

- **Asserts:** no module assembly (`LearnStack.Modules.*.{Domain,Application,Infrastructure}`)
  has a transitive dependency on `Sentry.*` packages. Only
  `LearnStack.Infrastructure.ErrorTracking` may reference the Sentry SDK.
- **Source:** ADR-0032 § Sub-decision 9;
  [09-error-handling.md § L1 Exception Handler](09-error-handling.md);
  [20-infrastructure-stack.md § Forbidden](20-infrastructure-stack.md).
- **Type:** xUnit + assembly-dependency walk. **Kind:** structural.
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`.
- **Phase:** 02a (Packet 3).

#### `Logging_Goes_Through_Microsoft_Extensions_Logging`

- **Asserts:** no module assembly imports `Serilog.ILogger` or
  `Serilog.Log.*`. Module code logs through
  `Microsoft.Extensions.Logging.ILogger<T>` (injected); Serilog is the
  implementation wired once at the composition root.
- **Source:** ADR-0032 § Sub-decision 8;
  [10-observability.md § Stack](10-observability.md).
- **Type:** xUnit + NetArchTest. **Kind:** structural.
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`.
- **Phase:** 02a (Packet 3).

#### `Modules_Do_Not_Reference_DeploymentMode`

- **Asserts:** no module assembly references
  `LearnStack.SharedKernel.Hosting` (the namespace that owns
  `DeploymentMode`). The composition root is the only sanctioned read
  site per Standards 20 § Composition Root and Deployment Mode.
- **Source:** ADR-0020;
  [20-infrastructure-stack.md § Composition Root and Deployment Mode](20-infrastructure-stack.md).
- **Type:** xUnit + NetArchTest. **Kind:** structural.
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`.
- **Phase:** 02a (Packet 3).

#### `OTel_Pipeline_Includes_TenantContextSpanProcessor`

- **Asserts:** the registered OpenTelemetry tracing pipeline includes the
  `TenantContextSpanProcessor`. Fails if a future composition-root edit
  removes the processor.
- **Source:** ADR-0032 § Sub-decision 10.
- **Type:** xUnit + service-collection inspection of
  `IOptions<OpenTelemetryTracerOptions>`. **Kind:** structural.
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`.
- **Phase:** 02a (Packet 3).

#### `TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing`

- **Asserts:** `TenantContextSpanProcessor.OnStart(activity)` does not
  throw when `ITenantContextAccessor.Current` is `null` (warm-up
  `Activity` instances created during startup, background tasks before
  any scope populated the accessor).
- **Source:** ADR-0032 § Sub-decision 10.
- **Type:** xUnit unit test (`LearnStack.Tests.Unit`). **Kind:** runtime.
- **Status:** **Implemented** — outside the Architecture assembly.
- **Phase:** 02a (Packet 3).

#### `IErrorTrackingProvider_Is_Singleton`

- **Asserts:** exactly one `IErrorTrackingProvider` implementation is
  registered per host, with singleton lifetime, selected at the composition
  root by `DeploymentMode`.
- **Source:** ADR-0032 § Sub-decision 9.
- **Type:** xUnit + service-collection inspection. **Kind:** structural.
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`.
- **Phase:** 02a (Packet 3).

### Repository layout and module boundaries

#### `No_Source_Folder_Named_Verticals`

- **Asserts:** no directory named `Verticals` exists anywhere under
  `backend/src`. ADR-0018 supersedes ADR-0011; domain-specific shapes are tenant
  customization data, never a source folder.
- **Source:** [ADR-0018](../decisions/0018-tenant-driven-customization-model.md).
- **Type:** xUnit + directory scan. **Kind:** structural.
- **Status:** **Implemented** — `RepositoryLayoutTests.cs`, green since Phase 01.
- **Phase:** 01.

#### `Core_Modules_HaveNo_DomainSpecific_Names`

- **Asserts:** no class, file, table, column, permission key, audit operation, feature
  key, or namespace inside a core module carries a domain term from the maintained
  forbidden list (`CEFR`, `English`, `Asana`, `Kyu`, `Dan`, `Kata`, `Chord`,
  `CodeChallenge`, …). Matching is on word segments, so `ClassName` and `Grade` survive
  while `CefrLevel` and `AsanaPose` do not.
- **Source:** [ADR-0018](../decisions/0018-tenant-driven-customization-model.md)
  (and its 2026-08-08 genericity-boundary amendment);
  [00-principles.md § 1](00-principles.md).
- **Type:** xUnit + NetArchTest over type / member names, plus a migration and
  permission-catalogue scan. **Kind:** structural.
- **Status:** **Registered.** This is the mechanical guarantee behind the platform's
  entire premise — "the core stays generic" — and it is the one rule in the whole
  corpus that has never had an implementation, while its far weaker sibling
  `No_Source_Folder_Named_Verticals` has been green since Phase 01. Renaming a folder is
  not the failure mode anyone was worried about; `CefrLevel` on an Education aggregate
  is.
- **Phase:** 02a (Packet 10).

#### `Frontend_Has_Only_The_Web_App`

- **Asserts:** `frontend/apps` contains exactly one Next.js application (`web`).
  A peer `studio` or `portal` app requires an ADR amending
  [ADR-0009](../decisions/0009-frontend-single-app-first.md).
- **Source:** ADR-0009.
- **Type:** xUnit + directory scan. **Kind:** structural.
- **Status:** **Implemented** — `RepositoryLayoutTests.cs`.
- **Phase:** 01.

#### `Generic_Primitives_Only_In_Renderer`

- **Asserts:** the frontend `PRIMITIVE_RENDERERS` map contains only the documented closed
  set of generic primitives. A new primitive is a LearnStack release guarded by
  CODEOWNERS, not a tenant action — tenant-specific blocks are `TenantPageBlock` rows
  pointing at a composite renderer key.
- **Source:** [ADR-0018 § Architecture tests](../decisions/0018-tenant-driven-customization-model.md);
  [32-tenant-customization-model.md § 2](../architecture/32-tenant-customization-model.md).
  Named in shipped code at `frontend/apps/web/src/lib/customization/primitives.ts`.
- **Type:** frontend test over the renderer map. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 10).

#### `Only_SanitizedHtmlPrimitive_Uses_DangerouslySetInnerHtml`

- **Asserts:** the sanitised-HTML primitive is the only component in `apps/web` that
  calls `dangerouslySetInnerHTML`, and it does so exclusively on the sanitiser's output.
  This is the rule that stops the `embed-html` sanitisation contract being bypassed by a
  convenient one-off.
- **Source:** [32-tenant-customization-model.md § 8.5](../architecture/32-tenant-customization-model.md)
  and its § 11 hard invariants.
- **Type:** ESLint rule in `frontend/`. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 10).

#### `ModuleDomain_DoesNotDependOn_OtherModuleDomain`

- **Asserts:** per module, `LearnStack.Modules.<X>.Domain` has no type reference into
  any other module's `Domain`. Cross-module navigation is one of the four sanctioned
  mechanisms in [ADR-0010](../decisions/0010-cross-module-communication.md) or it is
  nothing.
- **Source:** ADR-0010; [01-architecture-standards.md](01-architecture-standards.md).
- **Type:** xUnit theory + NetArchTest, one case per module. **Kind:** structural.
- **Status:** **Implemented** — `ModuleDependencyTests.cs`.
- **Phase:** 02a (Packet 2).

#### `ModuleDomain_DoesNotDependOn_AnyApplicationOrInfrastructure`

- **Asserts:** per module, `Domain` depends on neither `Application` nor
  `Infrastructure` — the dependency direction points inward only.
- **Source:** ADR-0010; [01-architecture-standards.md](01-architecture-standards.md).
- **Type:** xUnit theory + NetArchTest, one case per module. **Kind:** structural.
- **Status:** **Implemented** — `ModuleDependencyTests.cs`.
- **Phase:** 02a (Packet 2).

#### `Meta_NetArchTest_DetectsAPlantedViolation`

- **Asserts:** NetArchTest reports a **deliberately planted** forbidden dependency. If
  this test ever reports the planted dependency as absent, every NetArchTest-based row
  in this catalogue is vacuously green and the suite is meaningless.
- **Source:** [06-testing.md](06-testing.md) — a test suite must be able to fail.
- **Type:** xUnit + NetArchTest. **Kind:** structural (meta).
- **Status:** **Implemented** — `ModuleDependencyTests.cs`. Keep in perpetuity.
- **Phase:** 02a (Packet 2).

#### `Modules_Do_Not_Inject_Valkey_Directly`

- **Asserts:** no module assembly injects `IConnectionMultiplexer` or
  `IDistributedCache`. All cache access goes through `ICacheService`.
- **Source:** [20-infrastructure-stack.md § `ICacheService`](20-infrastructure-stack.md).
- **Type:** xUnit + NetArchTest. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 10).

#### `Modules_Do_Not_Read_Entitlement_Cache_Directly`

- **Asserts:** `platform_entitlement_cache` is referenced only from the Tenancy module's
  infrastructure. Every other read goes through `IFeatureFlags`.
- **Source:** [20-infrastructure-stack.md § Entitlement Projection](20-infrastructure-stack.md);
  [ADR-0021](../decisions/0021-feature-based-entitlement.md).
- **Type:** xUnit + source / SQL scan. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 10).

#### `Aggregates_Do_Not_Redeclare_Entity_Equality`

- **Asserts:** no type deriving from `Entity<>` declares a method named `Equals`, a
  method named `GetHashCode`, or an `op_Equality` / `op_Inequality` operator,
  **and** no such type declares `IEquatable<TSelf>` on itself. The last clause is
  not redundant: an explicitly-implemented `bool IEquatable<Course>.Equals(Course?)`
  is named `System.IEquatable<Course>.Equals` in metadata, so a name check alone
  misses it, and `List<Course>.Contains` then answers differently from `==`. Scope
  the check to interfaces declared on the type — the inherited
  `IEquatable<Entity<TId>>` must not trip it. **Do not implement that scoping as
  `GetInterfaces().Except(BaseType.GetInterfaces())`**: a derived type that
  explicitly re-implements the *inherited* `IEquatable<Entity<TId>>` declares no
  new interface and no method named `Equals` (the slot is
  `System.IEquatable<Entity<CourseId>>.Equals`), so that idiom sees nothing and
  the rule passes. Measured on such a type: `a == b` and `a.Equals(b)` are both
  correct, and `new List<Entity<CourseId>> { a }.Contains(b)` returns `true` for
  different ids. Match declared slots — Cecil's `TypeDefinition.Interfaces`, or
  `GetInterfaceMap` against explicit implementations — and match method names with
  `EndsWith("Equals", Ordinal)`.
  Overriding is already impossible — `Entity<TId>` seals `Equals(object?)` and
  `GetHashCode()`, and with both sealed a derived operator cannot silence
  CS0660 / CS0661 — but a derived **overload** such as `bool Equals(Course? other)`
  is a new method, so there is nothing to seal and the compiler is silent. Measured:
  with such an overload, `a.Equals(b)` returns `true` while `a == b` and
  `((Entity<CourseId>)a).Equals(b)` return `false` for the same pair. Three answers
  for one question, decided by static type.
- **Source:** [02-backend-coding.md § Domain Modeling](02-backend-coding.md);
  [ADR-0023 Amendment 3](../decisions/0023-strongly-typed-id-source-generator.md).
- **Type:** xUnit + NetArchTest. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 3b registers this entry, Packet 10 writes the test).

#### `Organization_Aggregate_Declared_In_Tenancy_Domain`

- **Asserts:** exactly one type named `Organization` exists across the **enumerated**
  set
  of `LearnStack.Modules.*.Domain` assemblies, and it is declared in
  `LearnStack.Modules.Tenancy.Domain`. Same for the `OrganizationBranding` value object.
  The assembly set is enumerated from the module list, not discovered by scanning loaded
  assemblies — a discovery-based set that silently misses a module makes this rule
  vacuously green, which is the failure `Meta_NetArchTest_DetectsAPlantedViolation`
  guards against generally. The rule constrains module `Domain` assemblies only:
  `OrganizationId` lives in `LearnStack.SharedKernel` per
  [ADR-0023 Amendment 2](../decisions/0023-strongly-typed-id-source-generator.md) and is
  out of scope here.
- **Source:** [ADR-0017 Amendment 2 (2026-08-10)](../decisions/0017-tenant-organization-hierarchy.md);
  [03-module-boundaries.md § Tenancy](../architecture/03-module-boundaries.md).
- **Type:** xUnit + reflection over the enumerated module `Domain` assemblies.
  **Kind:** structural.
- **Status:** **Implemented** (Packet 6 step 4,
  `LearnStack.Tests.Architecture`, `TenancyConventionTests`). `Organization`
  exists and is asserted; `OrganizationBranding` does not yet, and "exactly one,
  in Tenancy" is satisfied by none — which is what stops the first one landing in
  the wrong module. Closes in Packet 10 when the remaining module `Domain`
  assemblies carry types.
- **Phase:** 02a (Packet 6 introduces, Packet 10 closes).

#### `Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning`

- **Asserts:** no type implementing `IRequestHandler<,>`, `IRequestHandler<>` or
  `INotificationHandler<>` can reach more than one **distinct aggregate root** through
  its constructor parameters' `IAggregateWriteStore<TRoot, TId>` derivations, except the
  single handler on a literal allow-list — `ProvisionTenantCommandHandler`, which writes
  `Tenant` and its default `Organization` in one transaction per ADR-0042.
- **Source:** [ADR-0042](../decisions/0042-tenant-provisioning-cross-aggregate-transaction.md);
  [Architecture Standards § Aggregate Ownership](01-architecture-standards.md).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented.**
- **Phase:** 02a Packet 7.
- **Note:** the rule counts **aggregate roots reached through ports**, not `DbSet`
  use, and the change is not cosmetic. ADR-0042 § Implementation Notes specified a
  source scan for `Add` / `Update` / `Remove` against more than one `DbSet`; under the
  shipped dependency rules that scan can never fire, because a module's `Application`
  may not reference its `Infrastructure` and so no handler can name a `DbSet` at all. A
  rule at **Implemented** that cannot fire is worse than one at **Registered**, because
  the catalogue then claims coverage the suite does not have. See
  [ADR-0042 Amendment 1](../decisions/0042-tenant-provisioning-cross-aggregate-transaction.md).
- **Three escapes closed by measurement,** each found by putting the shape into a
  production assembly and watching all 76 cases pass: a **fused port**
  (`IFused : IAggregateWriteStore<A,_>, IAggregateWriteStore<B,_>`) is one parameter
  reaching two roots, so roots are counted rather than parameters; an
  **`INotificationHandler`** runs inside the ambient transaction and is the same write,
  so it is in the handler set; and `Type.GetConstructors()` is public-only, so the
  scan passes `NonPublic`.
- **What it does not catch:** a write routed through a helper that itself holds two
  ports, or through a second `DbContext` reached indirectly — the same limit
  [§ What a structural test proves](#what-a-structural-test-proves--and-what-it-does-not)
  states for every structural rule. The binding control is that the allow-list has one
  entry and growing it is a reviewed diff.
- **Mutation-checked.** A second handler taking two write ports, the sanctioned handler
  renamed, and the two ports fused into one — each turns the rule red.

### Persistence: concurrency and the unit of work

Source: [ADR-0039](../decisions/0039-optimistic-concurrency-token.md),
[ADR-0040](../decisions/0040-ambient-unit-of-work.md). Introduced by
[Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md); the two behavioural
rules that need a second `DbContext` are owed by Phase 03.

#### `Aggregates_With_Optimistic_Concurrency_Map_RowVersion`

- **Asserts:** every entity implementing `IOptimisticConcurrency` has its `Version`
  configured as the concurrency token against a `row_version` column, **and** that
  the property's `ValueGenerated` is `Never` with both save behaviours at `Save`.
  Neither `ValueGeneratedOnAddOrUpdate()` nor `IsRowVersion()` may appear: on a
  `long` property the two produce byte-identical metadata, and EF then omits the
  column from the `UPDATE` entirely, so the token stays `0` and every lost update
  succeeds ([ADR-0039 Amendment 1](../decisions/0039-optimistic-concurrency-token.md),
  measured). A structural test can see the metadata; it cannot see a silently
  inert token, which is why the assertion is on `ValueGenerated` and not on the
  call site.
- **Source:** ADR-0039 (Amendments 1 and 2);
  [05-database.md § Concurrency](05-database.md).
- **Type:** xUnit + EF model inspection. **Kind:** structural.
- **Status:** **Implemented** (Packet 6 step 4,
  `LearnStack.Tests.Architecture`, `PersistenceConventionTests`).
  Mutation-checked: dropping `.ValueGeneratedNever()` from `MapAuditColumns`
  fails this case and only this case.

#### `Migration_Startup_Project_References_EntityFrameworkCore_Design`

- **Asserts:** `backend/src/LearnStack.Api/LearnStack.Api.csproj` carries a
  `PackageReference` to `Microsoft.EntityFrameworkCore.Design`. `dotnet ef` resolves
  the design-time package from the **startup** project, and
  [`make migrate`](../standards/05-database.md) names that one; without the reference
  the tool refuses before it opens a connection. The failure is invisible to the test
  suite, which calls `Database.MigrateAsync()` directly — Packet 6 shipped a migration
  in exactly that state, green under Testcontainers and inapplicable by the only path
  the corpus documents.
- **Source:** [05-database.md § Migrations](05-database.md); the `migrate` target.
- **Type:** xUnit + project-file inspection. **Kind:** structural.
- **Status:** **Implemented** (Packet 6 step 4,
  `LearnStack.Tests.Architecture`, `PersistenceConventionTests`).

#### `Migrate_Target_Covers_Every_Migration_Chain`

- **Asserts:** every directory under `backend/src` carrying a
  `Persistence/Migrations` folder is reachable from the `migrate` recipe's project
  loop in the repo-root `Makefile` — scanned, not listed, so adding a chain and
  forgetting the recipe fails here. `make migrate` is the only path
  [05-database.md § Database roles](05-database.md) documents for applying a
  migration; its first version globbed `src/Modules` only, which left the platform
  chain unapplied everywhere except the Testcontainers fixtures, which call
  `Database.MigrateAsync()` directly and stayed green.
- **Source:** [05-database.md § Migrations](05-database.md); the `migrate` target.
- **Type:** xUnit + Makefile and directory inspection. **Kind:** structural.
- **Status:** **Implemented** (Packet 6 step 5,
  `LearnStack.Tests.Architecture`, `PersistenceConventionTests`).
  Mutation-checked: narrowing the loop back to `src/Modules` fails this case.

#### `Every_Foreign_Key_Has_A_Supporting_Index`

- **Asserts:** every foreign key in schema `public` has an index whose **leading**
  columns are the constraint's columns, or a **unique** index over a leading prefix
  of them — a unique prefix already yields at most one candidate row, which is why
  `tenants`' primary key supports the composite
  `fk_tenants_default_organization`. Every foreign key in this schema is
  `ON DELETE RESTRICT`, so every parent delete pays the child scan.
- **Source:** [05-database.md § Indexes](05-database.md).
- **Type:** **integration** test (Testcontainers + PostgreSQL), reading
  `pg_constraint` / `pg_index`. **Kind:** structural.
- **Status:** **Implemented** (Packet 6 step 5,
  `LearnStack.Tests.Integration`, `TenancySchemaTests`). It found two real gaps on
  its first run — `fk_organizations_reporting_parent` and
  `fk_platform_host_to_tenant_organization` — which is the evidence that it is not
  vacuous.

#### `SoftDelete_Advances_The_Row_Version`

- **Asserts:** `AuditableEntity.SoftDelete` leaves `Version` strictly greater than it
  was. Behavioural, because the structural rule cannot see it: before Packet 6 step 2
  `SoftDelete` stamped `UpdatedAt` / `UpdatedBy` directly rather than through the
  shared `Touch` primitive, so an increment placed only in `MarkUpdated` would have
  left a soft delete un-versioned and a client's pre-delete ETag would have kept
  satisfying `If-Match` on the row it deleted.
- **Source:** ADR-0039 § Why `MarkUpdated` and not an interceptor.
- **Type:** xUnit (`LearnStack.Tests.Unit`, `AuditableEntityTests`). **Kind:** behavioural.
- **Status:** **Implemented** (Packet 6 step 2). Mutation-checked: routing
  `SoftDelete` back to stamping the fields itself fails this case and only this
  case.

#### `Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork`

- **Asserts:** two halves. The composition root's persistence registration is run,
  and every `DbContext` service in it is one `AddModuleDbContext` registered —
  scoped, from an implementation factory, never a type registration EF could give
  its own connection. And under `backend/src`, exactly **five** files may reach for a
  connection at all: the two design-time factories, where a connection string is the
  point; the shared helper, which passes a *connection*; and the two composition roots —
  `LearnStack.Api`'s, which builds the one application data source behind its credential
  guard, and `LearnStack.Tools.Seeder`'s, which is the same act for a host with no HTTP
  surface. A sixth is a new decision. A context on its own connection never saw the
  announcement, so every read through it returns zero rows under the corrected policy —
  silently.

  The set is keyed on `directory/filename`, not the bare filename: two `Program.cs` now
  exist under `backend/src`, and a bare-name set would let the API's silently take the
  seeder's slot.
- **Source:** ADR-0040; [05-database.md § Forbidden](05-database.md).
- **Type:** xUnit + DI registration inspection and a source scan. **Kind:** structural.
- **Status:** **Implemented** (Packet 6 step 6; the allow-list widened to five and
  keyed by directory in Packet 7 step 10, `LearnStack.Tests.Architecture`,
  `PersistenceConventionTests`).

#### `TransactionBehavior_Does_Not_Reference_A_Module_Assembly`

- **Asserts:** `TransactionBehavior`'s constructor names `IUnitOfWork` and no
  `DbContext`, and `LearnStack.Application` references no module assembly — checked
  against the **project file** as well as the emitted assembly-reference table,
  because the compiler elides a reference whose types the IL never touches, so a
  dangling `<ProjectReference>` would leave a reflection-only check green. The
  assembly half carries a positive control.
- **Source:** ADR-0040; ADR-0033.
- **Type:** xUnit + assembly-reference and constructor inspection. **Kind:** structural.
- **Status:** **Implemented** (Packet 6 step 6,
  `LearnStack.Tests.Architecture`, `PersistenceConventionTests`).

#### `Modules_Do_Not_Parallelize_Over_The_Ambient_Connection`

- **Asserts:** no module code passes two `DbContext`-bound operations to
  `Task.WhenAll` / `Task.WhenAny`. One connection means one command at a time; a
  handler that fans out corrupts the protocol.
- **Source:** ADR-0040 § Nesting.
- **Type:** Roslyn/NetArchTest. **Kind:** structural.
- **Status:** **Awaiting backfill** — the rule is decided; no module code exists to
  violate it yet. **Phase:** 02a Packet 6 registers it; Phase 03 implements it with
  the first module that could.

### Tenancy and isolation

Source: [ADR-0003](../decisions/0003-tenant-isolation-defense-in-depth.md) (Amendments
1 and 3), [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md). Introduced by
[Phase 02a Packet 7](../roadmap/phase-02a-kernel-tenancy.md), closed by Packet 10.

Read § What a structural test proves before relying on any row in this section. The
first two rows are coverage checks; the last three are the proof.

#### `LearnStack_OutboxAdmin_Role_OnlyUsedBy_OutboxProcessor`

- **Asserts:** `ConnectionStrings:OutboxDispatcher` is resolved by `OutboxProcessor`
  and nothing else. A `GRANT` names a role, not a code path — every handler in the
  API process runs as the same role — so code-path confinement of a `BYPASSRLS`
  credential is carried here or nowhere.
- **Source:** [05-database.md § GRANT matrix](05-database.md); ADR-0003 Amendment 3.
- **Type:** NetArchTest + DI registration inspection. **Kind:** structural.
- **Status:** **Awaiting backfill** — cited by the standard, no dispatcher yet.
  **Phase:** 02b.

#### `CoreInfrastructure_DoesNotDependOn_AnyModule`

- **Asserts:** the core `LearnStack.Infrastructure` assembly references no
  `LearnStack.Modules.*` type. The reverse edge — a module's `Infrastructure`
  referencing core Infrastructure — is permitted and required, because
  `TenantScopedDbContext` and the query-filter seam live there
  ([Architecture Standards § Dependency Direction](01-architecture-standards.md)).
  This rule is the half that keeps it one-way: core Infrastructure is referenced by
  every module, so a single edge back into one makes the graph cyclic and makes that
  module impossible to extract.
- **Source:** [ADR-0002](../decisions/0002-initial-architecture.md);
  [ADR-0010](../decisions/0010-cross-module-communication.md);
  [Architecture Standards § Dependency Direction](01-architecture-standards.md).
- **Type:** NetArchTest. **Kind:** structural.
- **Status:** **Implemented** (Packet 7 step 3, `ModuleDependencyTests`).
- **Phase:** 02a Packet 7.

#### `Platform_DataSource_Resolved_Only_By_PlatformAdminScope`

- **Asserts:** the keyed `NpgsqlDataSource` built from `ConnectionStrings:PlatformAdmin`
  is resolvable only by `PlatformAdminScope`. Module code cannot reach the
  `BYPASSRLS` credential.
- **Source:** [05-database.md § How `EnterPlatformAdminScope(reason)` reaches
  `learnstack_platform`](05-database.md).
- **Type:** NetArchTest + DI registration inspection. **Kind:** structural.
- **Status:** **Implemented** (`PlatformAdminScopeConventionTests`, Packet 7 step 7). **Phase:** 02a Packet 7.
- **Note:** three legs, all live. The keyed-resolution scan, a scan that connection
  strings are read in exactly one file, and a self-check that the scan matched something
  at all — a two-path allow-list matching nothing would pass vacuously. This is the
  repository's first keyed DI registration, so the scan is the whole boundary: the key
  is a public const because `GetKeyedServices(KeyedService.AnyKey)` reaches a keyed
  registration whatever the key is spelled, so hiding the string buys nothing.
#### `Every_TenantOwned_Entity_HasFilterAndRlsPolicy`

- **Asserts:** every entity marked `[TenantOwned]` (or implementing `ITenantOwned`)
  has a **tenant key** (`TenantId`, or `Id` on the tenant-owned self-keyed class), an
  EF global query filter referencing it, and — in the migration that creates its
  table — `ENABLE` **and** `FORCE ROW LEVEL SECURITY` plus
  exactly one policy carrying both a `USING` and a `WITH CHECK` clause over
  `app.tenant_id`. A second **permissive** policy on the same table fails the test: that
  is the defect ADR-0003 Amendment 3 corrects.
- **Canonical name.** See § Canonical names and superseded spellings for the five
  superseded spellings.
- **Source:** ADR-0003 Amendment 3;
  [05-database.md § Tenant-Owned and Organization-Scoped Tables](05-database.md).
- **Type:** xUnit + EF model inspection + migration SQL scan. **Kind:** structural.
- **Status:** **Implemented** (Packet 7 step 3, `TenantScopingTests`) for the Tenancy
  module; Packet 10 closes it across every module.
- **Phase:** 02a (Packet 7 introduces, Packet 10 closes).
- **Note:** a marker-gated rule cannot catch a **missing** marker — it iterates what it
  finds. The companion case `The_Host_Map_Carries_No_Tenant_Marker` states the negative
  that matters most in this module: `platform_host_to_tenant` has a `TenantId` property
  and must carry neither the marker nor a filter, because a tenant-keyed predicate on the
  table read *in order to* determine the tenant makes host resolution return zero rows
  forever, on the anonymous page-load path, with no error anywhere.
- **Note:** the marker's scope is decided by **table class**, not by the presence of a
  `TenantId` property. `tenants` is tenant-owned **self-keyed** — its policy is on `id`
  and it carries no marker-driven `TenantId` filter — and `platform_host_to_tenant` is
  **platform-scoped** and takes no marker at all, because it is read in order to
  determine the tenant. See
  [Database Standards § Table classes](05-database.md);
  [Architecture Standards § Tenant-Scoped Code](01-architecture-standards.md) was
  corrected to match in the same pass.

#### `Every_OrgScoped_Entity_HasOrgIdAndFilter`

- **Asserts:** every entity marked `[OrganizationScoped]` carries a **nullable**
  `OrganizationId` (null = tenant-wide per ADR-0017), an org-aware EF query filter, an
  organization term `AND`-ed into the same single policy as the tenant term, and — in
  the migration that creates its table — the two `AS RESTRICTIVE` write guards,
  `FOR UPDATE` and `FOR DELETE`, that ADR-0003 Amendment 3 makes mandatory for every
  organization-scoped table. The guards are part of the assertion, not decoration:
  [the Tenancy module spec § Risks](../modules/tenancy/README.md) records the
  measurement — with the hatch set and the delete guard dropped, a `DELETE` removed
  another organization's row.
- **Canonical name.** See § Canonical names and superseded spellings for the four
  superseded spellings.
- **Source:** ADR-0017; ADR-0003 Amendment 3;
  [05-database.md § Tenant-Owned and Organization-Scoped Tables](05-database.md).
- **Type:** xUnit + EF model inspection + migration SQL scan. **Kind:** structural.
- **Status:** **Implemented** (Packet 7 step 3, `TenantScopingTests`) for the Tenancy
  module; Packet 10 closes it across every module.
- **Phase:** 02a (Packet 7 introduces, Packet 10 closes).

#### `No_IgnoreQueryFilters_Outside_PlatformAdminScope`

- **Asserts:** `IgnoreQueryFilters()` appears only inside the audited
  `EnterPlatformAdminScope(reason)` path.
- **Source:** ADR-0003; [11-security.md](11-security.md);
  [05-database.md § Forbidden](05-database.md).
- **Type:** xUnit + source scan; the permitted paths are a list inside the scan, not a
  call-site marker. **Kind:** structural.
- **Status:** **Implemented** (`PlatformAdminScopeConventionTests`, Packet 7 step 7).
- **Phase:** 02a (Packet 7).
- **Note:** a live negative — nothing under `backend/src` calls `IgnoreQueryFilters`
  today, and the rule exists so the first call is a deliberate edit to the exemption
  rather than a quiet one at a call site. A path check with no marker, deliberately: a
  comment is what a reviewer skims past.

#### `AllowsUnresolvedTenantContext_Only_On_Provisioning_Commands`

- **Asserts:** the `[AllowsUnresolvedTenantContext]` marker appears only on the narrow
  set of tenant-provisioning and platform-admin commands that legitimately run before a
  tenant is resolved. Any other request type carrying it fails the build.
- **Why it matters:** the marker is a deliberate hole in the tenant-context assertion at
  pipeline step 4. A hole nobody counts becomes a hole everybody uses; this test counts
  it. It replaces the `TenantContextBehavior.AllowsUnresolvedContext` predicate stub
  shipped in Packet 3.
- **Source:** ADR-0003; ADR-0032 § Sub-decision 2;
  [02-backend-coding.md § Pipeline Behaviors](02-backend-coding.md).
- **Type:** xUnit + reflection over `IRequest<>` implementations. **Kind:** structural.
- **Status:** **Implemented** (`RequestSurfaceTests`, Packet 7 step 6).
- **Phase:** 02a (Packet 7).
- **Note:** the set leg is **vacuous today** and the shape leg is not. There is not one
  production request type in the solution, so the permitted set is literally empty;
  `ProvisionTenantCommand` is the first to carry the marker, in Packet 7 step 9. What runs now
  is the guard on the attribute's own `AttributeUsage`: the behavior reads it with
  `inherit: false`, and flipping the attribute to `Inherited = true` is not an error and not a
  widening — it is a marker the pipeline silently stops following.
- **Note:** the permitted set is a **literal list of type names**, not a naming pattern. A rule
  satisfied by what an author calls a class is a rule nobody reviewed.

#### `TenantWide_Row_Of_TenantB_Is_Invisible_To_TenantA`

- **Asserts:** a row of tenant B with `organization_id IS NULL` — the defined
  representation of a tenant-wide row — returns **zero** rows when read under tenant A's
  context. This is the exact case the superseded RLS template leaked, and it leaked
  while satisfying every structural assertion above.
- **Runs as `learnstack_app`.** A non-owning, `NOBYPASSRLS` role. Connecting as the
  owner or as a `BYPASSRLS` role makes this test pass against an inert policy set.
- **Source:** ADR-0003 Amendment 3 § Test requirement.
- **Type:** **integration** test (Testcontainers + PostgreSQL), not an architecture
  test. **Kind:** runtime.
- **Status:** **Implemented, twice.** The schema-level case is Packet 6 step 4
  (`TenancySchemaTests`); the request-level one is Packet 7 step 11
  (`Database/TenantIsolationHttpTests`), which drives it through
  `HostClassificationMiddleware`, `TenantResolverMiddleware`, the announcement and the
  EF query filters, with the host header as the only input and no stubbed
  `ITenantContext`. The schema-level case moved forward because that class's own
  assertions needed the two-tenant seed anyway: without rows for both tenants, every
  count in it passed against dropped policies.
- **Reads `tenant_settings`, not `platform_host_to_tenant`.** The row shape the rule
  names is tenant-owned with `organization_id IS NULL`; the host table is
  platform-scoped and its policy has no organization term, so reading it would name the
  wrong mechanism. The first version of the request-level case did exactly that.
- **Phase:** 02a (Packet 6 the schema-level case; Packet 7 the request-level one).

#### `Write_With_Foreign_TenantId_Is_Rejected_By_WithCheck`

- **Asserts:** an `INSERT` or `UPDATE` carrying a `tenant_id` other than the caller's is
  rejected by the policy's `WITH CHECK` clause. Without an explicit `WITH CHECK`, a
  `USING`-only policy constrains reads and leaves writes unconstrained — a read-side
  test cannot observe that.
- **Runs as `learnstack_app`.**
- **Source:** ADR-0003 Amendment 3 § Test requirement;
  [05-database.md](05-database.md).
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** runtime.
- **Status:** **Implemented, twice.** Packet 6 step 4 (`TenancySchemaTests`) as a
  `[Theory]` over both halves, because `WITH CHECK` guards `INSERT` and `UPDATE` and a
  rule covering one leaves the other open. Packet 7 step 11
  (`Database/TenantIsolationHttpTests`) issues the write through a request: raw SQL on
  the ambient connection, so no query filter is in front of it and only `WITH CHECK` can
  refuse. The first version of that case asserted merely that an anonymous POST failed,
  and passed against a **deleted endpoint** and against a database with every policy
  dropped — which is why the entry now says what the case must observe rather than what
  it must return.
- **Phase:** 02a (Packet 6 the schema-level case; Packet 7 the request-level one).

`Tenant_A_cannot_read_Tenant_B_data`, `Org_X_cannot_read_Org_Y_within_TenantA` and
`Unsetting_tenant_context_returns_zero_rows_through_RLS` are ordinary integration tests
named in the phase document rather than catalogue-governed rules. All three shipped
alongside the two rules above in Packet 6 step 4, and Packet 7 step 11 re-runs them
through the request path in `Database/TenantIsolationHttpTests`.

Three things are worth recording about that second run, because each was a defect in its
first version. `Org_X_…` must read an **organization-scoped** table — `tenant_settings`,
whose policy carries an organization term — not `organizations`, which is tenant-wide and
where every organization is visible to every other by design; reading the latter and
narrowing the rows in the test's own handler tested the test.
`Unsetting_tenant_context_…` must actually run a query under an unresolved context, which
takes a `PlatformHost` request (a host in `Tenancy:PlatformHosts`); asserting a 404 for an
unknown host instead exercises the resolver and never reaches a table. And the suite as a
whole constrains the **composite** answer, not one layer: measured, deleting both EF query
filters leaves all five green because RLS holds, disabling RLS leaves the four reads green
because the filters hold, and removing both turns all five red.

#### `Every_Scoping_Interface_Carries_Its_Marker`

- **Asserts:** every entity implementing a scoping interface — `ITenantOwned`,
  `IOrganizationScoped` — also carries the marker attribute the filter and policy
  generators read. An entity that implements one and not the other is scoped in the type
  system and unscoped everywhere it matters.
- **Source:** [ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented** (Packet 7, `LearnStack.Tests.Architecture`).
- **Phase:** 02a Packet 7.

#### `The_Request_Filter_Sees_Every_Shape_MediatR_Dispatches`

- **Asserts:** the predicate that enumerates request types covers every shape MediatR
  dispatches, so a rule written over "all requests" is not silently blind to one of them.
  Measured facts behind it: `IStreamRequest<T>.GetInterfaces()` is empty and
  `IBaseRequest.IsAssignableFrom(IStreamRequest<>)` is false, so a filter written the
  obvious way misses streamed requests entirely.
- **Source:** [04-api-design.md](04-api-design.md).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented** (Packet 7, `LearnStack.Tests.Architecture`).
- **Phase:** 02a Packet 7.

#### `The_Sweep_Covers_Every_Production_Assembly`

- **Asserts:** every `LearnStack.*` project under `backend/src` is loadable by the rules
  that sweep production assemblies. A project the sweep cannot load is a project every
  reflection rule silently skips, which is worse than a rule that fails: it reports green
  over code it never read.
- **Note:** it is why adding a project — `LearnStack.Tools.Seeder` in Packet 7 step 10 —
  requires a `ProjectReference` from the architecture test project. The rule names the
  remedy in its own failure message.
- **Source:** [21-architecture-tests-catalogue.md § What a structural test proves](#what-a-structural-test-proves--and-what-it-does-not).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented** (Packet 7, `LearnStack.Tests.Architecture`).
- **Phase:** 02a Packet 7.

#### `Every_Write_Port_Is_Countable_Or_Enumerated`

- **Asserts:** every interface in a production assembly whose method takes a type from a
  module's `Domain` assembly — **directly, or inside a generic, array or by-ref wrapper**
  — either derives from `IAggregateWriteStore<TRoot, TId>`, and is therefore visible to
  the cross-aggregate census above, or appears on a literal allow-list. The list holds one
  name: `IPlatformHostMappingStore`.
- **Wrappers are unwrapped transitively,** because a bulk write port is written with one:
  `IEnumerable<Course>` lives in `System.Private.CoreLib`, so a check on the parameter's
  own assembly sees the wrapper rather than the domain type inside it — and the port would
  escape the enumeration while satisfying every word of what this rule claims.
- **Why it exists.** The census counts derivations, so a port that does not derive is
  invisible to it. One already is, deliberately: `PlatformHostMapping` is a projection
  with a string key rather than an aggregate root. That exemption is fine; being *silent*
  about it is not, because a second such port would join the first with nothing to notice,
  and the census that keeps ADR-0042's exception at one entry would stop describing the
  system.
- **Detected by shape, not by name.** "Takes a domain type" rather than "ends in `Store`":
  a rule keyed on a suffix is satisfied by renaming.
- **Source:** [ADR-0042](../decisions/0042-tenant-provisioning-cross-aggregate-transaction.md).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented** (Packet 7 review, `LearnStack.Tests.Architecture`,
  `AggregateWriteTests`).
- **Phase:** 02a Packet 7.

#### `Out_Of_Band_Setters_Open_Read_Only_Transactions`

- **Asserts:** the two components that announce a session variable outside the ambient
  unit of work — `CachedHostToTenantResolver` and `OrganizationScopeValidator` — each
  contain `SET TRANSACTION READ ONLY`, and contain it **at a lower source offset than
  their first `set_config(`**.
- **What the offset comparison does and does not prove.** PostgreSQL refuses
  `SET TRANSACTION` after the transaction's *first statement of any kind*, and this scan
  only orders it against the announcement. A setter that ran some other statement — a
  `SELECT`, a second `SET` — between `BEGIN` and `SET TRANSACTION READ ONLY` would satisfy
  the rule and fail at runtime. That failure is loud and immediate rather than silent,
  which is why the cheap ordering check is the one that ships; the expensive alternative
  is parsing the method for every command execution, and
  [§ What a structural test proves](#what-a-structural-test-proves--and-what-it-does-not)
  states the general limit.
- **Why the property matters at all.** Read-only is what makes an out-of-band setter of a
  session variable acceptable, because `learnstack_app` holds write grants on the tables
  these connections reach — so nothing but this statement stops a future edit from writing
  under an announcement no request made.
- **Why a scan and not a behavioural test.** The transaction is opened, used and disposed
  inside one method, so nothing outside can observe its settings. Measured: the resolver
  shipped without the statement while four carriers — Database Standards, Security
  Standards, the glossary and ADR-0040 — described it as read-only, and the validator two
  files away had carried it since Packet 6.
- **Source:** [ADR-0040](../decisions/0040-ambient-unit-of-work.md);
  [05-database.md](05-database.md); [11-security.md](11-security.md).
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** (Packet 7 review, `LearnStack.Tests.Architecture`,
  `TenancyConventionTests`).
- **Phase:** 02a Packet 7.

#### `Registering_The_Pipeline_Twice_Registers_It_Once`

- **Asserts:** calling `AddLearnStackMediatRPipeline` twice on one `ServiceCollection`
  yields the same registrations as calling it once — the same behaviour count and the same
  total.
- **The property is MediatR's, not ours.** `AddBehavior` deduplicates; measured at seven
  behaviours and eleven registrations either way. It is pinned because the repository
  depends on it and did not write it: every test fixture registers its probe handler by
  hand specifically to avoid re-running `AddMediatR`, and if deduplication stopped
  holding, that workaround would become load-bearing rather than cautious with nothing to
  say so. A doubled `TransactionBehavior` is a nested frame on every request.
- **A guard of our own was written and removed.** It changed nothing under mutation, and a
  guard no test can kill is a comment.
- **Source:** [ADR-0032 § Sub-decision 2](../decisions/0032-exception-handling-logging-and-observability.md).
- **Type:** xUnit + DI registration inspection. **Kind:** structural.
- **Status:** **Implemented** (Packet 7 review, `LearnStack.Tests.Architecture`,
  `CrossCuttingFoundationTests`).
- **Phase:** 02a Packet 7.

#### `Tenant_Context_Guard_Fires_Only_On_An_Unmarked_Transaction`

- **Asserts:** both arms of the `DbCommandInterceptor` guard. A command a module `DbContext` issues on a transaction no sanctioned setter announced throws `TenantContextMissingException`; the same command on an announced transaction runs. One arm is not the rule: a guard keyed on `TransactionBehavior` instead of on the marker passes the first arm and rejects the writes the idempotency store and the audit store legitimately make on their own short transactions.
- **Keyed on the transaction, not on the table.** An earlier wording said "a command against a `[TenantOwned]` table", and the rule's own name says otherwise. What shipped is the name: matching table names would put a parser between every query and the database, wrong on the first CTE, to decide something every command from a module context already answers — such a command belongs to a request that had a tenant to announce. Nothing is lost, because a platform-scoped read from a module context is exactly as much of a wiring bug as a tenant-owned one.
- **Runs as `learnstack_app`.**
- **Source:** [11-security.md § The out-of-band setters](11-security.md);
  [05-database.md § Connection Management](05-database.md).
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** runtime.
- **Status:** **Implemented** (`TenantContextGuardTests`, Packet 7 step 8).
- **Phase:** 02a Packet 7.
- **Note:** the marker is a flag on `NpgsqlUnitOfWork`, read through the seam member
  ADR-0040 Amendment 5 adds. **Only one of the seven sanctioned setters stamps it**, and
  that is the honest count: `TransactionBehavior` via `SetTenantContextAsync`. Of the
  other six, **five do not exist in code yet** — including the integration-event
  transport, which is the one other setter that *opens* the ambient transaction and will
  have to announce it when Phase 02b lands it — and the one that does,
  `OrganizationScopeValidator`, issues raw `NpgsqlCommand`s, which EF interception cannot
  see, so it needs neither a mark nor an exemption. (`CachedHostToTenantResolver` is not
  one of the seven: it sets `app.resolving_host`.) The exemption list is empty for the
  same reason, which is why `PlatformAdminScope` — a `BYPASSRLS` connection that announces
  no tenant by design — is invisible here by construction rather than by a hand-written
  exception someone later widens.
- **Note:** the guard is a **diagnostic above Row Level Security, never the boundary**.
  `Without_The_Guard_An_Unannounced_Read_Is_Silent_And_Empty` asserts the state it exists
  to make visible: safe already, because the predicate is `NULL`, and silent, which is the
  outage. It also does **not** close the unresolved-context case: `SetTenantContextAsync`
  writes the empty string for an unresolved context by design, so such a transaction is
  announced, passes the guard, and still reads nothing. `TenantContextBehavior` at pipeline
  step 4 is what refuses that, and remains the only thing in front of it.

#### `Db_Connection_String_Is_TransactionPooled`

- **Asserts:** the deployment configuration points at PgBouncer in **transaction**
  pooling mode. `SET LOCAL app.tenant_id` is transaction-scoped, so statement-mode
  pooling would reset the value between statements and silently break isolation.
- **Source:** [05-database.md § Connection Management](05-database.md).
- **Type:** xUnit + configuration inspection. **Kind:** structural.
- **Status:** **Registered** — needs a non-development deployment configuration to
  inspect.
- **Phase:** 11.

#### `User_Aggregate_Has_No_TenantScoped_Columns`

- **Asserts:** the `users` EF configuration declares only global attributes. Anything
  whose value depends on which tenant is asking lives on the membership or as a
  `TenantCustomFieldDef`, never as a column on the global aggregate. The reviewer's
  version of the same question is "which tenant authored this value?".
- **Source:** [Phase 03 § Attribute ownership](../roadmap/phase-03-identity-admin.md);
  [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md).
- **Type:** xUnit + EF model inspection. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 03.

#### `Every_TenantCustomFieldDef_Declares_PiiCategory`

- **Asserts:** every `TenantCustomFieldDef` carries a PII category — enforced on the
  aggregate's invariants and by a migration scan for a `NOT NULL` `pii_category` column.
  A custom field without a category cannot be routed by the GDPR erasure and export
  paths.
- **Source:** [Phase 03 § Tenant Custom Fields](../roadmap/phase-03-identity-admin.md);
  [ADR-0018](../decisions/0018-tenant-driven-customization-model.md).
- **Type:** xUnit + reflection, plus a migration scan. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 03.

#### `Tenant_Scoped_Export_Contains_No_Foreign_Tenant_Rows`

- **Asserts:** a data export run for a person holding memberships in **both** seed
  tenants, executed as `learnstack_app`, produces a single-tenant bundle. Requires two
  memberships: a single-tenant fixture passes against a broken export.
- **Runs as `learnstack_app`.**
- **Source:** [Phase 03 § Attribute ownership](../roadmap/phase-03-identity-admin.md);
  ADR-0003 Amendment 3 § Test requirement.
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** runtime.
- **Status:** **Registered.**
- **Phase:** 03.

### Audit

Source: [ADR-0033 Audit Durability Model](../decisions/0033-audit-durability-model.md)
(supersedes ADR-0016); [18-audit-coverage.md](18-audit-coverage.md). Introduced by
[Phase 02a Packet 9](../roadmap/phase-02a-kernel-tenancy.md).

#### `AuditEntry_Inherits_Entity_Not_AuditableEntity`

- **Asserts:** `AuditEntry` derives from `Entity<TId>`, not `AuditableEntity<TId>`.
  An audit row that carries `UpdatedAt` / `DeletedAt` is a mutable audit row, which is a
  contradiction.
- **Source:** ADR-0033 (carried from ADR-0016).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 9).

#### `MustClass_Audit_Writes_Share_The_Business_Transaction`

- **Asserts:** a MUST-class audit row is inserted on the **same transaction** as the
  business write it records. The proof is behavioural: one command produces exactly one
  `audit_log` row; a command whose durable audit write is forced to fail produces **zero**
  business rows and returns `503 audit_unavailable`; a denied MUST-class command produces
  exactly one row carrying the `denied` outcome and zero business rows.
- **Why it matters:** ADR-0016's "audit never blocks business logic" applied uniformly,
  which meant a privileged operation could commit while its audit row was lost. It also
  meant the audit insert could run outside the transaction that sets `app.tenant_id` —
  where Row Level Security rejects it. ADR-0033 puts the write inside the transaction, at
  the commit boundary, and this test is what holds the line.
- **Runs as `learnstack_app`.** A non-owning, `NOBYPASSRLS` role; connecting as the owner
  would pass against inert policies and prove nothing about the RLS half of the claim.
- **Source:** ADR-0033 § Decision + Implementation Notes.
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** runtime.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 9).

#### `Audit_Survives_Transaction_Rollback`

- **Asserts:** a MUST-class command whose transaction rolls back produces **zero**
  business rows and **exactly one** `audit_log` row, with outcome `failed`. Two cases: a
  forced fault at `COMMIT`, and the ordinary path where the handler calls `SaveChanges`
  and then returns `Result.Fail(...)`.
- **Why it matters:** the durable write happens before `COMMIT`, so a row that has been
  inserted is not yet durable. A design that marks the intent "consumed" at insert time
  and skips the standalone write on the way out loses the audit **and** the business
  change on every rolled-back MUST-class operation — and a per-request DI-scoped flag
  cannot observe a database rollback. This test is the only thing that distinguishes a
  correct implementation from that one.
- **Runs as `learnstack_app`.**
- **Source:** ADR-0033 § Decision.
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** runtime.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 9).

#### `Audit_Classification_Does_Not_Read_The_Database_On_The_Request_Path`

- **Asserts:** with the `audit_config` table made unreadable, a MUST-class command still
  completes and still writes its `audit_log` row at the in-process catalogue
  classification; and an operation absent from the catalogue is rejected with
  `audit_unclassified_operation`.
- **Why it matters:** `AuditLogBehavior` runs at pipeline step 3, `SET LOCAL
  app.tenant_id` is issued at step 6, and `audit_config` carries ENABLE + FORCE row level
  security. A classification query at step 3 therefore returns **zero rows silently** —
  not an exception — so a fail-closed `catch` around it can never fire and "RLS filtered
  everything" is indistinguishable from "this tenant has no overrides". Moving the
  classification off the request path is the fix; this test is what stops it drifting
  back.
- **Source:** ADR-0033 § Decision;
  [31-audit-subsystem.md § 5](../architecture/31-audit-subsystem.md).
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** runtime.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 9).

#### `AuditLog_Update_Is_Column_Restricted`

- **Asserts:** as `learnstack_app`, any `UPDATE` or `DELETE` on `audit_log` raises
  `42501` (the role holds neither privilege). As `learnstack_platform`, an `UPDATE`
  touching only `actor_email`, `ip_address`, `user_agent`, `before_state`, `after_state`
  and `changes` succeeds; an `UPDATE` touching any other column — `actor_user_id`,
  `operation`, `outcome`, `timestamp` — is rejected by `audit_log_append_only_guard`; and
  a `DELETE` succeeds, because the retention purge needs it.
- **Why it matters:** "append-only" stated as "no `UPDATE` or `DELETE` anywhere" is
  unimplementable — the corpus itself ships two mutating paths (GDPR redaction, retention
  purge). This test pins what is actually allowed so the rule is enforceable rather than
  aspirational.
- **Source:** ADR-0033; [18-audit-coverage.md § Storage](18-audit-coverage.md);
  [31-audit-subsystem.md § 7](../architecture/31-audit-subsystem.md).
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** runtime.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 9).

#### `Every_TenantOwned_Command_HasAuditCoverage`

- **Asserts:** every command touching a `[TenantOwned]` aggregate appears in its
  module's audit-coverage matrix with a MUST / SHOULD / MAY classification. An
  unclassified command fails the build rather than defaulting to silence.
- **Source:** [18-audit-coverage.md](18-audit-coverage.md); ADR-0033.
- **Type:** xUnit + reflection over commands cross-checked against the matrix.
  **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 9).

#### `Every_Module_Has_An_AuditCoverage_Matrix`

- **Asserts:** every module directory contains `docs/modules/<module>/audit.md` with a
  parseable coverage matrix.
- **Source:** [18-audit-coverage.md](18-audit-coverage.md).
- **Type:** xUnit + file scan. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 9).

#### `Modules_Do_Not_Write_AuditLog_Directly`

- **Asserts:** no module assembly references the `audit_log` table or the `AuditEntry`
  type outside `LearnStack.Infrastructure.Audit`. `IAuditStore` is the only write path.
- **Source:** ADR-0033 (carried from ADR-0016);
  [20-infrastructure-stack.md § Audit Plumbing](20-infrastructure-stack.md).
- **Type:** xUnit + NetArchTest + SQL scan. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 10).

#### `AuditEntry_Is_AppendOnly`

- **Asserts:** no `UPDATE` or `DELETE` statement targets `audit_log` anywhere in the
  codebase or in any migration **except** inside
  `LearnStack.Modules.Audit.Infrastructure` — the GDPR redaction handler, the per-module
  `IUserReferenceLocator` implementations, and the retention purge job. Every such site
  must be inside an `IPlatformAdminScope` block. `IAuditStore` is asserted to expose no
  update method at all.
- **Why the exception list is closed and named:** the earlier phrasing ("anywhere in the
  codebase") contradicted two paths the corpus ships by design and would have failed on
  its first green run. Naming the two sites keeps the rule enforceable; widening the list
  requires an ADR. The database-level guard is
  [`AuditLog_Update_Is_Column_Restricted`](#auditlog_update_is_column_restricted), which
  is what actually constrains *which columns* may change.
- **Source:** ADR-0033 (carried from ADR-0016).
- **Type:** xUnit + source / migration scan. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 9).

#### `OperationType_Enum_Matches_Catalog`

- **Asserts:** the `OperationType` enum and the audit-operation catalogue in
  [18-audit-coverage.md](18-audit-coverage.md) contain the same members.
- **Source:** ADR-0033 (carried from ADR-0016).
- **Type:** xUnit + reflection + Markdown parse. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 9).

> **Retired from this section.** `AuditLogBehavior_NeverBlocks_BusinessWrites` — see
> § Retired. Its assertion is now false for MUST-class audit.

### Hub contract surface

Source: [ADR-0034 Hub Contract Surface Invariant](../decisions/0034-hub-contract-surface-invariant.md),
[ADR-0019](../decisions/0019-learnstack-hub.md),
[ADR-0020](../decisions/0020-triple-deployment-hybrid-license.md).

#### `LearnStack_Modules_DoNotReference_Hub`

- **Asserts:** no module assembly references a Hub client type or the Hub URL.
- **Source:** ADR-0019; ADR-0034 invariant 2.
- **Type:** xUnit + NetArchTest. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 10).

#### `Hub_Client_Referenced_Only_By_Named_Adapters`

- **Asserts:** the Hub HTTP client type is constructed or injected **only** inside
  `IEntitlementProvider`, `IUsageReporter`, and `IHubTenantSync` implementations. Any
  other holder fails the build.
- **Why it matters:** this is the mechanical half of ADR-0034's second invariant, and it
  is the check that would have caught `CachedHostToTenantResolver` calling
  `IHubClient.LookupHostAsync` — an unrecorded endpoint, called from outside the
  sanctioned adapters, on the hot path of every anonymous public page load.
- **Source:** ADR-0034 § Implementation Notes.
- **Type:** xUnit + NetArchTest over constructor and field types. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02c.

#### `Hub_NeverStores_TenantData`

- **Asserts:** the Hub schema contains no tenant-content table (courses, lessons,
  learners, enrollments, sessions, media, content entries). Invariant 1 of ADR-0034.
- **Source:** ADR-0034 § Decision.
- **Type:** schema scan. **Kind:** structural.
- **Status:** **Registered** — owned and run by the `learnstack-hub` repository; listed
  here because the invariant is shared.
- **Phase:** 02c (Hub-side).

#### `Internal_API_Endpoints_AreNot_Public`

- **Asserts:** every `/api/internal/*` route is served by the internal listener only and
  is absent from the public route table and the public OpenAPI document.
- **Source:** ADR-0019; ADR-0034.
- **Type:** xUnit + route-table inspection. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02c.

#### `IEntitlementProvider_Implementations_Are_Three`

- **Asserts:** no `IEntitlementProvider` implementation exists outside the named three —
  `NullEntitlementProvider`, `HubEntitlementProvider`,
  `SignedLicenseKeyEntitlementProvider` — and the composition root selects one by
  `DeploymentMode`.
- **Source:** ADR-0020; ADR-0034.
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Registered** — vacuous until the second implementation exists; only
  `NullEntitlementProvider` ships before Phase 02c
  ([ADR-0035](../decisions/0035-demand-gated-infrastructure.md)).
- **Phase:** 02c.

#### `NullEntitlementProvider_NotRegistered_OutsideDevelopment`

- **Asserts:** once Phase 02c lands, `NullEntitlementProvider` is registered only under
  `DeploymentMode.Development`.
- **Source:** ADR-0020; ADR-0035 § Implementation Notes.
- **Type:** xUnit + service-collection inspection per mode. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02c.

#### `LicenseKey_Validation_Is_Pinned_RSA2048`

- **Asserts:** signed-licence verification pins RSA-2048 and rejects an algorithm named
  by the token itself.
- **Source:** ADR-0020.
- **Type:** xUnit. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 11 — the signed-licence adapter is demand-gated on a signed Self-Hosted
  contract ([ADR-0035](../decisions/0035-demand-gated-infrastructure.md)).

#### `Hub_Modules_DoNotReference_LearnStack_Internals`

- **Asserts:** Hub module assemblies reference LearnStack `Application.Contracts` DTOs
  only — never a LearnStack `Domain` or `Infrastructure` type. The mirror image of
  `LearnStack_Modules_DoNotReference_Hub`; without both, the contract is one-directional.
- **Source:** [24-learnstack-hub.md § 10](../architecture/24-learnstack-hub.md);
  ADR-0019; ADR-0034 invariant 2.
- **Type:** xUnit + NetArchTest. **Kind:** structural.
- **Status:** **Registered** — owned and run by the `learnstack-hub` repository; listed
  here because the invariant is shared.
- **Phase:** 02c (Hub-side).

#### `Stripe_SDK_Types_NotImportedOutsideInfrastructure`

- **Asserts:** `Stripe.*` types appear only inside
  `LearnStack.Hub.Modules.Subscriptions.Infrastructure.Stripe`.
- **Source:** [24-learnstack-hub.md § 10](../architecture/24-learnstack-hub.md);
  ADR-0019 § provider adapters.
- **Type:** xUnit + NetArchTest. **Kind:** structural.
- **Status:** **Registered** — owned and run by the `learnstack-hub` repository.
- **Phase:** 09b (Hub-side).

#### `Iyzico_SDK_Types_NotImportedOutsideInfrastructure`

- **Asserts:** `Iyzipay.*` types appear only inside the Subscriptions module's Iyzico
  infrastructure namespace.
- **Source:** [24-learnstack-hub.md § 10](../architecture/24-learnstack-hub.md);
  ADR-0019 § provider adapters.
- **Type:** xUnit + NetArchTest. **Kind:** structural.
- **Status:** **Registered** — owned and run by the `learnstack-hub` repository.
- **Phase:** 09b (Hub-side).

#### `Hub_Operator_JWT_NeverAccepted_On_LearnStack_Routes`

- **Asserts:** a `learnstack-hub` realm JWT is rejected by every LearnStack
  tenant-facing endpoint, and a `learnstack` realm token is rejected on
  `/api/internal/*`. The two-realm boundary from [ADR-0004](../decisions/0004-authentication-strategy.md)
  is the reason the Admin Studio proxies custom-domain submission instead of calling the
  Hub directly.
- **Source:** [24-learnstack-hub.md § 10](../architecture/24-learnstack-hub.md);
  ADR-0004; ADR-0019.
- **Type:** **integration** test, run from the Hub side against a LearnStack instance.
  **Kind:** runtime.
- **Status:** **Registered** — owned and run by the `learnstack-hub` repository; listed
  here because the boundary it defends is LearnStack's.
- **Phase:** 02c (Hub-side).

### Events, jobs, and correlation

Introduced by [Phase 02b](../roadmap/phase-02b-events-auth.md).

#### `Outbox_Row_Carries_Correlation_Context`

- **Asserts:** every persisted `outbox_messages` row has non-null
  `tenant_id` and `correlation_id` columns. Integration test that writes
  through `IOutbox.EnqueueAsync` and inspects the row.
- **Source:** ADR-0032 § Sub-decision 12;
  [ADR-0006](../decisions/0006-events-and-outbox.md) Amendment 1.
- **Type:** integration test (Testcontainers). **Kind:** runtime.
- **Status:** **Registered.**
- **Phase:** 02b.

#### `Hangfire_Job_Payloads_Include_TenantId`

- **Asserts:** Hangfire enqueue rejects job payloads missing `tenant_id`
  or `correlation_id`. Per the `JobActivator` contract the enqueue path
  fails at submission, not at activation, so the failure mode is loud.
- **Source:** ADR-0032 § Sub-decision 12; Phase 02b deliverable.
- **Type:** xUnit + Hangfire enqueue interceptor test. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02b.

#### `Integration_Event_Handler_Restores_Tenant_Context`

- **Asserts:** when an outbox consumer dispatches an integration event,
  the inner handler scope has `ITenantContext.IsResolved == true` before
  business code runs. Verifies the envelope-to-context restoration.
- **Source:** ADR-0032 § Sub-decision 12; Phase 02b deliverable.
- **Type:** integration test (Testcontainers). Runs against
  `InProcessEventBus` — which is a first-class transport with the same handler
  interface, inbox guard and context restoration as the durable path
  ([ADR-0035](../decisions/0035-demand-gated-infrastructure.md)), so the assertion does
  not wait on the Dapr adapter. **Kind:** runtime.
- **Status:** **Registered.**
- **Phase:** 02b.

#### `Integration_Event_Handlers_Use_InboxGuard`

- **Asserts:** every `IIntegrationEventHandler<T>` calls
  `IInboxGuard.IsAlreadyProcessedAsync` before any business logic.
- **Source:** [20-infrastructure-stack.md § `IEventBus`](20-infrastructure-stack.md);
  [ADR-0006](../decisions/0006-events-and-outbox.md).
- **Type:** xUnit + IL / source scan. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02b.

#### `Integration_Events_Inherit_From_IntegrationEventBase`

- **Asserts:** every type implementing `IIntegrationEvent` extends `IntegrationEventBase`,
  which carries `EventId`, `OccurredAt` and `TenantId` as `required` members and declares
  `Topic` and `PartitionKey` abstract, and is a JSON-serialisable record. The payload is
  written by `ToPayloadJson()`, which serialises by runtime type — serializing through the
  interface silently drops every member the concrete event adds.
- **Source:** [15-event-and-outbox.md § Architecture tests](../architecture/15-event-and-outbox.md);
  [ADR-0006](../decisions/0006-events-and-outbox.md).
- **Type:** xUnit + reflection over module assemblies. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02b.

#### `Integration_Event_Declares_PartitionKey`

- **Asserts:** every `IIntegrationEvent` resolves a non-null partition key. `PartitionKey`
  is abstract on `IntegrationEventBase`, so the compiler already refuses an event that
  omits it; the residual assertion is that the value is non-null and non-blank at
  runtime. `IntegrationEventEnvelope` reads it off the event — it is deliberately **not**
  threaded through `IEventBus` as a second parameter, which is the source of drift
  [ADR-0038](../decisions/0038-cross-cutting-port-and-event-contracts.md) removes. `InProcessEventBus`
  serialises dispatch per key: concurrent across keys, sequential within one.
- **Source:** [Phase 02b](../roadmap/phase-02b-events-auth.md);
  [15-event-and-outbox.md](../architecture/15-event-and-outbox.md).
- **Type:** xUnit + reflection over module assemblies. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02b.

#### `OutboxProcessor_NeverBlocks_OnSingleMessageFailure`

- **Asserts:** one poisoned message does not prevent the rest of its batch from being
  dispatched.
- **Source:** [15-event-and-outbox.md § Architecture tests](../architecture/15-event-and-outbox.md);
  Phase 02b deliverable.
- **Type:** **integration** test (Testcontainers). **Kind:** runtime.
- **Status:** **Registered.**
- **Phase:** 02b.

#### `Outbox_Claim_IsHeld_Until_Dispatch_Completes`

- **Asserts:** two concurrent `OutboxProcessor` instances draining one pending batch
  dispatch each message **exactly once** — the claim is held for the duration of the
  dispatch, not released when the row is read. Requires **two** processors: a
  single-processor test passes against the broken protocol.
- **Source:** [Phase 02b](../roadmap/phase-02b-events-auth.md);
  [15-event-and-outbox.md](../architecture/15-event-and-outbox.md).
- **Type:** **integration** test (Testcontainers, two processes). **Kind:** runtime.
- **Status:** **Registered.**
- **Phase:** 02b.

### Demand-gated: lands with its adapter

These rules are agreed but cannot be written until the technology they constrain is
wired. Their owning phase is the phase that lands the adapter, per
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md). Registering them here
rather than deleting them keeps the rule visible and stops it being re-invented under a
different name.

| Test | Rule | Lands in |
|---|---|---|
| `Dapr_PubSub_TopicNames_FollowConvention` | Topic names match `learnstack.{module}.{aggregate}` | [Phase 11](../roadmap/phase-11-production-hardening.md), with the Dapr adapter |
| `Dapr_SDK_Types_NotImportedOutsideInfrastructure` | Dapr SDK types appear only in `LearnStack.Infrastructure.*` | [Phase 11](../roadmap/phase-11-production-hardening.md) |
| `Modules_DoNotReference_DaprPackage` | No module assembly references the Dapr package | [Phase 11](../roadmap/phase-11-production-hardening.md) |
| `ICacheService_Is_OnlyCacheAbstraction` | No second cache abstraction is introduced alongside `ICacheService` | [Phase 11](../roadmap/phase-11-production-hardening.md) |

`Dapr_PubSub_TopicNames_FollowConvention` was previously listed as a Phase 02a
deliverable. Phase 02a ships `InProcessEventBus` and no Dapr components, so there is
nothing for that test to scan — it inspects the Dapr component bindings.

Deferring it left the convention unasserted against the transport that is actually
registered, which is the shape of gap this catalogue exists to close. It is therefore
**split in two**, and the transport-independent half is not deferred:

#### `Modules_Do_Not_Inject_IEventBus_Directly`

- **Asserts:** no type in a module assembly takes, returns or stores `IEventBus`, and no
  module takes or stores `IServiceProvider` as a service-locator escape hatch. Constructor
  and method parameters, return types, fields and properties are checked. The only
  sanctioned publisher is the `OutboxProcessor`; modules write to the outbox.
- **Source:** [20-infrastructure-stack.md § `IEventBus`](20-infrastructure-stack.md);
  [ADR-0010](../decisions/0010-cross-module-communication.md).
- **Type:** xUnit + reflection over module assemblies. **Kind:** structural.
- **Status:** **Implemented** (`CrossCuttingFoundationTests`). A module holding the bus
  gets a synchronous cross-module call with no durability and no transactional
  atomicity — a fifth cross-module mechanism in everything but name, and one that looks
  like it works in every development test because the in-process transport delivers
  inline. A namespace ban cannot express it: modules legitimately depend on
  `LearnStack.SharedKernel.Messaging` for `IIntegrationEvent` and
  `IIntegrationEventHandler<T>`. The module sweep is vacuous until a module ships code,
  so the checker is pointed at direct-injection, method-injection and service-locator
  deliberate offenders in the test assembly first.
- **Phase:** 02a Packet 5.

#### `Integration_Event_TopicNames_FollowConvention`

- **Asserts:** every declared integration-event type resolves a topic matching
  `learnstack.{module}.{aggregate}`, plus the Hub-only four-segment form
  `learnstack.hub.{domain}.{event}`. Segments start with a lower-case letter, may contain
  internal hyphens, and never end in a hyphen. Reads
  the event declarations, not a broker, so it holds for whichever `IEventBus`
  implementation is registered.
- **Source:** [20-infrastructure-stack.md § `IEventBus`](20-infrastructure-stack.md);
  [ADR-0006](../decisions/0006-events-and-outbox.md).
- **Type:** xUnit + reflection over module assemblies. **Kind:** structural.
- **Status:** **Implemented** (`CrossCuttingFoundationTests`). No module declares an
  event yet, so the module sweep is vacuous today; the convention checker is pointed at
  deliberate offenders first, so the rule can be shown to fire.
- **Phase:** 02a (Packet 5) — lands with `InProcessEventBus`, the first transport.

Writing it required a contract change. The rule reads the event **declarations**, and
while the topic was a producer-supplied string on the envelope nothing declared one —
the rule could not be written at all. `Topic` is now abstract on `IntegrationEventBase`,
alongside `PartitionKey` and for the same reason: it is a property of the event type,
not of one delivery, so a per-delivery parameter is a second source that can disagree
with the first.

`Dapr_PubSub_TopicNames_FollowConvention` keeps its Phase 11 slot and narrows to what
only it can check: that the Dapr component bindings agree with the topics the events
declare.

`Modules_Do_Not_Inject_Valkey_Directly` is **not** in this table. It constrains module
code rather than adapter code, holds regardless of which cache implementation is
registered, and is listed under § Repository layout and module boundaries as a
Packet 10 deliverable.

### Awaiting backfill

Every identifier previously parked in this section has been folded into the catalogue
above under its canonical name. Anything newly discovered in an ADR or standard is added
here with **Status: Registered** by the next PR that touches its source document —
registering costs one row, and an unregistered rule is how the six-spelling drift
started.

### Retired

#### `AuditLogBehavior_NeverBlocks_BusinessWrites`

- **Retired 2026-08-08** by [ADR-0033](../decisions/0033-audit-durability-model.md),
  which supersedes ADR-0016. The assertion is now false by design for MUST-class audit:
  a MUST-class audit failure **must** block the business write, because the audit row is
  part of the operation's contract and shares its transaction.
- **Replaced by** [`MustClass_Audit_Writes_Share_The_Business_Transaction`](#mustclass_audit_writes_share_the_business_transaction).
  The surviving half of the old rule — SHOULD/MAY-class audit never blocks — is asserted
  inside that test's SHOULD/MAY cases.
- Never implemented, so nothing was deleted from CI.

### API conventions (ADR-0024)

#### `Every_Endpoint_Is_Under_Versioned_Route`

- **Asserts:** every route in the production host's `EndpointDataSource` is
  under `/api/v{N}/`, except the unversioned infrastructure endpoints
  (`/healthz`, `/readyz`), the OpenAPI document and its UI, and the
  `/api/internal/*` Hub surface, which versions itself per ADR-0019. Paired
  with `The_Endpoint_Set_Is_Not_Empty` and
  `The_Production_Host_Sees_No_Test_Controller`, so it cannot pass by finding
  nothing or by inspecting the wrong host.
- **Source:** ADR-0024 § Implementation Notes.
- **Type:** xUnit + `EndpointDataSource` inspection over a
  `WebApplicationFactory<Program>` host. **Kind:** behavioural.
- **Status:** **Implemented** (`VersionedRouteEnforcementTests`, in
  `LearnStack.Tests.Integration`).
- **Phase:** 02a Packet 4.
- **Note:** with no production controller yet, this assertion runs over the
  host's infrastructure routes only — mutating `VersionedRouteConvention` or
  removing `app.MapControllers()` leaves it green, which was measured. What
  carries the rule today is the four startup guards below, each of which fails
  a real host. This entry becomes load-bearing the moment the first controller
  ships, which is why it is written against the endpoint set rather than
  against a controller list. It was also **first written as a reflection scan
  and was wrong twice over**, which is worth recording because both mistakes look like working
  tests. It scanned `Assembly.GetReferencedAssemblies()` — the emitted
  AssemblyRef table, not the project's references, and the compiler elides a
  reference whose types the IL never touches — so it reached four assemblies
  and no module, while MVC discovers controllers from the runtime dependency
  graph. And with no production controller to find, it passed vacuously: no-op'ing
  `VersionedRouteConvention.Apply` left every architecture test green while
  turning 9 of the 14 tests then in `LearnStack.Tests.Integration` red. That
  measurement is why Packet 4 removed the
  `FullyQualifiedName!~LearnStack.Tests.Integration` filter from the `backend`
  CI job.

#### `An_Absolute_Controller_Route_Fails_At_Startup` / `An_Absolute_Action_Route_Fails_At_Startup`

- **Asserts:** a controller or action declaring an absolute route template
  (`/x` or `~/x`) aborts host startup. MVC leaves an absolute template outside
  every prefix, so such an endpoint is served unversioned — the one escape from
  `VersionedRouteConvention` that no route-shape assertion can see, because the
  offending route simply is not where the test looks.
- **Source:** ADR-0024 § Implementation Notes.
- **Type:** xUnit + host startup. **Kind:** behavioural.
- **Status:** **Implemented** (`VersionedRouteEnforcementTests`).
- **Phase:** 02a Packet 4.

#### `A_Major_Outside_LiveMajors_Fails_At_Startup`

- **Asserts:** a controller declaring `[ApiVersion(N)]` for an `N` absent from
  `ApiVersioningExtensions.LiveMajors` aborts host startup, so a route can
  never be served under a major no OpenAPI document publishes and no generated
  SDK can call.
- **Source:** ADR-0024 § The version axis.
- **Type:** xUnit + host startup. **Kind:** behavioural.
- **Status:** **Implemented** (`VersionedRouteEnforcementTests`).
- **Phase:** 02a Packet 4.

#### `A_Bare_ControllerBase_Fails_At_Startup`

- **Asserts:** a controller without `[ApiController]` aborts host startup. Two
  defects travel together on that shape. It has no controller-level route
  template, so the convention has nothing to prefix and MVC routes every action
  at the bare `api/v{N}` with the resource segment dropped — two such
  controllers then collide as a 500 `AmbiguousMatchException` at request time,
  the one escape in this set that failed at *request* time rather than startup.
  And without `[ApiController]` the automatic 400 never runs, so a malformed
  body reaches the handler and surfaces as a 500 `internal_error` instead of the
  400 `validation_failed` Problem Details Standards 09 § API Surface fixes as
  the single error shape.
- **Source:** ADR-0024 § Implementation Notes; Standards 09 § API Surface.
- **Type:** xUnit + host startup. **Kind:** behavioural.
- **Status:** **Implemented** (`VersionedRouteEnforcementTests`).
- **Phase:** 02a Packet 4.

#### `An_Absolute_Internal_Route_Is_Exempt_At_Both_Levels`

- **Asserts:** `/api/internal/*` stays exempt whether its template is written
  relative or absolute, at the controller level and at the action level. The
  action-level guard normalised the template before testing the exemption and
  the controller-level one did not, so an absolute Hub route was refused at
  startup — on a surface ADR-0024 does not govern at all.
- **Source:** ADR-0019; ADR-0024 § The version axis.
- **Type:** xUnit + host startup. **Kind:** behavioural.
- **Status:** **Implemented** (`VersionedRouteEnforcementTests`).
- **Phase:** 02a Packet 4.

#### `A_Hand_Written_Prefix_That_Disagrees_With_The_Attribute_Fails_At_Startup`

- **Asserts:** a route template already written under `api/v{N}` must agree
  with the controller's `[ApiVersion]`. The idempotency guard that makes a
  double convention registration harmless would otherwise double as an escape
  hatch, with the route saying one major and the `x-version-introduced`
  extension — read off the attribute — saying another.
- **Source:** ADR-0024 § The version axis.
- **Type:** xUnit + host startup. **Kind:** behavioural.
- **Status:** **Implemented** (`VersionedRouteEnforcementTests`).
- **Phase:** 02a Packet 4.

#### `Live_Majors_Are_At_Most_Two_Adjacent`

- **Asserts:** `ApiVersioningExtensions.LiveMajors` holds at most two majors,
  they are distinct and adjacent, and none is below 1.
- **Source:** ADR-0024 § The version axis ("Two adjacent majors coexist";
  "No `/api/v0/*` endpoints exist or will exist").
- **Type:** xUnit. **Kind:** structural.
- **Status:** **Implemented** (`ApiConventionTests`).
- **Phase:** 02a Packet 4.

#### `Unversioned_Route_Prefixes_Are_Declared_Once`

- **Asserts:** `VersionedRouteConvention.UnversionedRoutePrefixes` equals
  exactly `["api/internal"]`, so a widening of the exemption set is a failing
  test rather than a silent hole.
- **Source:** ADR-0024 § The version axis; ADR-0019.
- **Type:** xUnit. **Kind:** structural.
- **Status:** **Implemented** (`ApiConventionTests`).
- **Phase:** 02a Packet 4.

#### `Every_Deprecated_Endpoint_Has_Sunset_And_Successor`

- **Asserts:** every controller action marked `[Obsolete]` declares both a
  sunset date and a successor route, and the emitted OpenAPI operation carries
  `x-sunset`, `x-successor` and `x-migration-guide`.
- **Source:** ADR-0024 § Lifecycle of a deprecated endpoint.
- **Type:** xUnit + OpenAPI document inspection. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** the packet that adds the first `/api/v2` endpoint. ADR-0024 states
  it lands "when the first `/v2` endpoint is added"; there is no deprecated
  operation before one exists, so registering it now records the name without
  claiming coverage.

### Error shape (Standards 04 § Error Responses, Standards 09 § API Surface)

#### `A_Non_Positive_Limit_Names_The_Parameter_The_Client_Sent`

- **Asserts:** `?limit=0` and `?limit=-5` return 400 with `errors.limit`, and
  with neither `$` nor `pagination` — the binder-internal names MVC produced
  when the kernel's `CursorPagination` was bound directly and its `init`
  accessor threw.
- **Source:** Standards 04 § Pagination; Phase 02a Packet 4's named correctness
  fix.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`ErrorShapeHttpTests`).
- **Phase:** 02a Packet 4.
- **Note:** the roadmap described this defect as an **unhandled
  `ArgumentOutOfRangeException` producing a 500**. Measured at the start of
  Packet 4 step 2, it was already a 400 — the `InvalidModelStateResponseFactory`
  wired one step earlier catches the binder's exception. What remained was that
  the 400 named no parameter the client had sent. The record is corrected here
  rather than in the roadmap's frozen packet text.

#### `A_Limit_Above_The_Maximum_Is_Clamped_Not_Rejected`

- **Asserts:** `?limit=9999` returns 200 with an effective limit of 100. The
  wire type deliberately does not enforce the ceiling, because
  `CursorPagination` clamps and two layers disagreeing about it is worse than
  either answer.
- **Source:** Standards 04 § Pagination.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`ErrorShapeHttpTests`).
- **Phase:** 02a Packet 4.

#### `A_Malformed_Sort_Names_The_Parameter_The_Client_Sent`

- **Asserts:** a `sort` value that violates the grammar — trailing comma, empty
  segment, a field starting with a digit, punctuation, the same field twice —
  returns 400 with `errors.sort`, keyed by the name the client sent rather than
  by the C# property or a binder key, carrying exactly one entry whose key is
  `lockey_invalid_value` and which has no `params`.
- **Note:** the entry is asserted whole, not merely present. Asserting presence
  hid a real gap: a richer, segment-bearing error was written for this path and
  was dead on arrival, because the wire type's `IValidatableObject` puts the
  failure in `ModelState` and `[ApiController]`'s automatic 400 answers before
  any action runs. The test could not tell the two bodies apart.
- **Source:** Standards 04 § Filtering and Sorting.
- **Type:** xUnit + HTTP, over `SortSpecificationTests` for the grammar itself.
  **Kind:** behavioural.
- **Status:** **Implemented** (`ErrorShapeHttpTests`, `SortSpecificationTests`).
- **Phase:** 02a Packet 4.

#### `A_Field_The_Endpoint_Does_Not_Allow_Is_Refused_By_Name`

- **Asserts:** a well-formed `sort` field outside the endpoint's allow-list
  returns 400 carrying `lockey_sort_field_not_allowed` with the field in
  `params`. Parsing and authorising are separate steps, and an ignored key
  would return a page in an order the client did not request.
- **Source:** Standards 04 § Filtering and Sorting.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`ErrorShapeHttpTests`).
- **Phase:** 02a Packet 4.

#### `List_Query_Parameters_Are_Published_Individually`

- **Asserts:** `cursor`, `limit`, `sort` and `q` appear as individual query
  parameters in the OpenAPI document, not as one opaque object. Standards 04
  § Filtering and Sorting requires each to be documented, and a generator that
  collapses a `[FromQuery]` complex type leaves the generated SDK unable to
  offer any of them as arguments.
- **Source:** Standards 04 § Filtering and Sorting, § OpenAPI.
- **Type:** xUnit + OpenAPI document inspection. **Kind:** behavioural.
- **Status:** **Implemented** (`ApiVersioningHttpTests`).
- **Phase:** 02a Packet 4.

#### `An_Unmatched_Route_Returns_Problem_Details` / `A_Wrong_Method_Returns_Problem_Details` / `An_Unsupported_Media_Type_Returns_Problem_Details`

- **Asserts:** an unmatched route (404), a wrong method (405) and an
  unsupported media type (415) each return `application/problem+json` with
  `code`, `messageKey`, `status` and `correlationId` — the same shape a handler
  error carries. Implemented as
  `An_Unmatched_Route_Returns_Problem_Details`,
  `A_Wrong_Method_Returns_Problem_Details` and
  `An_Unsupported_Media_Type_Returns_Problem_Details`.
- **Source:** Standards 04 § Error Responses; Standards 09 § API Surface.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`ErrorShapeHttpTests`).
- **Phase:** 02a Packet 4.
- **Note:** the three come from two different places and needed two hooks. 404
  and 405 are produced by **routing**, before MVC, so no MVC hook sees them —
  `UseStatusCodePages` does. 415 is produced by **MVC**, which already converted
  it to ASP.NET's own `ProblemDetails`: the right idea in the wrong shape, with
  no `code`, `messageKey` or `correlationId`. `IClientErrorFactory` replaces
  that conversion instead of layering over it.

### Idempotency and optimistic concurrency (Standards 04)

#### `A_Repeat_Replays_The_First_Response_Without_Doing_The_Work_Again`

- **Asserts:** two `POST`s carrying the same `Idempotency-Key` run the operation
  once and return byte-identical bodies, the second marked
  `Idempotency-Replayed: true`.
- **Source:** Standards 04 § Idempotency.
- **Type:** xUnit + HTTP over a probe that counts its own invocations.
  **Kind:** behavioural.
- **Status:** **Implemented** (`IdempotencyHttpTests`).
- **Phase:** 02a Packet 4.

#### `Two_Tenants_Using_The_Same_Key_Do_Not_Share_A_Response`

- **Asserts:** the same key under two tenants produces two runs and two bodies.
  The key is client-chosen, so two tenants will eventually pick the same one;
  a flat key space would hand the second one the first one's response body. Both
  clients run against **one** host — the store is a singleton and separate hosts
  would not share it, which would make the test pass for the wrong reason — and
  each names its tenant per request rather than switching a host-wide object no
  test restores.
- **Source:** Standards 04 § Idempotency; ADR-0003.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`IdempotencyHttpTests`).
- **Phase:** 02a Packet 4.

#### `A_Thrown_Attempt_Does_Not_Pin_The_Key`

- **Asserts:** an attempt that throws releases its key, so the retry runs.
  Recording a failure would replay it for the 24-hour retention window, turning
  one transient fault into a day of them. `A_Returned_5xx_Does_Not_Pin_The_Key_Either`
  covers the sibling branch — a handler that *returns* a 5xx rather than throwing
  is a different path, and it was untested.
- **Source:** Standards 04 § Idempotency.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`IdempotencyHttpTests`).
- **Phase:** 02a Packet 4.

#### `A_Malformed_Header_Fails_Rather_Than_Counting_As_Absent`

- **Asserts:** an unparseable `If-Match` fails the precondition rather than
  being read as absent. Reading "I could not parse your precondition" as "you
  did not send one" turns a conditional write into an unconditional one —
  exactly the overwrite the client was preventing. Paired with
  `A_Weak_Tag_Never_Matches`, which pins the strong comparison RFC 9110
  § 13.1.1 requires.
- **Source:** Standards 04 § Optimistic Concurrency.
- **Type:** xUnit. **Kind:** behavioural.
- **Status:** **Implemented** (`EntityTagTests`).
- **Phase:** 02a Packet 4.

#### `Idempotent_Endpoints_Are_Unsafe_Methods`

- **Asserts:** no endpoint marks a safe method `[Idempotent]`. An idempotency
  key exists to keep an operation with external side effects from happening
  twice; a safe method has none to repeat, so the attribute protects nothing and
  only makes a read fail for every client that did not send a header no read
  needs.
- **Source:** [ADR-0037](../decisions/0037-idempotency-key-contract.md).
- **Type:** xUnit over the host's real `EndpointDataSource`. **Kind:**
  structural.
- **Status:** **Implemented** (`IdempotentEndpointConventionTests`). The
  production surface carries no `[Idempotent]` endpoint until Phase 09, so the
  rule would pass vacuously; a companion test drives the same predicate over a
  probe host that *does* violate it, which is what distinguishes the guard from
  an empty assertion.
- **Phase:** 02a Packet 4.

#### `A_Sweep_Never_Destroys_A_Claim_Another_Thread_Just_Won`

- **Asserts:** the store's expiry sweep never removes an entry other than the one
  it observed. Removing by key alone deletes whatever sits there *now*, which —
  between the enumerator seeing an expired entry and the removal running — may be
  a live claim another thread just acquired; the next caller then finds the key
  absent and runs the operation a second time, concurrently with the first.
- **Source:** [ADR-0037](../decisions/0037-idempotency-key-contract.md).
- **Type:** xUnit stress test on a frozen, hand-advanced clock. **Kind:**
  behavioural.
- **Status:** **Implemented** (`InMemoryIdempotencyStoreTests`). The window is
  the sweep's own enumeration, so the test walks 400 expired entries per round
  while twelve dedicated threads take them over from staggered offsets. Verified
  by mutation: the key-only removal is killed 5/5, and the correct code passes
  10/10.
- **Phase:** 02a Packet 4.

#### `The_Same_Key_On_A_Different_Endpoint_Is_Refused_Not_Replayed`

- **Asserts:** a key presented for a different request answers **409**
  `idempotency_key_reuse` rather than replaying. Sibling cases cover a different
  body (`The_Same_Key_With_A_Different_Body_Is_Refused`) and a different user in
  one tenant (`The_Same_Key_From_A_Different_User_In_One_Tenant_Is_Refused`) —
  the three leaks a client-chosen key enables, closed by one fingerprint.
- **Source:** [ADR-0037](../decisions/0037-idempotency-key-contract.md) §
  Identity.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`IdempotencyHttpTests`).
- **Phase:** 02a Packet 4.

#### `A_Response_Too_Large_To_Store_Refuses_The_Retry_Rather_Than_Rerunning_It`

- **Asserts:** an outcome that exceeds the replay cap is tombstoned, so the retry
  answers **409** `idempotency_outcome_unavailable` and the operation runs once.
  Releasing the key instead would let it run twice with a `2xx` both times, on
  the surface Standards 04 reserves for payments.
- **Source:** [ADR-0037](../decisions/0037-idempotency-key-contract.md) § What is
  recorded.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`IdempotencyHttpTests`).
- **Phase:** 02a Packet 4.

#### `A_Result_That_Throws_After_Writing_Part_Of_The_Body_Still_Answers_A_Problem_Details_500`

- **Asserts:** when an action's result throws partway through writing, the client
  receives the RFC 7807 500 rather than the bytes the formatter managed to
  produce. The filter buffers the response body, and MVC returns normally from
  `next()` and rethrows only after the filter unwinds — so the buffer can already
  hold a half-written body. Copying it out starts the response, which both hands
  the client a truncated `2xx` and takes the exception away from
  `UseExceptionHandler`, whose 500 cannot be written once the response has
  started.
- **Source:** [ADR-0037](../decisions/0037-idempotency-key-contract.md);
  [ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md).
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`IdempotencyHttpTests`).
- **Phase:** 02a Packet 4.

#### `An_Idempotent_Operation_Publishes_Its_Header_In_The_Contract`

- **Asserts:** the OpenAPI document for an `[Idempotent]` operation carries the
  required `Idempotency-Key` header and documents its 409. Without it the
  attribute is invisible to the generated SDK, every call the SDK makes is
  answered 400, and "the first consumer is a one-attribute change" is not true.
- **Source:** [ADR-0037](../decisions/0037-idempotency-key-contract.md);
  Standards 04 § OpenAPI.
- **Type:** xUnit + HTTP against the emitted document. **Kind:** behavioural.
- **Status:** **Implemented** (`IdempotentEndpointConventionTests`).
- **Phase:** 02a Packet 4.

### Tenant and organization resolution (ADR-0036)

The binding evidence for this group is the **runtime** matrix in
[ADR-0036 § Architecture tests](../decisions/0036-tenant-resolution-trusted-inputs.md),
executed against a live PostgreSQL connected as `learnstack_app`. Data flow from a
header into a tenant context is not reliably provable by a type-reference scan — a
helper, an interface or an indirect assignment slips past one. The structural entries
below narrow where the bug can hide; they do not prove isolation. See § What a
structural test proves — and what it does not.

#### `Effective_Host_Normalization_Is_Total`

- **Asserts:** `EffectiveHost.Normalize` returns a value or `null` for every input and never throws — including the `xn--` forms that make `HostString.FromUriComponent` raise, which an anonymous remote client could otherwise use to drive unhandled exceptions into the error tracker. Covers the two corrections in [ADR-0036 Amendment 1](../decisions/0036-tenant-resolution-trusted-inputs.md): the port is stripped **before** the IPv4 test, so `1.2.3.4:443` is refused, and the result passes a letters-digits-hyphen-dot whitelist, so `IdnMapping`'s compatibility mapping cannot smuggle `/`, `@` or `%` past the input scan. And [Amendment 4](../decisions/0036-tenant-resolution-trusted-inputs.md)'s correction, which generalizes the same argument to the remaining input-side check: the IPv4 refusal re-runs on the value being returned, so a trailing dot cannot carry `1.2.3.4.` past it — nor can the fullwidth and ideographic dots `GetAscii` folds into `.` after the early check has already run. Paired with `Anything_Normalize_Accepts_Is_A_Host_The_Cache_Key_Accepts`, which is a **separate invariant**: `EffectiveHost.Normalize` and `CacheKey.ForHostMapping` are two spellings of "what counts as a host", written in different assemblies, and every input the first accepts the second must accept too. Checking either alone is how they drifted — the accepted-then-throwing literal above was a `500` and an unsampled error-tracker capture per request, from an unauthenticated caller, where a bodyless `404` was specified.
- **Source:** ADR-0036 § Normalization, Amendment 1, Amendment 4.
- **Type:** xUnit. **Kind:** behavioural.
- **Status:** **Implemented** (`EffectiveHostTests`).
- **Phase:** 02a Packet 4; the Amendment 4 correction and the pairing property, Packet 7 step 4.

#### `Tenant_Assertions_Are_Compared_Not_Resolved`

- **Asserts:** `X-Tenant-Id` and `X-Organization-Id` never select anything. An assertion that agrees with what the API resolved passes; one that disagrees is **404**, not 403 — a wrong tenant id must not be able to tell the difference between "exists, not yours" and "does not exist"; a malformed or repeated one is **400** and counted; and an unresolved context passes the request through to be refused downstream rather than inventing a tenant.
- **Source:** ADR-0036 § The reconciliation matrix.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`TenantAssertionHttpTests`).
- **Phase:** 02a Packet 4.

#### `Anonymous_Requests_Are_Rate_Limited_Per_Peer`

- **Asserts:** the anonymous budget is spent per socket peer, a request over it is **429** with `Retry-After` and the one Problem Details shape, and the partition key never comes from a header. architecture/30 has promised this middleware since Phase 01; from Packet 7 every novel `Host` value buys a Postgres round trip on a pre-auth surface.
- **Source:** Standards 04 § Request and Response Limits; ADR-0036.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`RateLimitingHttpTests`, two cases: the budget and
  its error shape, and that a rotating `X-Forwarded-For` buys nothing). The
  partition key is guarded from the other side by
  `Ambient_Forwarded_Headers_Refuse_To_Start` — measured, with
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED` set, seventy requests rotating that
  header produced **zero** rejections against eleven without it, and the
  composition root refuses to start in that configuration now.
- **Phase:** 02a Packet 4.

#### `Tenant_Headers_Are_Never_A_Resolution_Source`

- **Asserts:** no production type assigns `ITenantContext.TenantId` or `OrganizationId` from a bound `X-Tenant-Id` / `X-Organization-Id` value, in any deployment mode. There is no mode-guarded exception.
- **Source:** ADR-0036 § What the assertions do.
- **Type:** xUnit source scan over `LearnStack.Api`. **Kind:** structural.
- **Status:** **Implemented** (`TenancyConventionTests`). A scan rather than a dependency check, because the resolver that could misuse these values lands in Packet 7 — a scan holds the line from the day the symbol exists.
- **Phase:** 02a Packet 4.

#### `Effective_Host_Computed_In_One_Place`

- **Asserts:** only `EffectiveHostAccessor` reads a request host. Bans `HttpRequest.Host`, `RequestHeaders.Host`, `HeaderDictionary` indexers carrying a `Host` / `X-Forwarded-Host` / `X-LearnStack-Host` / `Forwarded` literal, and `UriHelper.GetDisplayUrl` / `GetEncodedUrl` everywhere else.
- **Source:** ADR-0036 § Effective host and the trusted hop.
- **Type:** xUnit source scan over `LearnStack.Api`. **Kind:** structural.
- **Status:** **Implemented** (`TenancyConventionTests`). Bans `Request.Host`, `GetDisplayUrl`, `GetEncodedUrl` and `X-Forwarded-Host` outside `EffectiveHostAccessor`.
- **Phase:** 02a Packet 4.
- **Note:** Analyzer rather than NetArchTest: three of the four banned inputs appear only as string literals inside header lookups, which a type-reference scan cannot see.

#### `Forwarded_Headers_Are_Not_Wired`

- **Asserts:** the forwarded-headers middleware is not registered at all, so
  `Request.Host` and the socket peer are never overwritten in place. Broader than
  the `XForwardedHost`-only rule this row reserved: the peer check in
  `EffectiveHostAccessor` reads `IHttpConnectionFeature.RemoteIpAddress`, which is
  the **same storage** the middleware mutates — so banning one forwarded header
  would not have protected it. A tripwire, not a prohibition: the API will want
  forwarded headers for rate limiting and audit, and when they land the peer must
  be captured before that middleware runs. Failing the build is what forces that
  ordering to be decided rather than discovered.
- **Source:** ADR-0036 § Effective host and the trusted hop.
- **Type:** xUnit + options inspection. **Kind:** structural.
- **Status:** **Implemented** (`ApiConventionTests`). Supersedes the reserved
  spelling `Forwarded_Host_Header_Is_Never_Read_Directly`, which named a narrower
  rule than the one that holds.
- **Phase:** 02a Packet 4.

#### `Trusted_Hop_Requires_Network_And_Secret`

- **Asserts:** the trusted-hop predicate is false unless **both** the socket peer is inside `Tenancy:TrustedHop:Networks` **and** a fixed-time secret comparison succeeds. Neither condition alone admits the hop. Also covers what an untrusted request does with the host header — ignored entirely, so a scanner learns nothing — and that a repeated header is ignored even over the hop.
- **Source:** ADR-0036 § Effective host and the trusted hop.
- **Type:** xUnit behavioural matrix. **Kind:** behavioural.
- **Status:** **Implemented** (`EffectiveHostAccessorTests`). Verified by mutation: dropping the network half leaves eleven of thirteen cases green, and the two that fail are the two that exist for it.
- **Phase:** 02a Packet 4.

#### `Trusted_Hop_Reads_The_Socket_Peer`

- **Asserts:** the network check reads `IHttpConnectionFeature.RemoteIpAddress`, never `HttpContext.Connection.RemoteIpAddress`.
- **Source:** ADR-0036 § Effective host and the trusted hop.
- **Type:** Roslyn analyzer + xUnit. **Kind:** structural.
- **Status:** **Registered** — and the ADR's stated reason for it does not survive measurement. The two are the **same storage**, and `UseForwardedHeaders` mutates it, so reading the feature rather than the property buys nothing once that middleware runs. What makes the read correct today is `Forwarded_Headers_Are_Not_Wired` above. This rule keeps its place as the thing to implement when forwarded headers land, with the peer captured *before* them.
- **Phase:** the packet that wires forwarded headers.

#### `Deployment_Mode_Is_Required_Configuration`

- **Asserts:** the composition root throws when `Deployment:Mode` is absent, unknown, or given as an ordinal, and the key is **not** present in `appsettings.json`. It shipped there as `Development` — the file that goes to every environment — with the same value as the code default, so every Development-guarded mechanism was on by default in a deployment that never set it. No guard on the *value* could have caught that; only a guard on the file.
- **Source:** ADR-0036 § There is no Development override.
- **Type:** xUnit + configuration-file inspection. **Kind:** behavioural (value) + structural (file).
- **Status:** **Implemented** in two halves — `DeploymentModeConfigurationTests` for the value, `ApiConventionTests` for the file. Verified by mutation: putting the key back into `appsettings.json` turns the file half red.
- **Phase:** 02a Packet 4.

#### `Assertion_Recorder_Is_The_Only_Mismatch_Writer`

- **Asserts:** no type other than an `ITenantAssertionRecorder` implementation writes a tenant-assertion mismatch to a log, a metric or `IAuditStore`.
- **Source:** ADR-0036 § Recording a rejected assertion.
- **Type:** xUnit source scan over `LearnStack.Api`. **Kind:** structural.
- **Status:** **Implemented** (`TenancyConventionTests`). Keyed on the two counter names, so Packet 9's auditing recorder inherits the same single-writer rule.
- **Phase:** 02a Packet 4.

#### `Assertion_Budget_Does_Not_Depend_On_ICacheService`

- **Asserts:** the anonymous-burst counters resolve no `ICacheService`. A cache outage must not decide whether a MUST-class security event is recorded.
- **Source:** ADR-0036 § Recording a rejected assertion.
- **Type:** xUnit reflection check over the `LearnStack.Api.Tenancy` namespace **and** a source scan over `LearnStack.Api/Tenancy`. **Kind:** structural.
- **Status:** **Implemented** (`TenancyConventionTests`). It shipped in Packet 4 as a **tripwire**, because `ICacheService` did not exist yet; Packet 5 ships the port, so the rule now carries the dependency check it was always meant to be. Both forms are kept: reflection catches an injected dependency, the scan catches a service-locator resolve, and neither sees the other's case.
- **Phase:** 02a Packet 4.

#### `Api_Registers_Only_The_Tenant_Realm_Authority`

- **Asserts:** the composition root registers exactly one JWT authority for `/api/v1/*`, the `learnstack` realm. A `learnstack-hub` token on a tenant-facing endpoint is 401.
- **Source:** ADR-0036 § The signals; ADR-0004 Amendment 1.
- **Type:** xUnit + DI inspection. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02b.
- **Note:** The integration test is the load-bearing half: the structural test passes while issuer validation is disabled in configuration.

#### `Resolving_Host_Is_Set_In_One_Place`

- **Asserts:** `set_config('app.resolving_host'` appears in exactly one file across
  `backend/src` — `CachedHostToTenantResolver`. The bare literal is deliberately not banned:
  the migration's own policy DDL must name the variable in order to read it.
- **Why it matters:** `app.resolving_host` is the only session variable whose value *is* the
  lookup key. The policy on `platform_host_to_tenant` admits exactly the row the setter
  announces, so a second setter is a second announcement on the one table read before any
  tenant context exists — the one place a widened read is not already caught by
  `app.tenant_id` being `NULL`.
- **Source:** [11-security.md § Tenant Context](11-security.md);
  [05-database.md § Table classes](05-database.md); ADR-0036.
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** (Packet 7 step 4, `TenancyConventionTests`).
- **Phase:** 02a Packet 7.

#### `Host_Classification_Applies_To_Tenant_Facing_Routes_Only`

- **Asserts:** host classification runs for `/api/v1/*` and for no other prefix. `/healthz`, `/readyz`, `/openapi/*`, `/admin/hangfire*` and `/api/internal/*` are asserted as a **prefix list**, not as endpoint literals — a closed allow-list written as literals 404s the entire Hub contract surface. The list's **contents** are pinned as well as its shape: an emptied or shortened list would otherwise start classifying the Hub surface with every case still green.
- **Source:** ADR-0036 § The reconciliation matrix.
- **Type:** xUnit + route-table inspection. **Kind:** structural.
- **Status:** **Implemented** (Packet 7 step 4, `HostClassificationScopeTests`).
- **Phase:** 02a Packet 7.
- **Note:** driven against `HostClassificationMiddleware.ClassifiesPath` rather than
  through the middleware. The rule is about paths, and routing a request to observe it
  would need a resolver and a database the decision never touches. The prefix-versus-
  literal distinction is asserted directly — every excluded prefix must also exclude
  everything beneath it — because that is the half whose absence 404s the Hub contract
  surface.

#### `TenantContext_Is_Constructed_Only_By_The_Factory`

- **Asserts:** `TenantContext` is sealed with no public constructor and `TenantContextFactory.Create` is its only entry point. Five conjuncts, and they need **two instruments** — which is why this is written out rather than expressed as one NetArchTest chain. Reflection covers sealedness, the absent public constructor, the absence of any `InternalsVisibleTo` on `LearnStack.SharedKernel` (one attribute would hand a whole assembly the constructor), and the single member whose return type mentions `TenantContext`. It cannot cover the fifth: a `new` expression is a call site, not a type reference. That one is a source scan — `TenantContext_Is_Instantiated_In_One_File` — banning `new TenantContext(` everywhere in the kernel but the factory's own file, which is exactly the residual an `internal` constructor leaves. **`internal` and not `private`:** C# has no friend types, so a private constructor and a top-level `TenantContextFactory` — the name ADR-0036, the glossary and two roadmap lines all carry — are mutually exclusive, and both normative carriers say only *public*. The factory returns `Result.Fail` on any disagreement and never a partially populated context.
- **Source:** ADR-0036 § The reconciliation matrix.
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented** (`TenantContextConstructionTests`, Packet 7 step 5).
- **Phase:** 02a Packet 7.

#### `TenantContext_Is_Instantiated_In_One_File`

- **Asserts:** the literal `new TenantContext(` appears in exactly one file under `backend/src/LearnStack.SharedKernel` — which is every file that can compile the call, since the constructor is `internal` and the assembly has no `InternalsVisibleTo` — `TenantContextFactory.cs`. Comments and whitespace are stripped first, because the files these rules cover argue in prose about the very literal they may not write.
- **Why it matters:** the second instrument `TenantContext_Is_Constructed_Only_By_The_Factory` needs and cannot be. `internal` blocks every other assembly, and nothing but a scan blocks a second caller inside the kernel itself — which would be a second entry point producing a context the matrix never decided.
- **Source:** [ADR-0036 § Rules](../decisions/0036-tenant-resolution-trusted-inputs.md).
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** (`TenantContextConstructionTests`, Packet 7 step 5).
- **Phase:** 02a Packet 7.

#### `SetTenant_Callers_Are_The_Enumerated_Four`

- **Asserts:** `ITenantContextAccessor.Current` is **written** only by `TenantResolverMiddleware`, `HubCorrelationMiddleware`, the Hangfire `JobActivator`, and the outbox / inbox handler scope. `EnterPlatformAdminScope` is not among them: it opens a second connection and sets no tenant context. Reads are unconstrained.
- **Source:** ADR-0036 § Rules, second bullet, as corrected by its erratum and
  [Amendment 2](../decisions/0036-tenant-resolution-trusted-inputs.md).
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** (`TenantContextConstructionTests`, Packet 7 step 5).
- **Phase:** 02a Packet 7.
- **Note:** the name predates the correction and is kept. `ITenantContextAccessor`
  declares one member, `ITenantContext? Current { get; set; }`, and the `SetTenant`
  this row used to name has never existed; ADR-0036 Amendment 2 fixes the ADR and
  keeps the test's spelling, because § Canonical names makes a rename its own
  liability and the name describes the caller set, which is what ADR-0036 decides.
- **Note:** a source scan rather than NetArchTest: NetArchTest resolves *type*
  references and cannot see a write to a property, which is the whole assertion —
  the same reason `Effective_Host_Computed_In_One_Place` is a scan. The needle
  (`.Current =`) is receiver-agnostic, so an unrelated `Activity.Current =` would trip
  it; that is a false positive to exempt by path, never a reason to filter by folder.
- **Note:** **two of the four callers exist** — `TenantResolverMiddleware` (Packet 7
  step 5) and the integration-event handler scope in `InProcessEventBus` (Packet 5).
  `HubCorrelationMiddleware` is Phase 02c and the Hangfire `JobActivator` is Phase 02b.
  Until they land the rule's live work is the **negative** — no writer outside the set.
  The first version of the test scanned only files whose path contained `Tenancy`,
  which deleted the `InProcessEventBus` writer from its own expectation *and* let a
  fifth writer anywhere else in the tree pass green: a rule whose job is the negative
  cannot be scoped to the folder its positives happen to live in.

#### `Requests_Are_Never_Streamed`

- **Asserts:** no production request type implements `IStreamRequest<>`, and (in `Handlers_Return_Result`) no type implements `IStreamRequestHandler<,>` or the void `IRequestHandler<>`.
- **Why it matters:** all three shapes run with **no pipeline behaviors at all**. MediatR routes a stream through `IStreamPipelineBehavior<,>`, of which this solution registers none; and measured against MediatR 12.4.1, `typeof(IRequestHandler<>).GetInterfaces()` is empty — the void handler does not derive from `IRequestHandler<T, Unit>` — while `Unit` does not implement `IResultBase`, which every LearnStack behavior is constrained on. So each shape bypasses the authority ceiling, validation, audit classification and `TransactionBehavior` — and therefore the `SET LOCAL app.tenant_id` that makes Row Level Security non-`NULL`. RLS keeps EF reads fail-closed; what is exposed is every effect that is not an EF read.
- **Source:** ADR-0032 § Sub-decision 2; [02-backend-coding.md § MediatR Use Cases](02-backend-coding.md).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented** (`RequestSurfaceTests` and `CrossCuttingFoundationTests`, Packet 7 step 6).
- **Phase:** 02a Packet 7.
- **Note:** vacuous today — nothing streams — and that is the point of landing it now. The shapes are invisible to the ordinary `IRequest<>` filter, so without this rule the first one to arrive would be counted as absent rather than caught.

#### `PublicSurface_Marker_Set_Is_Enumerated`

- **Asserts:** every `[PublicSurface]` request type appears in the enumerated set in [Standards 04 § Public surface](04-api-design.md) with its permitted methods; the default is `GET`/`HEAD` and a mutating entry states why. No `[PublicSurface]` type performs a tenant-owned write.
- **Source:** ADR-0036 § The reconciliation matrix;
  [Standards 04 § Public surface](04-api-design.md).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented** (`RequestSurfaceTests`, Packet 7 step 6).
- **Phase:** 02a Packet 7.
- **Note:** the two directions are not equally vacuous, and the existing note above covers
  only one of them. **Marked set → table** is vacuous while no type carries the marker.
  **Table → marked set** is live from the day it ships: the table may not name a type that
  carries no attribute, because an entry there reads as a reviewed decision and one with
  nothing behind it is a decision the pipeline never enforces. The table ships empty, so that
  leg asserts emptiness — and becomes an assertion about something the moment Phase 02d
  writes its first row.
- **Note:** the set ships **empty** in Packet 7, which registers no `[PublicSurface]`
  request type, and takes its first rows in
  [Phase 02d](../roadmap/phase-02d-walking-skeleton.md). The rule is vacuously green
  until then.

#### `PublicSurface_Requests_Are_Never_ReadSensitive`

- **Asserts:** no `[PublicSurface]` request type is classified MUST-class `read-sensitive`. Otherwise an anonymous `GET` becomes a durable standalone audit write.
- **Source:** ADR-0036 § The reconciliation matrix;
  [Standards 04 § Public surface](04-api-design.md).
- **Type:** xUnit + reflection (set-emptiness); the audit-catalogue cross-check from Packet 9. **Kind:** structural.
- **Status:** **Implemented** (`RequestSurfaceTests`, Packet 7 step 6) — as set-emptiness only.
- **Phase:** 02a Packet 7; the cross-check leg, Packet 9.
- **Note:** **vacuous on both sides today, and the Type field above said otherwise.** The
  catalogued instrument was an audit-catalogue cross-check against a catalogue that does not
  exist in code — `IAuditStore` and the operation catalogue are Packet 9 — so the leg that
  runs is the emptiness of the marked set, which makes the claim trivially true rather than
  checked. It is landed rather than deferred so that a marked type arriving before Packet 9
  turns this rule red and forces the question, instead of passing quietly under a rule whose
  stated instrument was never built.

#### `Organizations_Are_Read_By_Composite_Key`

- **Asserts:** `IOrganizationScopeValidator` and every organization read resolve by the composite key `(tenant_id, id)`, never by `id` alone. `pk_organizations` is the surrogate id, so a lookup by it is a well-formed, index-served query that returns another tenant's row — for the policy to hide if the announcement was made, and to hand back if it was not. Two legs: the raw-SQL leg pins the validator's `WHERE` clause and its `set_config` announcement (scanned, because a command's text is a string literal no type-reference test can see), and the EF leg bans `Organizations.Find`/`FindAsync`, which take the primary key and therefore cannot express the composite one. **The EF leg is vacuous today** and deliberately kept: nothing reads `organizations` through a `DbContext` until Packet 7 step 9 writes the first command, and a scan added only once there is something to catch is a scan nobody adds. The runtime suite cannot substitute for either leg — with the announcement made, the policy makes both spellings behave identically, which is defence in depth working and is exactly why the rule has to be structural.
- **Source:** ADR-0036 § The reconciliation matrix.
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** (`TenantContextConstructionTests`, Packet 7 step 5).
- **Phase:** 02a Packet 7.

#### `Tenant_Scope_Widening_Is_Never_Set_From_Request_Input`

- **Asserts:** `app.scope = 'tenant'` is derived from the actor's role plus a declared tenant-wide operation, never from a header, query parameter, cookie or body, and is unreachable under `TenantContextOrigin.HostOnly`.
- **Source:** ADR-0036 § The reconciliation matrix.
- **Type:** xUnit + NetArchTest. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02a Packet 7.
- **Note:** no `app.scope` carrier ships in Packet 7. `ITenantContext` exposes no scope
  member and the flag derives from the actor's **role**, which lands with `Membership` /
  `Role` in [Phase 03](../roadmap/phase-03-identity-admin.md) — after
  [Phase 02b](../roadmap/phase-02b-events-auth.md)'s authenticated principal, which is the
  prerequisite and not the carrier
  ([11-security.md § Tenant Context](11-security.md)). The rule holds as a negative until
  then — nothing sets the flag, so nothing sets it from request input — and becomes
  non-vacuous in Phase 03.

#### `The_Platform_Scope_Writes_No_Tenant_Context_And_Sets_No_Session_Variable`

- **Asserts:** `PlatformAdminScope.cs` contains none of `set_config(`, `SetTenantContextAsync` or `IUnitOfWork`, with comments and whitespace stripped first.
- **Why it matters:** it pins the complement of two closed sets, and getting either wrong reopens a set an ADR closed. `PlatformAdminScope` is **not** a fifth writer of `ITenantContextAccessor.Current` — [ADR-0036 § Rules](../decisions/0036-tenant-resolution-trusted-inputs.md) names it as explicitly not one, and `SetTenant_Callers_Are_The_Enumerated_Four` covers that globally. It is **not** an eighth out-of-band setter of `app.tenant_id` either: the role bypasses policies, so there is nothing to announce to, and [ADR-0040 Amendment 3](../decisions/0040-ambient-unit-of-work.md) closes that set at seven on the property that every one of them connects as `learnstack_app`. And it must not enlist on the ambient unit of work, which would put the bypass on the request's own connection and leave it there.
- **Source:** ADR-0003; ADR-0036 § Rules; ADR-0040 Amendment 3.
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** (`PlatformAdminScopeConventionTests`, Packet 7 step 7).
- **Phase:** 02a Packet 7.

#### `PlatformAdminScope_Entry_Requires_Platform_Permission`

- **Asserts:** `EnterPlatformAdminScope(reason)` cannot open without an authenticated principal holding a Platform-scope permission, and no handler carries both `[AllowsUnresolvedTenantContext]` and a platform-scope entry.
- **Source:** ADR-0036 § The platform-admin override is not a resolution source.
- **Type:** xUnit. **Kind:** behavioural.
- **Status:** **Implemented** (`PlatformAdminScopeConventionTests`, Packet 7 step 7) — conjunct A only.
- **Phase:** 02a Packet 7.
- **Note:** **the permission clause is live in its mechanism and vacuous in its subject;
  the marker clause is vacuous outright.** Two Notes previously stood here assigning
  "live" to opposite clauses — one written when the rule was Registered and one when it
  landed — and this replaces both.

  *Mechanism, live:* the gate is a real port, the registered implementation refuses
  everyone, `PlatformAdminScope` consults it, and no second implementation exists in any
  production assembly — which is how a permissive default actually arrives, registered
  elsewhere for a demo. The ordering, gate before the credential is touched, is
  behavioural and asserted in `PlatformAdminGateTests`; a structural rule cannot see it.

  *Subject, vacuous:* there is no permission to hold. `AuthorizationBehavior.Handle` is
  `return next()`, authentication arrives in
  [Phase 02b](../roadmap/phase-02b-events-auth.md), and the Platform-scope permission
  with the Identity module in [Phase 03](../roadmap/phase-03-identity-admin.md). So
  nothing exercises a *permitted* entry, and the gate refusing everyone blocks nothing
  this packet ships — Packet 9's GDPR redaction is the first real caller and inherits it.

  *Marker clause, still vacuous — but for a narrower reason since Packet 7 step 9:* no
  handler carries both `[AllowsUnresolvedTenantContext]` and a platform-scope entry.
  `ProvisionTenantCommand` now carries the first, and nothing carries the second, so the
  conjunction is empty because one half of it is — not because both are.

#### `Development_Only_Tenant_Header_Override_Is_Mode_Guarded`

- **Status:** **Reserved and retired.** Never implemented.
- **Why:** an early draft of ADR-0036 carried a `DeploymentMode.Development` flag that
  let `X-Tenant-Id` act as the resolution source, and this test would have guarded it.
  The flag was retired before it shipped: the trusted hop lets a `curl` supply an
  effective host that goes through the real resolver, the real policy and the real
  matrix, so there is no code path anywhere that writes a tenant id from a header. The
  name is recorded here so it does not reappear as a second spelling for something else.
- **Source:** ADR-0036 § There is no Development override.

## References

- [ADR-0003 Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md) (Amendment 3)
- [ADR-0018 Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md)
- [ADR-0032 Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md)
- [ADR-0033 Audit Durability Model](../decisions/0033-audit-durability-model.md)
- [ADR-0024 API Versioning Policy](../decisions/0024-api-versioning-policy.md)
- [ADR-0034 Hub Contract Surface Invariant](../decisions/0034-hub-contract-surface-invariant.md)
- [ADR-0035 Demand-Gated Infrastructure](../decisions/0035-demand-gated-infrastructure.md)
- [ADR-0036 Trusted Inputs for Tenant and Organization Resolution](../decisions/0036-tenant-resolution-trusted-inputs.md)
- [02-backend-coding.md § Pipeline Behaviors](02-backend-coding.md)
- [05-database.md § Tenant-Owned and Organization-Scoped Tables](05-database.md)
- [09-error-handling.md](09-error-handling.md)
- [10-observability.md](10-observability.md)
- [11-security.md § Tenant Context](11-security.md)
- [20-infrastructure-stack.md](20-infrastructure-stack.md)
- [Phase 02a Roadmap § Architecture Tests](../roadmap/phase-02a-kernel-tenancy.md)
- [Phase 02b Roadmap § Architecture Tests](../roadmap/phase-02b-events-auth.md)
- [add-architecture-test skill](../../.claude/skills/add-architecture-test/SKILL.md)
