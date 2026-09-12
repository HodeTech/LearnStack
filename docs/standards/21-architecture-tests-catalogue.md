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
tenant-isolation rule and **five** of the organization-scope rule, across eleven files
between them. § Canonical names and superseded spellings records the mapping, and what
survived the reconciliation.

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
- **The binding proof of isolation is behavioural.** Isolation is a property of what a
  query returns, and only a query can observe it. The proof lives in the
  [Phase 02a Packet 7](../roadmap/phase-02a-kernel-tenancy.md) integration suite, which
  connects as **`learnstack_app`** — a non-owning, `NOBYPASSRLS` role. A test that
  connects as the table owner or as a `BYPASSRLS` role passes even when every policy on
  every table is inert, and therefore proves nothing at all.

Rows in this catalogue carry a **Kind** — *structural*, *behavioural*, *compile-time* or
*startup*, defined in § Implementation status — so a reader can tell which question the
test answers.

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
| **structural** | A shape — types, references, attributes, configuration, source text, a schema catalogue, an endpoint list, a published document |
| **behavioural** | What the real thing does when exercised — a request sent, a pipeline or a predicate run, a query or a transaction executed, a script run |
| **compile-time** | That the build fails, via an analyzer diagnostic |
| **startup** | That the host refuses to start, via a composition-root guard |

A structural assertion that a rule *exists* is not a proof that it *holds*; see
§ What a structural test proves — and what it does not. `compile-time` and `startup`
are the two kinds that fail *before* anything can be observed misbehaving, which
is why they are named separately rather than folded into `structural`. The Kind follows
the question, not the assembly: an integration test that reads `pg_indexes` is
structural, and a unit test that runs a predicate is behavioural.

Claiming a rule is "enforced by an architecture test" when the test is registered but
not implemented is the failure mode this column exists to prevent.

### Implemented today

Ninety-four test methods exist in
[`backend/tests/LearnStack.Tests.Architecture`](../../backend/tests/LearnStack.Tests.Architecture),
shipped by [Phase 01](../roadmap/phase-01-repository-tooling.md),
[Phase 02a Packets 2–3](../roadmap/phase-02a-kernel-tenancy.md), Packet 4,
Packet 6, Packet 7, Packet 8, Packet 9 and Packet 10 — 119 cases once the theories
expand. Counted from `dotnet test --list-tests` after Packet 10's first step,
de-duplicated by method name; Packet 10 recounts when it closes. Counting the literals
`[Fact]` and `[Theory]` in the source gives 100 and is wrong both ways: seven are string
literals in `Every_Database_Test_Carries_The_Docker_Trait` and its companion, which grep
the suite for those very attributes, and the meta-test's `[Fact(DisplayName = …)]` is not
the literal at all. The runner is the authority here, which
is why this sentence now names the command rather than the packet.
Methods are not rows: a `[Theory]`
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

Packet 9 added **three** more behavioural rows to that assembly, all under § Audit:
`MustClass_Audit_Writes_Share_The_Business_Transaction` and
`Audit_Survives_Transaction_Rollback` in `AuditPipelineTests`, and
`AuditLog_Update_Is_Column_Restricted` in `AuditSchemaTests`. They are integration rows for the reason the
route-shape rule is: a reflection scan cannot see whether a row committed with the
business write, and a unit test against doubles passes whether or not Row Level Security
would have accepted the insert. The first two connect as `learnstack_app`;
`AuditLog_Update_Is_Column_Restricted` uses three roles by design, because its subject is
what each of them may and may not update.

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
| `ModuleContracts_DoNotDependOn_AnyModuleDomain` (per-module theory) | `ModuleDependencyTests.cs` |
| `Meta_NetArchTest_DetectsAPlantedViolation` | `ModuleDependencyTests.cs` |
| `Live_Majors_Are_At_Most_Two_Adjacent` | `ApiConventionTests.cs` |
| `Unversioned_Route_Prefixes_Are_Declared_Once` | `ApiConventionTests.cs` |
| `Forwarded_Headers_Are_Not_Wired` | `ApiConventionTests.cs` |
| `Deployment_Mode_Is_Required_Configuration` | `ApiConventionTests.cs` |
| `Effective_Host_Computed_In_One_Place` | `TenancyConventionTests.cs` |
| `Tenant_Headers_Are_Never_A_Resolution_Source` | `TenancyConventionTests.cs` |
| `Assertion_Recorder_Is_The_Only_Mismatch_Writer` | `TenancyConventionTests.cs` |
| `Assertion_Recorder_Is_The_Only_Writer_Of_Its_Audit_Slugs` | `TenancyConventionTests.cs` |
| `Assertion_Budget_Does_Not_Depend_On_ICacheService` | `TenancyConventionTests.cs` |
| `Organization_Aggregate_Declared_In_Tenancy_Domain` (per-type theory) | `TenancyConventionTests.cs` |
| `Aggregates_With_Optimistic_Concurrency_Map_RowVersion` | `PersistenceConventionTests.cs` |
| `Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork` | `PersistenceConventionTests.cs` |
| `The_registration_marker_does_not_vouch_across_containers` | `PersistenceConventionTests.cs` |
| `Every_Database_Test_Carries_The_Docker_Trait` (with its companion) | `PersistenceConventionTests.cs` |
| `Unique_Indexes_On_Soft_Deletable_Tables_Exclude_Deleted_Rows` (with its companion) | `PersistenceConventionTests.cs` |
| `Migrate_Target_Refuses_An_Aliased_Runtime_Credential` (per-alias theory) | `PersistenceConventionTests.cs` |
| `Migrate_Target_Redacts_A_Quoted_Value_Whole` (per-shape theory) | `PersistenceConventionTests.cs` |
| `Migrate_Target_Reads_The_Role_Through_A_Quoted_Value` | `PersistenceConventionTests.cs` |
| `Migrate_Target_Refuses_A_Uri_Without_Echoing_Its_Userinfo` | `PersistenceConventionTests.cs` |
| `TransactionBehavior_Does_Not_Reference_A_Module_Assembly` | `PersistenceConventionTests.cs` |
| `Migration_Startup_Project_References_EntityFrameworkCore_Design` | `PersistenceConventionTests.cs` |
| `Migrate_Target_Covers_Every_Migration_Chain` | `PersistenceConventionTests.cs` |
| `Migrate_Target_Applies_The_Tenancy_Chain_First` | `PersistenceConventionTests.cs` |
| `Audit_Closed_Set_Columns_Store_What_Their_Check_Admits` | `AuditConventionTests.cs` |
| `AuditEntry_Inherits_Entity_Not_AuditableEntity` | `AuditConventionTests.cs` |
| `AuditEntry_Is_AppendOnly` (with its companion) | `AuditConventionTests.cs` |
| `OperationType_Enum_Matches_Catalog` | `AuditConventionTests.cs` |
| `Every_Module_Has_An_AuditCoverage_Matrix` (with its companion) | `AuditConventionTests.cs` |
| `Every_Shipped_Request_Is_Registered` (with its companion) | `AuditCoverageTests.cs` |
| `Every_TenantOwned_Command_HasAuditCoverage` (catalogue → matrix, with its two companions) | `AuditCoverageTests.cs` |
| `Every_Matrix_Row_Whose_Command_Exists_Is_Registered` (matrix → catalogue, with its three companions) | `AuditCoverageTests.cs` |
| `Every_Module_With_An_Aggregate_Or_A_Request_Has_A_Matrix` (with its companion) | `AuditCoverageTests.cs` |
| `No_Set_Based_Write_Bypasses_The_Audit_Capture` (with its companion) | `AuditConventionTests.cs` |
| `PublicSurface_Requests_Are_Never_ReadSensitive` (with its companion) | `RequestSurfaceTests.cs` |
| `No_Source_Folder_Named_Verticals` | `RepositoryLayoutTests.cs` |
| `Frontend_Has_Only_The_Web_App` | `RepositoryLayoutTests.cs` |
| `Commit_Subject_Grammar_Is_Stated_Once` | `RepositoryLayoutTests.cs` |

Eleven further rules in this catalogue are **implemented outside** that assembly and are
no less binding. Seven of them could not live in it: a policy that is well-formed
and wrong, a foreign key with no index, or a row that did or did not commit with the
business write, is only visible against an applied schema.

| Rule | Where |
|---|---|
| `ValidationBehavior_DoesNotThrow_ValidationException` | `LearnStack.Tests.Unit` + `LearnStack.Tests.Integration` |
| `TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing` | `LearnStack.Tests.Unit` |
| `SoftDelete_Advances_The_Row_Version` | `LearnStack.Tests.Unit` (`AuditableEntityTests`) |
| `TenantWide_Row_Of_TenantB_Is_Invisible_To_TenantA` | `LearnStack.Tests.Integration` (`TenancySchemaTests`) |
| `Write_With_Foreign_TenantId_Is_Rejected_By_WithCheck` | `LearnStack.Tests.Integration` (`TenancySchemaTests`) |
| `Every_Foreign_Key_Has_A_Supporting_Index` | `LearnStack.Tests.Integration` (`TenancySchemaTests`) |
| `LearnStackException-DomainExceptionThrow` (`LS0001`) | `backend/analyzers/LearnStack.Analyzers` + `DomainExceptionThrowAnalyzerTests` |
| `MustClass_Audit_Writes_Share_The_Business_Transaction` | `LearnStack.Tests.Integration` (`AuditPipelineTests`) |
| `Audit_Survives_Transaction_Rollback` | `LearnStack.Tests.Integration` (`AuditPipelineTests`) |
| `AuditLog_Update_Is_Column_Restricted` | `LearnStack.Tests.Integration` (`AuditSchemaTests`) |
| `AuditStateCapture_ClearedPerRequest` | `LearnStack.Tests.Unit` (`AuditLogBehaviorTests`) |

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

The reconciliation was owed by [Phase 02a Packet 10](../roadmap/phase-02a-kernel-tenancy.md)
and is complete. The tenant and organization rules have run under their canonical names
since Packet 7 — across every module with a schema since Packet 8 — and
`Core_Modules_HaveNo_DomainSpecific_Names` is registered under its own for Packet 10. A
superseded spelling appears only in this catalogue, which maps each one to its canonical
name, and in Accepted ADR bodies, which keep it as history
([ADR-0041](../decisions/0041-correcting-false-statements-in-accepted-adrs.md)); the
rest of the corpus carries none.

### Entitlement projection schema

**Canonical: `LicenseKey_Payload_MatchesSchema`** — `entitlement-v1.schema.json` pinned
by a snapshot test in both repositories, registered under § Awaiting backfill for Phase
02c.

| Superseded spelling | Where it appeared |
|---|---|
| `EntitlementProjection_Shape_IsStable` | [ADR-0021](../decisions/0021-feature-based-entitlement.md) |

### Entitlement keys

**Canonical: `FeatureKey_AllReferences_AreInRegistry`** — one rule over every key a call
site names, feature, limit and killswitch alike.

| Superseded spelling | Where it appeared |
|---|---|
| `FeatureFlagKeys_AllReferences_AreInRegistry` | [ADR-0021](../decisions/0021-feature-based-entitlement.md), whose own 2026-05-18 amendment renames it |
| `LimitKeys_AllReferences_AreInRegistry` | [ADR-0021](../decisions/0021-feature-based-entitlement.md) |

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
   `[name](../standards/21-architecture-tests-catalogue.md#name_lowercased)` — GitHub
   lowercases the heading and keeps its underscores, so `Every_Module_Has_An_AuditCoverage_Matrix`
   is `#every_module_has_an_auditcoverage_matrix`.

When a rule is agreed but not yet written, register it with **Status: Registered** and
the owning packet. Registering costs one row and makes the gap legible; the alternative
is a rule that lives only in an ADR's implementation notes.

A rule may ship before its first subject exists only with a companion that plants the
violation it exists to catch and shows the rule reporting it. Without one, register the
rule and implement it with that subject: a rule with nothing to scan is green whether its
mechanism works or not, which is why
[ADR-0023 Amendment 1](../decisions/0023-strongly-typed-id-source-generator.md) moved
`Aggregate_Roots_Use_StronglyTypedId` to the first aggregate.

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
  Details body shape so a regression at any layer surfaces. **Kind:** behavioural.
- **Status:** **Implemented** — both variants, outside the Architecture assembly.
- **Phase:** 02a (Packet 3).

#### `Domain_Methods_Do_Not_Throw_For_Expected_Cases`

- **Asserts:** the Roslyn analyzer `LearnStackException-DomainExceptionThrow`
  (diagnostic id `LS0001`), run over the sources of the core `Domain` and `Application` and
  every module's, reports nothing the rule refuses: no **unsuppressed** report anywhere —
  that is a Warning nobody has justified — and no report at all, suppressed or not, inside a
  `Result`-returning method — property and indexer included, since `Result<T> Current => …`
  carries the same channel, and a local function or lambda counts as the member that contains
  it — because a member with a channel for an expected case has no excuse for throwing one. A suppressed report in a method that returns no result is the
  sanctioned aggregate-invariant throw and passes. Each project is also asserted to reference
  the analyzer as an analyzer, or the discipline holds only inside this test.
- **Source:** ADR-0032 § Sub-decision 4;
  [09-error-handling.md § Domain Exceptions](09-error-handling.md).
- **Type:** xUnit + Roslyn analyzer report inspection. **Kind:** structural — the question
  is a shape of the source, and the analyzer is how it is read. The compile-time row is the
  analyzer itself, `LearnStackException-DomainExceptionThrow`.
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`, over `AnalyzerReport`,
  Packet 10. The build cannot be the gate: `LS0001` is in `WarningsNotAsErrors` until the
  Phase 03 escalation, and a pragma is the sanctioned way to keep a genuine invariant throw —
  so the test compiles each project's sources itself, sets the severity, and reports
  suppressed diagnostics. It parses with the symbols the build defines, so an `#if` region is
  read rather than skipped, and it fails loudly on a source it cannot parse, because a compiler
  behind the SDK reads a new language feature as a syntax error and a scan that cannot read a
  file sees nothing in it. The wiring leg reads the reference as an element rather than as a
  line: the attributes may be written in either order, a commented-out reference is not a
  reference, and a `Condition` is refused outright — a conditional analyzer does not run in the
  configuration the condition excludes, and this rule cannot tell which that is. Its companion,
  `The_Domain_Exception_Report_Can_Actually_Fail`, plants all four shapes — thrown from a
  `Result` method, the same one silenced by a pragma, a pragma-silenced invariant guard, and
  an unsuppressed throw — and requires exactly the three the rule refuses. Mutation-checked:
  a `throw new DomainException(...)` in a `Result`-returning method in Tenancy fails it.
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
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`. Live since Packet 7 with
  Tenancy's three handlers; Customization's four joined in Packet 8, and all seven return
  a `Result`.
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

#### `JsonSchema_Net_Types_NotImportedOutsideInfrastructure`

- **Asserts:** no module assembly and no core assembly (`LearnStack.SharedKernel`,
  `LearnStack.Domain`, `LearnStack.Application`, `LearnStack.Application.Contracts`,
  `LearnStack.Api`, `LearnStack.Tools.Seeder`) depends on the `Json.Schema` namespace.
  Only `LearnStack.Infrastructure.Validation` references the package; everything else
  reaches the evaluator through `IJsonSchemaValidator`. The sweep includes the shared
  kernel deliberately — it declares the port, so it is the assembly most likely to reach
  for the library by accident. The seeder joined it in Packet 8 step 5, and it is the
  entry that needed a decision rather than a habit: as the second composition root it
  legitimately references the adapter **project** in order to register the port, and
  that reference is exactly what would let it name a `Json.Schema` type with no other
  rule noticing. `Adapters_Wrap_Provider_Exceptions` does **not** cover this: its forbidden list
  is a closed enumeration of network-reached provider SDKs, and an in-process evaluator
  is not one — the same split the corpus already made between
  `Dapr_SDK_Types_NotImportedOutsideInfrastructure` and the exception rule.
- **Source:** [ADR-0043](../decisions/0043-customization-payload-validation.md) § 1 and
  its § Architecture Tests.
- **Type:** xUnit + NetArchTest namespace-dependency scan. **Kind:** structural.
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`. Verified against a
  planted violation: a `Json.Schema` reference added to the shared kernel makes it fail.
- **Phase:** 02a (Packet 8).

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
- **Type:** xUnit unit test (`LearnStack.Tests.Unit`). **Kind:** behavioural.
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

- **Asserts:** no name the platform ships carries a term from the forbidden list below.
  Matching is on word segments — PascalCase and acronym boundaries, `_`, `-`, `.` and
  `/` — and a plural counts as its singular, so `ClassName` and `Grade` survive while
  `CefrLevel`, `asana-pose` and `Yogas` do not.
- **Forbidden terms:** `CEFR`, `English`, `Asana`, `Yoga`, `Kyu`, `Dan`, `Kata`, `Belt`,
  `Chord`, `CodeChallenge`, `IELTS`, `TOEFL`. This line is the list: the test reads it, so
  adding a term is a one-line change here and nowhere else.
- **Subjects:** in every production backend assembly, type, member, namespace and file
  names; the EF table and column names each module's model maps **and** the ones a migration
  creates in raw SQL, which belong to no model — `outbox_messages` and `idempotency_keys` are
  both; the audit operation slugs the catalogue registers; the feature, limit and killswitch
  keys; the primitive and composite renderer keys; the permission keys once the registry exists,
  and the block registry once [Phase 04](../roadmap/phase-04-cms-media-pages.md) ships it; and,
  across `frontend/` — every app and every package, because `packages/ui` is where a component
  extracted out of the app lands — file names and exported identifiers, comments stripped
  first. **Not** subjects: test code, the seeder's seed data — two demo tenants in
  unrelated domains are the point of it, so `SeedData.English` is data, not a platform
  name — and comments and free text. A capability that serves every domain is not a
  domain term: `ai.pronunciation_feedback` names what the platform does, not whom for
  ([Platform Vision § Genericity boundary](../architecture/01-platform-vision.md)).
- **Source:** [ADR-0018](../decisions/0018-tenant-driven-customization-model.md)
  (and its 2026-08-08 genericity-boundary amendment);
  [00-principles.md § 1](00-principles.md).
- **Type:** xUnit — reflection over the production assemblies, the EF models, the
  audit catalogue and the key registries, plus a file scan of `frontend/apps/web`.
  **Kind:** structural.
- **Status:** **Implemented** — `DomainGenericityTests.cs`, Packet 10. It is the mechanical
  guarantee behind the platform's entire premise — "the core stays generic" — and it had no
  implementation while its far weaker sibling `No_Source_Folder_Named_Verticals` stayed green
  from Phase 01: renaming a folder is not the failure mode anyone was worried about;
  `CefrLevel` on an Education aggregate is. Each subject asserts it read something, so a
  collector that stops seeing its names fails rather than reporting clean, and the companion,
  `The_Domain_Term_Scan_Can_Actually_Fail`, feeds the matcher every shape the Asserts line
  claims — and the ones it must not flag, `Grade`, `Danger` and `ai.pronunciation_feedback`
  among them — and exercises each collector on a probe: a type and member in this assembly, a
  model that maps `belt_ranks.kyu_level`, a `CREATE TABLE` a migration would carry, and a
  module that exports a default, a rename and a commented-out declaration. The term list is
  read whole: a term written with a digit, a space or a hyphen fails the parse rather than
  being dropped. Mutation-checked: a `CefrLevel` property on `Tenant` fails it.
- **Phase:** 02a (Packet 10).

#### `Frontend_Has_Only_The_Web_App`

- **Asserts:** `frontend/apps` contains exactly one Next.js application (`web`).
  A peer `studio` or `portal` app requires an ADR amending
  [ADR-0009](../decisions/0009-frontend-single-app-first.md).
- **Source:** ADR-0009.
- **Type:** xUnit + directory scan. **Kind:** structural.
- **Status:** **Implemented** — `RepositoryLayoutTests.cs`.
- **Phase:** 01.

#### `Commit_Subject_Grammar_Is_Stated_Once`

- **Asserts:** CI's commit-hygiene step runs the `commit-msg` hook, in its strict mode,
  and states no grammar of its own; the types the hook admits are exactly the types
  [Standards 14 § Commits](14-git-workflow.md#commits) tabulates; and the hook, run for
  real on a set of messages, admits and refuses each one as that section says — the
  breaking-change `!`, an empty scope, a first paragraph that runs onto a second line, a
  72-character subject counted in characters, git's `Revert "…"` subject, the autosquash
  markers locally and in CI, a leading `#` line with and without an editor, and a
  ten-megabyte paragraph inside the timeout. It then runs the step's own script against a
  scratch repository: a refused subject fails the step and is named in an `::error::`
  line, an admitted one passes, and a base that does not resolve fails rather than
  reporting success on a range it never read.
- **Why it matters:** three copies of the rule drifted with nothing comparing them — nine
  types in the standard, eleven in the hook, ten in CI, scope characters that disagreed,
  and two scripts judging different text (the hook the file's first line, CI git's joined
  first paragraph) — so subjects passed locally and failed the pull request. CI now runs
  the hook itself. Comparing text alone was not enough even then: a hook that matched its
  pattern and forgot to fail, or a CI step whose only mention of the hook was a comment,
  passed a comparison and fails this.
- **Source:** [14-git-workflow.md § Commits](14-git-workflow.md#commits).
- **Type:** xUnit + file scan + running the hook and the CI step. **Kind:** behavioural
  (the hook's verdicts and the step's) + structural (the type table; no grammar in CI).
- **Status:** **Implemented** — `RepositoryLayoutTests.cs`, Packet 10.
- **Phase:** 02a (Packet 10).

#### `Generic_Primitives_Only_In_Renderer`

- **Asserts:** the frontend `PRIMITIVE_KEYS` array and the backend's
  `PrimitiveRendererKey.All` each contain exactly the documented closed set of generic
  primitives. A new primitive is a LearnStack release guarded by CODEOWNERS, not a tenant
  action — tenant-specific blocks are `TenantPageBlock` rows pointing at a composite
  renderer key. Equality in both copies, where the composite rule below is containment:
  an `x-renderer` resolves against the backend's copy **on save**
  ([§ 8.1](../architecture/32-tenant-customization-model.md)), so a key only the frontend
  knows cannot be authored, and a key only the backend knows is saved and drawn by
  nothing.
- **Source:** [ADR-0018 § Architecture tests](../decisions/0018-tenant-driven-customization-model.md);
  [32-tenant-customization-model.md § 2](../architecture/32-tenant-customization-model.md).
  Named in shipped code at `frontend/apps/web/src/lib/customization/primitives.ts` and
  `LearnStack.Modules.Customization.Domain/Identifiers.cs`.
- **Type:** xUnit + a bounded scan of the named `as const` declaration. **Kind:** structural.
- **Status:** **Implemented** — `CustomizationRegistryTests.cs`. The set is written out
  as literals in the test rather than read from either registry, so a thirteenth
  primitive fails the rule instead of being absorbed by it — which is what the two
  copies had already done to each other before Packet 8 pinned them.
- **Phase:** 02a (Packet 8).

#### `Composite_Renderer_Keys_Match_The_Frontend_Registry`

- **Asserts:** every composite renderer key `frontend/apps/web/src/lib/customization/composites.ts`
  registers is declared by the backend's `CompositeRendererKey.All`. Containment, not
  equality: the documented set is nine and the frontend registers four, because the five
  shells land with the phases that render them — a declared-but-unregistered key renders
  `UnknownBlock` ([ADR-0013](../decisions/0013-page-block-schema-versioning.md)), while a
  registered-but-undeclared one makes the backend refuse a save for a renderer the page
  can draw. `Generic_Primitives_Only_In_Renderer` above is the sibling rule on the same
  file pair, pinning the primitive set.
- **Source:** [ADR-0018 § Renderer architecture](../decisions/0018-tenant-driven-customization-model.md);
  [32-tenant-customization-model.md § 2 and § 8.1](../architecture/32-tenant-customization-model.md).
- **Type:** xUnit + a bounded scan of the named `as const` declaration. **Kind:** structural.
- **Status:** **Implemented** — `CustomizationRegistryTests.cs`.
- **Phase:** 02a (Packet 8).

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
- **Phase:** 01.

#### `ModuleDomain_DoesNotDependOn_AnyApplicationOrInfrastructure`

- **Asserts:** per module, `Domain` references no LearnStack assembly but
  `LearnStack.SharedKernel` — no `Application` or `Infrastructure`, core or any module's —
  in its IL or its project file. The dependency direction points inward only.
- **Source:** ADR-0010; [01-architecture-standards.md](01-architecture-standards.md).
- **Type:** xUnit theory, one case per module, over referenced assemblies and project
  references. **Kind:** structural.
- **Status:** **Implemented** — `ModuleDependencyTests.cs`. Until Packet 10 it checked the
  module's **own** `Application` and `Infrastructure` and the core ones, so another
  module's reached no rule at all; it is now an allow-list, as the Application, Infrastructure
  and Contracts rules below are, with the declared leg an unused reference needs — read from
  `obj/project.assets.json`, the set restore resolved, because a regex over the project file saw
  one spelling of three and passed a forbidden edge written with `Condition` before `Include`,
  with single quotes, or injected from an imported `Directory.Build.props`. All three were
  measured. The four share a companion with
  `CoreApplication_DoesNotDependOn_Any_Infrastructure_Or_Module`:
  `The_Dependency_Matrix_Can_Actually_Fail` feeds each layer one reference it may hold and
  one it may not, and the core rule one of each family it forbids.
- **Phase:** 01; widened in 02a (Packet 10).

#### `ModuleContracts_DoNotDependOn_AnyModuleDomain`

- **Asserts:** per module, `LearnStack.Modules.<X>.Application.Contracts` has no type
  reference into **any** module's `Domain` — its own included. A contract is the
  cross-module surface, so a `Domain` type named there reaches every sender, which is
  `ModuleDomain_DoesNotDependOn_OtherModuleDomain`'s forbidden edge arrived at through
  the one assembly meant to be referenced widely.
- **Source:** ADR-0010; [ADR-0023 Amendment 8](../decisions/0023-strongly-typed-id-source-generator.md),
  which is what makes a `Guid` in a contract correct rather than sloppy — a module-local
  identifier crosses as `Guid` and the handler types it one layer in, while a
  `SharedKernel` identifier stays typed.
- **Type:** xUnit theory + NetArchTest, one case per module. **Kind:** structural.
- **Status:** **Implemented** — `ModuleDependencyTests.cs`.
- **Phase:** 02a (Packet 8).

#### `ModuleApplication_References_Only_Its_Own_Layers_And_Other_Contracts`

- **Asserts:** per module, `LearnStack.Modules.<X>.Application` references no LearnStack
  assembly but `LearnStack.SharedKernel`, its own `Domain` and `Application.Contracts`,
  and other modules' `Application.Contracts` — not in its IL, which is what it uses, and
  not in its project file, which is what it could start using with no further edit. Its
  own `Infrastructure` is outside the list: the composition root wires the implementation
  in.
- **Why it matters:** the Phase 01 rules checked `Domain` only, and a TODO in
  `ModuleDependencyTests` carried the rest until Packet 10. An `Application` that reaches
  another module's `Domain` or `Application` makes a cross-module call none of
  [ADR-0010](../decisions/0010-cross-module-communication.md)'s four mechanisms sanctions.
- **Source:** ADR-0010;
  [01-architecture-standards.md § Dependency Direction](01-architecture-standards.md).
- **Type:** xUnit theory, one case per module, over referenced assemblies and project
  references. **Kind:** structural.
- **Status:** **Implemented** — `ModuleDependencyTests.cs`, Packet 10, with
  `The_Dependency_Matrix_Can_Actually_Fail` as its companion.
- **Phase:** 02a (Packet 10).

#### `ModuleInfrastructure_References_Only_Its_Own_Layers_And_Core_Infrastructure`

- **Asserts:** per module, `LearnStack.Modules.<X>.Infrastructure` references no
  LearnStack assembly but `LearnStack.SharedKernel`, its own `Domain`, `Application` and
  `Application.Contracts`, and core `LearnStack.Infrastructure` — the shared persistence
  seam [Architecture Standards](01-architecture-standards.md) sanctions — in its IL or its
  project file. Provider SDKs are outside the rule; every other module and every other core
  assembly is inside it.
- **Why it matters:** `Module A → Module B.Infrastructure` and `→ Module B.Domain` are the
  two edges Architecture Standards forbids by name, and an infrastructure layer is where a
  shortcut to another module's tables looks most like plumbing. It is also the coupling
  that makes a module impossible to extract.
- **Source:** ADR-0002; ADR-0010;
  [01-architecture-standards.md § Dependency Direction](01-architecture-standards.md).
- **Type:** xUnit theory, one case per module, over referenced assemblies and project
  references. **Kind:** structural.
- **Status:** **Implemented** — `ModuleDependencyTests.cs`, Packet 10, with
  `The_Dependency_Matrix_Can_Actually_Fail` as its companion.
- **Phase:** 02a (Packet 10).

#### `ModuleContracts_Reference_Only_SharedKernel`

- **Asserts:** per module, `LearnStack.Modules.<X>.Application.Contracts` references no
  LearnStack assembly but `LearnStack.SharedKernel`, in its IL or its project file.
- **Why it matters:** wider than
  [`ModuleContracts_DoNotDependOn_AnyModuleDomain`](#modulecontracts_donotdependon_anymoduledomain),
  which bans a `Domain` only. A contract is referenced by every sender: one that referenced
  its own `Application` would export the handler assembly to all of them, and one that
  referenced another module's contracts would chain two modules' surfaces together.
- **Source:** [01-architecture-standards.md § Dependency Direction](01-architecture-standards.md);
  [ADR-0023 Amendment 8](../decisions/0023-strongly-typed-id-source-generator.md).
- **Type:** xUnit theory, one case per module. **Kind:** structural.
- **Status:** **Implemented** — `ModuleDependencyTests.cs`, Packet 10, with
  `The_Dependency_Matrix_Can_Actually_Fail` as its companion.
- **Phase:** 02a (Packet 10).

#### `CoreApplication_DoesNotDependOn_Any_Infrastructure_Or_Module`

- **Asserts:** `LearnStack.Application` — the pipeline behaviors — references no
  `LearnStack.Infrastructure*` assembly and no `LearnStack.Modules.*` assembly, in its IL or
  its project file.
- **Why it matters:** the core's half of the direction rule, as
  [`CoreInfrastructure_DoesNotDependOn_AnyModule`](#coreinfrastructure_doesnotdependon_anymodule)
  is core Infrastructure's. `TransactionBehavior_Does_Not_Reference_A_Module_Assembly`
  holds one behavior to it; this holds the assembly every behavior lives in.
- **Source:** [01-architecture-standards.md § Dependency Direction](01-architecture-standards.md).
- **Type:** xUnit over referenced assemblies and the project file. **Kind:** structural.
- **Status:** **Implemented** — `ModuleDependencyTests.cs`, Packet 10, with
  `The_Dependency_Matrix_Can_Actually_Fail` as its companion.
- **Phase:** 02a (Packet 10).

#### `Meta_NetArchTest_DetectsAPlantedViolation`

- **Asserts:** two things, because a NetArchTest rule goes vacuous two ways. That NetArchTest
  reports a **deliberately planted** forbidden dependency — if it ever reports that dependency
  as absent, every NetArchTest-based row in this catalogue is vacuously green and the suite is
  meaningless. And that every type a production assembly declares reaches NetArchTest's list at
  all: it drops anything whose full name begins with `System`, `Microsoft`, `xunit` or
  `netstandard`, ours included, and `namespace Microsoft.Extensions.DependencyInjection` is the
  idiomatic place for a registration extension. The day such a type is written, this fails and
  names it, rather than every rule quietly not seeing it.
- **Source:** [06-testing.md](06-testing.md) — a test suite must be able to fail.
- **Type:** xUnit + NetArchTest. **Kind:** structural (meta).
- **Status:** **Implemented** — `ModuleDependencyTests.cs`; the type-coverage half from
  Packet 10's review round, which measured a hand-written extension class in a `Microsoft.*`
  namespace inside a module assembly passing every NetArchTest-based rule. Keep in perpetuity.
- **Phase:** 01.

#### `Modules_Do_Not_Inject_Valkey_Directly`

- **Asserts:** no module assembly depends on `StackExchange.Redis` or on anything in
  `Microsoft.Extensions.Caching` — `IConnectionMultiplexer`, `IDistributedCache`,
  `IMemoryCache`. All cache access goes through `ICacheService`, because `CacheKey` is the
  isolation boundary of a cache and a module keying its own entries can collide one
  tenant's with another's; an in-process cache has the same hole as a distributed one.
- **Source:** [20-infrastructure-stack.md § `ICacheService`](20-infrastructure-stack.md).
- **Type:** xUnit + NetArchTest. **Kind:** structural.
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`, Packet 10. Its companion,
  `The_Direct_Client_Bans_Can_Actually_Fail`, plants an `IDistributedCache` holder in this
  test assembly and requires the same check to report it.
- **Phase:** 02a (Packet 10).

#### `Modules_Do_Not_Read_Entitlement_Cache_Directly`

- **Asserts:** no type outside an `IEntitlementProvider` implementation **reads or writes
  rows of** `platform_entitlement_cache` — no query against
  `TenancyDbContext.PlatformEntitlements` or `Set<PlatformEntitlement>()`, no
  `FromSql*` / `ExecuteSql*` naming the table, and no `SELECT` / `INSERT` / `UPDATE` /
  `DELETE` against it in hand-written SQL. The provider is its only sanctioned **reader
  and writer**. No module may query it, **Tenancy included**. Module code reads
  entitlement through `IFeatureFlags`, and `IFeatureFlags` reads the plan half through
  `IEntitlementProvider.GetAsync` rather than through the table.
- **Tenancy is inside the ban, not outside it.** `IFeatureFlags`'s implementation lives in
  `LearnStack.Modules.Tenancy.Infrastructure` because `tenant_feature_flags` is that
  module's table — which makes Tenancy the assembly most likely to reach for the
  entitlement table next door. It may not: a plan-projected key that resolved by SELECT
  would bypass the registered provider, so swapping the provider would stop changing the
  answer — the Phase 02a completion criterion — and ADR-0034's normative L1 →
  `ICacheService` → durable row honouring its grace window → Hub order would be
  evaluated by nobody.
- **What the rule does not touch is the schema.** Tenancy owns the table's *definition*.
  Packet 6 shipped its `CREATE TABLE`, its constraints, its row-security clauses and its
  grants in the Tenancy migration chain, `PlatformEntitlement` in
  `LearnStack.Modules.Tenancy.Domain`, and the entity's `IEntityTypeConfiguration` and
  `DbSet` in `LearnStack.Modules.Tenancy.Infrastructure`. Those four sites are outside the
  rule by construction, because the subject is **row access** and not mention. A
  mention-shaped rule would forbid the assembly the corpus puts the mapping in — the
  defect the audit rule below records for its own subject — and would collide with
  [`Every_TenantOwned_Entity_HasFilterAndRlsPolicy`](#every_tenantowned_entity_hasfilterandrlspolicy),
  which requires a `[TenantOwned]` entity to be mapped by its module's context so the
  query filter has somewhere to live. Deleting the mapping to satisfy this rule would
  delete an isolation layer. Widening past those four sites requires an ADR.
- **The subject is production assemblies.** The Packet 6 integration fixtures that
  `INSERT INTO platform_entitlement_cache` to prove the policy binds are test code and
  outside the rule — a test that proves a policy has to write the row the policy filters.
- **Source:** [ADR-0045 § 2](../decisions/0045-entitlement-and-feature-flag-socket.md);
  [20-infrastructure-stack.md § Entitlement Projection](20-infrastructure-stack.md);
  [ADR-0021](../decisions/0021-feature-based-entitlement.md).
- **Type:** xUnit + a DML scan over source and SQL — statements against the table, not
  occurrences of its name. **Kind:** structural.
- **Status:** **Implemented** — `EntitlementConventionTests.cs`, Packet 10, in two legs.
  The entity: the types that name `PlatformEntitlement` are the entity, its configuration,
  `TenancyDbContext` and an `IEntitlementProvider` implementation — asserting first that the
  scan sees `TenancyDbContext`'s `DbSet`, or it sees nothing. The context's exemption is for the
  **mapping**: the only member of it that may name the row is that `DbSet`'s getter, because a
  query helper declared on the context would launder the read — its caller names only the
  context, which every module may. That member scan reads a compiler-generated state machine as
  part of the method that declares it: an `async` helper's body lives there, and a walk over the
  declaring method alone saw boilerplate — measured, the synchronous twin of the same violation
  was caught and the `async` one was not. The SQL: no statement reads or writes the table's rows —
  after `FROM`, `JOIN`, `INTO`, `UPDATE`, `COPY`, `USING`, `TRUNCATE` or a comma, since an
  implicit join names it there, and not the DDL, grants and policies that name it — outside an
  exact-path list the Hub-backed provider joins in Phase 02c. Its companion, `The_Entitlement_Cache_Scan_Can_Actually_Fail`,
  feeds the pattern both kinds of statement and plants a `DbSet` reader. The rule got its
  subject from the socket Packet 9 ships:
  no row of the table has been read or written since Packet 6 created it, and until there
  was a port allowed to read it there was nothing to route through.
  `NullEntitlementProvider` answers from constants and touches no table; the first
  implementation that reads the row is `HubEntitlementProvider`, in Phase 02c. **Packet 9
  settled the `DbSet` question: it stays.** The mapping has to survive in some form
  because the query filter rides on it, and the isolation sweeps enumerate the model —
  a table mapped by configuration alone is one those sweeps would stop counting, which is
  how a policy regression goes unnoticed. What the rule constrains is who QUERIES the
  table, not whether the model knows about it.
- **Phase:** 02a (Packet 10).

#### `FeatureKey_AllReferences_AreInRegistry`

- **Asserts:** every `FeatureKey`, `LimitKey` and `KillswitchKey` a production call site
  names is a member of its registry — `FeatureKeys`, `LimitKeys`, `KillswitchKeys`. A key
  constructed anywhere else — a free-form string at a call site, a key invented in a
  payload — fails the build. The converse is not asserted: the registries carry the full
  vocabulary, and a declared key nothing reads yet is expected.
- **Canonical name.** No `s` after `Key`: the rule matches `FeatureKey` **references**,
  not `FeatureKeys.*` string constants
  ([ADR-0021 Amendment 1](../decisions/0021-feature-based-entitlement.md)). One document
  already cites this catalogue as its home —
  [26-hybrid-license-model.md § 0 and § 10](../architecture/26-hybrid-license-model.md) —
  and until this row existed, it cited a row that did not.
- **Source:** ADR-0021 Amendment 1;
  [ADR-0045 § 6](../decisions/0045-entitlement-and-feature-flag-socket.md);
  [21-feature-flags.md](../architecture/21-feature-flags.md).
- **Type:** xUnit + an IL scan (Mono.Cecil) for key construction outside the three
  registries. It reads the three ways a name is **spelled** — a constructor call, the `init`
  setter a `with` expression runs, and a key the runtime builds for a method that hands it a key
  type's token (`Activator.CreateInstance`, `ConstructorInfo.Invoke`). `default(FeatureKey)` is not scanned: it carries no
  name to be wrong about, every registry lookup misses it, and the compiler emits one into
  every async state machine that takes a key. **Kind:** structural.
- **Status:** **Implemented** — `EntitlementKeyTests.cs`, Packet 10, with
  `The_Entitlement_Key_Scans_Can_Actually_Fail` planting both spellings in this test assembly
  and requiring the same filter to report them — the probes only, since the test assembly also
  spells an undeclared key on purpose, where the rule below proves the aggregate refuses one. The
  rule asserts first that it sees the registries construct their own keys. The registries shipped in Packet 9 carrying the **full
  vocabulary**, not only the keys with a consumer, which measured empty at that point; the
  reading and its reasoning are in the Packet 9 delivery record. `LimitKeys` takes its
  strings from the Hub's `limits.`
  vocabulary, not from the `tenancy.max_learners` / `classroom.minutes_per_month`
  spellings this corpus used to name and
  [ADR-0045 Amendment 1](../decisions/0045-entitlement-and-feature-flag-socket.md)
  withdraws: the two sides shared no member, and the Hub is the side with merged code and
  two plan validators built on it. Every `FeatureKey` member carries the descriptor the
  same amendment fixes — its `Source`, its fail-open / fail-closed class, and its
  killswitch by name where it has one — so this rule's subject is a typed member rather
  than a string.
- **Phase:** 02a (Packet 10).

#### `PlanProjected_Keys_NotInTenantFlags`

- **Asserts:** no key whose catalog descriptor declares a plan-projected `Source` is
  written to `tenant_feature_flags`. The registry's `Source` descriptor is the join: a
  plan-projected key resolves through `IEntitlementProvider`, a tenant-flag key through
  the table, and never the other way round. Four legs: `Tenant.SetFeatureFlag` takes a
  `FeatureKey` rather than a string, so which key is a question the compiler asks; it refuses
  every plan-projected key and every undeclared one, and accepts every tenant-flag key; it is
  the only production code that creates a `TenantFeatureFlag`; exactly one entity type maps the
  table across every swept model, since a second mapping is a second write path that names
  neither the entity nor the table in any statement; and nothing under `backend/src` writes the
  table's rows — not SQL, and not a migration's `InsertData`, which writes a row with no
  statement anywhere in the file and is exactly where a plan key would be seeded.
- **Why it matters:** the two halves answer with different authority. A plan-projected key
  served from the tenant table is a tenant editing its own entitlement, which is the one
  thing the projection exists to prevent.
- **Source:** [ADR-0045 § 2](../decisions/0045-entitlement-and-feature-flag-socket.md);
  [21-feature-flags.md](../architecture/21-feature-flags.md).
- **Type:** xUnit + reflection over the key registries, an IL scan for the entity's creation
  sites, and the aggregate exercised with each key. **Kind:** structural + behavioural.
- **Status:** **Implemented** — `EntitlementKeyTests.cs`, Packet 10. The refusal lives in the
  aggregate, where the row comes into being, rather than in a validator a second caller could
  skip. Mutation-checked: dropping the `Source` guard from `Tenant.SetFeatureFlag` fails it.
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
- **Type:** xUnit + reflection over declared members. **Kind:** structural.
- **Status:** **Implemented** — `DomainModelTests.cs`, Packet 10, over every type below
  `Entity<>` in every production assembly **and** every `IAggregateRoot<TId>`, whatever it
  derives from — a root outside that hierarchy would carry no equality at all, and declaring one
  is the very thing this refuses. A method whose name merely ends in `Equals` — a domain
  predicate like `SlugEquals` — is not an equality member and is not reported. Its companion,
  `The_Aggregate_Shape_Rules_Can_Actually_Fail`, plants the three shapes the compiler does
  not stop — a typed overload, a declared `IEquatable<TSelf>`, and an explicit
  re-implementation of the inherited `IEquatable<Entity<TId>>`, and a `new GetHashCode()` that
  hides the sealed one, which the compiler allows (measured) — and requires the same predicate to
  report each. The declared-interface clause is belt and braces rather than a fourth shape:
  anything that satisfies `IEquatable<TSelf>` declares a method whose name ends in `Equals`,
  which the first check already reports. The operator pair is not among them: with `Equals(object?)` and
  `GetHashCode()` sealed, a derived `operator ==` cannot silence CS0660 / CS0661, so it
  fails the build rather than this test. Mutation-checked: a `bool Equals(Tenant?)` overload
  on `Tenant` fails it.
- **Phase:** 02a (Packet 3b registers this entry, Packet 10 writes the test).

#### `Aggregate_Roots_Use_StronglyTypedId`

- **Asserts:** every type implementing `IAggregateRoot<TId>` has a `TId` that carries
  Vogen's `[ValueObject<Guid>]` with `LearnStackVogenDefaults.IdMask`. The interface
  constraint already makes `TId` an `IStronglyTypedId<Guid>`; the attribute is what makes
  it a generated value object — its EF Core converter, its JSON converter and its
  `IsInitialized()` — rather than a hand-written struct that satisfies the interface.
- **Source:** [ADR-0023](../decisions/0023-strongly-typed-id-source-generator.md)
  (§ Implementation Notes and Amendment 1).
- **Type:** xUnit + reflection over every production assembly. **Kind:** structural.
- **Status:** **Implemented** — `DomainModelTests.cs`, Packet 10. ADR-0023 Amendment 1 placed
  it with the first aggregate, which Packet 6 shipped; no row carried it until Packet 10's
  sweep. The mask is read from the attribute's `conversions` argument rather than inferred
  from the attribute's presence — the converters are what the attribute is required for — and
  `The_Aggregate_Shape_Rules_Can_Actually_Fail` feeds the predicate a hand-written struct
  that satisfies `IStronglyTypedId<Guid>` and a value object declared with a narrower mask.
- **Phase:** 02a (Packet 10).

#### `Domain_Does_Not_Depend_On_Microsoft_EntityFrameworkCore_Except_Vogen_Emitted_Converters`

- **Asserts:** in `LearnStack.SharedKernel` and every module `Domain` assembly, the only
  types with an IL dependency on `Microsoft.EntityFrameworkCore` are the ones Vogen emits
  for a `[ValueObject]` type: the `EfCoreValueConverter` and `EfCoreValueComparer` nested
  in it, and the `__<Id>EfCoreExtensions` class beside it. Each is pinned to a real value
  object, so a hand-written class cannot pass by borrowing the name. Hand-written domain
  code names no EF Core type.
- **Why it matters:** [Architecture Standards § Build-time-only exceptions](01-architecture-standards.md)
  sanctions the EF Core reference for exactly those emitted types. Without a test the
  exception is an open door: the reference is already there, so the first
  `using Microsoft.EntityFrameworkCore;` in an aggregate compiles.
- **Source:** Architecture Standards § Build-time-only exceptions; ADR-0023.
- **Type:** xUnit + NetArchTest over the assemblies' types. **Kind:** structural.
- **Status:** **Implemented** — `ModuleDependencyTests.cs`, Packet 10; Architecture
  Standards had promised it with Packet 6. It asserts its premise — the emitted converters
  are found — and its companion, `The_Domain_EF_Core_Rule_Can_Actually_Fail`, requires the
  scan to report this test assembly's hand-written `DbContext` probes, and the predicate to
  refuse both emitted names borrowed by a type that is not a value object, and a third name
  borrowed inside one that is. It reads the IL directly rather than through NetArchTest, whose
  type list drops everything whose full name starts with `System` or `Microsoft` — including a
  type our own assembly declares, and `namespace Microsoft.EntityFrameworkCore` is exactly
  where an extension class for EF Core is idiomatically written. The IL walk reads generic
  constraints, `catch` clauses and generic call-site type arguments, each of which carries a type
  a signature scan does not. Mutation-checked: a planted `typeof(DbContext)` in `Tenancy.Domain`
  fails it, and so does one written in a `Microsoft.*` namespace, which the NetArchTest version
  passed.
- **Phase:** 02a (Packet 10).

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
  `LearnStack.Tests.Architecture`, `TenancyConventionTests`), and whole: it reads every
  module `Domain` assembly, including the four — Content, Education, Identity, Media —
  that declare no domain type yet, only an `AssemblyMarker`, so the first `Organization` or `OrganizationBranding` to land
  in one of them fails it. `OrganizationBranding` itself arrives with
  [Phase 06](../roadmap/phase-06-renderer-admin-studio.md)'s branding; until then "exactly
  one, in Tenancy" is satisfied by none, which is what stops the first one landing in the
  wrong module.
- **Phase:** 02a (Packet 6).

#### `Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning`

- **Asserts:** no type implementing `IRequestHandler<,>`, `IRequestHandler<>` or
  `INotificationHandler<>` can reach more than one **distinct aggregate root** through
  its constructor parameters' `IAggregateWriteStore<TRoot, TId>` derivations, except the
  single handler on a literal allow-list — `ProvisionTenantCommandHandler`, which writes
  `Tenant` and its default `Organization` in one transaction per ADR-0042.
- **Source:** [ADR-0042](../decisions/0042-tenant-provisioning-cross-aggregate-transaction.md);
  [Architecture Standards § Aggregate Ownership](01-architecture-standards.md).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented** (`AggregateWriteTests`).
- **Phase:** 02a (Packet 7).
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
[Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md). ADR-0040 staged the two
behavioural rules that need a second module `DbContext`, and the parallelism rule, for
Phase 03, because that was where it expected one; Packet 8 shipped it, and
[ADR-0040 § Architecture Tests](../decisions/0040-ambient-unit-of-work.md) names this
catalogue as the carrier of their status — so all three are Packet 10's.

#### `Aggregates_With_Optimistic_Concurrency_Map_RowVersion`

> Swept across **every module with a schema** from Packet 8, not only Tenancy. Read
> against one model the rule said nothing about the aggregates a second module
> shipped, while this entry and ADR-0039 both described it as covering every entity
> implementing `IOptimisticConcurrency`. It reads `Modules.Scoped`, the same
> enumerated list `Every_Module_With_A_Schema_Is_Swept` holds current, and asserts
> per model that the model has aggregates in it — one model carrying them satisfies
> a suite-wide total however many carry none.

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
- **Phase:** 02a (Packet 6; swept across every module with a schema in Packet 8).

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
- **Phase:** 02a (Packet 6).

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
- **Phase:** 02a (Packet 6).

#### `Migrate_Target_Applies_The_Tenancy_Chain_First`

- **Asserts:** the `migrate` recipe visits the Tenancy chain before the Audit chain.
  From Phase 02a Packet 9 the chains are no longer independent — `audit_config`
  carries the schema's only foreign key crossing two chains, to `tenants` — and the
  recipe's project list is a glob that expands alphabetically, with `Modules/Audit`
  ahead of `Modules/Tenancy`. The rule replays the list rather than searching it for
  a literal: each `backend/src` token is expanded against the chains that exist,
  ordinal-sorted the way a shell sorts a glob, and a chain already visited is skipped,
  which is what the recipe's own `applied` guard does. **Expanding the glob is the whole
  difference from a text search**, and it is what catches the mutation that actually
  happened: deleting the explicit Tenancy prefix leaves a recipe whose only token is the
  `Modules/*` glob, in which the literal `Modules/Tenancy` does not appear at all — so a
  search for it has nothing to compare, while the replay expands the glob and reports
  Audit first.
- **Why it is separate from the coverage rule:** coverage is not order.
  `Migrate_Target_Covers_Every_Migration_Chain` stays green when the Tenancy prefix
  is deleted, because the glob still reaches Tenancy — while every fresh deployment
  fails on `relation "tenants" does not exist` from that commit onward. On a database
  that already has the schema the difference is invisible, which is what makes a
  named rule the only thing that catches it.
- **Source:** [05-database.md § Migrations](05-database.md); the `migrate` target.
- **Type:** xUnit + Makefile and directory inspection. **Kind:** structural.
- **Status:** **Implemented** (Packet 9 step 3,
  `LearnStack.Tests.Architecture`, `PersistenceConventionTests`).
  Mutation-checked: deleting the Tenancy prefix from the recipe fails this case and
  no other.
- **Phase:** 02a (Packet 9).

#### `Migrate_Target_Refuses_An_Aliased_Runtime_Credential`

- **Asserts:** `make migrate` refuses a migration credential naming `learnstack_app`
  whichever alias Npgsql accepts for the user and the password — `Username`/`Password`,
  `UID`/`PWD`, `User ID`/`PSW`, `USERID`/`Pwd` — names the role it refused, and prints no
  password.
- **Why it matters:** the recipe recognised the first pair only, so the other three read
  the role as empty, passed the ownership check, and echoed the password into a terminal
  or a CI log — from the one target whose purpose is keeping that credential in one place.
- **Source:** [05-database.md § Database roles](05-database.md);
  [ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md).
- **Type:** xUnit executing the recipe (no database: the role check refuses first).
  **Kind:** behavioural.
- **Status:** **Implemented** (`PersistenceConventionTests`).
- **Phase:** 02a (Packet 6).

#### `Migrate_Target_Redacts_A_Quoted_Value_Whole`

- **Asserts:** a quoted value carrying a semicolon — either quote character, with the
  doubled-quote escape — is redacted whole, and the role after it is still read.
- **Why it matters:** Npgsql accepts a semicolon inside a quoted value, and a
  split-on-`;` redaction cut such a value in half: the first half matched the keyword
  table and was redacted, the second was printed.
- **Source:** [05-database.md § Database roles](05-database.md).
- **Type:** xUnit executing the recipe. **Kind:** behavioural.
- **Status:** **Implemented** (`PersistenceConventionTests`).
- **Phase:** 02a (Packet 6).

#### `Migrate_Target_Reads_The_Role_Through_A_Quoted_Value`

- **Asserts:** the field after a quoted password is still parsed as its own field, so
  the role check reads the role and not a fragment of the password.
- **Why it matters:** the complement of the redaction rule — a parser that cuts the value
  in the wrong place shifts every field after it, and the role check reads a fragment of
  the password as the role.
- **Source:** [05-database.md § Database roles](05-database.md).
- **Type:** xUnit executing the recipe. **Kind:** behavioural.
- **Status:** **Implemented** (`PersistenceConventionTests`).
- **Phase:** 02a (Packet 6).

#### `Migrate_Target_Refuses_A_Uri_Without_Echoing_Its_Userinfo`

- **Asserts:** a URI-form credential (`postgres://user:secret@host/db`, the form
  `DATABASE_URL` carries on several hosts) is refused without printing its userinfo, and
  the message names the key/value form that works.
- **Why it matters:** the URI form has no `password=` for a keyword redaction to find, so
  a recipe that only redacted keywords printed it whole.
- **Source:** [05-database.md § Database roles](05-database.md).
- **Type:** xUnit executing the recipe. **Kind:** behavioural.
- **Status:** **Implemented** (`PersistenceConventionTests`).
- **Phase:** 02a (Packet 6).

#### `Audit_Closed_Set_Columns_Store_What_Their_Check_Admits`

- **Asserts:** for each of `audit_log`'s three closed-set text columns — `outcome`,
  `operation_type`, `operation_class` — the set of values the property's EF value
  converter can write equals the set its own `CHECK` constraint admits, and every value
  round-trips back to the member it came from. Both sets are read from the **design-time**
  model rather than from the source, so the rule sees what EF will emit; the `CHECK`
  literals are parsed out of the constraint's own SQL rather than restated, because a
  third copy of the list is the one nothing compares.
- **Why it exists:** the three columns are rendered in **two different cases**, on
  purpose. `outcome` stores `success | denied | failed | indeterminate` because
  [ADR-0033 § 3](../decisions/0033-audit-durability-model.md) and
  [ADR-0044 § 5](../decisions/0044-audit-write-path.md) both write it lowercase;
  `operation_type` and `operation_class` store the C# member name unchanged, on the
  `ck_tenants_status` precedent, which is what lets the admin API's
  `?operationType=SecurityEvent` filter be the same string on both sides. An asymmetry
  held by nothing but two comments drifts, and it drifts silently in both directions: a
  converter that stopped lowercasing writes rows every `INSERT` rejects with `23514`, on
  the write path whose whole job is that the record survives; a parse that stopped being
  case-insensitive throws on **every** row the Phase 03 read API materialises; and an enum
  member added without its `CHECK` is a value the code can produce and the column cannot
  hold.
- **Source:** [ADR-0033 § 3](../decisions/0033-audit-durability-model.md);
  [ADR-0044 § 5](../decisions/0044-audit-write-path.md);
  [05-database.md § Constraints](05-database.md).
- **Type:** xUnit + EF design-time model inspection. **Kind:** structural.
- **Status:** **Implemented** (Packet 9 step 3, `LearnStack.Tests.Architecture`,
  `AuditConventionTests`). Mutation-checked three ways, each failing this case alone:
  flipping `ignoreCase` to `false`, dropping the `ToLowerInvariant()` from the write half,
  and removing one member from the `operation_class` `CHECK`. The commit that added the
  Packet 9 step 8 audit rules replaced this case instead of adding beside it, and the row
  here went on saying Implemented while no test read a `ck_audit_log_*` constraint; it was
  restored in the packet's external-review round and re-measured.
- **Phase:** 02a (Packet 9).

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
- **Phase:** 02a (Packet 6).

#### `Unique_Indexes_On_Soft_Deletable_Tables_Exclude_Deleted_Rows`

- **Asserts:** every unique index on an entity whose table carries a `deleted_at` column,
  across every module `DbContext`, is filtered `deleted_at IS NULL` — except an index that
  contains the whole primary key, which is a foreign-key target rather than a natural key,
  and the indexes held table-wide by a recorded decision, each named with its reason. The
  held set is checked against the model too, so an entry whose index has gone stops
  exempting anything.
- **Why it matters:** a table-wide unique lets a deleted row hold its key forever. Packet 9
  shipped `ux_audit_config_tenant_id_module_operation` unfiltered on a table whose rows have
  no setter — an override changes only by soft delete and a fresh declaration — so the
  first change of any override would have failed `23505` permanently. Every other
  soft-deletable table in the repository already filtered; nothing checked that the next
  one would.
- **Held by decision:** `ux_tenants_slug`. A tenant slug is a hostname, and whether a
  terminated tenant's slug may ever be reissued is
  [Phase 02c](../roadmap/phase-02c-hub-foundation.md)'s decision, recorded there.
- **Source:** [Database Standards § Soft Delete](05-database.md).
- **Type:** xUnit over the EF model (`LearnStack.Tests.Architecture`,
  `PersistenceConventionTests`). **Kind:** structural.
- **Status:** **Implemented** (Packet 9, fifth review). Its companion,
  `The_Soft_Delete_Index_Sweep_Can_Actually_Fail`, runs the predicate over a probe context
  carrying one unique index of each shape — counting deleted rows, partial, containing the
  primary key, and on a table with no `deleted_at` — and expects exactly the first.
  Mutation-checked: dropping the `audit_config` filter fails the rule on that index.
- **Phase:** 02a (Packet 9).

#### `Every_Database_Test_Carries_The_Docker_Trait`

- **Asserts:** every test file in `LearnStack.Tests.Integration` that declares a test and
  needs a container — one under `Database/`, or one anywhere that names `SchemaFixture`,
  `PostgresFixture` or the `SharedSchema` collection — carries
  `[Trait(RequiresDocker.Key, RequiresDocker.Value)]`.
- **Why it matters:** CI splits the integration assembly by that trait, and the two jobs'
  filters are exact complements, so a class that forgets it does not fail — it runs in the
  `backend` job, whose runner also has a Docker socket, starts its container and passes.
  Nothing goes red, and the Docker suite quietly stops being where the Docker tests live.
- **Source:** [06-testing.md](06-testing.md).
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** (`PersistenceConventionTests`), widened from `Database/` to
  the whole project in Packet 9's fifth review. Its companion,
  `The_Docker_Trait_Sweep_Can_Actually_Fail`, feeds the predicate the shapes it must and
  must not flag.
- **Phase:** 02a (Packet 6; widened in Packet 9).

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
- **Phase:** 02a (Packet 6).

#### `Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork`

> Widened to **six** files, keyed by directory, in Packet 8 step 3, and to **seven**
> in Packet 9 step 3: a design-time factory lands with every migration chain, so
> Customization's joined the allow-list and then Audit's.

- **Asserts:** two halves. The composition root's persistence registration is run,
  and every `DbContext` service in it is one `AddModuleDbContext` registered —
  scoped, from an implementation factory, never a type registration EF could give
  its own connection. And under `backend/src`, exactly **seven** files may reach for a
  connection at all: the four design-time factories — one per migration chain, where a
  connection string is the point; the shared helper, which passes a *connection*; and the
  two composition roots — `LearnStack.Api`'s, which builds the one application data
  source behind its credential guard, and `LearnStack.Tools.Seeder`'s, which is the same
  act for a host with no HTTP surface. An eighth is a new decision. A context on its own connection never saw the
  announcement, so every read through it returns zero rows under the corrected policy —
  silently.

  The set is keyed on `directory/filename`, not the bare filename: two `Program.cs` now
  exist under `backend/src`, and a bare-name set would let the API's silently take the
  seeder's slot.
- **Source:** ADR-0040; [05-database.md § Forbidden](05-database.md).
- **Type:** xUnit + DI registration inspection and a source scan. **Kind:** structural.
- **Status:** **Implemented** (Packet 6 step 6; the allow-list widened to five and
  keyed by directory in Packet 7 step 10, to six in Packet 8 step 3 and to seven in
  Packet 9 step 3, `LearnStack.Tests.Architecture`, `PersistenceConventionTests`).
- **Phase:** 02a (Packet 6).

#### `The_registration_marker_does_not_vouch_across_containers`

- **Asserts:** the marker `AddModuleDbContext` leaves is read per service collection: a
  collection it registered reports the context, and a second collection in the same
  process that registered the same context by hand — a scoped factory building it on its
  own options — reports none.
- **Why it matters:** `Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork`'s other leg,
  scoped from an implementation factory, cannot tell the helper's registration from a
  hand-rolled factory that gives the context its own connection — the ADR-0040 failure.
  For that one shape the marker is the whole guard, and a process-wide marker answered for
  whichever container registered first.
- **Source:** [ADR-0040](../decisions/0040-ambient-unit-of-work.md).
- **Type:** xUnit + DI registration inspection. **Kind:** structural.
- **Status:** **Implemented** (`PersistenceConventionTests`).
- **Phase:** 02a (Packet 6).

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
- **Phase:** 02a (Packet 6).

#### `A_Cross_Module_Read_Inside_The_Ambient_Transaction_Returns_Rows`

- **Asserts:** inside one unit of work, a row one module's `DbContext` writes is visible to
  a second module's `DbContext` before `COMMIT`, under the same tenant announcement —
  both are enlisted on the one connection and transaction.
- **Why it matters:** it is the first of the two properties
  [ADR-0040](../decisions/0040-ambient-unit-of-work.md) exists for. A context that opened
  its own connection would read committed data only, and every single-module test would
  still pass.
- **Source:** ADR-0040 § Context and § Implementation Notes.
- **Type:** **integration** test (Testcontainers + PostgreSQL), as `learnstack_app`.
  **Kind:** behavioural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 10) — ADR-0040 staged it for Phase 03, and the second module
  `DbContext` arrived in Packet 8.

#### `An_Outer_Failure_After_An_Inner_Write_Leaves_Zero_Rows_In_Both_Modules`

- **Asserts:** when a unit of work writes through two modules' `DbContext`s and the outer
  frame then fails, neither module's row survives.
- **Why it matters:** the second property ADR-0040 exists for — one transaction, so one
  outcome. Two connections would commit the inner write and roll back the outer, which is
  the partial state the ambient unit of work is there to make impossible.
- **Source:** ADR-0040 § Context and § Implementation Notes.
- **Type:** **integration** test (Testcontainers + PostgreSQL), as `learnstack_app`.
  **Kind:** behavioural.
- **Status:** **Registered.**
- **Phase:** 02a (Packet 10), for the same reason as the rule above.

#### `Modules_Do_Not_Parallelize_Over_The_Ambient_Connection`

- **Asserts:** no module code fans out — no `Task.WhenAll`, `WhenAny` or `WhenEach`, with or
  without an explicit type argument; no `Parallel.For`, `ForAsync`, `ForEach`, `ForEachAsync`
  or `Invoke`; no PLINQ `AsParallel()`; and no task started and stored instead of awaited, which
  is `Task.WhenAll` written out longhand — outside an exact-path list, empty today, whose entries
  each say why their concurrent work touches no connection. One connection means one command at a
  time; a handler that fans out corrupts the protocol. Stricter than ADR-0040's sentence,
  which bans two `DbContext`-bound operations: which operations are bound to the ambient
  connection cannot be read from source, and the honest rule is the one that can.
- **Source:** ADR-0040 § Nesting.
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** — `PersistenceConventionTests.cs`, Packet 10, after waiting
  for module code to scan: three modules have shipped handlers since Packets 7 and 8, and
  the rule asserts it read them. Its companion, `The_Parallelism_Scan_Can_Actually_Fail`,
  feeds it each fan-out shape — a `using static` import included — and two that are not.
- **Phase:** 02a (Packet 6 registers it; Packet 10 implements it).

### Tenancy and isolation

Source: [ADR-0003](../decisions/0003-tenant-isolation-defense-in-depth.md) (Amendments
1 and 3), [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md). Introduced by
[Phase 02a Packet 7](../roadmap/phase-02a-kernel-tenancy.md), closed by Packet 10.

Read § What a structural test proves before relying on any row in this section. Its
structural rows are coverage checks. The proof is `TenantWide_Row_Of_TenantB_Is_Invisible_To_TenantA`
and `Write_With_Foreign_TenantId_Is_Rejected_By_WithCheck`, with the isolation cases named
after them below — integration tests, all connected as `learnstack_app`.
`Tenant_Context_Guard_Fires_Only_On_An_Unmarked_Transaction` runs the same way but is a
diagnostic above row security, not the boundary, as its own note says.

#### `LearnStack_OutboxAdmin_Role_OnlyUsedBy_OutboxProcessor`

- **Asserts:** `ConnectionStrings:OutboxDispatcher` is resolved by `OutboxProcessor`
  and nothing else. A `GRANT` names a role, not a code path — every handler in the
  API process runs as the same role — so code-path confinement of a `BYPASSRLS`
  credential is carried here or nowhere.
- **Source:** [05-database.md § GRANT matrix](05-database.md); ADR-0003 Amendment 3.
- **Type:** NetArchTest + DI registration inspection. **Kind:** structural.
- **Status:** **Awaiting backfill** — cited by the standard, no dispatcher yet.
- **Phase:** 02b.

#### `CoreInfrastructure_DoesNotDependOn_AnyModule`

- **Asserts:** no core `LearnStack.Infrastructure*` assembly — the persistence one, Audit,
  Observability, ErrorTracking, Resilience and Validation — references a `LearnStack.Modules.*`
  assembly, in its IL or in its declared references. The reverse edge — a module's `Infrastructure`
  referencing core Infrastructure — is permitted and required, because
  `TenantScopedDbContext` and the query-filter seam live there
  ([Architecture Standards § Dependency Direction](01-architecture-standards.md)).
  This rule is the half that keeps it one-way: core Infrastructure is referenced by
  every module, so a single edge back into one makes the graph cyclic and makes that
  module impossible to extract.
- **Source:** [ADR-0002](../decisions/0002-initial-architecture.md);
  [ADR-0010](../decisions/0010-cross-module-communication.md);
  [Architecture Standards § Dependency Direction](01-architecture-standards.md).
- **Type:** xUnit over referenced assemblies and the restore graph. **Kind:** structural.
- **Status:** **Implemented** (Packet 7 step 3, `ModuleDependencyTests`). Packet 10's review
  round widened it from the persistence assembly to all six: the other five are referenced by
  the same composition root and close the same loop, and none was under any rule.
- **Phase:** 02a (Packet 7; widened in Packet 10).

#### `Platform_DataSource_Resolved_Only_By_PlatformAdminScope`

- **Asserts:** the keyed `NpgsqlDataSource` built from `ConnectionStrings:PlatformAdmin`
  is resolvable only by `PlatformAdminScope`. Module code cannot reach the
  `BYPASSRLS` credential.
- **Source:** [05-database.md § How `EnterPlatformAdminScope(reason)` reaches
  `learnstack_platform`](05-database.md).
- **Type:** NetArchTest + DI registration inspection. **Kind:** structural.
- **Status:** **Implemented** (`PlatformAdminScopeConventionTests`, Packet 7 step 7).
- **Phase:** 02a (Packet 7).
- **Note:** three legs, all live. The keyed-resolution scan, a scan that connection
  strings are read in exactly one file, and a self-check that the scan matched something
  at all — a two-path allow-list matching nothing would pass vacuously. This is the
  repository's first keyed DI registration, so the scan is the whole boundary: the key
  is a public const because `GetKeyedServices(KeyedService.AnyKey)` reaches a keyed
  registration whatever the key is spelled, so hiding the string buys nothing.
#### `Every_TenantOwned_Entity_HasFilterAndRlsPolicy`

> Swept across **every module with a schema** from Packet 8, not only Tenancy. The
> module list is enumerated and `Every_Module_With_A_Schema_Is_Swept` holds it current.

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
- **Status:** **Implemented** (Packet 7 step 3, `TenantScopingTests`; widened in Packet 8
  step 3 to every module that has a schema, over the enumerated `Modules.Scoped` list
  `Every_Module_With_A_Schema_Is_Swept` holds current).
  The policy count is read case-insensitively, because SQL keywords are: a second policy written
  `create policy … as restrictive` would be neither counted nor excluded, and the count is the
  whole of what that leg proves. Since Packet 10 the policy leg also reads the clauses, not only
  their presence: each of
  `USING` and `WITH CHECK` compares the key column — `tenant_id`, or `id` on the self-keyed
  table — to `NULLIF(current_setting('app.tenant_id', true), '')::uuid`, and its companion,
  `The_Tenant_Term_Check_Can_Actually_Fail`, breaks each clause in turn.
- **Phase:** 02a (Packet 7 introduces, Packet 8 widens).
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

#### `Every_Module_With_A_Schema_Is_Swept`

- **Asserts:** every module `Domain` assembly that declares a `[TenantOwned]` entity
  appears in `Every_TenantOwned_Entity_HasFilterAndRlsPolicy`'s enumerated module list.
  The sweep is enumerated rather than discovered, because a rule that scanned loaded
  assemblies would silently skip the module nobody referenced and pass vacuously — and
  the cost of enumerating is that the list goes stale. It did: the sweep read one
  assembly and one `DbContext` until Packet 8, so the second module's entities were
  invisible to the rule that names them. This is the guard on that direction. The
  *universe* it checks against is discovered from `backend/src/Modules` rather than
  written down, because a hard-coded universe cannot report the module missing from
  both lists — which is the same vacuity one level up.
- **Source:** [ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md);
  [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md).
- **Type:** xUnit + reflection over every module `Domain` assembly. **Kind:** structural.
- **Status:** **Implemented** — `TenantScopingTests.cs`. Verified against a planted
  violation: removing a module from the list makes it fail.
- **Phase:** 02a (Packet 8).

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
- **Status:** **Implemented** (Packet 7 step 3, `TenantScopingTests`; widened in Packet 8
  step 3 to every module that has a schema, over the enumerated `Modules.Scoped` list
  `Every_Module_With_A_Schema_Is_Swept` holds current).
- **Phase:** 02a (Packet 7 introduces, Packet 8 widens).

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
- **Note:** two legs, both live. The set leg holds the permitted set at exactly
  `ProvisionTenantCommand`, the one of the seven shipped request types that carries the
  marker (Packet 7 step 9). The shape leg guards the attribute's own `AttributeUsage`: the
  behavior reads it with `inherit: false`, and flipping the attribute to
  `Inherited = true` is not an error and not a widening — it is a marker the pipeline
  silently stops following.
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
  test. **Kind:** behavioural.
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
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** behavioural.
- **Status:** **Implemented, twice.** Packet 6 step 4 (`TenancySchemaTests`): a
  `[Theory]` over every table carrying a `WITH CHECK` for the `INSERT` half, and
  `Reassigning_An_Owned_Row_To_Another_Tenant_Is_Rejected` for the `UPDATE` half, because
  `WITH CHECK` guards both and a rule covering one leaves the other open. Packet 7 step 11
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
through the request path in `Database/TenantIsolationHttpTests`. The phase's completion
criteria name three more of the same kind, which Packet 10 adds:
`App_Role_Cannot_Enumerate_Host_Map`, `App_Role_Cannot_Enumerate_Tenants` and
`Tenant_A_Cannot_Repoint_Tenant_B_Host`.

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

> Swept across **every module with a schema** from Packet 8, not only Tenancy. This
> is the reverse-direction guard, so leaving it on one assembly was the worst of the
> three to leave behind: a second module's entity that dropped its marker fell out
> of all three scoping rules at once — this one, which enumerates the interface, and
> the two that enumerate the marker — and the suite stayed green. Measured.

- **Asserts:** every entity implementing a scoping interface — `ITenantOwned`,
  `IOrganizationScoped` — also carries the marker attribute the filter and policy
  generators read. An entity that implements one and not the other is scoped in the type
  system and unscoped everywhere it matters.
- **Source:** [ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented** (Packet 7, `TenantScopingTests`; widened to every module in
  Packet 8 step 3).
- **Phase:** 02a (Packet 7).

#### `The_Request_Filter_Sees_Every_Shape_MediatR_Dispatches`

- **Asserts:** the predicate that enumerates request types covers every shape MediatR
  dispatches, so a rule written over "all requests" is not silently blind to one of them.
  Measured facts behind it: `IStreamRequest<T>.GetInterfaces()` is empty and
  `IBaseRequest.IsAssignableFrom(IStreamRequest<>)` is false, so a filter written the
  obvious way misses streamed requests entirely.
- **Source:** [04-api-design.md](04-api-design.md).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented** (Packet 7, `RequestSurfaceTests`).
- **Phase:** 02a (Packet 7).

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
- **Status:** **Implemented** (Packet 7, `RequestSurfaceTests`).
- **Phase:** 02a (Packet 7).

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
- **Phase:** 02a (Packet 7).

#### `Out_Of_Band_Setters_Open_Read_Only_Transactions`

- **Asserts:** every file under `backend/src` that announces a session variable — `set_config(`
  in any casing, or a `SET [LOCAL|SESSION] app.<name>` carrying a value — is one of the closed
  set [Security Standards § The out-of-band setters](11-security.md) enumerates: the ambient
  unit of work, the audit store's writers, or a reader. Each reader
  (`CachedHostToTenantResolver`, `OrganizationScopeValidator`, `AuditConfigService`,
  `FeatureFlags`) issues `SET TRANSACTION READ ONLY` **first, in each announcing method** — read
  from the IL, in instruction order, so a statement in one method cannot cover an announcement in
  another, and a log line that names the statement does not count as issuing it. A setter the scan
  finds and no kind names is a new member of a closed set, and fails until the standard and
  ADR-0040 say which kind it is.
- **The reader set is wider than Security Standards' table**, deliberately.
  [Security Standards § The out-of-band setters](11-security.md) enumerates the setters of
  `app.tenant_id`, and says in as many words that `CachedHostToTenantResolver` is not among them
  — it announces `app.resolving_host`. This rule's subject is any session variable, so the
  resolver is a reader here and the two documents agree rather than differ.
- **Not scanned:** `Migrations/`, narrowly. A migration runs as `learnstack_migration`, outside
  any request, and what it carries is the policy DDL that *reads* these variables. A setter
  moved into one would be a setter this rule does not see, which is why the exemption is a
  directory rather than a pattern.
- **Why the order, exactly:** because the statement binds only what follows it. PostgreSQL does
  **not** refuse it after a first statement — measured on 18: issued after an `INSERT` it is
  accepted and the insert still commits — so the ordering is this rule's doing rather than the
  server's, and a guard that trusted the server would not be a guard.
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
- **Status:** **Implemented** (`TenancyConventionTests`). The Packet 7 version named two
  files, and the two setters Packet 9 added — `AuditConfigService` and `FeatureFlags` —
  announced `app.tenant_id` in a transaction that was not read-only while it stayed green,
  because nothing told it they existed. Packet 10 made it find the setters, and both
  loaders now open their transaction read-only. Its companion,
  `The_Setter_Scan_Can_Actually_Fail`, refuses a reader with no statement, one that issues it
  too late, one whose second announcement has none of its own, and one that only names the
  statement in a message — each written `async`, because a real reader is and an async method's
  statements live in a state machine. It also feeds the discovery pattern each spelling. Mutation-checked: dropping the statement from `FeatureFlags` fails it.
- **Phase:** 02a (Packet 7; discovery-based from Packet 10).

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
- **Phase:** 02a (Packet 7).

#### `Tenant_Context_Guard_Fires_Only_On_An_Unmarked_Transaction`

- **Asserts:** both arms of the `DbCommandInterceptor` guard. A command a module `DbContext` issues on a transaction no sanctioned setter announced throws `TenantContextMissingException`; the same command on an announced transaction runs. One arm is not the rule: a guard keyed on `TransactionBehavior` instead of on the marker passes the first arm and rejects the writes the idempotency store and the audit store legitimately make on their own short transactions.
- **Keyed on the transaction, not on the table.** An earlier wording said "a command against a `[TenantOwned]` table", and the rule's own name says otherwise. What shipped is the name: matching table names would put a parser between every query and the database, wrong on the first CTE, to decide something every command from a module context already answers — such a command belongs to a request that had a tenant to announce. Nothing is lost, because a platform-scoped read from a module context is exactly as much of a wiring bug as a tenant-owned one.
- **Runs as `learnstack_app`.**
- **Source:** [11-security.md § The out-of-band setters](11-security.md);
  [05-database.md § Connection Management](05-database.md).
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** behavioural.
- **Status:** **Implemented** (`TenantContextGuardTests`, Packet 7 step 8).
- **Phase:** 02a (Packet 7).
- **Note:** the marker is a flag on `NpgsqlUnitOfWork`, read through the seam member
  ADR-0040 Amendment 5 adds. **Only one of the eight sanctioned setters stamps it**, and
  that is the honest count: `TransactionBehavior` via `SetTenantContextAsync`. Of the
  other seven, **two do not exist in code yet** — the durable idempotency store, and the
  integration-event transport, which is the one other setter that *opens* the ambient
  transaction and will have to announce it when Phase 02b lands it — and the five that do,
  `OrganizationScopeValidator`, the two standalone audit writers and the two cached
  projection loaders (`AuditConfig` and the tenant flags), issue raw `NpgsqlCommand`s on
  connections of their own, which EF interception cannot see, so they need neither a mark
  nor an exemption. (`CachedHostToTenantResolver` is not one of the eight: it sets
  `app.resolving_host`.) The exemption list is empty for the
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
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** behavioural.
- **Status:** **Registered.**
- **Phase:** 03.

### Audit

Source: [ADR-0033 Audit Durability Model](../decisions/0033-audit-durability-model.md)
(supersedes ADR-0016; Amendment 2 restates its write path against the code that shipped
after it) and [ADR-0044 The Audit Write Path](../decisions/0044-audit-write-path.md),
which decides identity, multiplicity, capture and classification;
[18-audit-coverage.md](18-audit-coverage.md). Introduced by
[Phase 02a Packet 9](../roadmap/phase-02a-kernel-tenancy.md).

#### `AuditEntry_Inherits_Entity_Not_AuditableEntity`

- **Asserts:** `AuditEntry` derives from `Entity<TId>`, not `AuditableEntity<TId>`.
  An audit row that carries `UpdatedAt` / `DeletedAt` is a mutable audit row, which is a
  contradiction.
- **Source:** ADR-0033 (carried from ADR-0016).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented** (`AuditConventionTests`, Packet 9 step 8). Checked at every depth of the ancestry, not just the immediate base: `AuditableEntity<TId>` derives from `Entity<TId>`, so a check on the immediate base alone passes for a type that inherits it one level further down.
- **Phase:** 02a (Packet 9).

#### `MustClass_Audit_Writes_Share_The_Business_Transaction`

- **Asserts:** a MUST-class audit row is inserted on the **same transaction** as the
  business write it records. The proof is behavioural: one command produces exactly one
  row **per declared intent**, all on that transaction — `ProvisionTenantCommand` declares
  two, `tenancy.tenant.create` and `tenancy.organization.create`, and produces two; a
  command whose durable audit write is forced to fail produces **zero** business rows and
  returns `503 audit_unavailable`; a denied MUST-class command produces exactly one row
  carrying the `denied` outcome and zero business rows, and keeps its own `403` — the 503
  is for a write that would otherwise have **succeeded**
  ([ADR-0033 Amendment 1](../decisions/0033-audit-durability-model.md)).
- **The `denied` clause lands with Phase 03, and the rest lands in Packet 9.** Nothing in
  the Packet 9 pipeline can produce a `403`: `AuthorizationBehavior` is still the Packet 3
  pass-through, because the permission registry arrives with Identity in
  [Phase 03](../roadmap/phase-03-identity-admin.md). A case written now could only reach
  the `denied` outcome by returning `Result.Fail(forbidden)` from a **handler** — which
  produces the right row and the right standalone path, and proves nothing about the
  authorization step the clause is about. Splitting the rule's implementation is the
  honest answer: Packet 9 implements the first two clauses against the real pipeline, and
  the `denied` clause is written the day step 5 can refuse. Recording it here is what
  stops a later reader taking the whole rule as satisfied.
- **One row per intent, not one per request.** `IAuditStateCapture` holds an ordered list
  of intents, one per audited `(resource, operation)`, and only the **owning**
  unit-of-work frame (`IUnitOfWorkScope.IsOwner`) flushes it — draining every intent in
  the scope rather than only its own. A joiner writes nothing and signals nothing
  ([ADR-0044 § 3 and § 4](../decisions/0044-audit-write-path.md)). "Exactly one
  `audit_log` row per command" was this row's earlier assertion and is retired: it makes
  the `Organization | create` row [the Tenancy matrix](../modules/tenancy/audit.md)
  classifies MUST unwritable.
- **Why it matters:** ADR-0016's "audit never blocks business logic" applied uniformly,
  which meant a privileged operation could commit while its audit row was lost. It also
  meant the audit insert could run outside the transaction that sets `app.tenant_id` —
  where Row Level Security rejects it. ADR-0033 puts the write inside the transaction, at
  the commit boundary, and this test is what holds the line.
- **Runs as `learnstack_app`.** A non-owning, `NOBYPASSRLS` role; connecting as the owner
  would pass against inert policies and prove nothing about the RLS half of the claim.
- **Source:** ADR-0033 § Decision + Implementation Notes, Amendments 1 and 2;
  [ADR-0044 § 3, § 4, § 11](../decisions/0044-audit-write-path.md).
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** behavioural.
- **Status:** **Implemented** — `AuditPipelineTests.cs`, Packet 9. The second clause is
  `A_MUST_row_that_cannot_be_written_takes_its_business_write_down_with_it`: a trigger
  refuses the audit insert for one host, and the mapping that host names does not commit,
  the caller gets `audit_unavailable`, and the health check reports the refused standalone
  attempt. It was claimed here from the start and written only in the fifth review of
  Packet 9, and it is the direct proof of the rule's name — `xmin` cannot be, because EF
  Core runs each `SaveChanges` inside an open transaction under a savepoint, so rows on one
  transaction carry different `xmin`s. Its companions in the same file assert the halves
  that would otherwise let it pass vacuously: the row carries the tenant the transaction
  announced, and it carries the snapshot the interceptor captured. The owner-only half — a joiner frame writes no MUST rows and claims no commit
  — is `TransactionBehaviorTests.A_joiner_frame_writes_no_MUST_rows_and_claims_no_commit`,
  where a joiner can be constructed without a database.
- **Phase:** 02a (Packet 9).

#### `Audit_Survives_Transaction_Rollback`

- **Asserts:** a MUST-class command whose transaction does not commit still leaves the
  record behind, and the row says **which** of the two happened:
  - a **rollback** — including the ordinary path where the handler calls `SaveChanges`
    and then returns `Result.Fail(...)` — produces **zero** business rows and **exactly
    one** `audit_log` row per declared intent, outcome **`failed`**;
  - a **faulted `COMMIT`**, whose server-side result is unknown, produces the standalone
    re-write with outcome **`indeterminate`**, carrying the **same** `AuditEntryId` as
    the in-transaction attempt and a **fresh** `IClock` reading — which is what keeps the
    deliberate duplicate-id pair legal under the composite primary key `(id, timestamp)`.
    A `23505` on that re-write is positive evidence the `COMMIT` landed: it is logged at
    `Warning`, counted and swallowed, never surfaced as `audit_unavailable`.
- **Why it matters:** the durable write happens before `COMMIT`, so a row that has been
  inserted is not yet durable. A design that marks the intent "consumed" at insert time
  and skips the standalone write on the way out loses the audit **and** the business
  change on every rolled-back MUST-class operation — and a per-request DI-scoped flag
  cannot observe a database rollback. This test is the only thing that distinguishes a
  correct implementation from that one.
- **Runs as `learnstack_app`.**
- **Source:** ADR-0033 § Decision and Amendment 2;
  [ADR-0044 § 5](../decisions/0044-audit-write-path.md).
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** behavioural.
- **Status:** **Implemented** in part — `AuditPipelineTests.cs`, Packet 9: the seed's
  second run is refused, and the refusal is on the record beside the first run's untouched
  rows. That is not the rollback clause: nothing yet rolls back a command that declared
  two intents and counts zero business rows and one `failed` row per intent. Packet 10
  adds that case. The fresh-instant half is asserted against the real table by
  `AuditStoreTests.The_indeterminate_pair_is_two_rows_under_one_id` and
  `A_duplicate_on_the_standalone_re_write_is_positive_evidence_and_is_swallowed`, which
  also holds the `23505`-is-evidence rule and its counter.
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
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** behavioural.
- **Status:** **Registered** — and the name still has no code, but it is no longer "no
  code yet" in the ordinary sense, which is worth saying so a reader does not conclude the
  rule is unenforced. Both halves are held apart today:
  `AuditConfigServiceTests.A_read_failure_falls_back_to_the_declared_tier` drives the
  service against a dead data source and gets the declared tier back, and
  `AuditLogBehaviorTests.An_unregistered_request_is_refused_and_the_handler_never_runs`
  holds the rejection. What is missing is the end-to-end case this row names: a real MUST
  command through the real pipeline with `audit_config` made unreadable. Packet 9 closed
  without it; it lands in Packet 10, which gates phase exit on it.
- **Phase:** 02a (Packet 9 introduces; Packet 10 closes).

#### `AuditLog_Update_Is_Column_Restricted`

- **Asserts:** as `learnstack_app`, any `UPDATE` or `DELETE` on `audit_log` raises
  `42501` (the role holds neither privilege). As `learnstack_platform`, an `UPDATE`
  touching only `actor_email`, `ip_address`, `user_agent`, `before_state`, `after_state`
  and `changes` succeeds; an `UPDATE` touching any other column — `actor_user_id`,
  `operation`, `outcome`, `timestamp` — is rejected before the trigger runs, by the
  **column-level GRANT**; and a `DELETE` succeeds, because the retention purge needs it.
  As the table **owner** (`learnstack_migration`), the same off-list `UPDATE` is stopped
  **only** by `audit_log_append_only_guard`.
- **Three layers, three different actors** — measured on PostgreSQL 18.6
  ([ADR-0044 § 9](../decisions/0044-audit-write-path.md)): `learnstack_app` is stopped by
  the absent privilege (`42501`); `learnstack_platform` by the column-level GRANT; and the
  owner — who holds every privilege implicitly, and whom `FORCE` constrains by tenant
  rather than by immutability — by the trigger.
  The trigger is not redundant with the grant — it is the only layer that binds
  `learnstack_migration`, which is the role every migration runs as.
- **Why it matters:** "append-only" stated as "no `UPDATE` or `DELETE` anywhere" is
  unimplementable — the corpus itself ships two mutating paths (GDPR redaction, retention
  purge). This test pins what is actually allowed so the rule is enforceable rather than
  aspirational.
- **Source:** ADR-0033; [ADR-0044 § 9](../decisions/0044-audit-write-path.md);
  [18-audit-coverage.md § Storage](18-audit-coverage.md);
  [31-audit-subsystem.md § 7](../architecture/31-audit-subsystem.md).
- **Type:** **integration** test (Testcontainers + PostgreSQL). **Kind:** behavioural.
- **Status:** **Implemented** (Packet 9 step 3, `LearnStack.Tests.Integration`,
  `AuditSchemaTests`). Six cases carry it, one per clause rather than one per rule,
  because the three layers fail with three different errors and a single case asserting
  "refused" would not tell them apart: `TheRuntimeRoleCannotMutateTheLog` (the absent
  privilege, three statements), `ARedactingUpdateByThePlatformRoleSucceeds` and
  `ThePlatformRoleCannotUpdateAnythingElse` (the column GRANT, in both directions —
  including the mixed `UPDATE` naming one redactable column and one other, which is
  refused whole rather than applied in part), `TheRetentionDeleteByThePlatformRoleSucceeds`,
  `TheOwnerIsStoppedOnlyByTheTrigger` (which asserts the trigger's message, so a case
  passing on a policy refusal would fail), and
  `ThePlatformRoleHoldsExactlyTheSixColumnUpdateGrant` — the last because
  `information_schema.role_table_grants` reports table privileges only, so the column
  list is invisible to the GRANT-matrix assertion in `TenancySchemaTests`.
  `TruncateIsRefusedEvenForTheOwner` carries the second trigger, which no clause of this
  rule named because a row trigger cannot see `TRUNCATE`.
- **Phase:** 02a (Packet 9).

#### `AuditStateCapture_ClearedPerRequest`

- **Asserts:** after a request completes, success or failure, the scoped
  `IAuditStateCapture` holds no changes and no intent, and its state reads `None` for the
  next request. `Clear()` is called exactly once, by the **outermost** `AuditLogBehavior`,
  in its `finally`.
- **A joiner never clears.** Under [ADR-0040](../decisions/0040-ambient-unit-of-work.md)
  a nested dispatch reaches a joiner frame, and a joiner that cleared would erase the
  outer request's intents and every snapshot **before** the owner committed — the same
  hole [ADR-0044 § 4](../decisions/0044-audit-write-path.md) closes for the write and the
  commit signal. The nested case is therefore part of the assertion, not a variant of it.
- **Source:** [ADR-0033 Amendment 2 § 2](../decisions/0033-audit-durability-model.md);
  [ADR-0044 § 4](../decisions/0044-audit-write-path.md);
  [31-audit-subsystem.md § 13](../architecture/31-audit-subsystem.md), which names this
  rule as blocker-level.
- **Type:** xUnit over the behavior and the scoped capture, with a nested-dispatch case.
  **Kind:** behavioural.
- **Status:** **Implemented** (`AuditLogBehaviorTests`, Packet 9 step 8). A theory over three endings — success, refusal, handler exception — because each leaves the behaviour by a different door and only the `finally` is common to them; plus the nested case, so the pair pins "exactly once, by the outermost" rather than merely "at least once".
- **Phase:** 02a (Packet 9).

#### `Every_Shipped_Request_Is_Registered`

- **Asserts:** every request type with a handler — a closed `IRequestHandler<,>` or
  `IRequestHandler<>` implemented in any project under `backend/src` — is registered in the
  merged audit catalogue, `Off` included. An unclassified request fails the build rather
  than defaulting to silence; at runtime, reaching step 3 unclassified is
  `audit_unclassified_operation`, and there is no `RequestKind.Other`.
- **Why it is keyed on the request type.** `AuditLogBehavior` looks a registration up by
  request type, so the rule has to as well. The two slug-level directions cannot see a
  request type nobody registered when another request shares its slug: measured by the
  review of Packet 9, removing only `MustAudit<CreateOrganizationCommand>` left
  `tenancy.organization.create` registered through provisioning, both directions stayed
  green, and the running system would have refused every `CreateOrganizationCommand`.
- **Companion:** `The_Request_Sweep_Can_Actually_Fail` runs the predicate over exactly that
  catalogue and requires it to name the missing request.
- **The catalogue it reads is the discovered one — so the roots are held to it.** This rule
  constructs every `IAuditCatalogSource` by reflection, which is what lets it see a source
  nobody listed and what makes it blind to the registrations: deleting the API's
  `TenancyAuditCatalogSource` line left the whole suite green while the running system
  refused every Tenancy request as unclassified — measured by the second review of Packet 9.
  `CompositionRootCatalogueTests` in `LearnStack.Tests.Integration` is the runtime half: it
  resolves `IAuditCatalog` from the API's container and from the seeder's, and requires each
  to register every shipped request type and to hold exactly the entries the discovered
  sources declare. Mutation-checked for both roots.
- **Source:** [18-audit-coverage.md § The join](18-audit-coverage.md);
  [ADR-0044 § 6](../decisions/0044-audit-write-path.md).
- **Type:** xUnit + reflection over every backend assembly. **Kind:** structural.
- **Status:** **Implemented** (`AuditCoverageTests`, Packet 9's external-review round).
  Mutation-checked: removing that one registration fails this case.
- **Phase:** 02a (Packet 9).

#### `Every_TenantOwned_Command_HasAuditCoverage`

- **Asserts:** every entry the **in-code** audit catalogue holds — the one each module
  registers through `IAuditCatalogSource.Describe(IAuditCatalogBuilder)`, discovered from
  DI, which maps a **request type** to one or more `(operation, OperationType,
  OperationClass)` triples, and, for the off-path operations below, the same triple by slug
  alone — has a matrix row carrying the same slug, class and type.
  `OperationClass` is the declared tier and carries `Must | Should | May` only;
  `AuditClassification` is what `IAuditConfigService.ClassifyAsync` returns, and it is
  where `Off` and `Unclassified` live
  ([ADR-0044 Amendment 3 § 4](../decisions/0044-audit-write-path.md)). `Off` is a
  registration the builder takes — how a request that writes no row is declared, the
  test-only types among them — and not a fourth tier. That every request type is in the
  catalogue at all is [`Every_Shipped_Request_Is_Registered`](#every_shipped_request_is_registered)'s
  assertion, not this rule's.
- **The join key, and the two directions it runs in.** The key is `(module, operation)`,
  where `operation` is the dotted slug `{module}.{resource}.{verb}` —
  `tenancy.tenant.create`, `customization.content_type.publish`. The rule joins on the
  **request type** in code and cross-checks against the `Operation` column of
  `docs/modules/<module>/audit.md`.
  [ADR-0044 Amendment 3 § 1](../decisions/0044-audit-write-path.md) fixes what each
  direction binds to, because the two have different domains:
  - **Catalogue → matrix, total.** Every entry a *module's* `IAuditCatalogSource`
    registers has a row in that module's matrix carrying the same slug. No exemption.
    Test-only request types register in their fixtures rather than in a module source, so
    they are outside this direction.
  - **Matrix → catalogue, scoped to what exists.** A matrix row fails only when a request
    type that raises it **exists** and no catalogue entry names it. Classifying ahead of
    the command is what [18-audit-coverage.md](18-audit-coverage.md) asks for, and both
    shipped matrices say they do it; such a row carries `(planned)` in the `Operation`
    cell.
  - **Anti-rot, which is what stops the scoping becoming a hole.** A `(planned)` row whose
    command has since shipped **fails**. The marker is a claim the rule re-checks on every
    run, not an exemption from it. **Both directions ship as of Packet 9 step 8**, as two
    facts in `AuditCoverageTests`: `Every_TenantOwned_Command_HasAuditCoverage` for the
    catalogue-to-matrix half and `Every_Matrix_Row_Whose_Command_Exists_Is_Registered` for
    the reverse, which also carries the anti-rot check and a guard against sweeping no rows
    at all.
  - **Off the request path.** Some audited operations are not MediatR requests at all.
    Seven rows carry `(off-path)`: Tenancy's `platform.admin_scope.enter`,
    `tenancy.killswitch.toggle`, `tenancy.entitlement.refresh`,
    `tenancy.tenant_assertion.reject` and `tenancy.tenant_assertion.anonymous_burst` —
    snake_case within each segment since
    [ADR-0036 Amendment 7](../decisions/0036-tenant-resolution-trusted-inputs.md), so that
    one parser reads every audit slug and every permission key — and Audit's
    `audit.redaction.apply` and `audit.purge.apply`. They sit outside the request-type join
    in both directions and register by slug: the three whose writer ships through
    `DeclareOffPath` today, the other four — which also carry `(planned)` — when their
    writer lands.

  Stated as one unconditional join, the rule is red on its first run: the two shipped
  matrices classify many more operations than `backend/src/Modules` has request types, and
  the amendment measures the gap. The Tenancy matrix's earlier claim that
  `platform.admin_scope.enter` is the single row outside the join is corrected with it.
- **It is still a structural rule over the catalogue, not a Markdown parser.** The
  comparison reads one column of slugs, and the two markers ride in that same cell beside
  the slug rather than in a new column or a grammar the two matrices would have to share —
  which is what keeps a documentation edit from being a potential build break. The
  catalogue is the executable artifact, the matrix is the human-readable one, and neither
  is derived from the other — so neither can drift silently
  ([ADR-0044 § 6](../decisions/0044-audit-write-path.md)).
- **The verb is not the permission action set, deliberately.** Audit verbs come from the
  module's own coverage matrix — `create`, `publish`, `revise`, `rename`, `soft_delete`,
  … — because a set closed at Standards 19's `read | write | delete | admin` would
  record a rename and a publication as one operation. Only `{module}` and `{resource}` are
  required to match the permission key for the same resource; see
  [19-permissions.md](19-permissions.md) for the permission side of the pair.
- **Source:** [18-audit-coverage.md](18-audit-coverage.md); ADR-0033;
  [ADR-0044 § 6 and Amendment 3 § 1, § 4](../decisions/0044-audit-write-path.md).
- **Type:** xUnit + reflection over commands and the registered catalogue, cross-checked
  against the matrix's `Operation` column. **Kind:** structural.
- **Status:** **Implemented** (`LearnStack.Tests.Architecture`, `AuditCoverageTests`) — the
  **catalogue → matrix** direction, total per
  [ADR-0044 Amendment 3 § 1](../decisions/0044-audit-write-path.md): every entry the merged
  catalogue holds has a row whose `Operation` cell carries the same slug, with the same class
  and, where the row names one, the same type. The catalogue is merged from every
  `IAuditCatalogSource` a backend assembly ships, discovered rather than listed.
  Mutation-checked: changing a registered slug's class or type fails this case and no other.
  A row it cannot read is a failure too, not a skipped comparison: a class cell stating none
  of `MUST`, `SHOULD` or `MAY`, and a parenthesised operation type — extracted whatever it
  says, then read in the documentation's spelling or the enum's — that `OperationType` does
  not have. The second review of Packet 9 changed a registered MUST row's class to `Off`, and
  the third an annotation to `ReadSensitive` and to `bogus_kind`, and every case stayed green;
  `The_Forward_Sweep_Rejects_A_Class_Or_A_Type_It_Cannot_Read` is the companion. The row is
  looked for in the **registering module's** matrix only, and a row for it in any other
  module's matrix fails — moved or copied there, it is a classification the join never
  compared; the third review moved `tenancy.tenant.create` into Customization's table and
  every case stayed green. `platform.*` is the explicit exception: an off-path entry takes its
  module from its slug, `platform` names no module and has no matrix, so its row sits in the
  matrix of the module that writes it — Tenancy's for `platform.admin_scope.enter` — and in
  exactly one. `The_Forward_Sweep_Reads_Only_The_Registering_Module_s_Matrix` is that half's
  companion. The reverse direction is
  [`Every_Matrix_Row_Whose_Command_Exists_Is_Registered`](#every_matrix_row_whose_command_exists_is_registered),
  and "every request is classified" is
  [`Every_Shipped_Request_Is_Registered`](#every_shipped_request_is_registered).
- **Phase:** 02a (Packet 9).

#### `Every_Matrix_Row_Whose_Command_Exists_Is_Registered`

- **Asserts:** the **matrix → catalogue** direction of the join: every slug in a module
  matrix's `Operation` column — every slug in a cell, not only the first — is registered in
  the merged catalogue unless its cell carries `(planned)` or `(off-path)`; and a
  `(planned)` row whose slug the catalogue **does** register fails, because the marker
  outlived the command it was waiting for; **no slug is classified in two rows**, in one
  matrix or across two, whatever their markers — a copy is a second answer to the question
  the matrix exists to settle; and **a row's `(off-path)` marker agrees with how the
  catalogue registers its slug** — a slug a request type registers is not off-path, and one
  the catalogue declares off-path carries the marker.
- **Why the direction is scoped.** Classifying ahead of the command is what
  [18-audit-coverage.md](18-audit-coverage.md) asks for, so a row may name an operation
  nothing implements yet; the anti-rot check is what keeps that from becoming a hole. A
  shipped command nobody registered is caught by type, one rule up, which this slug-level
  direction cannot see.
- **Companion:** `The_Matrix_Sweep_Reads_Every_Slug_And_Can_Actually_Fail` feeds a fixture
  matrix with a two-slug cell, a stale `(planned)` marker and an unmarked unregistered row,
  and requires all three findings — the parser read only a cell's first slug until the
  review of Packet 9. `A_slug_classified_twice_fails_whichever_row_comes_first` feeds a
  correct row and a contradictory copy in both orders: the forward direction compared only
  the first carrier, so one order passed and the other failed, until the fourth review of
  Packet 9 measured it. Every carrier is compared now, and this direction refuses the copy.
  `An_off_path_marker_has_to_match_how_the_operation_is_registered` feeds both mismatches:
  until the fifth review of Packet 9 the marker was never compared with the registration,
  so a row could call a request-keyed operation off-path and step outside the join it
  belongs to — and the catalogue itself now refuses a slug registered both ways.
- **Source:** [18-audit-coverage.md § The join](18-audit-coverage.md);
  [ADR-0044 Amendment 3 § 1](../decisions/0044-audit-write-path.md).
- **Type:** xUnit + file scan against the merged catalogue. **Kind:** structural.
- **Status:** **Implemented** (`AuditCoverageTests`, Packet 9 step 8; every slug in a cell
  since the external-review round). Mutation-checked: reading one slug per cell fails the
  companion.
- **Phase:** 02a (Packet 9).

#### `Every_Module_Has_An_AuditCoverage_Matrix`

- **Asserts:** every module that has a spec — a directory under `docs/modules/` —
  contains `docs/modules/<module>/audit.md` with a coverage matrix. The file's existence is
  the assertion; the
  one column read from it is `Operation`, and reading it is
  [`Every_TenantOwned_Command_HasAuditCoverage`](#every_tenantowned_command_hasauditcoverage)'s
  job, not this rule's.
- **Why the subject is not every directory under `backend/src/Modules`.** `Modules.Names`
  discovers a directory per module, the Phase 01 scaffolds that hold nothing but an
  `AssemblyMarker.cs` included. A module gets its spec when it reaches "design stable,
  ready to implement"
  ([13-documentation.md § Per-Module Specifications](13-documentation.md)), so a scaffold
  has no operations to classify and nothing for a matrix to hold; a rule over every
  directory would be red for reasons that have nothing to do with audit coverage. The
  guard on the reverse direction — a module that ships an aggregate or a request type with no
  matrix — is
  [`Every_Module_With_An_Aggregate_Or_A_Request_Has_A_Matrix`](#every_module_with_an_aggregate_or_a_request_has_a_matrix),
  the shape `Every_Module_With_A_Schema_Is_Swept` already uses one level up.
  Packet 9 brings `docs/modules/audit/` under it as it ships the Audit module.
- **Source:** [18-audit-coverage.md](18-audit-coverage.md);
  [13-documentation.md § Per-Module Specifications](13-documentation.md).
- **Type:** xUnit + file scan. **Kind:** structural.
- **Status:** **Implemented** (`AuditConventionTests`, Packet 9 step 8). It also pins the
  set of spec directories to exactly `tenancy`, `customization` and `audit`, so a fourth
  module spec fails it until the list names it — with or without an `audit.md`. Its
  companion, `The_Matrix_Sweep_Can_Actually_Fail`, exercises the predicate against a
  directory genuinely lacking the file: every module has one today, so the rule alone passes whether its check works or is defeated — measured, a tautology left it green.
- **Phase:** 02a (Packet 9).

#### `Every_Module_With_An_Aggregate_Or_A_Request_Has_A_Matrix`

- **Asserts:** every module that ships an aggregate root or a request type — an
  `IAggregateRoot<>` implementation, or a request with a handler, whose namespace is
  `LearnStack.Modules.<Name>.…` — has `docs/modules/<name>/audit.md`.
- **Why it exists beside the rule above.** `Every_Module_Has_An_AuditCoverage_Matrix` walks
  the spec directories that exist, so a module that ships code with no spec directory at all
  passes it — the state in which the catalogue ↔ matrix join has nothing to compare
  against. [ADR-0044 Amendment 4 § 3](../decisions/0044-audit-write-path.md) binds the
  matrix to a module that has shipped an aggregate or a request type; this rule walks the
  code. It was request-only until the second review of Packet 9 added an aggregate to a
  scaffold module's `Domain`, with no handler and no matrix, and the rule stayed green —
  the Audit module itself ships aggregates and no request.
- **Companion:** `The_Module_Sweep_Can_Actually_Fail` feeds the discovery an aggregate root
  in a module with no spec directory and no request, and requires the predicate to name it.
- **Source:** [ADR-0044 Amendment 4 § 3](../decisions/0044-audit-write-path.md);
  [13-documentation.md § Per-Module Specifications](13-documentation.md).
- **Type:** xUnit + reflection + file scan. **Kind:** structural.
- **Status:** **Implemented** (`AuditCoverageTests`, Packet 9's external-review round).
  Mutation-checked: a blind predicate fails the companion.
- **Phase:** 02a (Packet 9).

#### `No_Set_Based_Write_Bypasses_The_Audit_Capture`

- **Asserts:** no source file under `backend/src` calls EF Core's set-based write APIs —
  `ExecuteUpdate`, `ExecuteDelete` and `ExecuteSql*`, synchronous or async. A mention in a
  comment does not count; the scan strips comments first.
- **Why.** The audit capture sees what the `ChangeTracker` holds and nothing else
  ([ADR-0044 § 7](../decisions/0044-audit-write-path.md)). A set-based write changes rows
  no entry describes, so a MUST-class operation written that way commits a row with no
  `before_state`, no `after_state` and no `changes` — and nothing fails, because the intent
  still writes its row. The three read as ordinary EF, which is what makes them the likely
  accident. Hand-written SQL on an `NpgsqlCommand` is outside this rule because it is
  visibly SQL: [Database Standards § Raw SQL](05-database.md#raw-sql) governs it, and uses
  it only where the module's matrix says how the write is recorded or why it is not —
  `customization_generations` is the shipped case.
- **Companion:** `The_Set_Based_Write_Sweep_Can_Actually_Fail` scans a probe directory
  holding one call split across two lines and one file that names every API only in a
  comment, and requires exactly the first. The companion proves the predicate; the rule
  itself first asserts its premise — that the same scan over `backend/src` finds the
  `SaveChangesAsync` calls that do go through the tracker — so a scan that read no file
  cannot pass as a clean one (the fifth review of Packet 9).
- **Source:** [ADR-0044 § 7](../decisions/0044-audit-write-path.md);
  [ADR-0033](../decisions/0033-audit-durability-model.md);
  [18-audit-coverage.md](18-audit-coverage.md).
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** (`AuditConventionTests`, Packet 9's review round). A live
  negative — nothing in `backend/src` uses any of the three — and mutation-checked: an
  `ExecuteDeleteAsync` planted in a module's persistence folder fails it.
- **Phase:** 02a (Packet 9).

#### `Modules_Do_Not_Write_AuditLog_Directly`

- **Asserts:** no module assembly **other than `LearnStack.Modules.Audit.*`** names the
  `audit_log` table or the `AuditEntry` type, with one further exclusion — a module's
  registered `IUserReferenceLocator`, whose column-restricted redaction `UPDATE`s are
  enumerated by [`AuditEntry_Is_AppendOnly`](#auditentry_is_appendonly). `IAuditStore` —
  implemented by `PostgresAuditStore` in `LearnStack.Infrastructure.Audit` — is the only
  path by which a module writes an audit row of its own. Inside the Audit module, direct
  `audit_log` SQL is confined to `LearnStack.Modules.Audit.Infrastructure` and bounded by
  that same closed list.
- **Why the exclusions are named.** `AuditEntry`, `AuditConfig` and `AuditDbContext` live
  in the Audit module ([ADR-0044 § 11](../decisions/0044-audit-write-path.md)), so a rule
  reading "no module assembly, outside `LearnStack.Infrastructure.Audit`" forbade the
  assembly the corpus puts the aggregate in — and contradicted the entry below,
  [`AuditEntry_Is_AppendOnly`](#auditentry_is_appendonly), which names the sites where a
  mutating statement may appear. The locators are the second exclusion and the one the
  Audit-module wording alone would miss: each lives in the module whose snapshots carry
  the user reference, so a rule bounded by `LearnStack.Modules.Audit.*` would fail on the
  first locator [Phase 03](../roadmap/phase-03-identity-admin.md) lands. Naming both makes
  the pair say one thing; widening the list past them requires an ADR.
- **Out of scope:** `AuditEntryId` and everything in `LearnStack.SharedKernel.Audit` — the
  ports (`IAuditStore`, `IAuditStateCapture`, `AuditEntryDraft`, `AuditIntent`) and the
  value types those ports carry (`OperationType`, `OperationClass`, `AuditOutcome`,
  `AuditClassification`, `AuditIntentState`, `CapturedEntityChange`). Every module may
  name them, and every module's `IAuditCatalogSource` has to: the first two appear in
  every triple it registers, which is why
  [ADR-0044 Amendment 3 § 3](../decisions/0044-audit-write-path.md) keeps them in
  SharedKernel instead of the Audit module's `Domain`, where they would close a project
  cycle. `AuditEntryId` is a SharedKernel cross-cutting identifier
  ([ADR-0023 Amendment 9](../decisions/0023-strongly-typed-id-source-generator.md)), so a
  scan matching type names by prefix would flag the id for the aggregate's sake.
- **Source:** ADR-0033 (carried from ADR-0016);
  [ADR-0044 § 11](../decisions/0044-audit-write-path.md);
  [20-infrastructure-stack.md § Audit Plumbing](20-infrastructure-stack.md).
- **Type:** xUnit + NetArchTest + SQL scan. **Kind:** structural.
- **Status:** **Implemented** — `AuditConventionTests.cs`, Packet 10, in three legs. The
  entity: across every production assembly, the types that name `AuditEntry` are a closed
  list — the entity, its EF configuration and `AuditDbContext` — so a change-tracker write
  or delete outside it fails, and the Phase 03 read API joins the list by an edit. The
  context's exemption is for the **mapping**: the only member of it that may name the entity is
  its `DbSet`'s getter, or a write placed inside the context would be invisible to a leg whose
  callers name only the context; the member scan reads an `async` member's state machine as part
  of the method that declares it, because that is where its body is. The SQL:
  exactly one file inserts into `audit_log` (`INSERT`, `MERGE` or `COPY`, quoted or
  schema-qualified), and it is `PostgresAuditStore` — the premise and the rule in one
  assertion. The name: no module but Audit names the table — and no statement anywhere in
  `backend/src` composes its table name by interpolation or concatenation, because a name the
  scan cannot read is a name no scan can govern, this rule's and the entitlement cache's alike.
  Its companion, `The_AuditLog_Write_Scan_Can_Actually_Fail`, feeds every pattern its shapes —
  including the lower-case prose a composed-name check must not mistake for SQL — and plants an
  entity writer the list must report.
- **Phase:** 02a (Packet 10).

#### `AuditEntry_Is_AppendOnly`

- **Asserts:** no `UPDATE` or `DELETE` statement targets `audit_log` anywhere in the
  codebase or in any migration **except** at three named sites: the GDPR redaction
  handler and the retention purge job, both in `LearnStack.Modules.Audit.Infrastructure`,
  and a module's registered `IUserReferenceLocator`, which redacts the payload columns of
  its own module's snapshots and therefore lives in that module rather than in the Audit
  one ([31-audit-subsystem.md § 10](../architecture/31-audit-subsystem.md)). Every such
  site must be inside an `IPlatformAdminScope` block. `IAuditStore` is asserted to expose
  no update method at all.
- **Why the exception list is closed and named:** the earlier phrasing ("anywhere in the
  codebase") contradicted paths the corpus ships by design and would have failed on its
  first green run. Naming the three sites keeps the rule enforceable and gives
  [`Modules_Do_Not_Write_AuditLog_Directly`](#modules_do_not_write_auditlog_directly) the
  same sites to exclude; widening the list requires an ADR. The database-level guard is
  [`AuditLog_Update_Is_Column_Restricted`](#auditlog_update_is_column_restricted), which
  is what actually constrains *which columns* may change.
- **Source:** ADR-0033 (carried from ADR-0016);
  [31-audit-subsystem.md § 10](../architecture/31-audit-subsystem.md).
- **Type:** xUnit + source / migration scan. **Kind:** structural.
- **Status:** **Implemented** (`AuditConventionTests`, Packet 9 step 8), in three parts: a source sweep over `backend/src` for an `UPDATE` or `DELETE` targeting `audit_log` — with or without `ONLY`, a schema qualifier or identifier quotes, which the first pattern missed until the fourth review of Packet 9 measured it; a companion, `The_AppendOnly_Sweep_Can_Actually_Fail`, that checks the pattern against the shapes it must catch and the shapes it must not, because with no offending statement anywhere the sweep passes whether it works or matches nothing; and a reflection check, `IAuditStore_Exposes_No_Update_Method`, that `IAuditStore` exposes exactly four write methods and no update. None of the three sanctioned redaction sites exists yet — they land in Phase 03 and Phase 11 — so the exemption set ships with the rule **empty**, as exact paths: the first site to land adds its own path, so it is exempted by name rather than the pattern widened. (A substring predicate stood there first, and exempted any Audit-module file whose path contained "Redaction".) Since the fifth review of Packet 9 the sweep also asserts its premise — it must have read `PostgresAuditStore`'s own `INSERT INTO audit_log` — because with no offender anywhere, a sweep that read nothing passed as well.
- **Phase:** 02a (Packet 9).

#### `Every_PII_Module_RegistersUserReferenceLocator`

- **Asserts:** every module whose audit payloads carry a user reference registers an
  `IUserReferenceLocator` implementation. A module that stores such a reference and ships
  no locator leaves rows a GDPR erasure cannot reach — the redaction handler redacts the
  actor columns centrally and delegates the payload columns to the module that knows
  which JSON paths in its own snapshots name a user
  ([31-audit-subsystem.md § 10](../architecture/31-audit-subsystem.md)).
- **Registered here, asserted in Phase 03.** `IUserReferenceLocator` and
  `UserGdprDeletedIntegrationEventHandler` land with the `users` table they need
  ([ADR-0044 § What we explicitly punted on](../decisions/0044-audit-write-path.md)), so
  until then there is no locator to count and no module obliged to have one. Reserving the
  name now is what [§ How to add an entry](#how-to-add-an-entry) prescribes for a rule
  that is agreed and not yet written: the row carries **Registered** and the phase that
  owes the code, and the alternative — a rule that lives only in an architecture
  section's prose — is how a second spelling starts.
- **Source:** [31-audit-subsystem.md § 10](../architecture/31-audit-subsystem.md);
  [ADR-0044 § What we explicitly punted on](../decisions/0044-audit-write-path.md).
- **Type:** xUnit + reflection over the registered locators, cross-checked against the
  modules whose audit payloads carry a user reference. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 03 — the phase that lands the interface and the handler that drives it.

#### `OperationType_Enum_Matches_Catalog`

- **Asserts:** the `OperationType` enum and the audit-operation catalogue in
  [18-audit-coverage.md § Operation Types](18-audit-coverage.md) contain the same members.
- **Both sides carry seven.** The enum — `LearnStack.SharedKernel.Audit.OperationType`
  ([ADR-0044 Amendment 3 § 3](../decisions/0044-audit-write-path.md)), described in
  [31-audit-subsystem.md](../architecture/31-audit-subsystem.md) — carries `Create`,
  `Update`, `Delete`, `ReadSensitive`, `SecurityEvent`, `PlatformAdmin` and `Action`, and
  matches [18-audit-coverage.md § Operation Types](18-audit-coverage.md) row for row.
  `platform-admin` does **not** subsume `action`:
  [ADR-0016's 2026-05-19 amendment](../decisions/0016-audit-log-subsystem.md) adds the
  first beside the second and calls the resulting seven-member enum binding. Adding an
  eighth type is a change to that table first; a member present on one side only is what
  this rule fails the build on.
- **Not the classification catalogue.** This rule reads the seven-row
  `OperationType` table, not a module's coverage matrix — so it is unaffected by
  [ADR-0044 § 6](../decisions/0044-audit-write-path.md)'s move of *classification* into
  code, and it legislates no matrix grammar.
- **Source:** ADR-0033 (carried from ADR-0016).
- **Type:** xUnit + reflection + a parse of that one table. **Kind:** structural.
- **Status:** **Implemented** (`AuditConventionTests`, Packet 9 step 8). Reads the first column of § Operation Types only — the same words appear in the prose around it — and asserts the row count before comparing, because a sweep that read no rows would agree with any enum.
- **Phase:** 02a (Packet 9).

> **Retired from this section.** `AuditLogBehavior_NeverBlocks_BusinessWrites` — see
> § Retired. Its assertion is now false for MUST-class audit.

### Hub contract surface

Source: [ADR-0034 Hub Contract Surface Invariant](../decisions/0034-hub-contract-surface-invariant.md),
[ADR-0019](../decisions/0019-learnstack-hub.md),
[ADR-0020](../decisions/0020-triple-deployment-hybrid-license.md).

#### `LearnStack_Modules_DoNotReference_Hub`

- **Asserts:** no module assembly depends on `LearnStack.Infrastructure.Hub` — where
  Phase 02c's adapter lives — or on the Hub's own `LearnStack.Hub` namespaces, and no
  module source names a Hub client, `HubOptions`, a `"Hub:"` configuration path or the bare
  `"Hub"` section literal — the house idiom declares a section name as a `const string` and
  binds through the constant, so the literal is a spelling actually in use. Two
  legs because no Hub client exists before Phase 02c: the namespaces it will arrive in, and
  the names a module would have to write to reach it any other way.
- **Source:** ADR-0019; ADR-0034 invariant 2.
- **Type:** xUnit + NetArchTest + source scan. **Kind:** structural.
- **Status:** **Implemented** — `CrossCuttingFoundationTests.cs`, Packet 10, with
  `The_Direct_Client_Bans_Can_Actually_Fail` planting a type from a `LearnStack.Hub`
  namespace and feeding the source leg the spellings it must catch — and the entitlement
  cache family, named `hub`, which it must not.
- **Phase:** 02a (Packet 10).

#### `Host_Resolution_Makes_No_Outbound_Calls`

- **Asserts:** host resolution succeeds with the Hub client registered as a throwing stub
  ([27-custom-domain-tls.md § 10](../architecture/27-custom-domain-tls.md)). Until a Hub
  client exists to register, the structural half stands in for it: the one
  `IHostToTenantResolver`, `CachedHostToTenantResolver`, takes and holds no `HttpClient`,
  `IHttpClientFactory`, gRPC channel or Hub client — only its caches, its options and the
  application data source.
- **Why it matters:** host resolution runs on every anonymous page load before a tenant is
  known, and [ADR-0034](../decisions/0034-hub-contract-surface-invariant.md) forbids it to
  call the Hub so that a Hub outage cannot take tenant sites down. Resolution with no
  network and no tenant context is `Host_Resolves_With_No_Tenant_Context_Under_Rls`; this
  is the half a new constructor parameter would break without any test noticing.
- **Source:** ADR-0034; [20-infrastructure-stack.md](20-infrastructure-stack.md).
- **Type:** xUnit + reflection over the resolver; an integration test for the stub leg.
  **Kind:** structural (the resolver's dependencies) + behavioural (the stub).
- **Status:** **Implemented** for the structural half — `TenancyConventionTests.cs`,
  Packet 10. It asserts its premise, that `CachedHostToTenantResolver` is the only
  `IHostToTenantResolver`, then walks the resolver's constructor parameters and its fields —
  instance and static, declared and inherited — and those of every LearnStack class it depends
  on, for anything in `System.Net.Http`, `System.Net.Sockets`, `System.Net.WebSockets` or
  `Grpc`, a Hub client, or an `IServiceProvider`, which answers for every registered type and
  would otherwise hide one. Ports are not followed: a port is governed where it is declared.
  A second leg reads the resolver's IL — signatures, method bodies, and the state machines its
  async methods compile into — because `new HttpClient()` inside a method is a call out no walk
  over declarations can see. Its companion,
  `The_Outbound_Dependency_Scan_Can_Actually_Fail`, requires each hiding place to be found: a
  constructor parameter, a handler one LearnStack type down, a
  `private static readonly HttpClient`, an inherited field, a service provider, and a client
  built inside a method body. The stub leg is Registered.
- **Phase:** 02a (Packet 10) for the structural half; the stub leg with the first Hub
  client, in [Phase 02c](../roadmap/phase-02c-hub-foundation.md).

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
- **It bounds the ceiling; it does not require the count.** One implementation exists
  today, and once written the rule is not vacuous: a **fourth** implementation fails it
  the day it lands, before the other two exist. The second
  lands in Phase 02c and the third in
  [Phase 11](../roadmap/phase-11-production-hardening.md), and the composition-root
  clause describes the end state — until
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md)'s trigger fires,
  `NullEntitlementProvider` is what every mode registers
  ([ADR-0020 Amendment 2026-09-07](../decisions/0020-triple-deployment-hybrid-license.md)).
- **Source:** ADR-0020 (and its 2026-09-07 Amendment); ADR-0034;
  [ADR-0045 § 4](../decisions/0045-entitlement-and-feature-flag-socket.md).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02c.

#### `NullEntitlementProvider_NotRegistered_OutsideDevelopment`

- **Asserts:** for each `DeploymentMode`, once that mode's own `IEntitlementProvider`
  implementation exists, `NullEntitlementProvider` is no longer registered for it —
  leaving `Development`, the one mode that keeps it permanently, as the end state the
  rule's name describes.
- **It asserts nothing in Phase 02a, by decision.** Packet 9 registers
  `NullEntitlementProvider` in **every** deployment mode — it is the gate's working
  default implementation under
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md), and a mode-conditional
  registration would make four of the five modes unbootable for a capability none of them
  yet uses. The rule binds per mode, from the phase that lands that mode's own
  implementation: Phase 02c for the three Hub-backed modes,
  [Phase 11](../roadmap/phase-11-production-hardening.md) for `SelfHostedAirGapped`
  ([ADR-0020 Amendment 2026-09-07](../decisions/0020-triple-deployment-hybrid-license.md);
  [ADR-0045 § 4](../decisions/0045-entitlement-and-feature-flag-socket.md)).
- **Source:** ADR-0020 (and its 2026-09-07 Amendment); ADR-0035 § Implementation Notes.
- **Type:** xUnit + service-collection inspection per mode. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** 02c for the three Hub-backed modes — `SaaS`, `Dedicated`,
  `SelfHostedOnline`; 11 for `SelfHostedAirGapped`.

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
  **Kind:** behavioural.
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
- **Type:** integration test (Testcontainers). **Kind:** behavioural.
- **Status:** **Registered.**
- **Phase:** 02b.

#### `Hangfire_Job_Payloads_Include_TenantId`

- **Asserts:** Hangfire enqueue rejects job payloads missing `tenant_id`
  or `correlation_id`. Per the `JobActivator` contract the enqueue path
  fails at submission, not at activation, so the failure mode is loud.
- **Source:** ADR-0032 § Sub-decision 12; Phase 02b deliverable.
- **Type:** xUnit + Hangfire enqueue interceptor test. **Kind:** behavioural.
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
  not wait on the Dapr adapter. **Kind:** behavioural.
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
- **Type:** **integration** test (Testcontainers). **Kind:** behavioural.
- **Status:** **Registered.**
- **Phase:** 02b.

#### `Outbox_Claim_IsHeld_Until_Dispatch_Completes`

- **Asserts:** two concurrent `OutboxProcessor` instances draining one pending batch
  dispatch each message **exactly once** — the claim is held for the duration of the
  dispatch, not released when the row is read. Requires **two** processors: a
  single-processor test passes against the broken protocol.
- **Source:** [Phase 02b](../roadmap/phase-02b-events-auth.md);
  [15-event-and-outbox.md](../architecture/15-event-and-outbox.md).
- **Type:** **integration** test (Testcontainers, two processes). **Kind:** behavioural.
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
  `IIntegrationEventHandler<T>`. The module sweep has had a subject since Packet 7 —
  three modules ship code — and the checker is also pointed at direct-injection,
  method-injection and service-locator deliberate offenders in the test assembly, so a
  clean sweep is evidence rather than an absence.
- **Phase:** 02a (Packet 5).

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
registered, and is listed under § Repository layout and module boundaries, where
Packet 10 implemented it.

### Awaiting backfill

Every identifier previously parked in this section has been folded into the catalogue
above under its canonical name. Anything newly discovered in an ADR or standard is added
here with **Status: Registered** by the next PR that touches its source document —
registering costs one row, and an unregistered rule is how the six-spelling drift
started.

Phase 02a Packet 10 swept the corpus for architecture-test names that no row carried. The
ones Phase 02a owns have entries above; the rest are listed here, each **Awaiting
backfill** — waiting for the subject its Owning phase column names. The entry a row
becomes, with the full Asserts / Source / Type lines, is written by the PR that
implements it.

| Test | Rule | Kind | Owning phase | Named in |
|---|---|---|---|---|
| `LearnStackJob_RunAsync_SetsTenantBeforeExecute` | `LearnStackJob.RunAsync` is non-virtual, and its write to `ITenantContextAccessor.Current` — the Hangfire writer among the four `SetTenant_Callers_Are_The_Enumerated_Four` enumerates — precedes `ExecuteAsync` | structural | [Phase 02b](../roadmap/phase-02b-events-auth.md), with `LearnStackJob` | [Tenant Isolation](../architecture/09-tenant-isolation.md) |
| `Provider_SDK_Types_NotImportedOutsideInfrastructure` | A provider SDK's types — LiveKit, the Keycloak admin client, SeaweedFS, a payment SDK — appear only in the `LearnStack.Infrastructure.*` adapter that wraps it, never in a module. A superset of [`Adapters_Wrap_Provider_Exceptions`](#adapters_wrap_provider_exceptions), which already holds the exception types of the SDKs it lists — the one slice of this rule that runs today | structural | [Phase 02b](../roadmap/phase-02b-events-auth.md), with the first provider adapter; it then binds per provider as each adapter lands | [Tenant Isolation](../architecture/09-tenant-isolation.md) |
| `Backend_RequiresJwt_OnAllAuthenticatedRoutes` | Every endpoint outside the public allow-list answers `401` without a bearer token — the guard that holds while the gateway's OIDC block is commented out | behavioural | [Phase 02b](../roadmap/phase-02b-events-auth.md), with authentication | [API Gateway](../architecture/30-api-gateway.md) |
| `LicenseKey_Payload_MatchesSchema` | `entitlement-v1.schema.json` is pinned by a snapshot test run in **both** repositories against the same checked-in schema | structural | [Phase 02c](../roadmap/phase-02c-hub-foundation.md), with the schema | [Hybrid License Model](../architecture/26-hybrid-license-model.md); [ADR-0021](../decisions/0021-feature-based-entitlement.md), as `EntitlementProjection_Shape_IsStable` — see § Canonical names |
| `Entitlement_Read_Path_Falls_Through_To_Durable_Row` | With L1 and L2 flushed and the Hub unreachable, the tenant resolves from `platform_entitlement_cache` and no exception escapes the flag read | behavioural | [Phase 02c](../roadmap/phase-02c-hub-foundation.md), with `HubEntitlementProvider` | [Hybrid License Model](../architecture/26-hybrid-license-model.md) |
| `CustomDomain_TenantId_NeverReadFrom_RequestBody` | Custom-domain submission derives the tenant from the authenticated session, never from the body or the query | behavioural | [Phase 02c](../roadmap/phase-02c-hub-foundation.md), Hub side, per [ADR-0022](../decisions/0022-custom-domain-tls.md) | ADR-0022; [Custom Domain TLS](../architecture/27-custom-domain-tls.md) |
| `Cert_PrivateKey_NeverLeavesVault_To_Logs` | The log redaction filter strips a PEM private-key block before a line is emitted, in every deployment mode | behavioural | [Phase 02c](../roadmap/phase-02c-hub-foundation.md) on the Hub side, per ADR-0022, which applies it in every mode; on the LearnStack side [Phase 11](../roadmap/phase-11-production-hardening.md), with the TLS automation — the first LearnStack code that handles certificate material, and a Self-Hosted air-gapped deployment has no Hub | ADR-0022; [Custom Domain TLS](../architecture/27-custom-domain-tls.md) |
| `Frontend_Has_Only_The_OperatorPortal_App` | The Hub repository ships exactly one frontend application, the operator portal | structural | [Phase 02c](../roadmap/phase-02c-hub-foundation.md), owned and run by the `learnstack-hub` repository | [ADR-0019](../decisions/0019-learnstack-hub.md) |
| `Customization_Reference_Resolution_Is_Batched` | Resolving an entry's references costs a small constant number of queries, not one per reference | behavioural | [Phase 05](../roadmap/phase-05-education-learning-content.md), with the batched reference walk — Phase 02d's lesson body carries its fields inline and resolves no reference | [Tenant Customization Model](../architecture/32-tenant-customization-model.md); [Phase 05](../roadmap/phase-05-education-learning-content.md) |
| `Permission_Definitions_DeclareScope` | Every registered permission declares its `PermissionScope` — Platform, Tenant or Organization | structural | [Phase 03](../roadmap/phase-03-identity-admin.md), with the permission registry | [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md) |
| `Permission_Scope_Matches_Resource_Scope` | An `Organization`-scope permission is checked only against an `[OrganizationScoped]` resource, and a `Tenant`-scope permission never gains organization filtering | structural | [Phase 03](../roadmap/phase-03-identity-admin.md) | [Permissions](19-permissions.md); `add-permission` |
| `Permission_Keys_Match_Convention` | Every registered key parses as `{module}.{resource}.{action}` with an action from the closed set | structural | [Phase 03](../roadmap/phase-03-identity-admin.md) | [Permissions](19-permissions.md); `add-permission` |
| `Permission_Registry_Has_DeniedTest` | Every registered key has at least one test that is denied it | structural | [Phase 03](../roadmap/phase-03-identity-admin.md) | [Permissions](19-permissions.md); `add-permission` |
| `Search_Kinds_AreNot_Domain_Prefixed_In_Code` | No module registers a domain-prefixed search kind; every domain-shaped index arrives through a `TenantContentType` | structural | [Phase 04](../roadmap/phase-04-cms-media-pages.md), with `ITenantSearch` | [Search](../architecture/20-search.md) |
| `Block_Schemas_Are_Immutable_After_Publish` | A published page-block schema version is never edited in place; a breaking change ships a new version | behavioural | [Phase 04](../roadmap/phase-04-cms-media-pages.md), with `TenantPageBlock` | [ADR-0013](../decisions/0013-page-block-schema-versioning.md); `add-page-block` |
| `Scoring_Rules_Compile_Against_Sandbox` | Every `TenantScoringRule` expression compiles inside the DSL sandbox and nowhere else | behavioural | [Phase 05](../roadmap/phase-05-education-learning-content.md), with the evaluator | [Phase 05](../roadmap/phase-05-education-learning-content.md); `add-tenant-scoring-rule` |
| `Completion_Rules_Are_Boolean_Pure` | A `TenantCompletionRule` expression returns a boolean and reads nothing outside its own inputs | behavioural | [Phase 05](../roadmap/phase-05-education-learning-content.md), with the evaluator — Phase 07 consumes it and writes no rule code | [Phase 05](../roadmap/phase-05-education-learning-content.md); `add-tenant-completion-rule` |
| `LicenseKey_Validation_ChecksRevocationList` | A licence id in the revocation set is rejected | behavioural | [Phase 11](../roadmap/phase-11-production-hardening.md), with signed licence keys | [Hybrid License Model](../architecture/26-hybrid-license-model.md) |
| `CustomDomain_PublicSuffixList_Enforced` | The custom-domain validator rejects a public-suffix TLD | behavioural | [Phase 02c](../roadmap/phase-02c-hub-foundation.md), Hub side — the check is in `CustomDomain.Create`, which the Hub's submission path owns | [ADR-0022](../decisions/0022-custom-domain-tls.md); [Custom Domain TLS](../architecture/27-custom-domain-tls.md) |
| `CustomDomain_Revocation_RemovesTenantResolverMapping` | A revoked domain resolves to nothing | behavioural | [Phase 11](../roadmap/phase-11-production-hardening.md), with custom-domain TLS automation | [Custom Domain TLS](../architecture/27-custom-domain-tls.md) |
| `Apisix_RouteYaml_IsValid` | `apisix test` accepts the route file | behavioural | [Phase 11](../roadmap/phase-11-production-hardening.md), with the APISIX adapter | [API Gateway](../architecture/30-api-gateway.md) |
| `Apisix_Routes_Declare_Explicit_Priority` | Every route sets `priority` | structural | [Phase 11](../roadmap/phase-11-production-hardening.md) | [API Gateway](../architecture/30-api-gateway.md) |
| `Apisix_Public_Routes_Outrank_Authenticated_Catchall` | No route without `openid-connect` shares a prefix with a higher- or equal-priority route that has it | structural | [Phase 11](../roadmap/phase-11-production-hardening.md) | [API Gateway](../architecture/30-api-gateway.md) |
| `Apisix_Uri_Patterns_Are_RadixtreeValid` | A route pattern has at most one `*`, and only as its final segment | structural | [Phase 11](../roadmap/phase-11-production-hardening.md) | [API Gateway](../architecture/30-api-gateway.md) |
| `Apisix_NeverFronts_InternalApi` | `/api/internal/*` answers `404` from the gateway — no route is defined for it | behavioural | [Phase 11](../roadmap/phase-11-production-hardening.md) | [API Gateway](../architecture/30-api-gateway.md) |
| `Partition_Manager_Job_Is_Registered_AtStartup` | The `audit_log` partition-management job is registered at startup | structural | [Phase 11](../roadmap/phase-11-production-hardening.md), with the job it guards | [ADR-0028](../decisions/0028-audit-log-partition-management.md); [ADR-0044](../decisions/0044-audit-write-path.md); [Phase 11](../roadmap/phase-11-production-hardening.md) |

### Retired

#### `Development_Only_Tenant_Header_Override_Is_Mode_Guarded`

- **Retired** before it was implemented.
- **Why:** an early draft of ADR-0036 carried a `DeploymentMode.Development` flag that
  let `X-Tenant-Id` act as the resolution source, and this test would have guarded it.
  The flag was retired before it shipped: the trusted hop lets a `curl` supply an
  effective host that goes through the real resolver, the real policy and the real
  matrix, so there is no code path anywhere that writes a tenant id from a header. The
  name is recorded here so it does not reappear as a second spelling for something else.
- **Source:** ADR-0036 § There is no Development override.

#### `Audit_Config_Failure_Rejects_Operation`

- **Withdrawn** before implementation — an earlier draft of the audit subsystem named it,
  and it was withdrawn rather than renamed. Under [ADR-0033](../decisions/0033-audit-durability-model.md) as settled, a
  tenant-override read failure falls back to the in-process catalogue, which carries the
  same MUST floor, so the assertion would have locked in a platform-wide denial of
  service triggered by a cache outage.
- **Replaced by** [`Audit_Classification_Does_Not_Read_The_Database_On_The_Request_Path`](#audit_classification_does_not_read_the_database_on_the_request_path),
  which asserts the property that matters
  ([31-audit-subsystem.md § 13](../architecture/31-audit-subsystem.md)).

#### `AuditLogBehavior_NeverBlocks_BusinessWrites`

- **Retired 2026-08-08** by [ADR-0033](../decisions/0033-audit-durability-model.md),
  which supersedes ADR-0016. The assertion is now false by design for MUST-class audit:
  a MUST-class audit failure **must** block the business write, because the audit row is
  part of the operation's contract and shares its transaction.
- **Replaced by** [`MustClass_Audit_Writes_Share_The_Business_Transaction`](#mustclass_audit_writes_share_the_business_transaction).
  The surviving half of the old rule — SHOULD/MAY-class audit never blocks — is asserted
  by `AuditLogBehaviorTests` (the best-effort cases) and `AuditStoreTests`
  (`A_best_effort_failure_is_logged_and_dropped`), not by that integration test.
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
  `WebApplicationFactory<Program>` host. **Kind:** structural — it reads the started
  host's endpoint list rather than sending a request.
- **Status:** **Implemented** (`VersionedRouteEnforcementTests`, in
  `LearnStack.Tests.Integration`).
- **Phase:** 02a (Packet 4).
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
- **Type:** xUnit + host startup. **Kind:** startup.
- **Status:** **Implemented** (`VersionedRouteEnforcementTests`).
- **Phase:** 02a (Packet 4).

#### `A_Major_Outside_LiveMajors_Fails_At_Startup`

- **Asserts:** a controller declaring `[ApiVersion(N)]` for an `N` absent from
  `ApiVersioningExtensions.LiveMajors` aborts host startup, so a route can
  never be served under a major no OpenAPI document publishes and no generated
  SDK can call.
- **Source:** ADR-0024 § The version axis.
- **Type:** xUnit + host startup. **Kind:** startup.
- **Status:** **Implemented** (`VersionedRouteEnforcementTests`).
- **Phase:** 02a (Packet 4).

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
- **Type:** xUnit + host startup. **Kind:** startup.
- **Status:** **Implemented** (`VersionedRouteEnforcementTests`).
- **Phase:** 02a (Packet 4).

#### `An_Absolute_Internal_Route_Is_Exempt_At_Both_Levels`

- **Asserts:** `/api/internal/*` stays exempt whether its template is written
  relative or absolute, at the controller level and at the action level. The
  action-level guard normalised the template before testing the exemption and
  the controller-level one did not, so an absolute Hub route was refused at
  startup — on a surface ADR-0024 does not govern at all.
- **Source:** ADR-0019; ADR-0024 § The version axis.
- **Type:** xUnit + host startup. **Kind:** behavioural.
- **Status:** **Implemented** (`VersionedRouteEnforcementTests`).
- **Phase:** 02a (Packet 4).

#### `A_Hand_Written_Prefix_That_Disagrees_With_The_Attribute_Fails_At_Startup`

- **Asserts:** a route template already written under `api/v{N}` must agree
  with the controller's `[ApiVersion]`. The idempotency guard that makes a
  double convention registration harmless would otherwise double as an escape
  hatch, with the route saying one major and the `x-version-introduced`
  extension — read off the attribute — saying another.
- **Source:** ADR-0024 § The version axis.
- **Type:** xUnit + host startup. **Kind:** startup.
- **Status:** **Implemented** (`VersionedRouteEnforcementTests`).
- **Phase:** 02a (Packet 4).

#### `Live_Majors_Are_At_Most_Two_Adjacent`

- **Asserts:** `ApiVersioningExtensions.LiveMajors` holds at most two majors,
  they are distinct and adjacent, and none is below 1.
- **Source:** ADR-0024 § The version axis ("Two adjacent majors coexist";
  "No `/api/v0/*` endpoints exist or will exist").
- **Type:** xUnit. **Kind:** structural.
- **Status:** **Implemented** (`ApiConventionTests`).
- **Phase:** 02a (Packet 4).

#### `Unversioned_Route_Prefixes_Are_Declared_Once`

- **Asserts:** `VersionedRouteConvention.UnversionedRoutePrefixes` equals
  exactly `["api/internal"]`, so a widening of the exemption set is a failing
  test rather than a silent hole.
- **Source:** ADR-0024 § The version axis; ADR-0019.
- **Type:** xUnit. **Kind:** structural.
- **Status:** **Implemented** (`ApiConventionTests`).
- **Phase:** 02a (Packet 4).

#### `Every_Deprecated_Endpoint_Has_Sunset_And_Successor`

- **Asserts:** every controller action marked `[Obsolete]` declares both a
  sunset date and a successor route, and the emitted OpenAPI operation carries
  `x-sunset`, `x-successor` and `x-migration-guide`.
- **Source:** ADR-0024 § Lifecycle of a deprecated endpoint.
- **Type:** xUnit + OpenAPI document inspection. **Kind:** structural.
- **Status:** **Registered.**
- **Phase:** unscheduled — no phase in the roadmap adds a `/api/v2` endpoint. ADR-0024
  states it lands "when the first `/v2` endpoint is added"; there is no deprecated
  operation before one exists, so registering it now records the name without claiming
  coverage. It cannot be forgotten: a `/v2` endpoint fails
  `A_Major_Outside_LiveMajors_Fails_At_Startup` until `LiveMajors` gains `2`, and that
  edit is where this rule is implemented.

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
- **Phase:** 02a (Packet 4).
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
- **Phase:** 02a (Packet 4).

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
- **Phase:** 02a (Packet 4).

#### `A_Field_The_Endpoint_Does_Not_Allow_Is_Refused_By_Name`

- **Asserts:** a well-formed `sort` field outside the endpoint's allow-list
  returns 400 carrying `lockey_sort_field_not_allowed` with the field in
  `params`. Parsing and authorising are separate steps, and an ignored key
  would return a page in an order the client did not request.
- **Source:** Standards 04 § Filtering and Sorting.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`ErrorShapeHttpTests`).
- **Phase:** 02a (Packet 4).

#### `List_Query_Parameters_Are_Published_Individually`

- **Asserts:** `cursor`, `limit`, `sort` and `q` appear as individual query
  parameters in the OpenAPI document, not as one opaque object. Standards 04
  § Filtering and Sorting requires each to be documented, and a generator that
  collapses a `[FromQuery]` complex type leaves the generated SDK unable to
  offer any of them as arguments.
- **Source:** Standards 04 § Filtering and Sorting, § OpenAPI.
- **Type:** xUnit + OpenAPI document inspection. **Kind:** structural.
- **Status:** **Implemented** (`ApiVersioningHttpTests`).
- **Phase:** 02a (Packet 4).

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
- **Phase:** 02a (Packet 4).
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
- **Phase:** 02a (Packet 4).

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
- **Phase:** 02a (Packet 4).

#### `A_Thrown_Attempt_Does_Not_Pin_The_Key`

- **Asserts:** an attempt that throws releases its key, so the retry runs.
  Recording a failure would replay it for the 24-hour retention window, turning
  one transient fault into a day of them. `A_Returned_5xx_Does_Not_Pin_The_Key_Either`
  covers the sibling branch — a handler that *returns* a 5xx rather than throwing
  is a different path, and it was untested.
- **Source:** Standards 04 § Idempotency.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`IdempotencyHttpTests`).
- **Phase:** 02a (Packet 4).

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
- **Phase:** 02a (Packet 4).

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
- **Phase:** 02a (Packet 4).

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
- **Phase:** 02a (Packet 4).

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
- **Phase:** 02a (Packet 4).

#### `A_Response_Too_Large_To_Store_Refuses_The_Retry_Rather_Than_Rerunning_It`

- **Asserts:** an outcome that exceeds the replay cap is tombstoned, so the retry
  answers **409** `idempotency_outcome_unavailable` and the operation runs once.
  Releasing the key instead would let it run twice with a `2xx` both times, on
  the surface Standards 04 reserves for payments.
- **Source:** [ADR-0037](../decisions/0037-idempotency-key-contract.md) § What is
  recorded.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`IdempotencyHttpTests`).
- **Phase:** 02a (Packet 4).

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
- **Phase:** 02a (Packet 4).

#### `An_Idempotent_Operation_Publishes_Its_Header_In_The_Contract`

- **Asserts:** the OpenAPI document for an `[Idempotent]` operation carries the
  required `Idempotency-Key` header and documents its 409. Without it the
  attribute is invisible to the generated SDK, every call the SDK makes is
  answered 400, and "the first consumer is a one-attribute change" is not true.
- **Source:** [ADR-0037](../decisions/0037-idempotency-key-contract.md);
  Standards 04 § OpenAPI.
- **Type:** xUnit + HTTP against the emitted document. **Kind:** structural — the published document's shape.
- **Status:** **Implemented** (`IdempotentEndpointConventionTests`).
- **Phase:** 02a (Packet 4).

### Tenant and organization resolution (ADR-0036)

The binding evidence for this group is the **behavioural** matrix in
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
- **Phase:** 02a (Packet 4; the Amendment 4 correction and the pairing property, Packet 7 step 4).

#### `Tenant_Assertions_Are_Compared_Not_Resolved`

- **Asserts:** `X-Tenant-Id` and `X-Organization-Id` never select anything. An assertion that agrees with what the API resolved passes; one that disagrees is **404**, not 403 — a wrong tenant id must not be able to tell the difference between "exists, not yours" and "does not exist"; a malformed or repeated one is **400** and counted; and an unresolved context passes the request through to be refused downstream rather than inventing a tenant.
- **Source:** ADR-0036 § The reconciliation matrix.
- **Type:** xUnit + HTTP. **Kind:** behavioural.
- **Status:** **Implemented** (`TenantAssertionHttpTests`).
- **Phase:** 02a (Packet 4).

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
- **Phase:** 02a (Packet 4).

#### `Tenant_Headers_Are_Never_A_Resolution_Source`

- **Asserts:** no production type assigns `ITenantContext.TenantId` or `OrganizationId` from a bound `X-Tenant-Id` / `X-Organization-Id` value, in any deployment mode. There is no mode-guarded exception.
- **Source:** ADR-0036 § What the assertions do.
- **Type:** xUnit source scan over `LearnStack.Api`. **Kind:** structural.
- **Status:** **Implemented** (`TenancyConventionTests`). A scan rather than a dependency check, because the resolver that could misuse these values lands in Packet 7 — a scan holds the line from the day the symbol exists.
- **Phase:** 02a (Packet 4).

#### `Effective_Host_Computed_In_One_Place`

- **Asserts:** only `EffectiveHostAccessor` reads a request host. Bans `HttpRequest.Host`, `RequestHeaders.Host`, `HeaderDictionary` indexers carrying a `Host` / `X-Forwarded-Host` / `X-LearnStack-Host` / `Forwarded` literal, and `UriHelper.GetDisplayUrl` / `GetEncodedUrl` everywhere else.
- **Source:** ADR-0036 § Effective host and the trusted hop.
- **Type:** xUnit source scan over `LearnStack.Api`. **Kind:** structural.
- **Status:** **Implemented** (`TenancyConventionTests`). Outside `EffectiveHostAccessor` it bans a host read on any receiver — `request.Host` as well as `context.Request.Host` — plus `Headers.Host`, `Headers["Host"]`, `Headers.TryGetValue("Host"`, `HeaderNames.Host`, `GetTypedHeaders`, `GetDisplayUrl`, `GetEncodedUrl`, `X-Forwarded-Host`, `HeaderNames.XForwardedHost`, the `"Forwarded"` literal, `HeaderNames.Forwarded`, `X-LearnStack-Host` and `TrustedHopOptions.HostHeaderName` — the last two declared, and so exempt, in `TrustedHopOptions`. Packet 10 added the header collection's routes to `Host` and the `Forwarded` header, which this entry named and the scan did not read; its review round added the receiver's own spelling and the `TryGetValue` form, and made the rule run the matcher its companion feeds rather than a copy of it. `The_Host_Read_Scan_Can_Actually_Fail` is that companion, and it also feeds `builder.Host.UseSerilog`, which is not a request host. The second round made the header spellings case-blind, as a header name is — `Headers["host"]` reads the same header — and made `GetTypedHeaders` a ban on what is read *from* it: the helper also carries `IfMatch` and `Range`, and refusing the call would refuse an ETag read with a message about trusted hops. Mutation-checked: a `request.Host.Value` read in `TenantResolverMiddleware` fails it, and so does `Headers["host"]`.
- **Phase:** 02a (Packet 4).
- **Note:** a source scan rather than NetArchTest: most of the banned inputs are header names that appear only as string literals inside header lookups, which a type-reference scan cannot see.

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
- **Phase:** 02a (Packet 4).

#### `Trusted_Hop_Requires_Network_And_Secret`

- **Asserts:** the trusted-hop predicate is false unless **both** the socket peer is inside `Tenancy:TrustedHop:Networks` **and** a fixed-time secret comparison succeeds. Neither condition alone admits the hop. Also covers what an untrusted request does with the host header — ignored entirely, so a scanner learns nothing — and that a repeated header is ignored even over the hop.
- **Source:** ADR-0036 § Effective host and the trusted hop.
- **Type:** xUnit behavioural matrix. **Kind:** behavioural.
- **Status:** **Implemented** (`EffectiveHostAccessorTests`). Verified by mutation: dropping the network half leaves eleven of thirteen cases green, and the two that fail are the two that exist for it.
- **Phase:** 02a (Packet 4).

#### `Trusted_Hop_Reads_The_Socket_Peer`

- **Asserts:** the network check reads `IHttpConnectionFeature.RemoteIpAddress`, never `HttpContext.Connection.RemoteIpAddress`.
- **Source:** ADR-0036 § Effective host and the trusted hop.
- **Type:** Roslyn analyzer + xUnit. **Kind:** structural.
- **Status:** **Registered** — and the ADR's stated reason for it does not survive measurement. The two are the **same storage**, and `UseForwardedHeaders` mutates it, so reading the feature rather than the property buys nothing once that middleware runs. What makes the read correct today is `Forwarded_Headers_Are_Not_Wired` above. This rule keeps its place as the thing to implement when forwarded headers land, with the peer captured *before* them.
- **Phase:** unscheduled — no phase in the roadmap wires forwarded headers.
  `Forwarded_Headers_Are_Not_Wired` fails the build on the commit that does, and that
  commit implements this rule.

#### `Deployment_Mode_Is_Required_Configuration`

- **Asserts:** the composition root throws when `Deployment:Mode` is absent, unknown, or given as an ordinal, and the key is **not** present in `appsettings.json`. It shipped there as `Development` — the file that goes to every environment — with the same value as the code default, so every Development-guarded mechanism was on by default in a deployment that never set it. No guard on the *value* could have caught that; only a guard on the file.
- **Source:** ADR-0036 § There is no Development override.
- **Type:** xUnit + configuration-file inspection. **Kind:** startup (value) + structural (file).
- **Status:** **Implemented** in two halves — `DeploymentModeConfigurationTests` for the value, `ApiConventionTests` for the file. Verified by mutation: putting the key back into `appsettings.json` turns the file half red.
- **Phase:** 02a (Packet 4).

#### `Assertion_Recorder_Is_The_Only_Mismatch_Writer`

- **Asserts:** no type other than an `ITenantAssertionRecorder` implementation writes a tenant-assertion mismatch to a log, a metric or `IAuditStore`.
- **Source:** ADR-0036 § Recording a rejected assertion.
- **Type:** xUnit source scan over `LearnStack.Api`. **Kind:** structural.
- **Status:** **Implemented** (`TenancyConventionTests`), in two halves. `Assertion_Recorder_Is_The_Only_Mismatch_Writer` is keyed on the two counter names and scans `LearnStack.Api`; `Assertion_Recorder_Is_The_Only_Writer_Of_Its_Audit_Slugs`, added in Packet 9, is keyed on the two audit slugs and covers the `IAuditStore` clause this row always claimed — the counter names cannot catch a second writer that goes straight to the store, which is the dangerous one, because the row's tenant is what keeps an anonymous caller from choosing whose audit log grows.
- **The second half scans all of `backend/src`, not just the API project**, and that is the correction its own first draft needed: a module is precisely where `IAuditStore` is reachable from a handler, and a narrow scan exempts the dangerous half. Measured — planting the burst slug in `LearnStack.Modules.Tenancy.Application` passed the narrow version and fails the wide one. `TenancyAuditCatalogSource` is exempt because declaring a slug is not writing a row; removing that exemption turns the rule red, which is what makes it a real exemption rather than a decorative one. Its companion, `The_Slug_Scan_Finds_The_Files_It_Exempts`, runs the same scan with no exemptions and requires it to find exactly those two files — with nothing offending, a scan whose exemption logic had broken open would pass as well.
- **Phase:** 02a (Packet 4; the second half, Packet 9).

#### `Assertion_Budget_Does_Not_Depend_On_ICacheService`

- **Asserts:** the anonymous-burst counters resolve no `ICacheService`. A cache outage must not decide whether a MUST-class security event is recorded.
- **Source:** ADR-0036 § Recording a rejected assertion.
- **Type:** xUnit reflection check over the `LearnStack.Api.Tenancy` namespace **and** a source scan over `LearnStack.Api/Tenancy`. **Kind:** structural.
- **Status:** **Implemented** (`TenancyConventionTests`). It shipped in Packet 4 as a **tripwire**, because `ICacheService` did not exist yet; Packet 5 ships the port, so the rule now carries the dependency check it was always meant to be. Both forms are kept: reflection catches an injected dependency, the scan catches a service-locator resolve, and neither sees the other's case.
- **Phase:** 02a (Packet 4).

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
- **Phase:** 02a (Packet 7).

#### `Host_Classification_Applies_To_Tenant_Facing_Routes_Only`

- **Asserts:** host classification runs for `/api/v1/*` and for no other prefix. `/healthz`, `/readyz`, `/openapi/*`, `/admin/hangfire*` and `/api/internal/*` are asserted as a **prefix list**, not as endpoint literals — a closed allow-list written as literals 404s the entire Hub contract surface. The list's **contents** are pinned as well as its shape: an emptied or shortened list would otherwise start classifying the Hub surface with every case still green.
- **Source:** ADR-0036 § The reconciliation matrix.
- **Type:** xUnit over `HostClassificationMiddleware.ClassifiesPath`. **Kind:** behavioural.
- **Status:** **Implemented** (Packet 7 step 4, `HostClassificationScopeTests`).
- **Phase:** 02a (Packet 7).
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
- **Phase:** 02a (Packet 7).

#### `TenantContext_Is_Instantiated_In_One_File`

- **Asserts:** the literal `new TenantContext(` appears in exactly one file under `backend/src/LearnStack.SharedKernel` — which is every file that can compile the call, since the constructor is `internal` and the assembly has no `InternalsVisibleTo` — `TenantContextFactory.cs`. Comments and whitespace are stripped first, because the files these rules cover argue in prose about the very literal they may not write.
- **Why it matters:** the second instrument `TenantContext_Is_Constructed_Only_By_The_Factory` needs and cannot be. `internal` blocks every other assembly, and nothing but a scan blocks a second caller inside the kernel itself — which would be a second entry point producing a context the matrix never decided.
- **Source:** [ADR-0036 § Rules](../decisions/0036-tenant-resolution-trusted-inputs.md).
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** (`TenantContextConstructionTests`, Packet 7 step 5).
- **Phase:** 02a (Packet 7).

#### `SetTenant_Callers_Are_The_Enumerated_Four`

- **Asserts:** `ITenantContextAccessor.Current` is **written** only by `TenantResolverMiddleware`, `HubCorrelationMiddleware`, the Hangfire `JobActivator`, and the outbox / inbox handler scope. `EnterPlatformAdminScope` is not among them: it opens a second connection and sets no tenant context. Reads are unconstrained.
- **Source:** ADR-0036 § Rules, second bullet, as corrected by its erratum and
  [Amendment 2](../decisions/0036-tenant-resolution-trusted-inputs.md).
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** (`TenantContextConstructionTests`, Packet 7 step 5).
- **Phase:** 02a (Packet 7).
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
- **Phase:** 02a (Packet 7).
- **Note:** vacuous today — nothing streams — and that is the point of landing it now. The shapes are invisible to the ordinary `IRequest<>` filter, so without this rule the first one to arrive would be counted as absent rather than caught.

#### `PublicSurface_Marker_Set_Is_Enumerated`

- **Asserts:** every `[PublicSurface]` request type appears in the enumerated set in [Standards 04 § Public surface](04-api-design.md) with its permitted methods; the default is `GET`/`HEAD` and a mutating entry states why. No `[PublicSurface]` type performs a tenant-owned write.
- **Source:** ADR-0036 § The reconciliation matrix;
  [Standards 04 § Public surface](04-api-design.md).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented** (`RequestSurfaceTests`, Packet 7 step 6).
- **Phase:** 02a (Packet 7).
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
- **Type:** xUnit + reflection, cross-checked against the merged audit catalogue. **Kind:** structural.
- **Status:** **Implemented** (`RequestSurfaceTests`) — set-emptiness from Packet 7 step 6,
  and the cross-check against `IAuditCatalog` since Packet 9's external-review round: every
  `[PublicSurface]` request type is looked up in the catalogue the composition roots build,
  and one registered MUST-class `read-sensitive` fails. The marked set is empty until
  [Phase 02d](../roadmap/phase-02d-walking-skeleton.md), so the companion
  `The_PublicSurface_Cross_Check_Can_Actually_Fail` runs the predicate over a marked probe
  registered that way; inverting the predicate fails it.
- **Phase:** 02a (Packet 7; the cross-check leg, Packet 9).
- **Note:** the leg was catalogued at Packet 7 against a catalogue that did not yet exist,
  and shipped as set-emptiness so that a marked type arriving first would force the
  question. Packet 9 shipped the catalogue and, until its review round, not the leg — the
  row said Implemented for a check it did not make.

#### `Organizations_Are_Read_By_Composite_Key`

- **Asserts:** `IOrganizationScopeValidator` and every organization read resolve by the composite key `(tenant_id, id)`, never by `id` alone. `pk_organizations` is the surrogate id, so a lookup by it is a well-formed, index-served query that returns another tenant's row — for the policy to hide if the announcement was made, and to hand back if it was not. Two legs: the raw-SQL leg pins the validator's `WHERE` clause and its `set_config` announcement (scanned, because a command's text is a string literal no type-reference test can see), and the EF leg bans `Organizations.Find`/`FindAsync`, which take the primary key and therefore cannot express the composite one. **The EF leg is vacuous today** and deliberately kept: Packet 7 step 9 shipped the first command, and nothing calls `Organizations.Find` or `FindAsync`: the one `DbContext` read of the table, the seeder's ownership check, filters by slug under the tenant's announcement. A scan added only once there is something to catch is a scan nobody adds. The runtime suite cannot substitute for either leg — with the announcement made, the policy makes both spellings behave identically, which is defence in depth working and is exactly why the rule has to be structural.
- **Source:** ADR-0036 § The reconciliation matrix.
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** (`TenantContextConstructionTests`, Packet 7 step 5).
- **Phase:** 02a (Packet 7).

#### `Tenant_Scope_Widening_Is_Never_Set_From_Request_Input`

- **Asserts:** `app.scope = 'tenant'` is derived from the actor's role plus a declared tenant-wide operation, never from a header, query parameter, cookie or body, and is unreachable under `TenantContextOrigin.HostOnly`. Until Phase 03 derives it from a role, that holds as its strongest form: no production code sets the variable — no `set_config('app.scope'`, no `SET [LOCAL|SESSION] app.scope` — outside an exact-path list, empty until the Phase 03 setter adds its own path, and `ITenantContext` has no scope member for request input to reach.
- **Source:** ADR-0036 § The reconciliation matrix.
- **Type:** xUnit + source scan + reflection. **Kind:** structural.
- **Status:** **Implemented** — `TenancyConventionTests.cs`, Packet 10. It asserts its
  premise first — a row-security policy does read `app.scope`, so the variable guards
  something — and its companion, `The_Scope_Setter_Scan_Can_Actually_Fail`, feeds the
  pattern every setter spelling and a policy's read, which it must not flag.
  Mutation-checked: a planted `set_config('app.scope', …)` in `FeatureFlags` fails it.
- **Phase:** 02a (Packet 7 registers it; Packet 10 implements it).
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
- **Why it matters:** it pins the complement of two closed sets, and getting either wrong reopens a set an ADR closed. `PlatformAdminScope` is **not** a fifth writer of `ITenantContextAccessor.Current` — [ADR-0036 § Rules](../decisions/0036-tenant-resolution-trusted-inputs.md) names it as explicitly not one, and `SetTenant_Callers_Are_The_Enumerated_Four` covers that globally. It is **not** a ninth out-of-band setter of `app.tenant_id` either: the role bypasses policies, so there is nothing to announce to, and [ADR-0040 Amendments 3 and 7](../decisions/0040-ambient-unit-of-work.md) close that set at eight on the property that every one of them connects as `learnstack_app`. And it must not enlist on the ambient unit of work, which would put the bypass on the request's own connection and leave it there.
- **Source:** ADR-0003; ADR-0036 § Rules; ADR-0040 Amendments 3 and 7.
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** (`PlatformAdminScopeConventionTests`, Packet 7 step 7).
- **Phase:** 02a (Packet 7).

#### `PlatformAdminScope_Entry_Requires_Platform_Permission`

- **Asserts:** `EnterPlatformAdminScope(reason)` cannot open without an authenticated principal holding a Platform-scope permission, and no handler carries both `[AllowsUnresolvedTenantContext]` and a platform-scope entry.
- **Source:** ADR-0036 § The platform-admin override is not a resolution source.
- **Type:** xUnit — reflection, a source scan, and a call to the registered gate.
  **Kind:** structural (the marker and the scan) + behavioural (the gate refuses).
- **Status:** **Implemented** (`PlatformAdminScopeConventionTests`, Packet 7 step 7) — conjunct A only.
- **Phase:** 02a (Packet 7).
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
  this packet ships. Packet 9 gives the scope its first real work and inherits the gate:
  the platform-scope `security-event` row the scope writes on entry through
  `IAuditStore.WritePlatformScopeAsync`
  ([ADR-0044 § 10](../decisions/0044-audit-write-path.md)). The killswitch toggle is not
  that caller. Packet 9 ships `platform_killswitches`, its policies, the overlay and the
  cache family and **no writer at all**: a toggle command would sit behind
  `DenyAllPlatformAdminGate` carrying a permission key nothing registers, so
  [Phase 03](../roadmap/phase-03-identity-admin.md) owns the command, its permission and
  its runbook
  ([ADR-0045 Amendment 1 § 4](../decisions/0045-entitlement-and-feature-flag-socket.md)).
  Neither is the GDPR redaction handler, which lands in Phase 03 with the `users` table
  it needs.

  *Marker clause, still vacuous — but for a narrower reason since Packet 7 step 9:* no
  handler carries both `[AllowsUnresolvedTenantContext]` and a platform-scope entry.
  `ProvisionTenantCommand` now carries the first, and nothing carries the second, so the
  conjunction is empty because one half of it is — not because both are.

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
