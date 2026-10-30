# 21 — Architecture Tests + Analyzers Catalogue

**Status:** Active
**Derives from:** [ADR-0070 Owned Tenant BFF Sessions](../decisions/0070-owned-tenant-bff-sessions.md),
[ADR-0032 Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md)
(ships the first batch of catalogue entries),
[ADR-0054 Bounded Public Renderer Admission](../decisions/0054-bounded-public-renderer-admission.md)
(accepted replacement proofs implemented),
[ADR-0027 Frontend UI Localization](../decisions/0027-frontend-i18n.md)
(P02d-6 proofs implemented; manual smoke recorded),
[ADR-0055 Public Renderer Bootstrap Failures](../decisions/0055-public-renderer-bootstrap-failures.md)
(replacement admission proofs implemented; both review rounds complete),
[ADR-0056 Public Demo Browser and Lighthouse Enforcement](../decisions/0056-public-demo-browser-and-lighthouse-enforcement.md)
(accepted 2026-10-11; browser/audit replacement proofs implemented),
[ADR-0057 Transactional Event Production and Completion](../decisions/0057-transactional-event-production-and-completion.md)
(producer lifecycle, payload and production-census proofs implemented),
[ADR-0046 Durable Event Delivery and Recovery](../decisions/0046-durable-event-delivery-and-recovery.md)
(P02b-2 transition, terminal-witness and hosted runtime proofs implemented;
all four steps completed both independent review rounds; packet complete and merged;
the [merge closeout](../roadmap/phase-02b-delivery-log.md#p02b-2-merge-and-closeout-2026-10-13)
owns CI/merge status; P02b-3 Steps 1–6 implement consumer runtime, defaults and
production-census enforcement; its
[Step 6 record](../roadmap/phase-02b-delivery-log.md#p02b-3-step-6--production-enforcement-and-pr-closeout-2026-10-13)
owns validation, review and PR closeout status),
[ADR-0047 Transactional Background Job Runtime](../decisions/0047-transactional-background-job-runtime.md)
(Accepted 2026-10-15; P02b-4 obligations implemented and locally qualified;
the [Step 9 record](../roadmap/phase-02b-delivery-log.md#p02b-4-step-9--enforcement-and-packet-closeout-2026-10-18)
owns review, packet and exact PR-head CI/job-budget status),
[ADR-0058 Bounded Stock Hangfire Maintenance](../decisions/0058-bounded-stock-hangfire-maintenance.md)
(Accepted 2026-10-16; maintenance extensions and runtime proofs implemented),
[ADR-0059 Owned Hangfire Server Generations](../decisions/0059-owned-hangfire-server-generations.md)
(Accepted 2026-10-16; lifecycle qualification implemented),
[ADR-0060 Fatal Hangfire Startup and Unsafe Unwind](../decisions/0060-fatal-hangfire-startup-and-unsafe-unwind.md)
(Accepted 2026-10-16; exceptional boundary and runtime proofs implemented),
[ADR-0061 Known External Job Failure Policy](../decisions/0061-known-external-job-failure-policy.md)
(Accepted 2026-10-17; Step 7 qualified and Step 9 enforcement implemented),
[ADR-0062 Native Crash Proof CI Provisioning](../decisions/0062-native-crash-proof-ci-provisioning.md)
(Accepted 2026-10-18; native execution authority implemented),
[ADR-0063 Complete Bounded CI Test Shards](../decisions/0063-complete-bounded-ci-test-shards.md)
(Accepted 2026-10-18; general CI obligation implemented; the phase record owns
final-source hosted qualification),
[ADR-0075 Ordinary CI Job Window](../decisions/0075-ordinary-ci-job-window.md)
(Accepted 2026-10-27; the ordinary-budget extension below is implemented with
offline controls and source-bound hosted A/B; the phase owns evidence review),
[ADR-0064 Tenant API Authentication Boundary](../decisions/0064-tenant-api-authentication-boundary.md)
(Accepted 2026-10-19; P02b-5 obligations implemented and merged through PR #33;
the [merge closeout](../roadmap/phase-02b-delivery-log.md#p02b-5-merge-and-closeout-2026-10-20)
owns final-source hosted qualification),
[ADR-0065 Bounded Tenant Assertion Diagnostics](../decisions/0065-bounded-tenant-assertion-diagnostics.md)
(Accepted 2026-10-20; extends existing obligations below; audit-budget and
telemetry controls implemented; the
[P02b-5 merge closeout](../roadmap/phase-02b-delivery-log.md#p02b-5-merge-and-closeout-2026-10-20)
owns current qualification status),
[ADR-0066 Bounded Tenant Subject Admission](../decisions/0066-bounded-tenant-subject-admission.md)
(Accepted 2026-10-20; extends the existing subject-admission obligation;
the [delivery record](../roadmap/phase-02b-delivery-log.md#adr-0066-acceptance-and-implementation-2026-10-20)
owns source-bound validation, reviews and hosted qualification),
[ADR-0067 Development Tenant Access-Token Issuance](../decisions/0067-development-tenant-access-token-issuance.md)
(Accepted 2026-10-21; formalizes the existing issuance obligation's authority
without adding a rule or changing its implementation status).
Identity foundation registration additionally derives from Accepted
[ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md),
[ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md) and
[ADR-0074](../decisions/0074-membership-admission-under-ordered-locks.md).
Accepted live-authorization extensions additionally derive from
[ADR-0076](../decisions/0076-live-scoped-authorization.md) and
[ADR-0077](../decisions/0077-trusted-authorization-evidence.md).
Accepted fixed-seed prerequisite extensions derive from
[ADR-0080](../decisions/0080-fixed-seed-prerequisite-read-scope.md); their
separate obligations are tracked below.
Accepted fixed-origin completion extensions derive from
[ADR-0081](../decisions/0081-fixed-seed-completion-before-origin-adoption.md);
its separate obligations are tracked below.
The implemented Identity obligations and extensions name their actual proof
owners below; the [phase Status block](../roadmap/phase-03-identity-admin.md) owns
current qualification and review state.
The catalogue grows as subsequent ADRs and phases land their tests; per-test ownership
stays with the originating ADR / standard.

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

**329 test methods run in
[`backend/tests/LearnStack.Tests.Architecture`](../../backend/tests/LearnStack.Tests.Architecture),**
shipped by [Phase 01](../roadmap/phase-01-repository-tooling.md),
[Phase 02a Packets 2–3](../roadmap/phase-02a-kernel-tenancy.md), Packet 4, Packet 6, Packet 7,
Packet 8, Packet 9, Packet 10, P02d-1, P02d-2, P02d-3, P02d-4 Steps 1–3, P02b-1,
P02b-2 Steps 2–4, P02b-3 Steps 3–6 and P02b-4 Steps 1–8, with further
source-boundary, observation and production-manifest companions in Step 9.
Its nineteen packet obligations are implemented with actual canonical owners and
complementary structural, native-runtime and PostgreSQL controls. The
[P02b-4 merge closeout](../roadmap/phase-02b-delivery-log.md#p02b-4-merge-and-closeout-2026-10-19)
owns its final review/CI qualification. Authentication, realm and BFF obligations
have unit/integration/frontend/offline owners in their entries below; the
[Phase 02b Status block](../roadmap/phase-02b-events-auth.md) links current delivery
and qualification. The analyzer and asynchronous inventory controls also have
actual owners; registration alone promotes none of these obligations.
Methods are not rows:
a `[Theory]` is one row and many cases,
and most rows pair a rule with the companion assertion that stops it passing vacuously.

**Every number in this section is recomputed by `The_Catalogue_Counts_Its_Own_Rules`.** They
are a second copy of what the repository already knows, and the first version of this section
proved the point by being wrong on arrival: it published "ninety-five in that assembly" in the
same commit that added three more. The method count uses **compiled methods**
carrying `[Fact]`/`[Theory]`, discovered by reflection.
Attributes written as fixture strings and declarations removed by conditional
compilation do not count; an actual `[Fact(DisplayName = …)]` method does.

**The rows and the code are held together by a test, not by a table.** This section used to
carry a hand-written list of rule → file, and it went stale the way any second copy does — on
the day it was deleted it named ninety-four methods and the suite declared a hundred and
thirty-one.
`Every_Implemented_Rule_Names_A_Test_That_Exists` replaces it: every entry below whose
**Status** reports *Implemented* in a file of that assembly must be a test method of exactly
that name, or the build fails. It reads all three spellings the entries
actually use — the bare class, the file with its extension, and `Class.Method`, which is how
the entries outside this assembly name their test — because matching only the file spelling was
measured at **38** of the 95 entries on the day it was written, and a guard that silently checks
two fifths of its subject is the defect this section is about. It also refuses an entry naming a
test class that exists nowhere, because otherwise a renamed or deleted file drops its entries
out of the subject instead of failing.

**254 rules in this catalogue are Implemented, and 149 of them are in that assembly.**
The other 105 are no less binding, and most could not live there. The table says where
and why, and deliberately carries no per-row count: those are the numbers nothing
recomputes, and the first version of this table claimed "three rules" for a suite
that holds ten.

| Where | Why not the architecture assembly |
|---|---|
| `LearnStack.Tests.Integration`, against an applied schema | A policy that is well-formed and wrong, a foreign key with no index, a row that did or did not commit with the business write, and a request that reached a route are only visible against a real PostgreSQL and a real host. |
| `LearnStack.Tests.Unit` | What one type does when exercised: `ValidationBehavior` returning rather than throwing, an idempotency sweep racing a claim, a malformed `If-Match` failing rather than counting as absent, the span processor surviving a missing context, a soft delete advancing the row version. A reflection scan sees none of it. |
| `backend/analyzers/LearnStack.Analyzers` | `LearnStackException-DomainExceptionThrow` is the analyzer itself; the architecture assembly runs it over module source in `Domain_Methods_Do_Not_Throw_For_Expected_Cases`. |
| `frontend/` | `Only_SanitizedHtmlPrimitive_Uses_DangerouslySetInnerHtml` is an ESLint rule, with a Vitest case that lints fixtures through the real configuration so a preset that stops applying it fails rather than falls silent. |

The architecture assembly and Docker-free integration cases run in the `backend` CI check;
`Requires=Docker` integration cases run in `backend integration (Testcontainers)`.
A rule belongs where it can actually fail: the route-shape rule was
originally written as a reflection scan in the architecture assembly and passed against a host
serving unversioned endpoints.

`Meta_NetArchTest_DetectsAPlantedViolation` deserves its own note: it plants a forbidden
dependency and asserts NetArchTest **finds** it, and it asserts that every type a production
assembly declares reaches NetArchTest's list at all. If either half ever passes in the inverted
sense, every other NetArchTest-based row in this catalogue is vacuously green. Keep it in
perpetuity.

Every rule in this document carries its own **Status** line, and that line is the authority.

## Canonical names and superseded spellings

One rule, one identifier. When a document, skill, or comment uses a superseded spelling,
the fix is to replace it with the canonical name — not to add a row here.

### Terminal audit witness rules

`Outbox_Terminal_Audit_Identity_Is_Row_Bound` and
`Delivery_Terminal_Audit_Identity_Is_Row_Bound` share the `TerminalOffenders`
architecture detector because both use the same restricted witness/writer seam.
Distinct producer and subscriber planted controls and real-row integration
proofs enforce their respective identities; these remain two catalogue rules.

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
  read rather than skipped, and fails loudly on an unparseable source. The wiring
  leg evaluates actual MSBuild imports and analyzer-reference metadata rather than
  assuming each project spells out its own reference. It also verifies the
  compiler-visible `LS0001` scope flag; shared assembly wiring for `LS0002` must
  not broaden the older rule. Its companion,
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
  `backend/analyzers/LearnStack.Analyzers`. The unconditional reference in
  `backend/src/Directory.Build.props` loads it as an in-tree analyzer, with
  `ReferenceOutputAssembly="false"`; the compiler-visible
  `LearnStackAnalyzeDomainExceptionThrow` property preserves its exact
  Domain/Application scope. The same reference loads
  [LS0002](#learnstackexception-sensitiveexceptionmessage-roslyn-analyzer) in all
  production projects. Do not duplicate per-project references.
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

#### `LearnStackException-SensitiveExceptionMessage` (Roslyn analyzer)

- **Rule name:** `LearnStackException-SensitiveExceptionMessage`.
- **Diagnostic id:** `LS0002`.
- **Asserts:** direct thrown Exception construction is checked for sensitive
  names in direct string references, interpolation and string concatenation,
  within the bounded semantic contract in ADR-0032 Amendment 6. All production
  C# projects under `backend/src` are wired automatically, while `LS0001` keeps
  its Domain/Application scope. The runtime and analyzer use one internal
  source for the complete single/two-word token union and word matcher.
- **Severity:** Warning, including CI; `WarningsNotAsErrors` preserves this
  posture until Phase 03 exit, when both diagnostics become Errors.
- **Proofs:** compiler-clean positive/negative cases, exact ID/location/severity
  and no `AD0001`; independent union and two-word-only mutation; evaluated
  MSBuild/new-project wiring and unchanged `LS0001` scope; a real `CI=true`
  Warning build and an unrelated-warning failure control. No taint analysis or
  JavaScript/TypeScript coverage is claimed.
- **Source:** [ADR-0032 Amendment 6](../decisions/0032-exception-handling-logging-and-observability.md#amendment-6--sensitive-exception-message-diagnostic-2026-10-25).
- **Kind:** compile-time.
- **Status:** **Implemented** — analyzer, `SensitiveExceptionMessageAnalyzerTests`
  and `AnalyzerBuildTests` in `LearnStack.Tests.Unit`.
- **Supporting architecture proofs:**
  `SensitiveExceptionMessageTests` runs
  `Production_Exception_Messages_Do_Not_Contain_Sensitive_References` for both
  production configurations, including suppressed reports;
  `The_Sensitive_Report_Sees_Suppressed_And_Conditional_Violations` plants them.
  `AnalyzerWiringTests` checks evaluated settings and unconditional reference
  ancestry, with OS/CI/Choose and editorconfig/targets suppression controls.
  These are supporting proofs of this rule, not additional catalogue rules.
- **Phase:** 02b (P02b-8).

#### `Durable_Async_Store_Inventory_Covers_The_Storage_Census`

- **Asserts:** the canonical erasure inventory covers the actual event/job
  storage census and all guarded vendor tables, including explicit control-only
  classifications. Added tables, missing inventory rows and a removed row are
  discriminated by planted controls. This is a coverage check, not a proof of
  field-level erasure, legal compliance or personal-data absence.
- **Source:** ADR-0046/0047/0057 and
  [Data Protection inventory](../architecture/23-data-protection.md#durable-asynchronous-store-inventory),
  accepted through [P02b-8](../roadmap/phase-02b-delivery-log.md#p02b-8-decision-package-2026-10-25).
- **Type:** storage-definition and documentation census with dirty controls.
- **Kind:** structural.
- **Status:** **Implemented** — `AsyncStoreInventoryTests` reads the ordered,
  compiled `UpOperations` for Platform, Audit, Tenancy, Customization and Education,
  including expanded vendor installer SQL. It reconciles every table with the
  canonical inventory or an explicit synchronous/module exclusion. Opaque
  procedural DDL, stored projections and unsupported schema/path changes fail
  closed with their origin; planted controls assert the intended reason.
  Vendor rows independently match
  `HangfireStorageContract.Columns`. Added/dropped/renamed storage, every removed
  row, duplicate/malformed tables and blank vendor classifications fail the
  controls. Audit/provider/diagnostic/backup/volatile custody entries are required;
  the independent live PostgreSQL `r`/`p`/`m` relation census remains a separate
  backstop, with `StorageRelationCensusTests` checking each kind. Exact vendor
  column shape remains the independent `HangfireSchemaShape.Verify` runtime proof.
- **Phase:** 02b (P02b-8).

#### `Handlers_Return_Result`

- **Asserts:** every `IRequestHandler<TRequest, TResponse>` implementation in any
  `LearnStack.*` project under `backend/src` (derived from the tree, not listed) has
  `TResponse : IResultBase`. Every pipeline behavior is constrained
  `where TResponse : IResultBase`, so a handler returning a raw DTO runs with none of
  them: no validation, no audit classification, no authority ceiling and no transaction.
  A module `DbContext` it resolves is refused outside the ambient transaction, so its EF
  access fails loudly. SQL it issues on `IUnitOfWork.Connection` carries no tenant
  announcement and reads zero rows.
- **What it does not catch:** code that never dispatches a request: a controller that
  queries, or that opens the unit of work and announces a tenant itself. No rule in this
  catalogue constrains what a controller depends on.
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

#### `Descriptors_preserve_array_order_and_canonical_localized_fallback`

- **Asserts:** the ADR-0051 text-card profile preserves descriptor order and canonical
  localized labels. Companion cases refuse malformed/duplicate/missing descriptors,
  invalid labels, unsupported shapes/composites and keywords with bounded JSON Pointers;
  schemas without root `x-fields` retain their original admission.
- **Source:** [ADR-0051](../decisions/0051-ordered-text-card-presentation.md).
- **Type:** xUnit, exercising admission and semantic resolution. **Kind:** behavioural.
- **Status:** **Implemented** (P02d-2 Step 1, `TextCardPresentationTests`).
- **Phase:** 02d (P02d-2).

#### `Exact_reads_distinguish_new_bindings_from_pins_and_preserve_stored_descriptor_order`

- **Asserts:** exact revision reads preserve authored order through JSONB and use
  their own stored definition; seed verification rejects changed schemas/labels under
  an existing pin. New bindings and existing pins have distinct eligible states.
- **Source:** [ADR-0043](../decisions/0043-customization-payload-validation.md);
  [ADR-0051](../decisions/0051-ordered-text-card-presentation.md).
- **Type:** xUnit + PostgreSQL exact reads and seed execution. **Kind:** behavioural.
- **Status:** **Implemented** (P02d-2, `P02d2FoundationTests` and `SeederTests`).
- **Phase:** 02d (P02d-2).

#### `Text_Card_Rendering_Preserves_Order_And_Uses_Text_Sinks`

- **Asserts:** the P02d-6 renderer preserves authored order/labels, treats
  unsafe-looking strings as text, and keeps unsupported data out of active sinks.
  Public descriptors are projected only after P02d-4 content eligibility.
- **Source:** [ADR-0051](../decisions/0051-ordered-text-card-presentation.md).
- **Type:** frontend component and public-boundary tests. **Kind:** behavioural.
- **Status:** **Implemented** — P02d-6 Step 3, `PublicServerRenderingTests`,
  `frontend/apps/web/src/components/public/lesson.test.tsx` and the unchanged
  product-route presentation checks in `scripts/verify-public-rendering.mjs`.
  P02d-4 supplies eligible descriptors; Step 3 proves ordered escaped text,
  unsupported fallbacks and exact schema changes through the production renderer.
- **Phase:** 02d (P02d-4/P02d-6).

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
  audit catalogue and the key registries, plus a file scan of every app and package
  under `frontend/`. **Kind:** structural.
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

#### `Production_Code_Does_Not_Branch_On_Demo_Tenant_Literals`

- **Asserts:** production code does not specialize behavior by demo tenant identity.
  The guard reads identity literals from the authoritative `SeedData` declaration,
  not a second hard-coded list. Its source reader must observe the declared values
  and fail on an incomplete or unreadable declaration rather than report clean.
- **Source:** [Phase 02d G20](../roadmap/phase-02d-delivery-log.md#p02d-2-accepted-answers);
  [ADR-0079](../decisions/0079-bounded-identity-administration.md) and
  [ADR-0081](../decisions/0081-fixed-seed-completion-before-origin-adoption.md)
  select the closed fixed-origin security data.
- **Scope:** P02d-2 accepted G20(a)'s source;
  [P02d-5](../roadmap/phase-02d-delivery-log.md#p02d-5-accepted-answers)
  accepted bounded seed/development/test exemptions, and
  [P02d-6](../roadmap/phase-02d-delivery-log.md#p02d-6-accepted-answers)
  accepted production frontend TS/JS and JSON subjects, including renderer and
  message values. Matching/built-in detail, the Seeder-reference fence and
  behavioral ownership are in the
  [P7 package](../roadmap/phase-02d-delivery-log.md#p02d-7-genericity-enforcement),
  approved on 2026-10-11; Step 2 implements the exact-literal/reference fences.
  ADR-0056's separate
  browser/Lighthouse measurement decision and its implemented controls complement
  this source fence; neither proves all dynamically assembled tenant comparisons.
- **Type:** Roslyn C#/embedded-JSON scan, TypeScript AST/JSON scan, compiled and
  restore-resolved project references, with planted offenders and clean controls.
  **Kind:** structural.
- **Status:** **Implemented** — `DomainGenericityTests.cs` (partial source in
  `DomainGenericityTests.DemoIdentities.cs`) and frontend
  `demo-genericity.test.ts`/`demo-genericity-analysis.ts`. `DemoIdentities.Create`
  projects categorized values from typed `SeedData`; the architecture suite
  verifies category coverage and exact frontend fixture drift. Canonical built-ins
  come from their owning declarations. Production census includes Seeder
  orchestration, frontend packages/messages/configuration/native code; only the
  exact seed declaration and named test/development data are exempt. The
  fixed-origin declarations remain inside that census: only the six literal
  nodes of the two exact immutable `SeedAdministrationOriginManifest` records
  receive bounded data admission. Their IDs/slugs and derived management hosts
  match `SeedData`, with an independent fixed-pin test; exact compiled All/Find
  callers are enumerated. Extra/computed records, executable branches, copied
  management-host literals, aliases and unclassified conditional variants refuse.
  This extension is qualified under the separately registered
  [completion rule](#fixed_seed_origin_adoption_requires_completed_foundation).
  Complete
  decoded values match ordinally; GUIDs match by value. C# conditional symbol
  configurations are parsed independently to a fixed point (at most eight symbols;
  excessive or unreadable configurations fail). UTF-8 literals and emitted JSX
  text/attribute values are included. Frontend
  runtime imports cannot hide helpers behind test filenames or directories;
  exact standalone proof/development entries retain their bounded exemptions.
  Generated/dependency roots are exact paths. Known environment/OS/editor/log
  files are omitted only when Git also ignores them; source and unknown formats
  retain fail-closed admission. Local module URL suffixes,
  supported tsconfig aliases, workspace exports, re-exports, import-equals and
  literal `require.resolve` edges enter the closure. Ambiguous local JS/TS and alias
  targets are conservatively included; only owned workspace manifests select
  workspace exports, and duplicate identities refuse. Missing/unsupported local
  edges and unclassified repository roots/source formats fail. Installed external
  packages are resolved under the known dependency roots; local/link targets
  enter the census or refuse. Native `.server` edges require the actual pinned
  TypeScript output mapping; the known stylesheet is an explicit inert asset.
  Backend `DomainGenericityTests.SourceCensus.cs` cross-checks the source set with
  Release MSBuild-evaluated `Compile` inputs, `ProjectReference` paths and actual
  output/intermediate roots.
  Nested `bin`/`obj` remain authored source. Linked inputs outside the census and
  unmodeled build properties, custom targets/imports/SDKs and references outside
  the admitted production project set refuse rather than silently omit code.
  Dirty/clean controls cover these admission boundaries. Computed runtime imports
  fail rather than omit a helper.
  The rule does not claim
  to prove dynamically assembled identities.
  `Production_Projects_Do_Not_Reference_The_Seeder`
  reads every non-Seeder production assembly and restore graph. Its compiled
  test edge and restored fixture are deliberate forbidden-reference controls.
  The existing real API/PostgreSQL presentation proof retains original pins and
  the unaffected tenant; `orderedLessonFields` is its shared behavioral predicate
  with planted label/value/order/active-markup/language controls. Browser
  observation of these fields remains P7 Step 3.
- **Phase:** 02d (P02d-2 source; P02d-5/6 scope; P02d-7 implementation exit).

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

#### `Standard_Status_Headers_Match_The_Index`

- **Asserts:** each of the twenty-two standards declares the same status in its own
  header as [the index](README.md) assigns it in the table, and the sentence that counts
  the split matches the table it summarises. The index owns the current counts.
- **Why it matters:** these are two views of one claim, *what is actually enforced*, and
  they disagreed for a month: every document declared `Active` while the index classified
  eight of them `Adopted`, with the index carrying a paragraph saying so and asking the
  reader to prefer it. The header is what an author sees, the table what a reviewer reads,
  and a promotion that lands in one and not the other leaves the corpus asserting both.
  Counting them in words is a third copy, so it is checked too.
- **Source:** [README.md § Honest status today](README.md),
  [13-documentation.md](13-documentation.md).
- **Type:** xUnit + a parse of the headers, the table and the summary sentence.
  **Kind:** structural.
- **Status:** **Implemented** — `CorpusConsistencyTests.cs`, Packet 10. Its companion,
  `The_Corpus_Guards_Can_Actually_Fail`, feeds each parser the shapes it must read and the
  ones it must not.
- **Phase:** 02a (Packet 10).

#### `Every_Implemented_Rule_Names_A_Test_That_Exists`

- **Asserts:** every entry in this catalogue whose **Status** reports *Implemented* in a
  file of the architecture assembly is a test method of exactly that name; and no entry
  names a test class that exists nowhere in the repository. All three spellings the entries
  use count — the bare class, the file with its extension, and `Class.Method` — and the rule
  pins its own reach to the count § Implemented today publishes, because a matcher that
  recognised only the file spelling covered 38 of the ninety-five, one that could not read
  `Class.Method` left three shipped rules with nothing to resolve, and an entry whose class
  stops resolving leaves the subject instead of failing.
- **Why it matters:** the **Status** line is the authority on whether a rule runs, and it
  is prose. A renamed method, a moved file or a rule quietly deleted leaves the entry
  claiming the opposite of the truth — worse than a rule that was never written, because a
  reader stops looking. This replaces the hand-written rule → file table that used to live
  in § Implemented today, which on the day it was deleted named ninety-four methods while
  the suite declared a hundred and thirty-one.
- **Source:** [§ Naming convention](#naming-convention), this document's own Status lines.
- **Type:** xUnit + a parse of this file against the suite's method names. **Kind:**
  structural.
- **Status:** **Implemented** — `CorpusConsistencyTests.cs`, Packet 10.
- **Phase:** 02a (Packet 10).

#### `No_Architecture_Test_Is_Skippable`

- **Asserts:** no test in the architecture assembly carries a `Skip` on its `[Fact]` or
  `[Theory]`, and — at the runner, where the rule itself cannot see — no backend suite's
  `.trx` reports a case that did not run. P02d-5 G38(d) extends the runner to
  frontend workspaces: `scripts/run-frontend-tests.mjs` discovers test packages and
  reads nonempty Vitest JSON outcomes, refusing failures, skipped/todo cases,
  missing scripts/reports and omitted discovered files. Real clean/planted runs in
  `frontend-runner.test.ts` prove skip/todo, missing-script/report and omission
  refusals; CLI success alone cannot close the rule.
- **Why it matters:** "architecture tests are non-skippable" is a policy the corpus states
  in three places and nothing enforced. Adding `Skip = "…"` is one edit, the suite goes
  green, and it reports the same number of passing files as before — which is precisely
  the situation the policy exists to prevent, since a rule that can be turned off for a
  release is a rule nobody has to satisfy. Two layers, because one cannot be enough. The
  source scan reads each attribute's argument list with a depth counter, over text whose
  comments and literal contents are gone: a pattern that stopped at the first `)` could not
  see a `Skip` written **after** `DisplayName = "(meta) …"`, and the assembly ships exactly
  one such attribute — on the meta-test that certifies every NetArchTest row is not
  vacuous. The current `ci-test-shards.py` / `ci-native-tests.py` execution owners
  and `ci-test-shard-results.py` aggregates require complete discovered case-ID
  unions with every outcome Passed, which catches a `Skip` placed on the scan
  itself; a skipped test does not execute, so it cannot report its own absence. An
  earlier version instead
  exempted this rule's own file, so the companion's fixtures were not read as code — and
  the exemption made the four corpus guards the only tests in the assembly that one edit
  could switch off. Stripping literals removed the reason for it.
- **Source:** [06-testing.md § Architecture tests](06-testing.md).
- **Type:** xUnit + a source scan with comments and literals stripped, plus a `.trx`
  assertion in CI. **Kind:** structural.
- **Status:** **Implemented** — `CorpusConsistencyTests.cs`, Packet 10.
- **Phase:** 02a (Packet 10).

#### `The_Catalogue_Counts_Its_Own_Rules`

- **Asserts:** the four numbers [§ Implemented today](#implemented-today) publishes about
  this catalogue — the test methods the architecture assembly declares, the entries
  reported *Implemented*, how many of those name a class of that assembly, and how many do
  not — are the numbers a recount produces.
- **Why it matters:** § Implemented today deleted a hand-written rule → file table on the
  argument that a second copy goes stale and a test does not, and kept four numbers, which
  are a second copy too. They were wrong on arrival: the commit that published "ninety-five
  in that assembly" added three entries to that assembly in the same diff. Those numbers
  are what a reader uses to judge whether the catalogue is honest about its own coverage,
  so they are the last thing that should be taken on trust.
- **Source:** [§ Implemented today](#implemented-today).
- **Type:** xUnit + a recount of the suite and of this file. **Kind:** structural.
- **Status:** **Implemented** — `CorpusConsistencyTests.cs`, Packet 10.
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

- **Asserts:** the sanitised-HTML primitive is the only component in the frontend that
  calls `dangerouslySetInnerHTML`, and it does so exclusively on the sanitiser's output.
  This is the rule that stops the `embed-html` sanitisation contract being bypassed by a
  convenient one-off.
- **Source:** [32-tenant-customization-model.md § 8.5](../architecture/32-tenant-customization-model.md)
  and its § 11 hard invariants.
- **Type:** ESLint rule in `frontend/`. **Kind:** structural.
- **Status:** **Implemented** — four `no-restricted-syntax` selectors in
  `frontend/packages/config/eslint/index.cjs`, Packet 10, chosen so the rule refuses the ways
  the prop **reaches an element** and leaves alone the ways code inspects or strips it: the JSX
  attribute a component writes; a key in an object being *built*, scoped to `ObjectExpression`
  because ESTree gives a destructuring binding the same node and the unscoped form refused
  `const { dangerouslySetInnerHTML, ...safe } = props`, the idiom that guarantees the prop is
  **not** forwarded; the assignment onto a props object, scoped to the left-hand side because
  the unscoped form refused a read that renders nothing; and the name itself wherever it is
  written as a string, because a selector keyed on an identifier cannot see through
  `const k = 'dangerouslySetInnerHTML'` followed by `{ [k]: … }` — which is not an exotic
  bypass but what a generic field renderer looks like. What is left uncovered is a name
  assembled from fragments at runtime. Packet 10
  also gave `frontend/packages/ui` a configuration and a `lint` script, because `pnpm lint`
  walked only `apps/web` and the package [ADR-0009 § Decision](../decisions/0009-frontend-single-app-first.md)
  sends an extracted primitive to was linted by nothing. Its companion is
  `frontend/apps/web/src/test/lint-rules.test.ts`, which lints each of those spellings through
  the real configuration — the app's and the package's — and asserts both the rule that answers
  and its **severity**, because a rule downgraded to `warn` still reports while `pnpm lint`
  exits 0. `pnpm lint` is green whether a rule is configured or not, and a rule nothing violates
  looks exactly like a rule that is not there. The primitive's own file carries the single
  suppression when [Phase 05](../roadmap/phase-05-education-learning-content.md) ships the
  `embed-html` sanitisation contract; nothing calls the API today, which is why the rule lands
  before the first caller.
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
  module `Domain` assembly, including the two — Content and Media —
  that declare no domain type yet, only an `AssemblyMarker`, so the first `Organization` or `OrganizationBranding` to land
  in one of them fails it. `OrganizationBranding` itself arrives with
  [Phase 06](../roadmap/phase-06-renderer-admin-studio.md)'s branding; until then "exactly
  one, in Tenancy" is satisfied by none, which is what stops the first one landing in the
  wrong module.
- **Phase:** 02a (Packet 6).

#### `Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning`

- **Asserts:** no type implementing `IRequestHandler<,>`, `IRequestHandler<>`,
  `INotificationHandler<>` or `IIntegrationEventHandler<>` can reach more than one
  **distinct aggregate root** through
  its constructor parameters' `IAggregateWriteStore<TRoot, TId>` derivations, except the
  single exact assembly/type identity — Tenancy's `ProvisionTenantCommandHandler`,
  which writes
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
- **Permanent planted backstop:**
  `Cross_Aggregate_Census_Catches_Fused_Separate_Notification_And_Internal_Constructors`
  exercises the same production predicate with fused/two-port request and notification
  handlers, a non-public constructor, and same-root/non-handler negative controls.
  P02b-3 Step 6 adds
  `Cross_Aggregate_Census_Catches_Integration_Handlers_And_Inherited_Contracts` for
  fused/separate integration ports, inherited contracts and a same-simple-name
  integration offender tested independently of the other dirty subjects. The
  actual provisioning type alone is the clean exception control.
- **What it does not catch:** a write routed through a helper that itself holds two
  ports, arbitrary nested `ISender.Send` sequences, or a second `DbContext` reached
  indirectly — the same limit
  [§ What a structural test proves](#what-a-structural-test-proves--and-what-it-does-not)
  states for every structural rule. The binding control is that the allow-list has one
  entry and growing it is a reviewed diff.
- **Mutation-checked.** A second handler taking two write ports, the sanctioned handler
  renamed, and the two ports fused into one — each turns the rule red.

#### `Customization_Projection_Does_Not_Validate_On_Read`

- **Asserts:** the internal display projection never depends on
  `IJsonSchemaValidator`; schema admission/body validation stay on the write path.
  The scanner follows helpers across all production assemblies, including all
  four Customization layers. Its module census and planted validator-dependent
  offenders prevent an empty or narrowed scan from passing silently.
- **Source:** ADR-0043 and [Standards 20's projection cache contract](20-infrastructure-stack.md#icacheservice-state).
- **Type:** xUnit + Mono.Cecil. **Kind:** structural.
- **Status:** **Implemented** (`CustomizationProjectionTests`). The companion
  `Projection_validator_guard_detects_direct_and_helper_dependencies` proves
  interface, concrete-adapter, constraint, catch, attribute, wrapped generic,
  lambda and state-machine dependencies are detected, with a clean negative
  control.
`Projection_validator_guard_follows_domain_contracts_and_external_production_helpers`
  plants dependencies in the production Cecil models and uses the real root
  predicate and helper census to prove Domain, Contracts and core helper coverage.
- **Phase:** 02d (P02d-3).

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

- **Asserts:** the `migrate` recipe visits Tenancy before Audit and Education,
  and Audit before Identity. Education's four organization-immutability triggers
  call Tenancy's shared function (P02d-1); ADR-0072's Identity chain invokes the
  [central creation-witness installer](05-database.md#lock-snapshot-and-adoption-conditions)
  after Audit. The three dependent-first companions exercise and repair the same detector.
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
- **Status:** **Implemented** (Packet 9 step 3; expanded in P02d-1 Step 2 and
  P03-1 Step 3, `LearnStack.Tests.Architecture`, `PersistenceConventionTests`).
  Mutation-checked: deleting the Tenancy prefix fails this case; the Audit-first,
  Education-first and Identity-before-Audit companions each exercise and repair
  the same order detector.
- **Phase:** 02a (Packet 9); expanded in 02d (P02d-1) and 03 (P03-1).

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
  `ON DELETE RESTRICT`, or `ON DELETE CASCADE` for a child inside an aggregate boundary
  ([Database Standards § Foreign keys between tenant-owned tables](05-database.md#foreign-keys-between-tenant-owned-tables)),
  so every parent delete pays the child scan.
- **Source:** [05-database.md § Indexes](05-database.md).
- **Type:** **integration** test (Testcontainers + PostgreSQL), reading
  `pg_constraint` / `pg_index`. **Kind:** structural.
- **Status:** **Implemented** (Packet 6 step 5,
  `LearnStack.Tests.Integration`, `TenancySchemaTests`). It found two real gaps on
  its first run — `fk_organizations_reporting_parent` and
  `fk_platform_host_to_tenant_organization` — which is the evidence that it is not
  vacuous.
- **Phase:** 02a (Packet 6).

#### `Every_TenantOwned_Foreign_Key_Includes_TenantId`

- **Asserts:** every foreign key between tenant-owned tables pairs the child's tenant
  key with the parent's tenant key, in addition to parent identity. The self-keyed
  `tenants` exception is exact: a child references `tenants.id` through its own
  `tenant_id`; `tenants.default_organization_id` still requires the composite
  `(id, default_organization_id) → (tenant_id, id)` key. A single-column parent-id
  reference, or a composite key that does not actually pair the tenant columns,
  fails. Table classification must cover the applied schema, not only a hand-picked
  list of Education tables.
- **Source:** [ADR-0003 Amendment 6](../decisions/0003-tenant-isolation-defense-in-depth.md#amendment-6--insert-scope-and-parent-mirrors-2026-09-14);
  [05-database.md § Foreign keys between tenant-owned tables](05-database.md#foreign-keys-between-tenant-owned-tables).
- **Type:** xUnit + applied PostgreSQL schema catalogue. **Kind:** structural.
- **Status:** **Implemented** (P02d-1 step 1, `LearnStack.Tests.Integration`,
  `OrgScopeSchemaTests`). Companions plant missing and positionally mispaired tenant
  columns, and unsafe replacements in both self-keyed directions, then exercise
  the same detector against the repaired key.
- **Phase:** 02d (P02d-1).

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
- **Status:** **Implemented** (`PersistenceConventionTests`, Packet 9, fifth review).
  Its companion,
  `The_Soft_Delete_Index_Sweep_Can_Actually_Fail`, runs the predicate over a probe context
  carrying one unique index of each shape — counting deleted rows, partial, containing the
  primary key, and on a table with no `deleted_at` — and expects exactly the first.
  Mutation-checked: dropping the `audit_config` filter fails the rule on that index.
- **Phase:** 02a (Packet 9).

#### `Every_Database_Test_Carries_The_Docker_Trait`

- **Asserts:** every test file in `LearnStack.Tests.Integration` that declares a test and
  needs a container — one under `Database/`, or one anywhere that names `SchemaFixture`,
  `PostgresFixture`, `PublicReadFixture`, `KeycloakFixture` or the `SharedSchema` /
  `SharedKeycloakRealm` / `TenantRealmApiTestGroup` collections — carries
  `[Trait(RequiresDocker.Key, RequiresDocker.Value)]`.
  ADR-0062's shared native trait belongs to exactly the two complete native proof
  classes, including all predicate/theory/mutant rows. A nonempty source closure
  rejects an unclassified direct or delegated sidecar/preflight test caller;
  qualified identities, aliases, source extension methods and legitimate partial
  declarations retain ownership. Conditional source is parsed with the actual
  Integration Release project's evaluated build symbols; inactive branches are
  not flattened into that population. This bounded source census is not arbitrary
  dynamic-call analysis.
- **Why it matters:** CI separates Docker from the remaining tests by complementary
  filters, so a class that forgets the trait does not fail — it runs in the
  `backend` job, whose runner also has a Docker socket, starts its container and passes.
  Nothing goes red, and the Docker suite quietly stops being where the Docker tests live.
- **Source:** [06-testing.md](06-testing.md).
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** (`PersistenceConventionTests`), widened from `Database/` to
  the whole project in Packet 9's fifth review. Its companion,
  `The_Docker_Trait_Sweep_Can_Actually_Fail`, feeds the predicate the shapes it must and
  must not flag, including the added native ownership/closure controls in P02b-4's
  ADR-0062 follow-up and P02b-6's provider-fixture/missing-trait controls.
  Actual discovered case IDs and complete partition results
  are additionally checked by `scripts/ci-native-tests.py`.
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
> P02d-1 step 2 adds Education's factory as the eighth connection site.

- **Asserts:** two halves. The composition root's persistence registration is run,
  and every `DbContext` service in it is one `AddModuleDbContext` registered —
  scoped, from an implementation factory, never a type registration EF could give
  its own connection. Under `backend/src`, the configuration/construction scan
  admits exactly **thirteen** files: six design-time factories, one per migration
  chain; `ModuleDbContextRegistration`, which passes a *connection*;
  `PersistenceCompositionExtensions`, with the guarded platform-role builder;
  `ApplicationDataSource`, shared by the API and one-shot Seeder;
  ADR-0046's confined `OutboxDispatcherDataSource`; and ADR-0072's deployment-only
  `LearnStack.Tools.IdentityAuthorityInstaller/Program.cs`; and ADR-0076/0077's
  private authorization and evidence source builders. Another construction
  site requires its owning decision and an explicit allow-list change.
  A context on its own connection never saw the announcement, so every read through
  it returns zero rows under the corrected policy — silently.

  The set is keyed on `directory/filename`, not the bare filename: two files with
  the same name cannot silently share a sanctioned construction site. The lexical
  scan covers `UseNpgsql`, `AddDbContext`, data-source builders/factories and raw
  connection constructors. Connection acquisition from an already configured
  source remains governed by its named owner contract.
  The complete compiled EF transaction-enrollment set contains the common
  module factory, the existing global binding store and the exact contextual
  Identity reenrollment helper. That helper requires a clean tracker, the same
  physical connection and a current matching tenant announcement outside global
  mode; it sets no scope and creates no connection. Missing/extra enrollment,
  executable-alias and removed-guard controls fail.
- **Source:** ADR-0040; ADR-0046's confined dispatcher; ADR-0072's deployment
  authority installer; [05-database.md § Forbidden](05-database.md).
- **Type:** xUnit + DI registration inspection, compiled caller census and source guards. **Kind:** structural.
- **Status:** **Implemented** (Packet 6 step 6; the allow-list widened to five and
  keyed by directory in Packet 7 step 10, to six in Packet 8 step 3 and to seven in
  Packet 9 step 3, eight in P02d-1 step 2 and eleven with the ADR-0046/0072
  construction sites, and thirteen with the independent authorization/evidence
  sources; `LearnStack.Tests.Architecture`, `PersistenceConventionTests`).
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

- **Asserts:** `TransactionBehavior` delegates to `TransactionExecutionCoordinator`,
  whose constructor names `IUnitOfWork` and no `DbContext`.
  `LearnStack.Application` references no module assembly — checked
  against the **project file** as well as the emitted assembly-reference table,
  because the compiler elides a reference whose types the IL never touches, so a
  dangling `<ProjectReference>` would leave a reflection-only check green. The
  assembly half carries a positive control. The immutable exact-Type read-only
  declaration is constructed only by Tenancy's shared site-read registration for
  `GetAccountSiteQuery` and consumed only by the existing transaction owner as a
  frozen Type set. API and Seeder call that registration. Complete production IL
  construction/consumer/caller censuses include clean and planted mutation controls;
  this authenticated exception does not acquire the public marker.
- **Source:** ADR-0040; ADR-0033; ADR-0064 § Closed authenticated read.
- **Type:** xUnit + assembly-reference, constructor and production IL inspection.
  **Kind:** structural.
- **Status:** **Implemented** (Packet 6 step 6,
  `LearnStack.Tests.Architecture`, `PersistenceConventionTests`; P02b-5 Step 2
  extends the same owner with the exact authenticated read-only declaration).
- **Additional controls:** the consumed coordinator mode admits only the
  sanctioned exact-type selection and existing public/job fallback. A name-based
  override and independent immutable declaration/catalog metadata mutations fail
  the owning predicate. Additive composition retains every declaration while the
  frozen exact-type set deduplicates them.
- **Phase:** 02a (Packet 6); 02b (P02b-5 Step 2 extension).

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
- **Status:** **Implemented** — `AmbientUnitOfWorkTests` (integration), Packet 10.
  Customization writes, Tenancy reads it before `COMMIT`, and a second connection sees
  nothing — the control that stops the case agreeing with itself, since a context that
  committed on `SaveChanges` would satisfy the first half alone. Mutation-checked: giving each
  context its own connection fails both cases in this file.
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
- **Status:** **Implemented** — `AmbientUnitOfWorkTests` (integration), Packet 10: two
  modules write, the outer frame throws before the commit, and both tables are empty read from
  a connection of its own.
- **Phase:** 02a (Packet 10), for the same reason as the rule above.

#### `Modules_Do_Not_Parallelize_Over_The_Ambient_Connection`

- **Asserts:** no module or shared Application/Infrastructure execution code fans
  out — no `Task.WhenAll`, `WhenAny` or `WhenEach`, with or
  without an explicit type argument; no `Parallel.For`, `ForAsync`, `ForEach`, `ForEachAsync`
  or `Invoke`; no PLINQ `AsParallel()`; and no task started and stored instead of awaited, which
  is `Task.WhenAll` written out longhand — outside an exact-path list whose entries
  each say why their concurrent work touches no ambient connection. The entries are
  `LearnStack.Infrastructure/Caching/InMemoryCacheService.cs`: singleton cache flights
  race their own deadline, without a scoped unit or module context; and
  `LearnStack.Infrastructure/MultiTenancy/CachedHostToTenantResolver.cs`: singleton
  host lookup flights use their own read-only connection before tenant admission.
  The census includes the separate `LearnStack.Infrastructure.Audit` project.
  One connection means one command at a time; a handler that fans out corrupts the
  protocol. Stricter than ADR-0040's sentence,
  which bans two `DbContext`-bound operations: which operations are bound to the ambient
  connection cannot be read from source, and the honest rule is the one that can.
- **Source:** ADR-0040 § Nesting; ADR-0047's retained recurrence owners.
- **Type:** xUnit + source scan. **Kind:** structural.
- **Status:** **Implemented** — `PersistenceConventionTests.cs`, Packet 10, after waiting
  for module code to scan: three modules have shipped handlers since Packets 7 and 8, and
  the rule asserts it read them. Its companion, `The_Parallelism_Scan_Can_Actually_Fail`,
  feeds it each fan-out shape — a `using static` import included — and two that are not.
  P02b-1 extends the census to shared execution state;
  `The_parallelism_census_reaches_shared_code_and_bounds_its_singleton_exceptions`
  plants violations in every covered root and proves both exemptions are
  path-specific.
  P02b-4 Step 9 adds `Recurrence_parallel_allowance_is_limited_to_exact_owned_expressions_and_publication`:
  nine invocation expressions in three actual recurrence owners are admitted only
  with exact method signatures, suppressed flow and actual-task publication.
  The remaining source in each file is still scanned; planted copies reject
  changed callbacks, missing subjects/publication and extra parallel or stored work.
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

- **Asserts:** only the confined dispatcher infrastructure resolves the named keyed,
  lazy `ConnectionStrings:OutboxDispatcher` source in the API host. Seeder/web and
  arbitrary keyed/service-provider resolution are refused. Physical connections
  verify the exact outbox role, bounded attributes and memberships, including
  direct/transitive incoming membership from other non-superuser roles. A nonempty
  production census has planted clean/dirty resolution and registration controls.
  Named worker/store source readers require unique literal method signatures,
  visibility and staticness; a same-name overload grants no authority. Real
  role/grant probes include valid and elevated/transitive negative controls.
  The closed job facade binds the two actual messaging-owner tasks before startup;
  only positive source-use completion permits shared-pool disposal. Exact cleanup
  owners, immutable capacity input and memory-only health observations retain
  their caller/field boundaries and planted escape controls.
- **Source:** [05-database.md § GRANT matrix](05-database.md#grant-matrix);
  [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md).
- **Type:** architecture/DI census and real PostgreSQL role probes.
  **Kind:** structural + startup + behavioural.
- **Status:** **Implemented** (`OutboxDispatchConventionTests`,
  `OutboxDispatcherRoleGuardTests`, `OutboxProcessorTests`): exact nonempty
  whole-production credential/factory/consumer census, keyed registration and
  constructor bindings, planted clean/dirty helper/delegate/async controls, and
  real PostgreSQL role/grant/startup refusal proofs. Exact unique literal
  source-reader signatures, visibility and staticness reject named-method overloads;
  those focused controls and both fresh fix reviews passed at `606362b`.
  [OutboxDispatchConventionTests.CredentialBindingMutationTests.Credential_census_rejects_compiled_connection_string_indexers_and_composed_keys](../../backend/tests/LearnStack.Tests.Architecture/OutboxDispatchBoundaryTests.cs)
  plants actual compiled concatenation, interpolation, formatting and configuration
  indexer calls, with clean configuration/dictionary/diagnostic controls.
  [OutboxDispatcherRoleGuardTests.App_membership_in_the_dispatcher_is_refused_directly_and_transitively_then_restoration_connects](../../backend/tests/LearnStack.Tests.Integration/Database/OutboxDispatcherRoleGuardTests.cs)
  proves an existing app session can escalate through the planted grant, then
  verifies refusal on a new dispatcher physical connection and successful
  connection after grant removal, for both direct and transitive membership.
- **Phase:** 02b, P02b-2.

- **Accepted extension:** ADR-0047 preserves this canonical name while adding
  enumerated H1 credential consumers and the minimal lifecycle reader. P02b-4
  Step 2 implements role/table grants; Step 4 adds the closed facade, source pin
  and concrete cleanup-owner censuses in
  [BackgroundJobRuntimeCompositionBoundaryTests](../../backend/tests/LearnStack.Tests.Architecture/BackgroundJobRuntimeCompositionBoundaryTests.cs)
  and [PrivilegedDispatchCleanupBoundaryTests](../../backend/tests/LearnStack.Tests.Architecture/PrivilegedDispatchCleanupBoundaryTests.cs).
  The [Step 4 continuation](../roadmap/phase-02b-delivery-log.md#source-lifetime-and-facade-continuation-2026-10-16)
  owns current qualification/review status. Existing dispatcher enforcement stays
  Implemented; full Step 4 review gates remain open.

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

- **Asserts:** the keyed `PrivilegedDataSource` facade for
  `ConnectionStrings:PlatformAdmin` is resolvable only by `PlatformAdminScope`.
  Its private lazy Npgsql pool is never registered as a raw keyed service. Module
  code cannot reach the `BYPASSRLS` credential; every acquisition also refuses an
  active ordinary-job admission, including warm-pool reuse (P02b-4 Step 1).
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

#### `Every_OrgScoped_Table_Has_An_Organization_Immutability_Guard`

- **Asserts:** every tenant-owned, organization-scoped table has an effective row
  guard refusing an `organization_id` change, including NULL-to-value and
  value-to-NULL transitions. The canonical shape is the shared BEFORE UPDATE trigger;
  `audit_log`'s append-only guard is accepted only with independent proof that it
  refuses the same change. The shared function cannot assume a surrogate `id` exists.
  Scope follows table class: `platform_host_to_tenant` is platform-scoped and
  `outbox_messages`' organization column is tenant-wide event metadata, so those two
  are not subjects. An exemption must not silently exclude a newly introduced
  org-scoped table.
- **Source:** [ADR-0003 Amendment 6](../decisions/0003-tenant-isolation-defense-in-depth.md#amendment-6--insert-scope-and-parent-mirrors-2026-09-14);
  [05-database.md § Tenant-Owned and Organization-Scoped Tables](05-database.md#tenant-owned-and-organization-scoped-tables).
- **Type:** xUnit + applied PostgreSQL schema catalogue, with behavioral companion
  evidence for the append-only alternative. **Kind:** structural.
- **Status:** **Implemented** (P02d-1 step 1, `LearnStack.Tests.Integration`,
  `OrgScopeSchemaTests`). Companions plant missing, disabled, conditional and
  column-restricted triggers on a new table, and remove each existing guard.
  Separate app-login proofs exercise the shared function on a no-id natural-key
  table and the audit trigger after a committed test-only UPDATE grant.
- **Phase:** 02d (P02d-1).

#### `Every_Organization_Mirroring_Child_Has_A_Parent_Scope_Guard`

- **Asserts:** every declared parent-mirroring relation has its tenant-composite
  foreign key and an independent, enabled BEFORE INSERT OR UPDATE row trigger.
  The trigger invokes a SECURITY INVOKER function with a restricted search path,
  schema-qualified parent lookup, both tenant and parent-id predicates, and
  `FOR KEY SHARE`. Its nullable organization comparison and uniform `23514` refusal
  cover missing, hidden and mismatched parents. Initial subjects are Lesson → Course,
  CourseTranslation → Course and LessonTranslation → Lesson. Identity adds
  MembershipProfile → Membership and MembershipRoleAssignment → Membership;
  its combined invoker trigger retains the same parent-scope contract.
  Subject discovery and declaration coverage must make an omitted relation fail.
- **Source:** [ADR-0003 Amendment 6](../decisions/0003-tenant-isolation-defense-in-depth.md#amendment-6--insert-scope-and-parent-mirrors-2026-09-14);
  [ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md);
  [05-database.md § Parent organization mirrors](05-database.md#parent-organization-mirrors);
  [Education data model](../modules/education/README.md#data-model-and-invariants).
- **Type:** xUnit + EF model and applied PostgreSQL schema inspection. **Kind:** structural.
- **Status:** **Implemented** (P02d-1 step 2, `LearnStack.Tests.Integration`,
  `EducationStructureTests`). Companions plant missing declarations, relations and
  triggers, and weaken timing, scope predicates, search path, invoker mode and locking.
  Separate `learnstack_app` proofs exercise insertion, reparenting, the lock before the
  FK runs and temporary-table shadowing. A well-shaped trigger alone is not an
  isolation proof.
- **Phase:** 02d (P02d-1); P03-1 contextual foundation.

#### `Pattern_A_Content_Uses_Translation_Satellites`

- **Asserts:** every declared Pattern A root keeps translatable fields on its
  contained satellite, whose natural key is `(parent_id, locale)`, whose tenant and
  nullable organization columns carry the isolation markers and EF filter, and whose
  slug key is exactly `(tenant_id, locale, slug)`. Parent identity and organization
  never widen that namespace. Parent translatable fields and ad-hoc locale columns
  such as `title_en` are rejected. Education satellites carry no surrogate id,
  independent audit/soft-delete columns or concurrency token; their parent foreign
  keys are tenant-composite and CASCADE, while Lesson → Course is RESTRICT. The
  satellite column inventory pins the current Education module spec, not every
  field that another Pattern A entity could contain; a schema extension updates
  the owning spec and inventory together.
- **Source:** [ADR-0008](../decisions/0008-localization-schema.md);
  [08-localization.md § Pattern A](08-localization.md#pattern-a--side-translation-table-default-for-content-shaped-entities);
  [Education data model](../modules/education/README.md#data-model-and-invariants).
- **Type:** xUnit + EF model and migration/schema inspection. **Kind:** structural.
- **Status:** **Implemented** (P02d-1 step 2, `LearnStack.Tests.Integration`,
  `EducationStructureTests`). Companions plant parent translatable fields, ad-hoc
  locale columns (including misplaced `locale` and `slug` suffixes), absent
  satellites and widened slug keys. Separate model probes exercise unmapped CLR
  properties and shadow properties mapped to localized column names. Both retain
  `SlugKey` / `slug_key` as valid non-routable authoring handles. The nonempty
  declaration set is checked against actual model types and applied schema metadata.
- **Phase:** 02d (P02d-1).

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
- **Status:** **Implemented**, twice. The schema-level case is Packet 6 step 4
  (`TenancySchemaTests`); the request-level one is Packet 7 step 11
  (`TenantIsolationHttpTests`), which drives it through
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
- **Status:** **Implemented**, twice. Packet 6 step 4 (`TenancySchemaTests`): a
  `[Theory]` over every table carrying a `WITH CHECK` for the `INSERT` half, and
  `Reassigning_An_Owned_Row_To_Another_Tenant_Is_Rejected` for the `UPDATE` half, because
  `WITH CHECK` guards both and a rule covering one leaves the other open. Packet 7 step 11
  (`TenantIsolationHttpTests`) issues the write through a request: raw SQL on
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
criteria name three more of the same kind, which Packet 10 shipped in `TenancySchemaTests`:
`App_Role_Cannot_Enumerate_Host_Map`, `App_Role_Cannot_Enumerate_Tenants` and
`Tenant_A_Cannot_Repoint_Tenant_B_Host` — the last in both halves, the `INSERT` that
`WITH CHECK` refuses with `42501` and the `UPDATE` that the policy refuses as a silent zero.
The `UPDATE` half announces the victim's own host first and reads the row it is about to
fail to change, so what refuses the write is the policy rather than a row that was never
visible.

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
  module's `Domain` assembly — **directly, or inside a generic, array or by-ref
  wrapper** — with typed-key-only read methods explicitly enumerated — either derives
  from `IAggregateWriteStore<TRoot, TId>`, and is therefore visible to the
  cross-aggregate census above, or appears on a literal allow-list. The list holds one
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
- **Detected by shape, not by port name.** A typed identifier can also reach a
  key-only delete. Only literal, method-level read exemptions bypass the key scan;
  adding a write to an approved read interface remains visible. The planted companion
  proves direct/wrapped domain writes and void/value-returning key-only writes count,
  while the enumerated typed-key reads remain admitted under ADR-0023.
- **Source:** [ADR-0042](../decisions/0042-tenant-provisioning-cross-aggregate-transaction.md).
- **Type:** xUnit + reflection. **Kind:** structural.
- **Status:** **Implemented** (Packet 7 review, `LearnStack.Tests.Architecture`,
  `AggregateWriteTests`).
- **Phase:** 02a (Packet 7).

#### `Seeder_Does_Not_Call_Tenant_Context_Setters`

- **Asserts:** seed orchestration and ownership verification do not directly invoke
  tenant/session context setters or open their own database announcement transaction.
  Verification runs through contextual `ISender` requests and the admitted pipeline;
  the existing trusted seed context construction remains permitted. A source scan
  covers production seeder callers and cannot pass by collecting no subjects. An IL
  scan also refuses direct EF mutation/SaveChanges/set-based SQL and ad hoc
  `NpgsqlCommand` construction; its planted companion distinguishes these from
  immutable data construction, composition and request dispatch.
- **Source:** [Phase 02d G15](../roadmap/phase-02d-delivery-log.md#p02d-2-accepted-answers)
  and [Security Standards § The out-of-band setters](11-security.md#the-out-of-band-setters).
- **Type:** xUnit + source/IL scans with companions that plant caller and persistence
  violations and verify permitted request dispatch/context composition.
  **Kind:** structural.
- **Status:** **Implemented** (P02d-2 Step 4, `SeederConventionTests`), including
  its planted companion; production readers/writers dispatch only contextual requests.
- **Phase:** 02d (P02d-2).

#### `Seeder_Uses_The_Shared_Application_Role_Guard`

- **Asserts:** the one-shot tool builds its application pool through the same named
  guard as HTTP; direct execution cannot silently use an owner or BYPASSRLS credential.
- **Source:** [ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md);
  [Database Standards § Database roles](05-database.md#database-roles).
- **Type:** xUnit + IL call census. **Kind:** structural.
- **Status:** **Implemented** (P02d-2 PR review, `SeederConventionTests`). The shared
  credential parser is exercised in `ApplicationDataSourceGuardTests`; real direct and
  transitive bypass refusal is proved through both builder callers in `UnitOfWorkTests`.
- **Phase:** 02d (P02d-2).

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
- **Why the order, exactly:** because the statement binds only what follows it. A guard that
  trusted the server to complain would not be a guard — see the next bullet for what the
  server actually does.
- **What the scan does and does not prove.** It proves the statement is **executed** before
  the announcement in the same method: the literal arms the check and a call to an
  `Execute…` member confirms it, so deleting the execution and leaving the command fails the
  rule. Measured — an earlier version set its flag on the string alone, and removing
  `await readOnly.ExecuteNonQueryAsync(…)` left it green while the server reported
  `transaction_read_only=off`.
  What it does **not** prove is that no other statement ran first. A setter that issued a
  `SELECT` between `BEGIN` and `SET TRANSACTION READ ONLY` still satisfies this rule — and,
  measured on PostgreSQL 18, the server accepts that: neither an earlier `SELECT` nor an
  earlier `INSERT` refuses the statement, and the earlier insert still commits. So the
  ordering against the announcement is this rule's doing rather than the server's, and
  nothing here observes the session's actual `transaction_read_only` setting;
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
  too late, one whose second announcement has none of its own, one that only names the
  statement in a message, and one that **builds the command and never runs it** — each written
  `async`, because a real reader is and an async method's statements live in a state machine. It
  also feeds the discovery pattern each spelling. Mutation-checked twice: dropping the statement
  from `FeatureFlags` fails it, and so does replacing `AuditConfigService`'s execution with a
  read of the command's text.
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
  ADR-0040 Amendment 5 adds. The MediatR transaction owner and P02b-3's transport
  stamp it through `SetTenantContextAsync`; acceptance and prepared execution
  announce their own ambient transaction before app-role commands. The durable
  idempotency store uses raw app-role SQL on its short announced owners, which
  EF interception does not see; it receives no EF exemption. The
  [Security setter census](11-security.md#the-out-of-band-setters) owns the
  complete logical set. Global, discovery and fixed-seed transitions retain their
  separately enumerated owner checks. `PlatformAdminScope` announces no Tenant
  and acquires no ordinary-context exemption.
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
  [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md);
  [ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md).
- **Type:** xUnit + EF model inspection. **Kind:** structural.
- **Test owner:** `GlobalIdentityModelTests`.
- **Status:** **Implemented** (`GlobalIdentityModelTests`) — actual global User model, exact scalar population and independent self filter.
- **Phase:** P03-1 Step 3; contextual ownership extends in Step 4.

### Accepted Identity foundation obligations

These obligations derive from ADR-0072/0073. Each Status states actual delivery;
Registered owners remain planned classes, not passing controls. Promotion to
Implemented requires nonempty production subjects and dirty/clean controls.

#### `Mapped_Properties_Have_Explicit_Personal_Data_Classification`

- **Asserts:** the coordinator compares every real mapped EF property with its
  module's explicit personal-data declaration, including inherited keys, raw
  subjects and stable linkage. Missing/conflicting declarations and a planted
  neutral-name omission fail the same complete census. Diagnostics reveal no
  values; snapshot/redaction and sink canaries remain independent controls.
- **Source:** [ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md);
  [Data Protection](../architecture/23-data-protection.md).
- **Type:** complete EF model/declaration comparison and planted omissions.
  **Kind:** structural.
- **Test owner:** `DataProtectionConventionTests`.
- **Status:** **Implemented** (`DataProtectionConventionTests`) — P03-1 Step 2;
  current nonempty production models,
  the deliberately empty migration-only Platform context, real neutral-property
  negative/positive controls and annotation/schema neutrality. Global and
  contextual Identity models participate in the complete census.
- **Phase:** P03-1 Step 2, before the first Identity personal-data writer.

#### `Global_Identity_Storage_Is_Subject_Confined`

- **Asserts:** actual app-role reads expose only the announced verified subject;
  missing/reset/malformed/foreign authority refuses. EF filters, applied
  policies/grants/functions, owner DELETE/TRUNCATE refusal and pooled cleanup
  match the sole [Database contract](05-database.md#identity-storage-contract),
  with positive and planted-widening controls. The current applied schema has
  55 tables, six chains and five Identity definer functions.
- **Source:** [ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md);
  [Database Standards](05-database.md#identity-storage-contract).
- **Type:** PostgreSQL catalogue, app-role isolation and owner controls.
  **Kind:** behavioural, with structural companions.
- **Test owner:** `GlobalIdentitySchemaTests`.
- **Status:** **Implemented** (`GlobalIdentitySchemaTests`, integration assembly) — actual self reads/writes, compiled/applied body/ACL/caller census, committed widening/removal controls and owner mutation refusal.
- **Phase:** P03-1 Step 3.

#### `Global_Identity_Creation_Uses_The_Common_Audit_Owner`

- **Asserts:** exact prepared global authority uses the common coordinators,
  typed creation reference and sole central SQL writer. Success/User reciprocity,
  failure/unknown-COMMIT reconciliation, exact duplicate evidence and precommit
  refusal preserve distinct outcomes. Missing/foreign/stale/reused owners,
  ordinary/global crossover, metadata substitution and copied audit SQL fail.
  The exact migration installer adds no runtime module/entity dependency or writer.
- **Source:** [ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md);
  [Database Standards](05-database.md#global-central-audit-branch);
  [Audit Coverage](18-audit-coverage.md#global-identity-creation).
- **Type:** production owner/installer census and actual app-role creation,
  reconciliation and fault controls. **Kind:** behavioural and structural.
- **Test owners:** `IdentityAuditConventionTests`, `GlobalIdentityBootstrapTests`.
- **Status:** **Implemented** (`IdentityAuditConventionTests`; `GlobalIdentityBootstrapTests`, integration assembly) — closed actual owner/call sites, real PostgreSQL/provider/COMMIT fault twins and exact evidence controls; all eight central seams ship.
- **Phase:** P03-1 Step 3; no HTTP creation route is admitted.

#### `Identity_Membership_Invariants_Use_Fresh_Ordered_Locks`

- **Asserts:** actual Membership/child and protected Role mutations preserve the
  total eligible-admin invariant under canonical ordered locks. Held-lock races
  observe a real competing commit through fresh post-wait reads; wrong isolation,
  forbidden upgrades, incomplete first-admin state and closure/final-admin races
  refuse. Scope/reference/lifecycle controls cover tenant and branch variants.
  Creation, Suspended-to-Active and every assignment addition require the actual
  target User fence and a fresh actual-trigger eligibility check even when the
  complete plan is preheld. Missing/wrong/shared-only/foreign-backend fences
  refuse as fixed internal plan faults, distinct from uniform unknown/nonadmitting
  target refusals. Exact current-backend heldness is observed without an
  acquisition probe; `pg_locks` does not prove transaction ownership or history.
  Closed caller/source/receipt controls reject session-lock APIs and actual
  transaction-end tests prove lifetime; a session-only fixture exposes the
  observation limit. Profile edits and assignment removals take no User fence
  solely for admission. Fixed C#/SQL User/Tenant key vectors agree. Disposable
  fixture-only closure competitors temporarily accommodate only the User update
  guard and owner CHECK policy, restore both in `finally`, and prove the policy/guard/
  body/ACL census afterward, with independent dirty and clean controls; they
  introduce no production closure writer.
- **Source:** [ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md);
  [ADR-0074](../decisions/0074-membership-admission-under-ordered-locks.md);
  [Database lock contract](05-database.md#lock-snapshot-and-adoption-conditions).
- **Type:** real app-role PostgreSQL mutation/concurrency controls and closed
  transaction-only acquisition/caller census. **Kind:** structural and behavioural.
- **Test owners:** `IdentityMembershipTests`, `IdentityMutationLockTests`, `IdentityFoundationConventionTests`,
  `MembershipAdministrationProfileTests`, `MembershipAdministrationAssignmentTests`,
  `MembershipAdministrationRaceTests`.
- **Status:** **Implemented** (`IdentityFoundationConventionTests`; `IdentityMembershipTests`, integration assembly) — real app-role races, immediate/deferred guards,
  transaction-only caller/receipt controls, fixture restoration and local
  command-path measurement. Current qualification/review state lives in the
  [phase Status block](../roadmap/phase-03-identity-admin.md).
- **Phase:** P03-1 Step 4.

#### `Identity_Seed_Uses_Finite_Adopted_Manifests`

- **Asserts:** one versioned compiled source and generated SQL kernel agree on
  complete scope/key/version/fingerprint. Finite templates/grants, ordered seed
  acts and interrupted/no-write/conflict reruns preserve actors/credentials and
  later adopted state. Registry additions grant nothing to existing Roles; seed
  authority is default-denied in API composition.
- **Source:** [ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md);
  [Permissions foundation](19-permissions.md#identity-foundation-contract);
  [Identity seed sequence](../modules/identity/README.md#first-admin-and-seed-sequence).
- **Type:** actual manifest/command census, generated-artifact equality and seed/rerun
  controls. **Kind:** structural and behavioural.
- **Test owners:** `IdentityFoundationConventionTests`, `IdentitySeederTests`.
- **Status:** **Implemented** (`IdentityFoundationConventionTests`; `IdentitySeederTests`, integration assembly) — actual owned private-witness handoff, finite
  production commands, no-write/interrupted/conflict reruns and central MUST
  rollback/contained capture controls.
- **Phase:** P03-1 Step 4.

#### `Permission_Definitions_DeclareScope`

- **Asserts:** each definition declares Platform, Tenant or Organization scope;
  identity is `(PermissionScope, PermissionKey)`. The actual nonempty foundation
  registry equals the accepted finite five-pair manifest; missing/duplicate/wrong-
  scope definitions and inconsistent consumers fail.
- **Source:** [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md);
  [ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md);
  [Permissions Standards](19-permissions.md#identity-foundation-contract).
- **Type:** compiled definitions/consumers with planted controls. **Kind:** structural.
- **Test owners:** `IdentityFoundationConventionTests`, `OperationPermissionConventionTests`.
- **Status:** **Implemented** (`IdentityFoundationConventionTests`) — the actual five-pair registry, command metadata,
  generated artifacts and planted definition controls are exercised.
- **Implemented P03-2 metadata extension:** ADR-0076 composes
  actual module definition sources and three nonassignable virtual-public pairs;
  the five-pair assignable manifest remains intact. Complete metadata/consumer
  controls exercise actual module sources and the complete production request
  universe, with missing/extra compiled request/source controls and conflicting
  runtime registration cases. The
  [Phase 03 Status block](../roadmap/phase-03-identity-admin.md)
  owns source-bound policy/evidence qualification.
- **Phase:** P03-1 Step 4; P03-2 Step 3 extension.

### Accepted live-authorization obligations

Accepted ADR-0076/0077 govern the following implemented controls. Each entry
names its actual proof owners; the
[Phase 03 Status block](../roadmap/phase-03-identity-admin.md)
and delivery records own source-bound qualification. Acceptance alone never
promotes an unrun proof.

#### `Live_Authorization_Uses_Bounded_Independent_Owners`

- **Asserts:** actual verified candidate checks use the confined app-role READ
  ONLY owner, exact actor/tenant/org and generation, four-slot source and tracked
  job authorization lane. Every business connector can remain held while admitted
  nested/post-lock checks complete. Actual transaction-pooled backend/PID clean
  controls and a shared-backend dirty topology prove independent capacity;
  parent uncommitted grants never supply authority. Saturation, cancellation,
  delayed/failed cleanup, shutdown and pool reuse preserve retained ownership.
- **Startup qualification:** exact API/Seeder callers bind the auxiliary lanes to
  the live business witness under [Standard 05](05-database.md#authorization-and-evidence-source-ledger);
  closed factory/binder callers and bound-overload removal/extra-call controls
  complement actual same-cluster/PgBouncer positives and foreign-cluster/expiry negatives.
- **Source:** [ADR-0076](../decisions/0076-live-scoped-authorization.md);
  [source ledger](05-database.md#authorization-and-evidence-source-ledger).
- **Type:** source/caller/role census plus real app-role PostgreSQL/PgBouncer
  progress and lifetime controls. **Kind:** behavioural and structural.
- **Test owners:** `IdentityAuthorizationReaderTests`,
  `AuthorizationReadSourceTests`, `AuthorizationPoolProgressTests`,
  `IdentityAuthorizationConventionTests`, `TrustedOperationSourceConventionTests`,
  `OperationPolicyOwnershipTests`, `IdentityAuditConventionTests`.
- **Status:** **Implemented** — `IdentityAuditConventionTests`; source/caller controls
  complement the real PostgreSQL/PgBouncer owners above.
- **Coverage:** the confined reader/source, actual PostgreSQL/
  PgBouncer policy progress, typed HTTP candidates and finite source ownership
  have actual controls. The live membership adapter, HTTP candidate source and
  private deferred evidence factory join the exact production caller/DI census.
- **Phase:** 03 (P03-2).

#### `Protected_Operations_Require_Explicit_Live_Policies`

- **Asserts:** complete real HTTP/request/source composition explicitly classifies
  public, AccountSite, permission-bound and confined seed/system operations.
  The unchanged canonical eight-step pipeline admits application requests through
  `AuthorizationBehavior`. The baseline HTTP census contains exactly eight public GET/HEAD
  actions, AccountSite GET and `GET /api/v1/identity/memberships`; exact named
  policies and the default/fallback refusal guard own the two authenticated reads.
  Live reader/evaluator/current-context/evidence aliases and qualification startup
  remain explicit, with no alternate pass-through behavior.
  Unclassified or underprivileged requests cannot dispatch at HTTP or direct
  application entry. Fresh live Membership/capability, denial/resource ordering,
  committed revocation, same-key scoped delegation, finite manifest adoption and
  retained public/AccountSite ceilings have useful production positive/denied
  controls. Ambient/SystemActor or realm-role values alone supply no exemption.
  The exact own-profile request uses coverage-only `OwnMembershipRead`, has no
  stored permission or tenant-wide read witness, and checks the actor's exact
  current Membership without NULL-org fallback. Its owning module declares
  physical READ ONLY independently of AccountSite. Member detail's matched
  Tenant clause may widen reads; its Organization clause and all writes stay
  scoped. Exact current-invocation borrowers and the separate Membership target
  decision source join the compiled census with missing/extra/alias controls.
  Pure protected MUST-sensitive reads retain no payload on failed physical
  audit COMMIT and report audit_unavailable for both scoped and Tenant clauses.
  The exact current application decision/request gates this mapping; ordinary
  mutations, cancellations and unrelated owners retain their primary failure
  and original uncertain completion state.
- **Source:** [ADR-0076](../decisions/0076-live-scoped-authorization.md);
  [ADR-0077](../decisions/0077-trusted-authorization-evidence.md);
  [ADR-0079](../decisions/0079-bounded-identity-administration.md);
  [Permissions](19-permissions.md#live-permission-enforcement-contract).
- **Type:** complete compiled metadata/caller census, real direct dispatch and
  administrative-directory HTTP/database controls. **Kind:** behavioural and structural.
- **Test owners:** `IdentityAuthorizationConventionTests`, `OperationPermissionConventionTests`,
  `OperationPolicyOwnershipTests`, `OperationHttpClassificationTests`,
  `OperationAuthorizationCoordinatorTests`, `IdentityOperationAuthorizerTests`,
  `RoleGrantCommandTests`, `RoleGrantCommandIntegrationTests`,
  `MembershipAdministrationProfileTests`, `MembershipAdministrationAssignmentTests`,
  `MembershipAdministrationRaceTests`,
  `MembershipDetailCommitFailureTests`,
  `IdentityAuthorizationHttpTests`, `UndeclaredHttpOperationTests`.
- **Status:** **Implemented** — `OperationPolicyOwnershipTests`; actual composition and
  caller controls complement the direct/HTTP proofs above.
- **Phase:** 03 (P03-2).

#### `Authorization_Denials_Have_PreSuccess_Durability_And_Stable_Identity`

- **Asserts:** one central occurrence with fixed id/microsecond decision time
  and complete frozen draft is distinct from plural ordinary resource intents.
  ReadWrite outer success commits unresolved events atomically; ReadOnly or
  ownerless success requires standalone acknowledgement first. Per-event state,
  rollback/unknown COMMIT, expected-key full scoped duplicate matching, real
  nested acts versus re-entry, Off/SHOULD/MAY/override-Off and process-crash controls
  preserve the precise guarantee without claiming a persistent replay ledger.
  The independent two-slot app-role evidence lane retains actual cleanup and
  progresses while business owners remain held; audit fault preserves a real
  refusal but prevents otherwise-successful outer commit/disclosure.
- **Source:** [ADR-0077](../decisions/0077-trusted-authorization-evidence.md);
  [Audit Coverage](18-audit-coverage.md#trusted-authorization-evidence).
- **Type:** common-owner/source census plus real PostgreSQL/HTTP, process crash,
  uncertainty, capacity and cleanup controls. **Kind:** behavioural and structural.
- **Test owners:** `AuthorizationEvidenceCoordinatorTests`, `AuthorizationEvidenceStoreTests`,
  `AuthorizationEvidenceSourceTests`, `AuthorizationEvidenceBarrierTests`,
  `AuthorizationPoolProgressTests`, `IdentityAuditConventionTests`.
- **Status:** **Implemented** — `AuthorizationEvidenceBarrierTests`; real transaction,
  crash and reconciliation controls complement the source/lifetime owners above.
- **Phase:** 03 (P03-2).

#### `Authorization_Evidence_Uses_Trusted_Bounded_Provenance`

- **Asserts:** frozen actor/Membership/Role/source/route and admitted address/UA
  facts come only from enumerated trusted owners, never arbitrary handler
  dictionaries, claims or forwarded input. Direct/nested/job scopes preserve
  ownership and clearing. Exact successful claim-only header refusals use their
  Membership-confirmed tenant and separate per-occurrence event without a host
  budget debit. Typed sink canaries, source/caller dirty controls and per-row
  retention/redaction declarations preserve personal linkage honestly.
- **Source:** [ADR-0077](../decisions/0077-trusted-authorization-evidence.md);
  [Audit Coverage](18-audit-coverage.md#trusted-authorization-evidence).
- **Type:** real source/HTTP/record composition and diagnostic sink controls.
  **Kind:** behavioural and structural.
- **Test owners:** `AuthorizationEvidenceCoordinatorTests`,
  `IdentityDiagnosticSinkTests`, `AuthorizationEvidenceApiSourceTests`,
  `IdentityAuthorizationHttpTests`, `TenantAuthenticationRejectionAuditTests`,
  `IdentityAuditConventionTests`.
- **Status:** **Implemented** — `IdentityAuditConventionTests`; closed provenance
  controls complement the real source/HTTP/evidence owners above.
- **Phase:** 03 (P03-2).

#### `Permission_Scope_Matches_Resource_Scope`

- **Asserts:** exact registered scoped pairs match admitted target facts;
  Organization grants apply only through the matching branch Membership.
  Tenant grants alone remove no filter or write scope. For human reads, the
  declared operation witness alone permits accepted tenant-wide reads;
  independent HostScope and organization write guards survive. Missing/forged/other-branch target metadata
  and prohibited scope implications refuse.
- **Source:** [ADR-0076](../decisions/0076-live-scoped-authorization.md);
  [Permissions](19-permissions.md#live-permission-enforcement-contract).
- **Type:** actual registry/consumer metadata plus scoped direct/HTTP controls.
  **Kind:** structural and behavioural.
- **Test owners:** `IdentityAuthorizationConventionTests`, `OperationPermissionConventionTests`,
  `OperationReadScopeTests`, `MembershipDirectoryIntegrationTests`,
  `IdentityOperationAuthorizerTests`.
- **Status:** **Implemented** — `OperationPermissionConventionTests`; actual policy
  metadata and scoped direct/HTTP controls preserve read/write boundaries.
- **Phase:** 03 (P03-2).

#### `Permission_Keys_Match_Convention`

- **Asserts:** every actual module definition has the closed three-segment key,
  module/resource/action grammar and explicit scope. Missing/inactive/duplicate
  pairs and invalid action/name controls fail; virtual-public pairs are separate
  from assignable grants and cannot expand the retained v1 manifest.
- **Source:** [ADR-0076](../decisions/0076-live-scoped-authorization.md);
  [Permissions](19-permissions.md).
- **Type:** complete nonempty compiled source/consumer census with dirty controls.
  **Kind:** structural.
- **Test owners:** `IdentityAuthorizationConventionTests`, `OperationPermissionConventionTests`,
  `OperationPolicyCatalogTests`.
- **Status:** **Implemented** — `OperationPermissionConventionTests`; actual nonempty
  registry and invalid-key controls enforce the closed grammar.
- **Phase:** 03 (P03-2).

#### `Permission_Registry_Has_DeniedTest`

- **Asserts:** each actual scoped pair has meaningful actual production-consumer
  denial proof. Virtual-public and confined seed/system classes have their own
  explicit negative controls; an empty scan, matching test name or test-only
  operation cannot establish coverage. Missing-pair and removed-test controls
  fail the complete cross-reference.
- **Source:** [ADR-0076](../decisions/0076-live-scoped-authorization.md);
  [Permissions](19-permissions.md).
- **Type:** registry/production-consumer proof map with removed/missing controls.
  **Kind:** structural with behavioural subjects.
- **Test owners:** `OperationPermissionConventionTests`,
  `RoleGrantCommandIntegrationTests`, `IdentityAuthorizationHttpTests`,
  `SeedOperationSourceTests`,
  `DeliveryOperationSourceTests`, `TrustedJobOperationSourceTests`.
- **Status:** **Implemented** — `OperationPermissionConventionTests`; the complete
  scoped-pair map binds real delegation positives/denials and distinct virtual/finite
  controls.
- **Phase:** 03 (P03-2).

<a id="pending-identity-extensions-to-implemented-rules"></a>

### Identity extensions to Implemented rules

The following rules retain **Implemented** for their shipped populations.
P03-1 extends their named owners without creating duplicate catalogue rules or
promoting later live-admission controls. The phase Status block and delivery log
own current qualification and review state.

| Existing canonical rule(s) | Accepted extension and implemented owner scope |
|---|---|
| [`ModuleInfrastructure_References_Only_Its_Own_Layers_And_Core_Infrastructure`](#moduleinfrastructure_references_only_its_own_layers_and_core_infrastructure), [`CoreInfrastructure_DoesNotDependOn_AnyModule`](#coreinfrastructure_doesnotdependon_anymodule), [`Modules_Do_Not_Write_AuditLog_Directly`](#modules_do_not_write_auditlog_directly) | Step 3: exact migration-only installer/body/caller exception; no copied audit SQL, core module/entity reference or new SQL writer. |
| [`Migrate_Target_Covers_Every_Migration_Chain`](#migrate_target_covers_every_migration_chain), [`Every_Module_With_A_Schema_Is_Swept`](#every_module_with_a_schema_is_swept), [`Every_TenantOwned_Entity_HasFilterAndRlsPolicy`](#every_tenantowned_entity_hasfilterandrlspolicy) | Steps 3/4: sixth Identity chain/Audit prerequisite and complete seven-class population, retaining tenant predicates. |
| [`Every_TenantOwned_Foreign_Key_Includes_TenantId`](#every_tenantowned_foreign_key_includes_tenantid), [`Unique_Indexes_On_Soft_Deletable_Tables_Exclude_Deleted_Rows`](#unique_indexes_on_soft_deletable_tables_exclude_deleted_rows) | Steps 3/4: exact Membership-to-global-User FK and permanent binding index. Other tenant references retain composite/self-keyed shape. |
| [`Every_Organization_Mirroring_Child_Has_A_Parent_Scope_Guard`](#every_organization_mirroring_child_has_a_parent_scope_guard) | Contextual Identity: both contained Membership children join the independent EF/applied relation census and real guard/FK mutation controls; the invoker guard preserves canonical parent-scope refusal and the same fixed adapter descriptor. |
| [`AllowsUnresolvedTenantContext_Only_On_Provisioning_Commands`](#allowsunresolvedtenantcontext_only_on_provisioning_commands), [`Tenant_Context_Guard_Fires_Only_On_An_Unmarked_Transaction`](#tenant_context_guard_fires_only_on_an_unmarked_transaction) | Step 3: closed global authority mode and crossover refusal; provisioning marker/current four context writers do not grow. P03-2 separately implements the accepted ninth candidate setter. |
| [`AuditEntry_Is_AppendOnly`](#auditentry_is_appendonly), [`MustClass_Audit_Writes_Share_The_Business_Transaction`](#mustclass_audit_writes_share_the_business_transaction) | Step 3: ordinary pending sentinel/global-owner refusal, typed global composition and two new central seams with independent failure/uncertainty/duplicate proof. Six ordinary seams and two confined global seams are implemented. |
| [`Tenant_Authentication_Profile_Is_Closed`](#tenant_authentication_profile_is_closed), [`Keycloak_Roles_Are_Not_Application_Authority`](#keycloak_roles_are_not_application_authority), [`Authenticated_Rate_Limits_Use_Validated_Subject`](#authenticated_rate_limits_use_validated_subject), [`Authentication_Middleware_Order_Is_Enforced`](#authentication_middleware_order_is_enforced) | Step 5: verified subject before quota/binding SQL, optional actor evidence, normalized existing binding, uniform 401 and immutable feature/caller census. Numeric quota/fault/once-per-request semantics remain. |
| [`Authenticated_Http_Surface_Is_Enumerated_And_ReadOnly`](#authenticated_http_surface_is_enumerated_and_readonly) | Step 5: existing-binding read, unchanged endpoint/SDK union, AccountSite404/405 and native publication. No POST or tenantless completion; P03-3 owns transport activation. |

### Accepted P03-3 extensions and new obligations

Source: [ADR-0078](../decisions/0078-native-session-scope-and-self-entry.md),
[ADR-0079](../decisions/0079-bounded-identity-administration.md),
[ADR-0080](../decisions/0080-fixed-seed-prerequisite-read-scope.md) and
[ADR-0081](../decisions/0081-fixed-seed-completion-before-origin-adoption.md). These are
Accepted extensions with individually recorded statuses below; shipped
populations above remain Implemented. A source census, proposed test name or
document check does not qualify a new runtime population. The phase Status block
owns delivery state, and later HTTP/native/browser consumers retain their own proof.

| Existing canonical rule | P03-3 extension / proof owner |
|---|---|
| `Global_Identity_Storage_Is_Subject_Confined` and `Global_Identity_Creation_Uses_The_Common_Audit_Owner` | Extend the exact applied function/body/ACL/caller census from five to eight; qualify actual attempt/capacity/discovery owners, no counter reset or general User projection. Existing central success/reconciliation seams remain sole writers. |
| `Identity_Seed_Uses_Finite_Adopted_Manifests` | `IdentitySeederTests` and Tenancy seed controls: pristine-prefix versions, edited completed-state preservation, terminal/tombstone refusal, no individual grant restoration, per-write fence/version recheck and real held-seed locale/profile/grant competitors. Fixed origin adoption's qualified marker-2 transitions, warm preservation, legacy upgrade and zero-dispatch repeat belong to [the separate completion rule](#fixed_seed_origin_adoption_requires_completed_foundation). ADR-0080's [temporary read](#fixed_seed_prerequisites_use_a_closed_temporary_read_scope) and [complete writer census](#tenant_bound_seed_writes_recheck_under_ordered_fences) retain separate authority and proof populations; their own rows record status. |
| `Tenant_Scope_Widening_Is_Never_Set_From_Request_Input`, `Permission_Scope_Matches_Resource_Scope`, `Out_Of_Band_Setters_Open_Read_Only_Transactions`, `Protected_Operations_Require_Explicit_Live_Policies` and `Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork` | Existing human witness, scope-carrier and source/attachment owners gain the exact separate fixed-seed producers and reader populations under [the eight-consumer prerequisite rule](#fixed_seed_prerequisites_use_a_closed_temporary_read_scope) and [one-consumer completion rule](#fixed_seed_origin_adoption_requires_completed_foundation). Their private typed transition shares the same two scope-only enable/clear SQL sites. Preserve Protected-human minters, current owner/source enrollment, ten Tenant setters and four context writers; no new physical owner or READ ONLY claim. Their separate rule rows record qualification under the named owners. |
| `Permission_Definitions_DeclareScope` and `Permission_Registry_Has_DeniedTest` | `OperationPermissionConventionTests` and actual command controls: exact v1 five Identity pairs plus v2 three Tenant Tenancy pairs; finite Any/All alternatives, disabled default, explicit additive adoption and independent denied coverage. No blanket key/scope implication. |
| `Identity_Membership_Invariants_Use_Fresh_Ordered_Locks` | Actual internal member profile/status/assignment commands, retained Role-root grant locks and fresh post-wait delegation; no extra User fence for profile/removal. `MembershipAdministrationProfileTests`, `MembershipAdministrationAssignmentTests` and `MembershipAdministrationRaceTests` own their app-role proof. Existing deferred/MUST rollback and last-admin guarantees remain; later HTTP/native consumers retain separate qualification. |
| `Authenticated_Http_Surface_Is_Enumerated_And_ReadOnly` and `Authentication_Middleware_Order_Is_Enforced` | Exact protected write/global-self descriptors supplement the historical read-only union. Source admission follows verified-subject quota, separate native evidence never rewrites provider facts, and both HTTP/direct application entry retain independent policy checks. Wrong-method/masking, legacy/public and OpenAPI/SDK caller controls cover the complete union. |
| `Bff_Session_Refresh_And_Revocation_Have_One_Owner` | Actual entry/selection/switch owners, unchanged-context refresh with zero new admission-event delta, original absolute deadline, exact-subject live-destination replacement and actual cancellation/cleanup. |
| `Bff_Auth_Admission_And_Csrf_Are_Closed` and `Bff_Host_Cookies_And_History_Are_Isolated` | Exact native form/path/field census, nine advisory flags, no capability in aggregate ETag, bounded snapshot admission 128 global/eight per owner and 32 MiB serialized ceiling. Actual native HTTPS/browser same-label/truncation/Unicode different-subject recovery, four-origin cookies, late publication, history/no-script, en/tr and manual accessibility. |

#### `Fixed_Seed_Prerequisites_Use_A_Closed_Temporary_Read_Scope`

- **Asserts:** the exact eight fixed consumers use a separate private witness,
  actual invocation and immutable declaration on the same ordinary ReadWrite
  owner after their complete ordered plan and target preparation. API, human,
  nested, event and origin-adoption sources cannot obtain it. Separate owner
  state and exact scope-only enable/clear sites preserve Tenant/organization/
  generation, and SQL/EF return narrow before usable evidence or mutation.
  ADR-0081's distinct completion entry reuses one private typed transition and
  those same two SQL sites; its separate minter/consumer census never widens this
  exact eight-consumer pristine population.
  The closed operation exposes no callback, submitted SQL or reusable scope lease.
  Fixed owning-contract readers return only admission/refusal or opaque one-use
  evidence; personal metadata never becomes a DTO, diagnostic, cache or receipt.
  The common first SaveChanges barrier acts before audit/domain-event capture;
  entry/exit change detection, the EF command
  guard, owned raw audit/outbox writes, commit and nested/context/validation paths
  refuse during verification; misuse and uncertain cleanup stay rollback-only.
- **Required controls:** planted extra minters/callers/sites and human-witness
  regressions; actual app-role SQL/EF foreign-Tenant masking and cross-org write
  refusal; same-org and empty saves, owned raw writes and commit refusal;
  enable/clear acknowledgement loss, cancellation, source/generation expiry,
  nested entry, leaked scope and pooled reuse. Prove exact announcement
  preservation and narrow reads after restoration. The held Yoga profile-edit
  race, both commit orders, untouched interrupted resume and completed no-write
  repeat must observe real target state and successful profile counts.
  `FixedSeedPrerequisiteConventionTests`' owner/caller checks and scope sanitizer
  follow the shared private transition while exempting exactly the two reviewed
  SQL sites. Planted extra sites and cross-purpose substitutions
  remain failures; completion has its own independent census below.
- **Source:** [ADR-0080](../decisions/0080-fixed-seed-prerequisite-read-scope.md);
  [ADR-0081](../decisions/0081-fixed-seed-completion-before-origin-adoption.md);
  [operation-bound read authority](05-database.md#operation-bound-read-authority)
  and [warm-seed evidence](05-database.md#warm-seed-evidence-and-origin-adoption).
- **Type:** exact production source/caller/statement census plus real app-role
  PostgreSQL lifetime, fault and competing-commit controls.
  **Kind:** structural and behavioural.
- **Test owners:** `FixedSeedPrerequisiteConventionTests`,
  `FixedSeedPrerequisiteReadTests`.
- **Status:** **Implemented** — `FixedSeedPrerequisiteConventionTests`, `FixedSeedPrerequisiteReadTests`;
  [Step 3 closeout](../roadmap/phase-03-delivery-log.md#p03-3-step-3-review-round-2-and-closeout-2026-10-29).
- **Phase:** P03-3 Step 3.

#### `Fixed_Seed_Origin_Adoption_Requires_Completed_Foundation`

- **Asserts:** only the fixed `AdoptSeedAdministrationOriginCommand` consumes the
  separate privately minted completion witness, with its actual active finite
  Seed source,
  independently captured declaration and immutable prepared target snapshot.
  The minter validates actual owner/unit, retained complete exclusive Tenant plan
  and locked Tenant/existing-mapping or fenced mapping-absence evidence;
  caller versions, hashes or Booleans cannot
  supply admission. Owning contracts verify the retained completed foundation
  on that owner, with narrow Tenancy/Customization reads and fixed Education/
  Identity branch reads inside the temporary hatch. Restoration precedes usable
  results or mutation. The eight pristine consumers and Protected-human witness
  remain separate; no global/provider read, new owner or general reader is added.
- **Required controls:** exact separate minter/consumer/projection census and
  forged-source/policy/request/target/plan/owner/generation controls; reject
  cross-purpose reuse, extra scope SQL, callback/lease surfaces and profile/
  global/provider value projections. Reuse the prerequisite rule's shared private
  transition/barrier controls without enlarging its pristine census.
  Actual app-role PostgreSQL controls refuse provisioning-only and every
  incomplete stage, including a final other-branch profile and content pin;
  completed legacy/fresh installations preserve lawful profile/assignment/grant,
  locale/theme/Organization edits and retained terminal nonadmins. A revoked
  former admin needs a qualifying local replacement; both kernel pairs remain
  required and lawful nonkernel removals remain removed. Required Membership
  aggregate version zero refuses; positive current retained state proves no
  original profile-initializer or fresh global eligibility history.
  Cover held-Tenant competing commits in both orders, actual target locks,
  unchanged mapping/marker and successful audit counts on refusal, plural-MUST
  rollback, exact-existing mappings, foreign masking, restoration/cleanup and
  independent two-Tenant resume. Forward guards admit only qualified `0→2` and
  `1→2`, reject new `0→1`, decrements and jumps, and preserve old marker-1 history.
  Runner controls prove marker 0 dispatch/expect-2, marker 1 exact-mapping check
  then dispatch/expect-2, marker 2 exact-mapping check then zero dispatch/witness/
  hatch/intent, and other-marker refusal. Warm `make seed` accepts marker 2.
  Marker-2 repeat records historical adoption, not current mutable-graph proof;
  genuine unknown-COMMIT controls follow the canonical recovery contract without
  inferring rollback or automatically replaying a mutation.
- **Source:** [ADR-0081](../decisions/0081-fixed-seed-completion-before-origin-adoption.md);
  [closed completion authority](05-database.md#fixed-seed-completion-read-authority)
  and [warm-seed evidence](05-database.md#warm-seed-evidence-and-origin-adoption).
- **Type:** exact compiled source/consumer/projection and shared SQL-site census,
  plus real app-role PostgreSQL completion, transition, fault and race controls.
  **Kind:** structural and behavioural.
- **Test owners:** `SeedOriginCompletionConventionTests`,
  `SeedOriginCompletionTests`.
- **Status:** **Implemented** — `SeedOriginCompletionConventionTests`, `SeedCompletionReadTests`,
  `SeedOriginCompletionTests` and `SeedOriginCompletionGraphTests`;
  [Step 3 closeout](../roadmap/phase-03-delivery-log.md#p03-3-step-3-review-round-2-and-closeout-2026-10-29).
- **Phase:** P03-3 Step 3; Phase 06 reopens the admitted content-writer premise.

#### `Tenant_Bound_Seed_Writes_Recheck_Under_Ordered_Fences`

- **Asserts:** the actual 21 v1 writer types and separate fixed origin-adoption
  writer each hold their complete ordered Tenant/root plan and freshly check
  prerequisites on that owner before mutation. The eight temporary readers use
  the [prerequisite rule](#fixed_seed_prerequisites_use_a_closed_temporary_read_scope);
  `ProvisionTenant` stays INSERT-first. The twelve content
  writers use narrow owning contracts for pristine Tenancy and absence of all
  retained Role history, plus exact target/predecessor/natural-key/version and
  immutable authored pins under the exact fixed source. Proposed Course/Lesson
  creation cannot skip its missing predecessor; fixed branding requires completed
  Customization on the same narrow owner and supplied plan, with exactly two
  prerequisite caller sites and one independent reader implementation.
  Tenant-wide Role storage,
  denied DELETE and v1 Role-before-Membership ordering establish the stage proof;
  inactive, tombstoned and undeclared Role IDs cannot evade it. Built-in event
  competitors use their separate source and at least shared Tenant before
  quota/concept locks. Origin adoption retains its separate completion/topology
  and plural-audit contract under the [separate completion rule](#fixed_seed_origin_adoption_requires_completed_foundation);
  global User adoption stays outside Tenant ownership.
- **Required controls:** independently complete writer/competitor census with
  missing/extra-source and removed-guard controls; actual held-Tenant/first-Role
  competitors and both commit orders for all twelve content writer types;
  retained/conflicting/absent roots, exact fixed-source denial and untouched
  interruption at every stage, including later canonical Course/Lesson creation
  and premature branding followed by successful ordinary resume. Preserve valid
  completed edits, prove no-write
  repeats and refuse incompatible incomplete history. This proves the admitted
  writer population, not an all-branch snapshot or forged database history.
- **Source:** [ADR-0079 Amendment 2](../decisions/0079-bounded-identity-administration.md#amendment-2--complete-seed-write-census-and-prerequisite-proof-2026-10-29),
  [ADR-0080](../decisions/0080-fixed-seed-prerequisite-read-scope.md) and
  [ADR-0081](../decisions/0081-fixed-seed-completion-before-origin-adoption.md);
  [warm-seed evidence](05-database.md#warm-seed-evidence-and-origin-adoption).
- **Type:** complete compiled production-writer/source census and real app-role
  PostgreSQL interruption, preservation and competing-commit controls.
  **Kind:** structural and behavioural.
- **Test owners:** `SeedContinuationConventionTests`, `SeedContentContinuationTests`,
  `IdentitySeederTests` and `SeedAdministrationOriginTests`. The
  [Phase 03 Status block](../roadmap/phase-03-identity-admin.md) distinguishes these
  local controls from later HTTP/native/browser and final A/B qualification.
- **Status:** **Implemented** — `SeedContinuationConventionTests`, `SeedContentContinuationTests`,
  `IdentitySeederTests` and `SeedAdministrationOriginTests`;
  [Step 3 closeout](../roadmap/phase-03-delivery-log.md#p03-3-step-3-review-round-2-and-closeout-2026-10-29).
- **Phase:** P03-3 Step 3; Phase 06 reopens staging before mutable content administration.

#### `Native_Scope_Evidence_Is_Separate_And_Host_Bound`

- **Asserts:** verified provider subject, normalized stored actor and native scope
  are separate immutable evidence. Current/previous purpose-secret qualification,
  expiry, exact header/source/request binding and real host ceiling refuse
  foreign/stale/partial/retargeted evidence. Provider/native tuple disagreement
  and legacy assertion mismatch never select a winner. Native Tenant/Organization
  scope cannot admit Platform/public/provisioning authority or a SQL write hatch.
- **Source:** ADR-0078; [Security contract](11-security.md#native-scope-and-global-self-admission).
- **Type:** compiled caller/source/pipeline census and actual HTTP/app-role
  candidate/AccountSite controls. **Kind:** structural and behavioural.
- **Planned test owners:** `NativeScopeAdmissionTests`,
  `IdentityAuthorizationHttpTests`, `IdentityAuthorizationConventionTests`.
- **Status:** **Registered.**
- **Phase:** P03-3 Steps 2/7/8.

#### `Global_Self_Entry_Is_Bounded_And_Subject_Confined`

- **Asserts:** only the exact three subject/quota/native-host descriptors branch
  before ordinary resolution. Genuine absence differs from terminal/conflicting
  binding. Own discovery derives actor from subject and leaks no foreign cursor,
  profile, Role or cross-module SQL data. The tenth captured-scope setter uses
  sequential physically READ ONLY owners and retains cleanup. Real PostgreSQL
  concurrent creation, failed/unknown commit, restart, UTC regression, selected
  attempt/permanent bounds and seed-inclusive counters preserve central atomic
  MUST creation and natural-key convergence. Real tenantless/actorless provider
  entry creates no Membership or fabricated session; existing bindings consume
  no new creation attempt.
- **Source:** ADR-0078; [Storage contract](05-database.md#p03-3-storage-and-replay-contract).
- **Type:** actual app-role PostgreSQL/HTTP/provider/native controls plus exact
  function/policy/privilege/source census. **Kind:** behavioural and structural.
- **Planned test owners:** `GlobalIdentityBootstrapTests`,
  `IdentitySelfEntryHttpTests`, `IdentitySelfDiscoveryOwnerTests`,
  `NativeScopeAdmissionDurabilityHttpTests`, `NativeScopeAdmissionTests` and
  `TenantBffSessionTests`. The source/global/HTTP/READ ONLY core has local controls;
  the later provider/native/browser obligations still require qualification.
- **Status:** **Registered.**
- **Phase:** P03-3 Steps 2/7/8.

#### `Protected_Idempotent_Replay_Reauthorizes_Original_Target`

- **Asserts:** actual Postgres adapter survives two owners/restart and enforces
  1,000 retained tenant slots, existing-key precedence, expired-row recycling and
  old-fence refusal. Capacity/claim owners close before business User/Tenant locks;
  completion follows actual business disposal. Protected version fingerprints,
  successful-only bounded receipts and original typed targets survive uncertainty
  without an exactly-once claim. Fresh target/self/current-authority MUST receipt
  read commits before replay; revoked caller, successor Membership and failed
  replay-audit controls preserve the stored completion without disclosure.
- **Source:** ADR-0079; [Storage contract](05-database.md#p03-3-storage-and-replay-contract).
- **Type:** actual PostgreSQL/process-restart/HTTP and source/target census.
  **Kind:** behavioural and structural.
- **Test owners:** `PostgresIdempotencyStoreTests`, `ModuleReceiptDisclosureTests`,
  `ModuleReceiptAuditFailureTests`, `UnitOfWorkCommitCleanupTests`,
  `ProtectedIdempotencyHttpTests`, `IdempotencyConventionTests`.
  These include internal/module/component controls; production HTTP/native replay
  qualification remains required by the full rule before Implemented status.
- **Status:** **Registered.**
- **Phase:** P03-3 Steps 6/7/8.

#### `Tenancy_Default_And_Archive_Use_Fresh_Ordered_Fences`

- **Asserts:** actual default/archive transactions and invoker guards preserve
  an Active own default and at least one Active Organization. Independent default
  selection and archive write their single declared root, require orgless current
  Tenant capability and fresh post-wait lifecycle/authorization. Held competing
  branch mutations/default changes/archives prove both winner orders, timeouts,
  direct app-role refusal and central MUST rollback. Retained NULL-default
  no-op/reset and competing archive controls cannot manufacture INSERT-first
  initiality; real untouched provisioning remains allowed. Locale/default/theme human
  wrappers cannot manufacture Seed provenance or bypass root versions.
- **Source:** ADR-0079; [Tenancy specification](../modules/tenancy/README.md).
- **Type:** actual app-role races/guard/fault controls and finite policy/source
  census. **Kind:** behavioural and structural.
- **Test owners:** `TenancyAdministrationTests`,
  `TenancyAdministrationRaceTests`, `TenancyAdministrationBranchRaceTests`,
  `TenancyAdministrationAdditionalProofTests`, `TenancyAdministrationMigrationTests`,
  `TenancyDefaultInitialityTests`, `TenancyOrganizationConflictTests`,
  `TenancyWriterTests`, `TenancySchemaTests`, and their protected HTTP counterparts.
- **Status:** **Registered.**
- **Phase:** P03-3 Steps 3/5/7.

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
- **Implemented P03-2 extension.** ADR-0077 adds one central denial occurrence
  beside the separately classified resource intents. Real policy refusals count
  both populations exactly; `AuthorizationEvidenceBarrierTests` covers absorbed
  denials, ReadWrite/ReadOnly/ownerless pre-success barriers, fixed-key
  reconciliation and fault/crash outcomes under
  [Audit Coverage](18-audit-coverage.md#trusted-authorization-evidence).
  These controls extend Packet 9's success/rollback baseline. A handler-returned
  `Result.Fail(forbidden)` alone proves neither policy admission nor the separate
  event's pre-success barrier. The
  [Phase 03 Status block](../roadmap/phase-03-identity-admin.md)
  owns source-bound qualification.
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
- **Status:** **Implemented** — `AuditPipelineTests` (integration), Packet 10. With
  `SELECT` on `audit_config` **revoked** from `learnstack_app`, the seed's MUST commands still
  complete and still write their rows. Then the property itself, which is **not** about the
  answer: a failed read and a read that never happened return the same tier, because
  `ClassifyAsync` falls back to the declared class on either — so the case observes whether the
  data source was **asked for**, through a `Lazy` whose factory records the request. A MUST
  never asks; a SHOULD, the overridable tier, does ask, fails under the revoke, and still
  answers `Should`. The revoke is what makes the read observable at all: under Row Level
  Security it would return zero rows **silently**, and silence is indistinguishable from "this
  tenant has no overrides". Mutation-checked twice: removing the loader's fallback fails it, and
  so does moving the MUST short-circuit after the read — which left every other suite in the
  repository green. `AuditLogBehaviorTests.An_unregistered_request_is_refused_and_the_handler_never_runs`
  holds the other half — the `audit_unclassified_operation` rejection — where a handler can be
  registered for a request the catalogue does not know; the seeder's composition root ships one
  request type per shipped command and has no unregistered one to send.
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
  next request. `Clear()` is called exactly once, by the **outermost**
  `AuditExecutionCoordinator`, in its `finally`; `AuditLogBehavior` delegates to it.
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
  `DeliveryExecutionTests` additionally observes the unmodified production capture
  populated by real saves, then empty on successful, faulted and lost-COMMIT
  delivery. A completed retry produces no new frame or indeterminate row.
  `AuditCaptureWiringTests` checks the real `IAuditStateCapture`/`IAuditSubject`
  aliases and cross-scope isolation. `AuditStateCaptureTests` exercises cleared
  nested state independently; the behavior tests retain exactly-once call proof.
- **Phase:** 02a (Packet 9).

#### `Every_Shipped_Request_Is_Registered`

- **Asserts:** every request type with a closed `IRequestHandler<,>` or
  `IRequestHandler<>`, and every concrete `IIntegrationEventHandler<>` implemented
  in any project under `backend/src`, is registered in the merged audit catalogue,
  `Off` included. Delivery classification keys the concrete handler, not its event;
  sibling handlers remain independent. Discovery fails loudly on incomplete loads.
  An unclassified request fails the build rather
  than defaulting to silence; at runtime, reaching step 3 unclassified is
  `audit_unclassified_operation`, and there is no `RequestKind.Other`.
- **Why it is keyed on the request type.** `AuditLogBehavior` looks a registration up by
  request type, so the rule has to as well. The two slug-level directions cannot see a
  request type nobody registered when another request shares its slug: measured by the
  review of Packet 9, removing only `MustAudit<CreateOrganizationCommand>` left
  `tenancy.organization.create` registered through provisioning, both directions stayed
  green, and the running system would have refused every `CreateOrganizationCommand`.
- **Companion:** `The_Request_Sweep_Can_Actually_Fail` runs the predicate over exactly that
  catalogue and requires it to name the missing request. P02b-3 Step 6 adds
  handler-specific missing/Off/event-only and inherited-contract controls in
  `AuditCoverageIntegrationTests.cs`.
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
    Nine rows carry `(off-path)`: Tenancy's `platform.admin_scope.enter`,
    `platform.outbox.dead_letter`, `platform.delivery.dead_letter`,
    `tenancy.killswitch.toggle`, `tenancy.entitlement.refresh`,
    `tenancy.tenant_assertion.reject` and `tenancy.tenant_assertion.anonymous_burst` —
    snake_case within each segment since
    [ADR-0036 Amendment 7](../decisions/0036-tenant-resolution-trusted-inputs.md), so that
    one parser reads every audit slug and every permission key — and Audit's
    `audit.redaction.apply` and `audit.purge.apply`. They sit outside the request-type join
    in both directions and register by slug: the four whose writer ships through
    `DeclareOffPath` today, the other five — which also carry `(planned)` — when their
    writer lands. ADR-0046 assigns the producer terminal writer to P02b-2 and the
    subscriber terminal writer to P02b-3; acceptance alone registers neither slug.

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

- **Asserts:** every module shipping an aggregate root, request type or concrete
  integration handler — an `IAggregateRoot<>`, request with a handler or
  `IIntegrationEventHandler<>`, whose namespace is
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
  P02b-3 Step 6 adds `Integration_Only_Module_Requires_An_Audit_Matrix`.
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
- **Status:** **Implemented** (`AuditConventionTests`, Packet 9 step 8), in three parts:
  a source sweep over `backend/src` for an `UPDATE` or `DELETE` targeting `audit_log` —
  with or without `ONLY`, a schema qualifier or identifier quotes, which the first
  pattern missed until the fourth review of Packet 9 measured it; a companion,
  `The_AppendOnly_Sweep_Can_Actually_Fail`, that checks the pattern against the shapes
  it must catch and the shapes it must not, because with no offending statement anywhere
  the sweep passes whether it works or matches nothing; and a reflection check,
  `IAuditStore_Exposes_No_Update_Method`, that `IAuditStore` exposes exactly eight
  write methods and no update: the event/job seams ship in Phase 02b, and
  ADR-0072's two global seams ship in P03-1 Step 3. None
  of the three sanctioned redaction sites exists yet — they land in Phase 03 and Phase
  11 — so the exemption set ships with the rule **empty**, as exact paths: the first
  site to land adds its own path, so it is exempted by name rather than the pattern
  widened. (A substring predicate stood there first, and exempted any Audit-module file
  whose path contained "Redaction".) Since the fifth review of Packet 9 the sweep also
  asserts its premise — it must have read `PostgresAuditStore`'s own `INSERT INTO
  audit_log` — because with no offender anywhere, a sweep that read nothing passed as
  well.
- **Phase:** 02a (Packet 9).

#### `Every_PII_Module_RegistersUserReferenceLocator`

- **Asserts:** every module whose audit payloads carry a user reference registers an
  `IUserReferenceLocator` implementation. A module that stores such a reference and ships
  no locator leaves rows a GDPR erasure cannot reach — the redaction handler redacts the
  actor columns centrally and delegates the payload columns to the module that knows
  which JSON paths in its own snapshots name a user
  ([31-audit-subsystem.md § 10](../architecture/31-audit-subsystem.md)).
- **Registered here, asserted in Phase 03.** The
  [packet plan](../roadmap/phase-03-identity-admin.md#inherited-architecture-obligations)
  assigns the applicable locator census and privileged redaction handler to
  P03-10. P03-1's User foundation supplies their identity prerequisite; it does
  not itself deliver the erasure workflow. This refines the phase-level timing in
  [ADR-0044 § What we explicitly punted on](../decisions/0044-audit-write-path.md),
  without granting a redaction writer. Until that implementation there is no
  locator population to count. Reserving the
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
  `tenant_id`, full W3C `correlation_id` and `partition_key` columns. Integration
  proof writes through `IOutbox.EnqueueAsync` and compares persisted `id` to
  `EventId`, announced tenant/organization and the event's declared key, including
  unresolved provisioning. A null/wrong-column control must fail.
- **Source:** ADR-0032 § Sub-decision 12, as bounded by
  [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md);
  [ADR-0006](../decisions/0006-events-and-outbox.md) Amendment 1.
- **Type:** integration test (Testcontainers). **Kind:** behavioural.
- **Status:** **Implemented** (`OutboxProducerTests`, `TenantCreatedProducerTests`);
  actual stored values and unresolved provisioning run as `learnstack_app`.
- **Phase:** 02b, P02b-1.

> `Hangfire_Job_Payloads_Include_TenantId` is retired by ADR-0047; see
> [the replacement mapping](#hangfire_job_payloads_include_tenantid).

#### `Integration_Event_Handler_Restores_Tenant_Context`

- **Asserts:** the independent delivery executor restores resolved tenant and
  organization before business construction, keeps effective SystemActor separate
  from human causation, and restores the prior context after actual cleanup. The
  real app-role transaction contains inbox, business, produced outbox, MUST audit
  and fenced ledger completion; refusal/rollback controls preserve isolation.
- **Source:** ADR-0032 § Sub-decision 12 and
  [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md#subscriber-acceptance-and-inbox-ownership).
- **Type:** actual `DeliveryExecutor` integration (Testcontainers), with constructor,
  callback, prior-context, failure and independent real-default controls.
  `InProcessEventBus` performs durable acceptance and constructs no business handler.
  **Kind:** behavioural.
- **Status:** **Implemented**
  (`DeliveryExecutionTests.Integration_Event_Handler_Restores_Tenant_Context`).
  The [HTTP producer proof](../../backend/tests/LearnStack.Tests.Integration/Database/OutboxProducerHttpTests.cs)
  also checks the real HTTP producer's stored trace,
  announced tenant/org and exact nonempty partition; the delivery proof checks
  the same quartet for its outgoing event. Actual production consumers are Active.
  Its lost-COMMIT theory independently counts exactly one row-bound standalone
  `indeterminate` audit beside the durable success row, observes one outer capture
  clear and proves that a rejected completed retry changes neither count.
- **Phase:** 02b, P02b-3.

#### `Integration_Event_Handlers_Use_InboxGuard`

- **Asserts:** the transport claims the shared inbox before constructing each
  declared subscription's handler. Business state, completed inbox, produced
  outbox, MUST audit and fenced ledger completion share the announced app-role
  transaction. Concurrent duplicates serialize; two same-module subscriptions
  have distinct identities. Stale tokens, expired leases and a closed timeout
  gate roll back all business work. Completed inbox rows survive targeted replay.
  A nonempty production census and planted dirty/clean controls prevent an empty
  handler set from appearing enforced.
- **Source:** [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md#subscriber-acceptance-and-inbox-ownership),
  superseding ADR-0006/0010's handler-called guard assertion.
- **Type:** architecture call-path census and integration tests (Testcontainers).
  **Kind:** structural + behavioural.
- **Status:** **Implemented** (`OutboxDispatchConventionTests`, with
  `DeliveryExecutionTests`, `CustomizationDefaultDeliveryTests` and
  `DeliveryDispatchStoreTests`). Step 6's
  [handler/manifest census](../../backend/tests/LearnStack.Tests.Architecture/IntegrationDeliveryBoundaryTests.cs)
  requires all production assemblies/layers and the two exact scoped Active
  bindings. It detects typed construction, inherited implementation/virtual-slot
  invocation, DI resolution through statically bound generic helpers, async and
  delegate bypasses, with missing-subject and dirty/clean controls. Generic type
  and method argument membership states bound recursive helper traversal.
  The [lifecycle census](../../backend/tests/LearnStack.Tests.Architecture/IntegrationDeliveryLifecycleBoundaryTests.cs)
  pins typed unit/frame and database-transaction completion sites, including
  known EF transaction/facade APIs, and their multiplicity. It also pins the
  three `SetTenantContextAsync` call sites in the ordinary owner, prepared delivery
  owner and durable acceptance, with missing-site and extra interface/concrete/
  inherited/async/delegate caller controls. These implement two of ADR-0040's
  eight logical setter paths rather than adding a new path. These are bounded
  static capability checks, not arbitrary
  reflection, raw SQL transaction analysis or branch-order proofs; the real
  PostgreSQL tests own inbox-before-construction and transaction atomicity.
  The owner census also pins completion construction to the executor's owned
  path, with extra direct/asynchronous and missing-constructor-site controls.
  Earlier structural companions remain
  `OutboxDispatchConventionTests.Delivery_owner_and_inbox_capabilities_are_closed`
  and
  [Delivery_owner_census_rejects_extra_direct_delegate_async_and_inherited_capabilities](../../backend/tests/LearnStack.Tests.Architecture/DeliveryOwnerBoundaryTests.cs).
  They enumerate all production assemblies, prepared-owner minting/consumption,
  inbox callers, completion subtypes and common-owner fence/commit capabilities.
- **Phase:** 02b, P02b-3.

#### `Integration_Events_Inherit_From_IntegrationEventBase`

- **Asserts:** every type implementing `IIntegrationEvent` extends `IntegrationEventBase`,
  which carries `EventId`, `OccurredAt` and `TenantId` as `required` members and declares
  `Topic` and `PartitionKey` abstract, and is a JSON-serialisable record. The payload is
  written by `ToPayloadJson()`, which serialises by runtime type — serializing through the
  interface silently drops every member the concrete event adds.
- **Source:** [15-event-and-outbox.md § Architecture tests](../architecture/15-event-and-outbox.md);
  [ADR-0006](../decisions/0006-events-and-outbox.md).
- **Type:** xUnit + reflection over module assemblies. **Kind:** structural.
- **Status:** **Implemented** (`IntegrationEventContractTests`); all production projects
  and module layers, required base members and dirty/clean inheritance controls.
- **Phase:** 02b.

#### `Integration_Event_Declares_PartitionKey`

- **Asserts:** every concrete integration event declares its partition source:
  the aggregate identifier, or explicit tenant-wide `TenantId`. The key is that
  identifier's canonical GUID representation and is non-null/non-blank. A complete
  production-module census checks declaration/derivation, with planted constant,
  unrelated-ID and missing-declaration offenders plus clean controls. Behavioral
  cases compare actual source/key values. The envelope reads the event's key;
  it cannot hold a second answer. This establishes derivation, not durable order;
  ADR-0046 defines eligible-selection order and its limits.
- **Source:** [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md);
  [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md#producer-dispatch-and-recovery);
  [15-event-and-outbox.md](../architecture/15-event-and-outbox.md#ordering).
- **Type:** xUnit + reflection/IL/source scan and value probes.
  **Kind:** structural + behavioural.
- **Status:** **Implemented** (`IntegrationEventContractTests`, `OutboxProducerTests`);
  stored-source IL grammar, independently varied identifiers, dirty/clean controls and
  persisted keys. Unsupported derivations fail loudly.
- **Phase:** 02b, P02b-1 (derivation); P02b-2 (selection-order bounds).

#### `Outbox_Enqueue_Uses_The_Announced_Transaction`

- **Asserts:** enqueue uses the enrolled owner's live announced connection and
  transaction as `learnstack_app`, for resolved and unresolved provisioning.
  Mismatched/empty/sentinel tenants, read-only units and stale generations refuse;
  no extra connection, commit or publish occurs. Snapshot cases cover failed or
  canceled setup, foreign/reused transaction objects, joins and every terminal
  boundary. No-listener trace context, exact causal/envelope metadata and runtime
  JSON survive persistence. Complete canonical UTF-8 payloads at the inclusive
  262,144-byte bound succeed; oversized/multibyte cases refuse before INSERT.
  JSON metadata is SQL NULL. Duplicate INSERT fails; rollback and MUST-audit
  failure leave source/outbox uncommitted.
- **Source:** [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md).
- **Type:** integration test (Testcontainers), with focused lifecycle unit cases.
  **Kind:** behavioural.
- **Status:** **Implemented** (`OutboxProducerConventionTests`, `OutboxProducerTests`,
  `UnitOfWorkTests`, `ProducerExecutionTests`, `OutboxContractTests`); transitive
  no-connection/commit/publication controls complement real transaction, byte-bound and
  metadata cases.
- **Phase:** 02b, P02b-1.

#### `Producer_Failures_Poison_Produced_Work`

- **Asserts:** successful outbox INSERT or successful-save event promotion leaves
  sticky receipts in its invocation and every ancestor. Direct and descendant
  receipts survive drain; absorbed `Result.Fail` cannot commit and poisons before
  UoW-frame resolution. An ordinary event-free absorbed refusal still commits.
  Failed/canceled saves do not promote; uncollected raises and unsuccessful
  enqueue attempts do not themselves count. Terminal/same-scope reuse cases prove
  source lists never replay into another transaction.
- **Source:** [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md).
- **Type:** integration and focused lifecycle unit tests. **Kind:** behavioural.
- **Status:** **Implemented** (`ProducerExecutionTests`,
  `OutboxProducerTests.Actual_INSERT_receipts_prevent_an_absorbed_direct_or_descendant_refusal_from_committing`,
  `DomainEventProductionTests`); actual INSERT/save receipts, every ancestor, event-free
  controls and terminal reuse.
- **Phase:** 02b, P02b-1.

#### `Domain_Event_Completion_Is_Owner_Only`

- **Asserts:** a single owner dispatches successful-save events FIFO before
  invariants/MUST audit/commit; nested joiners neither dispatch nor clear owner
  state. Repeated saves, detach/delete, rollback/cancellation, save failures and
  terminal cleanup preserve attribution without loss/duplication. At most 256
  events belong to the transaction; a 257th rolls back all source/outbox work.
  Returned versions remain accurate; MUST-audit failure preserves atomicity.
  A controlled non-MediatR owner opens the common audit frame, classifies direct
  work and reconciles once, retaining nested command intents without duplication.
- **Source:** [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md).
- **Type:** integration and lifecycle unit tests. **Kind:** behavioural.
- **Status:** **Implemented** (`DomainEventProductionTests.The_owner_drains_FIFO_across_sources_saves_delete_and_detach_without_duplicates`,
  `TenantCreatedProducerTests`, `ProducerExecutionTests`); real save qualification,
  FIFO/bounds, owner/common-audit atomicity and cleanup.
- **Phase:** 02b, P02b-1; P02b-3 Steps 1–5 deliver production subscriber execution.

#### `Domain_Event_Handlers_Are_Outbox_Only`

- **Asserts:** the complete production-module domain-handler census translates
  facts to outbox only. Traverse helpers, interface targets and derived class
  implementations; root mutation,
  write stores, nested requests, further raises, notification publication and
  external providers are forbidden. Every forbidden path has a planted offender
  and a clean translation control, so empty subjects or shallow traversal fail.
- **Source:** [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md).
- **Type:** xUnit + reflection/IL/source scan. **Kind:** structural.
- **Status:** **Implemented** (`DomainEventHandlerTests`); assembly-scoped transitive
  helper/interface/derived-class traversal, all production layers and dirty/clean
  capability controls.
  Static analysis does not claim to resolve arbitrary reflection or dynamically loaded
  code.
- **Phase:** 02b, P02b-1.

#### `Integration_Event_Payloads_Contain_Only_Identifiers`

- **Asserts:** beyond inherited identity/tenant/time and computed routing members,
  production payload fields are GUIDs or approved strongly-typed identifiers,
  optionally nullable. Reject text, collections, DTOs, binary/raw JSON and custom
  serializer overrides with planted controls. A non-empty production census and
  concrete-runtime JSON round-trip prove fields survive the frozen serializer.
  Existing partition and announced-transaction rules own derivation, byte/metadata
  admission and rollback/atomicity; this rule does not duplicate those names.
- **Source:** [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md).
- **Type:** xUnit + reflection/IL/source scan and serialization probes.
  **Kind:** structural + behavioural.
- **Status:** **Implemented** (`IntegrationEventContractTests`); independent reflection
  checks, forbidden serialization/member controls and concrete runtime JSON probes.
- **Phase:** 02b, P02b-1.

#### `OutboxProcessor_NeverBlocks_OnSingleMessageFailure`

- **Asserts:** ordinary publication failure and a sustained terminal-audit failure
  backlog permit unrelated normal publication and expired-claim recovery to
  progress. A callback ignoring cancellation retains its live task-owned scope,
  blocks its own partition and makes health unhealthy; unrelated keys progress
  within the approved four-task bound. Saturation stops acquisition, late results
  cannot acknowledge, and shutdown waits finitely without disposing live resources.
- **Source:** [15-event-and-outbox.md § Architecture tests](../architecture/15-event-and-outbox.md);
  Phase 02b deliverable.
- **Type:** **integration** test (Testcontainers). **Kind:** behavioural.
- **Status:** **Implemented** (`OutboxProcessorTests`, `OutboxPublishAttemptTests`):
  actual hosted ordinary-failure, retained-scope/cleanup, partition hold, four-task
  saturation and finite-shutdown proofs; hostile synchronous construction and
  cancellation callbacks retain ownership through completion. A real PostgreSQL
  audit-grant refusal backlog outliving its leases preserves terminal rollback
  while independent durable acceptance and recovery continue.
- **Phase:** 02b.

#### `Outbox_Claim_IsHeld_Until_Dispatch_Completes`

- **Asserts:** two independent processor instances have at most one live owner of
  a row. A claim commits before publication and is fenced by a fresh acquisition
  token and database-time lease. Release-on-read controls fail. Expiry/crash permits
  redelivery; same-worker reacquisition rejects both stale success and stale failure.
  This proves live ownership and at-least-once recovery, not exactly-once publication.
- **Source:** [Phase 02b](../roadmap/phase-02b-events-auth.md);
  [15-event-and-outbox.md](../architecture/15-event-and-outbox.md).
- **Type:** **integration** test (Testcontainers, two independent instances).
  **Kind:** behavioural.
- **Status:** **Implemented** (`OutboxProcessorTests`, `OutboxDispatchStoreTests`):
  two actual API workers contend while a live publication is held; committed
  leases, repeat reads, expiry/reclaim and stale transitions have real-role
  controls. Removing live-lease admission makes the hosted contention proof fail.
- **Phase:** 02b.

#### `Outbox_Retry_And_Terminal_Transitions_Are_Fenced`

- **Asserts:** Normal claims count once, stop at six and use the five approved delays.
  Expired claims require fresh-token recovery; a sixth expired claim never publishes a
  seventh time. Every transition checks identity, token and database-time expiry.
  Terminal requests never reenter normal publication, including after audit refusal.
- **Source:** [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md);
  [P02b-2 proof obligations](../roadmap/phase-02b-delivery-log.md#decisive-proof-obligations).
- **Type:** production architecture census with planted controls where structural;
  real PostgreSQL/runtime integration proofs where behavioural.
  **Kind:** structural + behavioural.
- **Status:** **Implemented** (`OutboxDispatchConventionTests`,
  `OutboxDispatchStoreTests`): exact production literal-SQL writer census with
  quoted/schema/case/CTE and missing/extra-writer controls; committed claim/backoff,
  expiry/reclaim, sixth-claim recovery and terminal-request refusal proofs. The
  SQL census does not claim arbitrary dynamic-SQL dataflow analysis.
- **Phase:** 02b, P02b-2.

#### `Outbox_Terminal_Transition_And_Must_Audit_Are_Atomic`

- **Asserts:** The fenced terminal UPDATE and MUST/SecurityEvent INSERT commit
  together. Failed audit or stale ownership commits neither; terminalization retries
  never publish. Unknown COMMIT reconciles durable state before retry and cannot
  duplicate the terminal audit. Exact real-role grants allow only the declared writes.
- **Source:** [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md);
  [P02b-2 proof obligations](../roadmap/phase-02b-delivery-log.md#decisive-proof-obligations).
- **Type:** integration tests (Testcontainers), including physical COMMIT-response
  loss and real-role grant controls.
  **Kind:** behavioural.
- **Status:** **Implemented** (`OutboxDispatchStoreTests`,
  `OutboxDispatchMigrationTests`), with these proofs:
  [OutboxDispatchStoreTests.Refused_audit_rolls_back_the_terminal_update_and_retains_the_request_for_audited_retry](../../backend/tests/LearnStack.Tests.Integration/Database/OutboxDispatchStoreTests.cs),
  [OutboxDispatchStoreTests.Expiry_alone_refuses_every_transition_without_changing_state](../../backend/tests/LearnStack.Tests.Integration/Database/OutboxDispatchStoreTests.cs),
  [OutboxDispatchStoreTests.A_lost_physical_COMMIT_response_reconciles_durable_terminal_state_without_duplicate_audit](../../backend/tests/LearnStack.Tests.Integration/Database/OutboxDispatchStoreTests.cs),
  [OutboxDispatchStoreTests.A_lost_COMMIT_response_and_failed_reconciliation_preserve_both_failures_without_duplicate_audit](../../backend/tests/LearnStack.Tests.Integration/Database/OutboxDispatchStoreTests.cs),
  and [OutboxDispatchMigrationTests.Outbox_role_has_no_audit_read_mutation_or_business_access](../../backend/tests/LearnStack.Tests.Integration/Database/OutboxDispatchMigrationTests.cs)
  prove audit-grant refusal, atomic rollback/retry, stale ownership, durable
  reconciliation and completed-row replay without duplicate audit.
- **Phase:** 02b, P02b-2.

#### `Outbox_Terminal_Audit_Identity_Is_Row_Bound`

- **Asserts:** Only the successful fenced UPDATE result mints a restricted
  EventTerminalTransition witness. A nonempty production mint/call-site census has
  planted clean/dirty controls. Real bypass-role proofs substitute tenant,
  organization, event and transaction against that witness and roll back both writes;
  matching organization-scoped identity succeeds.
- **Source:** [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md);
  [P02b-2 proof obligations](../roadmap/phase-02b-delivery-log.md#decisive-proof-obligations).
- **Type:** production architecture census with planted controls where structural;
  real PostgreSQL/runtime integration proofs where behavioural.
  **Kind:** structural + behavioural.
- **Status:** **Implemented** (`OutboxDispatchConventionTests`,
  `OutboxDispatchStoreTests`): exact nonempty production mint/write censuses with
  planted controls and real-row identity/transaction refusal proofs.
- **Phase:** 02b, P02b-2.

#### `Outbox_Type_Resolution_Is_Declared_And_Bounded`

- **Asserts:** Only declared concrete contract identities and explicit compatible
  aliases resolve; collisions refuse composition. Unknown/retired/malformed/mismatched
  data terminalizes without arbitrary type loading. Frozen options, canonical UTF-8
  admission, microsecond timestamp comparison and full row/envelope agreement have
  positive and negative controls.
- **Source:** [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md);
  [P02b-2 proof obligations](../roadmap/phase-02b-delivery-log.md#decisive-proof-obligations).
- **Type:** production architecture census with planted controls where structural;
  real PostgreSQL/runtime integration proofs where behavioural.
  **Kind:** structural + startup + behavioural.
- **Status:** **Implemented** (`OutboxDispatchConventionTests`,
  `OutboxEnvelopeReaderTests`, `OutboxEventRegistryTests`): declared resolver and
  transitive production helper census, planted loading calls and envelope probes.
- **Phase:** 02b, P02b-2.

#### `Outbox_Pending_Subscriptions_Preserve_Events`

- **Asserts:** TenantCreated rows and aliases assigned Pending manifests remain
  unchanged through repeated
  Pending polls, even with malformed stored payloads: no acquisition, attempts,
  validation or handler construction. Declared intentional absence is a distinct
  tested acknowledgement policy. Test-owned Active contracts exercise the shipped
  publish/mark path. P02b-3 Step 5 activates both real production subscriptions;
  Pending-manifest fixtures retain the policy proof.
- **Source:** [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md);
  [P02b-2 proof obligations](../roadmap/phase-02b-delivery-log.md#decisive-proof-obligations).
- **Type:** integration tests (Testcontainers) and unit tests of production
  composition and manifest refusal.
  **Kind:** startup + behavioural.
- **Status:** **Implemented** (`OutboxProcessorTests`, `OutboxDispatchStoreTests`,
  `OutboxProductionCompositionTests`, `OutboxEventRegistryTests`), with these proofs:
  [OutboxProcessorTests.Real_provisioning_rows_remain_Pending_across_hosted_polls_without_construction_or_mutation](../../backend/tests/LearnStack.Tests.Integration/Database/OutboxProcessorTests.cs),
  [OutboxDispatchStoreTests.Real_TenantCreated_contracts_and_aliases_are_held_before_any_payload_validation](../../backend/tests/LearnStack.Tests.Integration/Database/OutboxDispatchStoreTests.cs),
  [OutboxProductionCompositionTests.Real_provisioning_contract_activates_both_distinct_default_consumers_with_exact_handler_bindings](../../backend/tests/LearnStack.Tests.Unit/Api/Composition/OutboxProductionCompositionTests.cs),
  and [OutboxEventRegistryTests.Active_manifest_and_discovered_handlers_must_match_in_both_directions](../../backend/tests/LearnStack.Tests.Unit/Infrastructure/Messaging/OutboxEventRegistryTests.cs)
  prove declared Pending holds, actual Active bindings and composition refusal.
  Test-owned Active publication
  runs in [OutboxProcessorTests.A_poison_first_row_terminalizes_with_MUST_audit_and_following_acceptance_progresses](../../backend/tests/LearnStack.Tests.Integration/Database/OutboxProcessorTests.cs);
  [OutboxProcessorTests.Explicit_intentional_absence_acknowledges_without_constructing_a_handler](../../backend/tests/LearnStack.Tests.Integration/Database/OutboxProcessorTests.cs)
  proves the distinct intentional-absence policy.
- **Phase:** 02b, P02b-2.

#### `Outbox_Health_And_Metrics_Are_Bounded`

- **Asserts:** Production worker registration, bounded deadlines/overruns/shutdown and
  real progress health have nonempty subjects and planted controls. Gauge callbacks
  read immutable snapshots without SQL; failed refresh retains stale data.
  Due/delayed/leased/deferred/terminal-requested/terminal rows distinguish gauges.
  Labels and diagnostics prohibit unbounded identities or sensitive
  exception/type/payload/connection data; /healthz remains liveness-only.
  Polymorphic helper traversal covers production-owned contracts. The external
  clock is pinned to the actual singleton `TimeProvider.System` instance; actual
  DI health must follow Healthy → Unhealthy → Healthy on its singleton observation,
  refusing a same-type detached factory.
- **Source:** [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md);
  [P02b-2 proof obligations](../roadmap/phase-02b-delivery-log.md#decisive-proof-obligations).
- **Type:** production architecture census with planted controls where structural;
  real PostgreSQL/runtime integration proofs where behavioural.
  **Kind:** structural + behavioural.
- **Status:** **Implemented** (`OutboxDispatchConventionTests`,
  `OutboxProcessorTests`, `OutboxObservationTests`,
  `OutboxProductionCompositionTests`): nonempty five-subject observation graph,
  actual gauge callbacks, async/production-owned polymorphic helpers and
  persistence/locator capability controls; actual DI missing/duplicate/wrong-factory
  controls plus bounded runtime, immutable snapshot, metric-label and liveness
  proofs. The external clock is pinned to the actual `TimeProvider.System` singleton,
  and actual DI health follows Healthy → Unhealthy → Healthy against the singleton
  observation; alternate clock and detached same-type factory controls are rejected.
  Focused verification and both fresh fix reviews pass at `606362b`.
- **Phase:** 02b, P02b-2.

#### `Integration_Event_Publication_Is_Durable_Acceptance`

- **Asserts:** Publish success means every required stable subscription has an
  immutable, durably accepted envelope without business-handler construction.
  Identical duplicates are idempotent, conflicting copies are refused and
  partial/uncertain acceptance retries safely without spending subscriber budgets.
- **Source:** [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md#subscriber-acceptance-and-inbox-ownership)
  and the [P02b-3 package](../roadmap/phase-02b-delivery-log.md#p02b-3-decision-package-2026-10-13).
- **Type:** real PostgreSQL/app-role integration and construction controls.
  **Kind:** behavioural.
- **Status:** **Implemented** (`DurableEventAcceptanceTests`, `InboxGuardTests`,
  `OutboxProcessorTests`, P02b-3 Step 2). Includes independent publisher transactions,
  blocked duplicate commit/rollback, immutable collisions, dropped COMMIT response,
  constructor refusal controls and the actual producer worker's durable handoff.
- **Phase:** 02b, P02b-3.

#### `Delivery_Retries_And_Replay_Are_Subscription_Scoped`

- **Asserts:** Independent subscription attempts/backoff/leases, terminal requests
  and replay preserve sibling and producer outcomes. Actual completion is fenced
  with the business transaction and sticky timeout gate; final-claim unknown
  COMMIT recovers from atomic inbox/ledger state. Replay resets one terminal
  ledger pair only, never its completed inbox or a healthy sibling.
- **Source:** [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md#subscriber-acceptance-and-inbox-ownership)
  and the [P02b-3 package](../roadmap/phase-02b-delivery-log.md#p02b-3-decision-package-2026-10-13).
- **Type:** real PostgreSQL worker/owner integration, controlled cancellation,
  lost-COMMIT and state-transition mutants. **Kind:** behavioural.
- **Status:** **Implemented** (`DeliveryDispatchStoreTests`, `DeliveryProcessorTests`,
  `DeliveryExecutionTests`, P02b-3 Step 4). Real-role tests cover independent
  retries, final-claim recovery, exact-tuple replay with terminal sibling controls,
  unchanged-row lock expiry, lost COMMIT and postcommit cleanup ownership.
- **Phase:** 02b, P02b-3.

#### `Delivery_Terminal_Audit_Identity_Is_Row_Bound`

- **Asserts:** The successful fenced subscriber terminal UPDATE alone mints a
  restricted witness binding actual tenant/org/subscription/event/token and live
  transaction. Mismatched draft identities/operation/class/type/transaction and
  failed audit roll back both writes; uncertain COMMIT never duplicates terminal
  audit. The scheduler has no retained payload/causal-envelope SELECT privilege.
- **Source:** [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md#atomic-terminal-audit-and-privilege-boundary)
  and [ADR-0044 Amendment 11](../decisions/0044-audit-write-path.md#amendment-11--shared-delivery-capture-exclusions-2026-10-13).
- **Type:** nonempty production mint/writer census with dirty/clean controls and
  real-role atomic transition integration. **Kind:** structural + behavioural.
- **Status:** **Implemented** (`OutboxDispatchConventionTests`,
  `DeliveryDispatchStoreTests`, P02b-3 Step 4). Exact producer/subscriber mint
  and writer censuses have planted dirty/clean controls; real-role tests pin draft
  substitutions, audit rollback, matching witnesses and uncertain-COMMIT reconciliation.
- **Phase:** 02b, P02b-3.

#### `Delivery_Execution_And_Observation_Are_Bounded`

- **Asserts:** Actual API subscriber worker, deadlines, ignored cancellation and
  disposal retain safe task-owned resources within the accepted capacity/shutdown
  bounds. Postcommit cleanup cannot undo/repeat a durable business effect. Closed
  diagnostics/finite labels, exact snapshot cadence and prior-snapshot retention
  preserve truthful registered health; gauge callbacks execute no SQL.
- **Source:** [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md#diagnostics-and-execution-bounds)
  and the [P02b-3 package](../roadmap/phase-02b-delivery-log.md#p02b-3-decision-package-2026-10-13).
- **Type:** production registration/observation/call-path census with planted
  dirty/clean controls, unit probes and real worker integration.
  **Kind:** structural + behavioural.
- **Status:** **Implemented** (`OutboxDispatchConventionTests`,
  `DeliveryProcessorTests`, `DeliveryObservationTests`, `DeliveryAttemptTests`,
  P02b-3 Step 4). The actual worker proves retained callbacks/cleanup, capacity,
  shutdown and refresh cadence. Production capability and memory-observation
  censuses have planted controls;
  literal-SQL checks cover exact declared writers/helpers, not arbitrary dataflow.
- **Phase:** 02b, P02b-3.

#### `Customization_Default_Initialization_Preserves_Touched_Concepts`

- **Asserts:** Both actual default subscriptions create one untouched root each
  with owner-minted UUIDv7, one generation bump and MUST audit. Any Draft, Active,
  Deprecated, successor-only or soft-deleted concept is preserved. Semantic-key
  serialization makes competing registration and initialization converge without
  overwrite, republish or resurrection; each effect has independent inbox state.
- **Source:** [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md#first-useful-production-flow)
  and the [P02b-3 package](../roadmap/phase-02b-delivery-log.md#p02b-3-decision-package-2026-10-13).
- **Type:** actual consumer integration, lifecycle/race/atomic-audit controls.
  **Kind:** behavioural.
- **Status:** **Implemented** — `CustomizationDefaultInitializationTests`,
  `CustomizationDefaultDeliveryTests` (P02b-3 Step 5): app-role lifecycle,
  ordinary-registration races, backlog/fresh delivery and atomic audit proofs.
- **Phase:** 02b, P02b-3.

#### `Seeder_BuiltIns_Use_Semantic_Identity`

- **Asserts:** Seeder discovers and exactly verifies built-ins by semantic
  key/version while preserving warm/generated IDs and authored exact IDs/pins.
  It uses the ordinary app-role pipeline without a worker or dispatcher credential;
  reruns are neutral, conflicting lifecycle/shape refuses without overwrite, and
  fresh seeding/delivery races converge in both winning orders.
- **Source:** [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md#first-useful-production-flow)
  and the [P02b-3 package](../roadmap/phase-02b-delivery-log.md#p02b-3-decision-package-2026-10-13).
- **Type:** actual Seeder/module/worker integration with warm-ID and race controls.
  **Kind:** behavioural.
- **Status:** **Implemented** — `SeederTests`,
  `CustomizationDefaultDeliveryTests` (P02b-3 Step 5): generated/warm identity,
  exact-shape/lifecycle refusal, neutral reruns and both Seeder/delivery race orders.
- **Phase:** 02b, P02b-3.

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
  and method parameters, return types, fields and properties are checked. A nonempty
  whole-production publication census admits only the private task-owned publish
  callback and the attempt constructed by `OutboxProcessor.DispatchNormalAsync`;
  interface/concrete/delegate/async publication and unowned attempts are refused.
  Modules write to the outbox.
- **Source:** [20-infrastructure-stack.md § `IEventBus`](20-infrastructure-stack.md);
  [ADR-0010](../decisions/0010-cross-module-communication.md).
- **Type:** xUnit + reflection over module assemblies and whole-production IL
  publication census. **Kind:** structural.
- **Status:** **Implemented** (`CrossCuttingFoundationTests`). A module holding the bus
  gets a synchronous cross-module call with no durability and no transactional
  atomicity — a fifth cross-module mechanism in everything but name, and one that looks
  like it works in every development test because the in-process transport delivers
  inline. A namespace ban cannot express it: modules legitimately depend on
  `LearnStack.SharedKernel.Messaging` for `IIntegrationEvent` and
  `IIntegrationEventHandler<T>`. The module sweep has had a subject since Packet 7 —
  four modules ship code — and the checker is also pointed at direct-injection,
  method-injection and service-locator deliberate offenders in the test assembly.
  P02b-2 Step 4 adds the complete production publication fence through the same
  canonical test; missing subjects, extra implementations/callers, delegate/async
  escapes and unowned attempts have dirty/clean controls. This is bounded IL
  capability inspection, not arbitrary reflection/dataflow analysis.
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
- **Status:** **Implemented** (`CrossCuttingFoundationTests`). The production
  census is non-empty and includes Tenancy's completed-provisioning event.
  Deliberate invalid topics, including trailing newlines, and clean controls
  exercise the same convention checker.
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
| `Provider_SDK_Types_NotImportedOutsideInfrastructure` | A provider SDK's types — LiveKit, the Keycloak admin client, SeaweedFS, a payment SDK — appear only in the `LearnStack.Infrastructure.*` adapter that wraps it, never in a module. A superset of [`Adapters_Wrap_Provider_Exceptions`](#adapters_wrap_provider_exceptions), which already holds the exception types of the SDKs it lists — the one slice of this rule that runs today | structural | [Phase 03](../roadmap/phase-03-identity-admin.md#goal), at the first runtime identity-provider adapter's actual SDK introduction; re-verify before its first writer. Direct-HTTP tooling does not qualify; it then binds per provider as each SDK adapter lands | [Tenant Isolation](../architecture/09-tenant-isolation.md); [P02b-6 accepted handoff](../roadmap/phase-02b-delivery-log.md#p02b-6-decision-package-2026-10-20) |
| `LicenseKey_Payload_MatchesSchema` | `entitlement-v1.schema.json` is pinned by a snapshot test run in **both** repositories against the same checked-in schema | structural | [Phase 02c](../roadmap/phase-02c-hub-foundation.md), with the schema | [Hybrid License Model](../architecture/26-hybrid-license-model.md); [ADR-0021](../decisions/0021-feature-based-entitlement.md), as `EntitlementProjection_Shape_IsStable` — see § Canonical names |
| `Entitlement_Read_Path_Falls_Through_To_Durable_Row` | With L1 and L2 flushed and the Hub unreachable, the tenant resolves from `platform_entitlement_cache` and no exception escapes the flag read | behavioural | [Phase 02c](../roadmap/phase-02c-hub-foundation.md), with `HubEntitlementProvider` | [Hybrid License Model](../architecture/26-hybrid-license-model.md) |
| `CustomDomain_TenantId_NeverReadFrom_RequestBody` | Custom-domain submission derives the tenant from the authenticated session, never from the body or the query | behavioural | [Phase 02c](../roadmap/phase-02c-hub-foundation.md), Hub side, per [ADR-0022](../decisions/0022-custom-domain-tls.md) | ADR-0022; [Custom Domain TLS](../architecture/27-custom-domain-tls.md) |
| `Cert_PrivateKey_NeverLeavesVault_To_Logs` | The log redaction filter strips a PEM private-key block before a line is emitted, in every deployment mode | behavioural | [Phase 02c](../roadmap/phase-02c-hub-foundation.md) on the Hub side, per ADR-0022, which applies it in every mode; on the LearnStack side [Phase 11](../roadmap/phase-11-production-hardening.md), with the TLS automation — the first LearnStack code that handles certificate material, and a Self-Hosted air-gapped deployment has no Hub | ADR-0022; [Custom Domain TLS](../architecture/27-custom-domain-tls.md) |
| `Frontend_Has_Only_The_OperatorPortal_App` | The Hub repository ships exactly one frontend application, the operator portal | structural | [Phase 02c](../roadmap/phase-02c-hub-foundation.md), owned and run by the `learnstack-hub` repository | [ADR-0019](../decisions/0019-learnstack-hub.md) |
| `Customization_Reference_Resolution_Is_Batched` | Resolving an entry's references costs a small constant number of queries, not one per reference | behavioural | [Phase 05](../roadmap/phase-05-education-learning-content.md), with the batched reference walk — Phase 02d's lesson body carries its fields inline and resolves no reference | [Tenant Customization Model](../architecture/32-tenant-customization-model.md); [Phase 05](../roadmap/phase-05-education-learning-content.md) |
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

#### `Hangfire_Job_Payloads_Include_TenantId`

- **Retired 2026-10-15** before implementation by ADR-0047. Tenant/org/trace belong
  in the authoritative LearnStack envelope; vendor messages are opaque references.
- **Replaced by** [BackgroundJob_Submission_Is_Transactional_And_Stamped](#backgroundjob_submission_is_transactional_and_stamped).

#### `LearnStackJob_RunAsync_SetsTenantBeforeExecute`

- **Retired 2026-10-15** before implementation by ADR-0047. The typed JobExecutor
  boundary replaces the unshipped vendor base class and direct activator pattern.
- **Replaced by** [BackgroundJob_Execution_Restores_Tenant_And_Trace](#backgroundjob_execution_restores_tenant_and_trace)
  and [BackgroundJob_Completion_Is_Owner_Atomic_And_Fenced](#backgroundjob_completion_is_owner_atomic_and_fenced).

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
  current production surface has no keyed endpoint; ADR-0079 selects the
  protected administration activation tracked by the
  [phase Status block](../roadmap/phase-03-identity-admin.md). A companion test
  drives the same predicate over a probe host that *does* violate it, which
  distinguishes the guard from an empty assertion.
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

- **Asserts:** `X-Tenant-Id` and `X-Organization-Id` never select anything. An
  agreeing assertion passes; a disagreement is 404, a malformed/repeated one is
  400 and counted, and unresolved context cannot invent a tenant. ADR-0065 adds
  at most 600 authenticated audit attempts per defended host tenant per instance
  per first-request-anchored monotonic minute across scoped recorders, actors,
  sources and dimensions. Failed writes spend permits; suppression preserves
  refusal, counts and exactly one attributed Warning without I/O or extending
  expiry. Anonymous and tenantless behavior remains separate. Expired-only
  sweeps inspect at most 128 entries per call, never evict live quotas and never
  duplicate retained queue keys; a 4097th actual host tenant remains independent.
- **Source:** ADR-0036 § The reconciliation matrix;
  [ADR-0065](../decisions/0065-bounded-tenant-assertion-diagnostics.md).
- **Type:** xUnit component/concurrency and actual registered HTTP/DI controls.
  **Kind:** behavioural.
- **Test owner:** Integration `TenantAssertionHttpTests.cs` owns refusal and
  actual singleton registration across request scopes. Unit
  `Api/Tenancy/TenantAssertionAuditBudgetTests.cs` owns concurrent 600, independent
  tenants, monotonic expiry, suppression not extending expiry, same-tenant reset,
  128-entry sweep and more-than-4096-host-tenant controls. Unit
  `Api/Tenancy/AuditingTenantAssertionRecorderTests.cs` owns write-failure spending,
  suppressed no-I/O, actor/source/dimension sharing, anonymous/tenantless behavior,
  closed metric labels, typed attribution and exactly one Warning (4001 or 4006).
  Integration `TenantAssertionAuditAdmissionTests.cs` owns at least 601 concurrent
  calls through actual registered scoped recorders sharing the singleton and one
  observable fake store; exactly 600 store attempts prove cross-scope accounting.
- **Status:** **Implemented** (`TenantAssertionHttpTests`,
  `TenantAssertionAuditAdmissionTests`, `TenantAssertionAuditBudgetTests` and
  `AuditingTenantAssertionRecorderTests`). The 2026-10-20 remediation implements
  ADR-0065's allowance and proves scope sharing against a scoped-budget mutant;
  acceptance alone was not implementation evidence.
- **Phase:** 02a (Packet 4); P02b-5 2026-10-20 review remediation owns ADR-0065.

#### `Anonymous_Requests_Are_Rate_Limited_Per_Peer`

- **Asserts:** the shipped direct anonymous budget is spent per socket peer, a request
  over it is **429** with `Retry-After` and the one Problem Details shape, and an
  untrusted header never creates its partition. architecture/30 has promised this
  middleware since Phase 01; from Packet 7 every novel `Host` value buys a Postgres
  round trip on a pre-auth surface.
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
- **Accepted extension — 2026-10-08:** ADR-0053 / P02d-5 G34 replaces socket-only
  partitioning for authenticated renderer traffic with one canonical-IP quota and a
  separate peer ceiling. Untrusted forwarding metadata remains ineffective; trusted
  malformed visitor metadata is bounded and refused before lookup. Existing direct
  socket controls remain Implemented. P5 Step 2 implements the extension, with
  `AnonymousRequestIdentityTests` covering strict bounded IP identity and hop
  admission, and `TrustedVisitorHttpTests` covering real Kestrel sockets, shared
  direct/SSR quotas, independent visitors, malformed/repeated metadata, rotation,
  pre-lookup novel-host bounds and the physical-peer ceiling. Positive public
  reads assert READ ONLY and the non-BYPASSRLS application role.
- **Implemented replacement, both review rounds passed — 2026-10-09:**
  ADR-0054 coordinated accounting is implemented by remediation Step 1.
  `AnonymousAdmissionTests` proves exhausted-known-visitor fairness, peer-gated
  allocation, last visitor/peer permit races, single creation, first-refusal
  Retry-After, request-result replay, cancellation and independent leases. Its
  sweep controls prove whole-registry bounded traversal, non-overlap, full-quota
  idle retirement, real fixed-window replenishment and shutdown/acquisition safety.
  `AnonymousLimiterLifecycleTests` retains both DI teardown paths and exact-once
  child disposal. `AnonymousAdmissionRetryTests` exercises actual ASP.NET endpoint
  refusal/retry with successful and refused global outcomes, without double debit.
  `TrustedVisitorHttpTests` supplies
  `Known_visitor_refusals_preserve_all_remaining_peer_permits_before_lookup`, which
  replaces the old refusal-debit proof: 60 admissions and 270 refusals leave 540
  peer permits. The independent physical-peer ceiling, direct/hop namespace and
  real HTTP/app-role controls remain enforced. Historical counts describe their
  original executions; remediation evidence belongs to the new delivery record.

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

- **Asserts:** only `ITenantAssertionRecorder` implementations write mismatch
  logs/metrics/audit rows, including the mismatch, unresolved and suppression
  counter names. A new suppression counter must not create a second writer.
- **Source:** ADR-0036 § Recording a rejected assertion;
  [ADR-0065](../decisions/0065-bounded-tenant-assertion-diagnostics.md).
- **Type:** xUnit source and compiled-IL/constant-field scans over production
  assemblies. **Kind:** structural.
- **Status:** **Implemented** (`TenancyConventionTests`), in two halves.
  `Assertion_Recorder_Is_The_Only_Mismatch_Writer` derives counter names from the
  actual recorder's constant declarations and `Meter.CreateCounter` calls,
  recursively including nested/generated owner types, and pins the current exact
  three-counter vocabulary.
  Inlined public-constant references and a fourth counter with a different prefix
  have planted second-writer controls; an unknown dynamic declaration refuses
  discovery. The compiled scan covers every production assembly.
  `Assertion_Recorder_Is_The_Only_Writer_Of_Its_Audit_Slugs`, added in Packet 9, is
  keyed on the two audit slugs and covers the `IAuditStore` clause this row always
  claimed — the counter names cannot catch a second writer that goes straight to the
  store, which is the dangerous one, because the row's tenant is what keeps an anonymous
  caller from choosing whose audit log grows.
- **The second half scans all production assemblies, not just the API project.**
  A module is precisely where `IAuditStore` is reachable from a handler. Compiled
  literal and constant-field discovery catches inlined references to both audit
  slugs. `TenancyAuditCatalogSource` is exempt because declaring a slug is not
  writing a row. Its companion, `The_Slug_Scan_Finds_The_Files_It_Exempts`, removes
  exemptions and requires exactly the audit recorder and catalogue owner types,
  qualified by their real assemblies. This is a type-owner census, not a claim
  that the current implementation scans exactly two source files. The assertion
  header reader guard likewise reads compiled constants and uses case-insensitive
  comparison. It recursively reads custom-attribute constructor/named arguments,
  arrays and member/parameter/return metadata, and pins the exact two header
  names; lowercase, public-constant, renamed-header and compiled FromHeader
  controls must fail.
- **Phase:** 02a (Packet 4; the second half, Packet 9).

#### `Assertion_Budget_Does_Not_Depend_On_ICacheService`

- **Asserts:** anonymous-burst counters and the authenticated singleton
  `TenantAssertionAuditBudget` resolve no `ICacheService`. Cache availability must
  not determine admitted MUST-class security-event recording. The source and
  dependency predicates cover the new budget without claiming accounting proof.
- **Source:** ADR-0036 § Recording a rejected assertion;
  [ADR-0065](../decisions/0065-bounded-tenant-assertion-diagnostics.md).
- **Type:** xUnit reflection check over the `LearnStack.Api.Tenancy` namespace **and** a source scan over `LearnStack.Api/Tenancy`. **Kind:** structural.
- **Status:** **Implemented** (`TenancyConventionTests`). It shipped in Packet 4 as a **tripwire**, because `ICacheService` did not exist yet; Packet 5 ships the port, so the rule now carries the dependency check it was always meant to be. Both forms are kept: reflection catches an injected dependency, the scan catches a service-locator resolve, and neither sees the other's case.
- **ADR-0065 extension:** `TenancyConventionTests` covers the shipped singleton
  under the same structural owner. Unit `TenantAssertionAuditBudgetTests` and Integration
  `TenantAssertionAuditAdmissionTests` own behavioural allowance/expiry/scope
  accounting under `Tenant_Assertions_Are_Compared_Not_Resolved` above.
- **Phase:** 02a (Packet 4); P02b-5 2026-10-20 remediation extends coverage.

#### `Tenant_Realm_Issuance_Matches_The_Api_Profile`

- **Asserts:** the committed tenant realm imported into pinned Keycloak issues
  ADR-0064's real scalar identity/API profile through code/S256 exchange for all
  four declared seed identities. Exact callbacks, confidential-secret/PKCE
  refusals, protected admin-only attributes and the actual `basic`/identity/
  audience mappers hold. Real tokens reach AccountSite through the API handler
  and app-role PostgreSQL; cross-scope/Hub requests refuse, while claimless
  principals retain the public ceiling without protected-read authority.
  Exact realm lifespan and absent client overrides are checked independently
  from bounded observed fresh expiration and shorter late-session issuance.
  Ordinary account-edit success, independent admin readback and dirty/clean
  mapper/profile/TTL variants prove protection and drift controls can fail.
  The same seed personas use only the declared self-service baseline; actual
  registration and writable-profile counterfactuals reach stored values rather
  than passing through a generic refusal or account-console HTML response.
- **Source:** ADR-0004; ADR-0064;
  [ADR-0067](../decisions/0067-development-tenant-access-token-issuance.md)
  (development issuance);
  [ADR-0068](../decisions/0068-development-warm-realm-qualification.md)
  (historical warm qualification);
  [Security 11](11-security.md#authentication);
  [accepted P02b-6 package](../roadmap/phase-02b-delivery-log.md#p02b-6-decision-package-2026-10-20).
- **Type:** xUnit Integration with Docker, real Keycloak code exchange, API
  HTTP and `learnstack_app` database controls. **Kind:** behavioural.
- **Test owner:** `TenantRealmIssuanceTests`, `TenantRealmProfileProtectionTests`
  and `TenantRealmApiTests` in
  `backend/tests/LearnStack.Tests.Integration`.
- **Status:** **Implemented** — `TenantRealmIssuanceTests`,
  `TenantRealmProfileProtectionTests` and `TenantRealmApiTests`; P02b-6 Step 4.
  Actual provider/API controls run in ordinary Docker CI; local and hosted
  receipts and independent review closure live in the Step 4 record.
- **ADR-0068 Accepted extension — 2026-10-22, implemented:** The added Asserts
  obligation covers historical changed-password/MFA login, four signed API
  personas, full before/after console claims and manifest/nonmanifest POST with
  independent stored-field readback.
  `TenantRealmApiTests.WarmQualification.cs` implements the actual historical
  journey alongside the existing self-service/issuance/profile controls. It
  measures both new scope refusals and the retained offline session/credential;
  no universal role-loss or immediate-revocation guarantee is inferred.
  [ADR-0068](../decisions/0068-development-warm-realm-qualification.md) and its
  [three-step record](../roadmap/phase-02b-delivery-log.md#adr-0068-acceptance-and-implementation-2026-10-22)
  own actual receipts, completed correction reviews and source-bound hosted proof.
  Existing Implemented status and rule counts remain unchanged.
- **Phase:** P02b-6 Steps 1/4.

- **ADR-0070 Accepted extension — 2026-10-23, implemented:** Actual
  fresh development issuance includes the exact selected login/logout/code/
  refresh success/error event set. `TenantRealmExportTests` validates the
  committed configuration; `TenantBffSessionTests` exercises stored events from
  actual grants, refresh and logout. The development retention limit is not an
  application-audit guarantee. Existing Implemented subjects/counts remain.

#### `Development_Realm_Reconciliation_Preserves_Accounts`

- **Asserts:** the actual shared development reconciler converges fresh and
  pre-mapper warm realms without resetting existing credentials or changing
  account IDs, unrelated user/profile/client state or the Hub realm. Independently
  observable identity/configuration conflicts, unqualified visibility definitions,
  group aggregation/inheritance and membership sources refuse before preparation.
  A conflict revealed by qualified metadata preparation permits no ordinary write;
  the fixed durable-progress notice survives failure and cleanup.
  Projected request/response and collection limits are checked before apply.
  Safe groups/memberships and actual account fields remain unchanged; derived
  profile metadata is verified with its global configuration rather than
  mistaken for stored-account data. The approved self-service adjustment adds
  only missing four-persona account capability and a compatible fixed-account
  console mapper. Stock bootstrap parity, existing/inherited grants and shared
  mapper effects on an already eligible nonmanifest user are independently
  observed; unsafe role/scope/mapper customization refuses before any pending
  write. Partial/uncertain writes require an
  explicit verified rerun. Independent readback checks protected claims/TTL;
  a completed third run performs zero mutations. Both seed/demo entries retain
  exact target/environment identity, bounded HTTP/process cleanup, secret
  confinement and local lock ownership under failure/cancellation/races.
- **Source:** ADR-0004; ADR-0064;
  [ADR-0068](../decisions/0068-development-warm-realm-qualification.md);
  [Security 11](11-security.md#authentication);
  [accepted P02b-6 package](../roadmap/phase-02b-delivery-log.md#p02b-6-decision-package-2026-10-20).
- **Type:** xUnit Integration with Docker and actual warm Keycloak Admin REST;
  offline Node tooling transport/process/CLI controls in meta.
  **Kind:** behavioural.
- **Test owner:** `TenantRealmReconciliationTests` and
  `TenantRealmSelfServiceTests` in `backend/tests/LearnStack.Tests.Integration`;
  shared reconciler Node controls under `scripts/`, including
  `reconcile-development-realm.test.mjs`, own bounded transport/process/CLI
  evidence. `frontend/apps/web/src/test/demo-realm-lifecycle.test.ts` owns the
  seed/demo caller's awaited handoff, selection, confinement and cancellation
  cleanup controls; its reconciler stub does not prove Admin REST convergence.
- **Status:** **Implemented** — `TenantRealmReconciliationTests` and
  `TenantRealmSelfServiceTests`, with offline Node controls in meta; P02b-6
  Steps 2–4. Actual warm-provider, lifecycle and cancellation receipts and
  independent review closure live in the Step 4 record.
- **ADR-0068 Accepted extension — 2026-10-22, implemented:** The added Asserts
  obligation is reproduced stock-graph qualification, all identity-independent
  refusals before preparation, exact metadata-only progress, separately counted
  ordinary writes, three qualified DELETEs and explicit partial-progress reruns.
  `TenantRealmReconciliationTests`,
  `TenantRealmSelfServiceTests`, `TenantRealmStockBaselineTests` and the shared
  Node controls own this extension. The stock-baseline owner reproduces the
  immutable historical expectation and the ordinary assignment path after explicit
  test-owned metadata setup; it does not prove automatic preparation.
  The second correction step adds actual historical automatic preparation,
  exact-profile readback, independent pre-write refusal and explicit interrupted
  resumption controls in `TenantRealmReconciliationTests`, with shared Node
  transport/progress/cleanup controls. The third step adds the actual historical
  credential/token/API/profile journey and a zero-write pass after self-service
  in `TenantRealmApiTests.WarmQualification.cs`. Actual entry experiments and
  their distinct bootstrap-source/cleanup limits live in the step record; stubbed
  lifecycle tests alone do not prove warm Admin REST or complete public audits.
  A hidden fresh-plan conflict may leave preparation metadata, but no ordinary
  mutation; the earlier zero-write scope remains historical implemented coverage.
  [ADR-0068](../decisions/0068-development-warm-realm-qualification.md) and its
  [three-step record](../roadmap/phase-02b-delivery-log.md#adr-0068-acceptance-and-implementation-2026-10-22)
  own actual receipts, completed correction reviews and source-bound hosted proof.
  Existing Implemented status and rule counts remain unchanged.
- **Phase:** P02b-6 Steps 2–4.

- **ADR-0070 Accepted extension — 2026-10-23, implemented:** Actual
  warm event reconciliation preserves effective prior selection, listeners/admin
  policy and qualified retention. `scripts/development-realm-events.test.mjs`
  independently requires all eight types and refuses missing readback; actual
  `TenantBffSessionTests` exercises warm convergence and the zero-write rerun.
  Conflicts refuse before preparation/ordinary mutation. Existing Implemented
  subjects/counts remain unchanged.

#### `Api_Registers_Only_The_Tenant_Realm_Authority`

- **Asserts:** the composition root registers exactly one tenant bearer scheme,
  pinned to the configured `learnstack` issuer. A real-handler request rejects a
  wrong issuer even under a trusted signing key, including `learnstack-hub`.
  DI inspection alone cannot prove issuer enforcement. Unknown hosts retain 404
  before validation and unclassified infrastructure/internal paths remain outside
  this tenant scheme.
- **Source:** ADR-0036 § The signals; ADR-0004 Amendment 1;
  [ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md).
- **Type:** xUnit DI inspection plus real-handler signed-token HTTP controls.
  **Kind:** behavioural, with complementary structural inspection.
- **Test owner:**
  `backend/tests/LearnStack.Tests.Integration/TenantAuthenticationProfileTests.cs`;
  active-host controls in `TenantAuthenticationHttpTests.cs` in that project.
- **Status:** **Implemented** (`TenantAuthenticationHttpTests`,
  `TenantAuthenticationProfileTests`).
- **Phase:** P02b-5 Step 1 isolated handler proof; Step 4 production activation;
  Step 5 final enforcement. No production registration before Step 4.

#### `Backend_RequiresJwt_OnAllAuthenticatedRoutes`

- **Asserts:** present invalid or undecidable tenant bearer credentials on known
  hosts return 401 with a bare Bearer challenge and no-store, including public
  endpoints and classified unmatched API paths.
  Absent credentials on exact AccountSite return masked 404 `not_found` on tenant,
  organization and platform hosts; the scoped authorization result handler cannot
  fall back to default 401. A claimless valid profile keeps HostOnly/public-only
  authority. Unknown hosts refuse before JWT work. Generic unresolved MediatR
  `tenant_mismatch` is unchanged; no blanket missing-token 401 is promised.
- **Source:** [ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md)
  § Pipeline and error precedence; [API Design](04-api-design.md#public-surface);
  [API Gateway](../architecture/30-api-gateway.md).
- **Type:** actual API-host signed-token/absence HTTP matrix with correlation
  normalization, public IdP-unavailable regression and scoped challenge controls.
  **Kind:** behavioural.
- **Test owner:**
  `backend/tests/LearnStack.Tests.Integration/TenantAuthenticationHttpTests.cs`.
- **Status:** **Implemented** (`TenantAuthenticationHttpTests`, `AccountSiteHttpTests`).
- **Phase:** P02b-5 Step 4 activation and Step 5 enforcement.

#### `Tenant_Authentication_Profile_Is_Closed`

- **Asserts:** the real stock handler enforces ADR-0064's signed RS256, issuer,
  audience, purpose/client, bounded lifetime and scalar UUID profile;
  ADR-0072 makes the wire actor optional compatibility evidence. Only a matching
  Active existing binding normalizes the application actor, after subject quota;
  unknown/conflicting/nonadmitting/unavailable bindings refuse uniformly.
  duplicate JSON/security properties and duplicate or unsupported JOSE parameters
  fail. Credential shape/size and configured-key-only discovery are bounded.
  Controlled metadata proves added-key refresh and rejection after observed key
  withdrawal, respecting stock refresh minima. Canary controls cover vendor events
  1/3/7 across enabled sinks, while independent closed LearnStack outcomes remain
  visible. Invalid credentials never become absence. ADR-0065 additionally
  qualifies process-wide native HTTP/DNS metric projection: real durations and
  bounded tags remain, metadata canaries are absent from all exported channels,
  and an ordinary distinct HTTP IP/port/path/query canary is absent from metrics
  while its ordinary trace remains visible. DNS retains only `error.type`; HTTP
  retains bounded method/status/protocol/scheme/error dimensions.
  Programmatic `AlwaysOff` additionally closes metric exemplars: removed point
  attributes must not survive in exemplar filtered attributes when standard or
  histogram-specific environment filters request `always_on` or `trace_based`.
  The OTLP decoder inspects exemplars as well as points; unprojected controls
  establish that the rejected attributes would otherwise be exported.
- **Source:** [ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md)
  § Access-token profile and § Vendor diagnostic boundary;
  [ADR-0065](../decisions/0065-bounded-tenant-assertion-diagnostics.md)
  § Outbound telemetry scope;
  [ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md).
- **Type:** real JwtBearer/OIDC handler with controlled signed-token issuer,
  discovery/JWKS server and actual logging/export/error-capture controls.
  **Kind:** behavioural.
- **Test owner:**
  `backend/tests/LearnStack.Tests.Integration/TenantAuthenticationProfileTests.cs`;
  actual sink/export controls in `TenantAuthenticationDiagnosticsTests.cs` and
  `TenantAuthenticationDiagnosticsTests.Metrics.cs` in the same project.
  `Database/TenantExistingBindingAdmissionTests*.cs` adds actual app-role binding,
  startup, outage, no-allocation, lookup/pool and cancellation controls;
  `TenancyIdentity/TenantRealmApiTests.ExistingBinding.cs` supplies the real
  provider actorless-existing-binding positive.
- **Status:** **Implemented** (`TenantAuthenticationProfileTests` and the
  diagnostic owners above). ADR-0065's ordinary HTTP hostname/IP/numeric-port/
  path/query negatives retain real unprojected positives and ordinary traces.
  `TenantAuthenticationDiagnosticsTests.SuppressionMetrics.cs` additionally
  qualifies the actual suppression counter through production OTLP, with a
  bounded receipt for the correlated Warning before its logger owner exits.
  Actual Serilog factory/console controls cover configured-issuer and malformed
  payload canaries. Event 3's adapter control is synthetic; the ordinary
  configuration-manager branch is not claimed to emit it naturally. Warm cached
  known-key acceptance during a failed refresh and cold missing-key refusal are
  separate controls; manual refresh is not a five-minute automatic-cadence proof.
- **Phase:** P02b-5 Step 1 isolated foundation; Steps 4–5 actual active path.

#### `Authentication_Middleware_Order_Is_Enforced`

- **Asserts:** the actual chain preserves early admission/body limits, host
  classification, authentication, credential enforcement, subject quota,
  existing-binding normalization, tenant
  resolution/assertions, authorization, closed-surface admission and dispatch in
  ADR-0064 order. Unknown-host/early-quota precedence survives; subject refusal
  prevents durable mismatch recording. A planted reorder fails. Production scheme
  and authorization registration remain absent until atomic Step 4 activation,
  including minimal hosting's implicit middleware behavior.
  The complementary bounded source proof resolves custom `Use*` helpers before
  framework terminals and checks real middleware/OpenAPI wrapper leaf shapes.
  Direct extra authentication and copied-receiver mutations inside the existing
  wrappers fail; direct receiver aliases refuse unverifiable composition. This
  includes fluent extra calls in the actual Program/resolver statements:
  application-builder receiver chains fail with a specific diagnostic, while
  the existing health endpoint-convention builder remains clean. This
  is not a general C# semantic analysis of arbitrary reflection or dynamic code.
- **Source:** [ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md)
  § Pipeline and error precedence;
  [ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md).
- **Type:** whole-chain actual-host HTTP controls plus a planted reorder control.
  **Kind:** behavioural.
- **Test owner:**
  `backend/tests/LearnStack.Tests.Integration/TenantAuthenticationHttpTests.cs`.
- **Status:** **Implemented** (`TenantAuthenticationHttpTests`,
  `AuthenticatedRateLimitTests`, `TenantExistingBindingAdmissionTests`).
- **Phase:** P02b-5 Step 3 component harness; Step 4 actual activation; Step 5
  complete production enforcement.

#### `Authenticated_Http_Surface_Is_Enumerated_And_ReadOnly`

- **Asserts:** the production census closes the finite HTTP surface at 31 MVC
  actions and 27 distinct `IRequest` roots, including the public and authenticated
  reads, ten protected keyed writes and native session-scope admission. It also
  checks the separate three exact GlobalSelf middleware routes. The canonical
  name is historical: administration includes writes, and sensitive reads can
  require a ReadWrite owner to commit MUST audit evidence. AccountSite and public
  reads retain their own physical ReadOnly contracts.
- **Structural boundary:** the independent endpoint declarations pin route, verb,
  request, response, policy and audit metadata; the protected descriptors pin the
  keyed operation shapes. Compiled metadata, source and IL controls check the
  supported MVC filter and authorization composition, controller/helper persistence,
  request construction and transitive dispatch across production assemblies.
  Dirty controls exercise extra or missing routes, changed descriptors and capability
  paths. Native source admission and the exact GlobalSelf route descriptors have
  separate entry controls; `HostAndNativeSessionScope` is a distinct origin and
  native evidence does not rewrite provider facts. GlobalSelf helpers distinguish
  static binding from ordinary `callvirt`/`ldvirtftn` slot reuse; unsupported generic
  or MethodImpl class-slot resolution refuses. MVC checks cover filter scope,
  ordering, factory arguments and reuse, including the filter-provider inventory. These
  bounded checks do not model arbitrary reflection or dynamic framework behavior.
- **Complementary runtime boundary:** actual HTTP and application-role PostgreSQL
  controls exercise authorization, physical ownership and audit behavior. Protected
  keyed-write metadata is distinct from the executable factory, replay and cleanup
  owners and their runtime controls. OpenAPI/SDK controls check their own served
  and generated contract surfaces. The [operation matrix](../decisions/0079-bounded-identity-administration.md#scoped-policy-and-mutation-ownership)
  is the authority for the administration policy and audit contract.
- **Source:** [ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md),
  [ADR-0076](../decisions/0076-live-scoped-authorization.md),
  [ADR-0078](../decisions/0078-native-session-scope-and-self-entry.md),
  [ADR-0079](../decisions/0079-bounded-identity-administration.md), and
  [Security Standards](11-security.md#the-rule).
- **Type:** production source, IL and metadata census with dirty/clean controls,
  complemented by HTTP, database and contract controls. **Kind:** structural and
  behavioural.
- **Test owners:** `AuthenticatedHttpSurfaceTests`, `NativeScopeAdmissionTests`,
  `AccountSiteHttpTests` and `AccountSiteQueryTests`; protected-write and
  OpenAPI/SDK runtime controls remain with their respective owners.
- **Status:** **Implemented** (`AuthenticatedHttpSurfaceTests`); the
  [Phase 03 Status block](../roadmap/phase-03-identity-admin.md) owns packet
  qualification. The P03-2 directory extension was the earlier finite surface;
  ADR-0078 and ADR-0079 extend it with native entry and administration.
- **Phase:** 02b-5 (closed authenticated read); 03 (scoped authorization, native
  entry and bounded administration).

#### `Keycloak_Roles_Are_Not_Application_Authority`

- **Asserts:** no production call site treats realm/client roles, email, display
  name or arbitrary claim/scope text as application permission or promotes tenant
  authority from authentication alone. The complete call-site census has real
  subjects and planted role/claim-based authorization violations, plus clean
  controls; missing subjects or a weakened predicate fail. Phase 03 retains
  membership and permission authority.
- **Source:** [ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md)
  § Access-token profile and § Resolution and attribution; ADR-0004;
  [ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md).
- **Type:** complete production source/IL call-site census with planted compiled
  violations and predicate-mutation controls. **Kind:** structural.
- **Test owner:**
  `backend/tests/LearnStack.Tests.Architecture/AuthenticationAuthorityTests.cs`.
  Its real production graph separately closes verified-subject and normalized
  identity creation, feature writes,
  immutable shape and direct actor/scope/subject accessors. Signature, local,
  field, method and generic-argument uses close projection acquisition and
  forwarding, including object/generic formatting. Raw IdentityModel accessors
  and inherited/implemented authorization metadata are covered too, including
  `AuthenticateResult.Principal`, `AuthenticationTicket.Principal` and
  `IAuthorizationService.AuthorizeAsync`. Exact base `AuthorizeAttribute.Roles`
  has its own dirty control, separate from derived-attribute rejection. Compiled
  role/claim, header-reparse, derived-attribute, formatting, deconstruction and
  forged/clone probes use the same detectors; weakened predicates, missing real
  subjects and same-owner additional writes fail independently.
  The HTTP classifier's single `IAuthorizeData.Roles` read is confined to an
  immediate null/empty check that refuses nonempty metadata. Removed/reversed
  rejection controls preserve the raw-role ban. Its empty branch must still
  derive and pass the exact canonical key-metadata gate, refuse on false, and
  publish that same policy only on success; substituted/bypassed/reversed gates
  and null policy publication have independent dirty controls. Classification
  forwards only the actual same-request endpoint. The authorization handler
  delegates to the separately enumerated protected-replay owner; both adapters
  still reject extra identity formatting. The policy/receipt censuses close the
  replay owner's actual candidate and three HTTP decision branches.
- **Status:** **Implemented** (`AuthenticationAuthorityTests`).
- **Phase:** P02b-5 Step 1 profile constraints; Step 5 complete production census.

#### `Authenticated_Rate_Limits_Use_Validated_Subject`

- **Asserts:** the additional 600/min fixed-window quota uses validated issuer
  and opaque subject, with no queue. Token rotation or tenant/organization changes
  cannot reset quota; distinct subjects have independent state. Invalid tokens
  gain no quota identity or early bypass. Existing visitor/peer limits remain.
  ADR-0072's verified-subject stage requires no wire actor and precedes every
  binding read. Reentry retains the original immutable subject provenance;
  changed or missing evidence cannot borrow an earlier decision or identity.
  Whole-chain reduced-budget controls isolate subject accounting beneath those
  floors; a separate proof covers the actual 600 boundary. Concurrent accounting,
  first-refusal Retry-After/429, a finite partition registry with refusal before
  allocation, bounded idle retirement and timer disposal are
  controlled; refusal stops downstream audit work without resetting live quotas.
  ADR-0066 controls use the actual default 4096/4097 boundary, at most 128
  acquisition inspections and one disposal, timer batches of at most 128,
  sole cleanup ownership and the 4224-resource bound. Blocking/fault controls
  cover disposal before owning allocation/debit, lost-slot/same-key/shutdown
  races, whole-batch attempts, sticky fault stopping new allocation/batches,
  existing-key quota service, and joined sync/async cleanup failure, including a
  throwing fault-metric listener and failed timer join without skipped retained
  resources or a stranded shared shutdown.
  Real signed HTTP and OTLP controls distinguish quota/capacity 429 from
  new-key fault 503, exact retry hints, one cached rejection across re-execution,
  and an unlabelled once-only fault transition without identity attributes.
- **Source:** [ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md)
  § Additional subject quota;
  [ADR-0066](../decisions/0066-bounded-tenant-subject-admission.md);
  ADR-0053 and ADR-0054's retained ingress floors.
- **Type:** real limiter concurrency/lifecycle controls and signed-token
  whole-chain HTTP tests, including actual-default boundary.
  **Kind:** behavioural.
- **Test owner:**
  `backend/tests/LearnStack.Tests.Integration/AuthenticatedRateLimitTests.cs`
  and its `Admission` partial; unit `Api/Authentication/TenantSubjectRateLimiterTests`
  and its lifecycle/fault/middleware partials. Actual export decoding uses
  `TenantAuthenticationDiagnosticCollector.MetricPoints`.
- **Status:** **Implemented** (`AuthenticatedRateLimitTests`,
  `TenantSubjectRateLimiterTests`).
- **Phase:** P02b-5 Step 3 bounded component; Step 4 whole-chain activation;
  Step 5 production enforcement. Phase 11 retains distributed capacity.

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

- **Asserts:** `ITenantContextAccessor.Current` is **written** only by the HTTP,
  internal Hub, job and integration-event boundaries. ADR-0047 names JobExecutor
  as the sole job-path writer, replacing the unshipped Hangfire JobActivator.
  `EnterPlatformAdminScope` opens a separate connection and writes no tenant
  context. Reads are unconstrained; no new writer is admitted before it ships.
- **Source:** ADR-0036 § Rules, second bullet, as corrected by its erratum and
  [Amendments 2 and 12](../decisions/0036-tenant-resolution-trusted-inputs.md).
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
- **Note:** **three of the four logical boundaries exist** — TenantResolverMiddleware,
  the integration-event acceptance/execution scope in InProcessEventBus and
  DeliveryExecutor, and JobExecutor's framework/business partials. The two JobExecutor
  files are one logical writer. HubCorrelationMiddleware belongs to Phase 02c.
  The whole-source census also refuses every writer outside the exact file set.
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
- **Status:** **Implemented** for the enumeration legs (`RequestSurfaceTests`, Packet 7
  step 6): every marked request type is a row of the table, every row names a marked
  type, and the attribute keeps the shape `TenantContextBehavior` reads it with. The
  table parser reads only the **Request type** cell; it does not parse each row's
  **Permitted methods** cell. P02d-4's implemented
  `PublicSurface_Endpoints_Dispatch_Through_The_Pipeline` independently checks the
  literal endpoint census and exact `GET`/`HEAD` methods. Implemented controller
  persistence guards and `PublicSurface_Transactions_Refuse_Writes` cover the
  tenant-owned-write boundary. Per-row method-cell agreement remains manual.
- **Phase:** 02a (Packet 7) for enumeration; P02d-4 Steps 1–3 for the implemented
  endpoint/dispatch and physical `READ ONLY` proofs under
  [ADR-0052](../decisions/0052-anonymous-public-read-boundary.md).
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

- **Asserts:** ordinary organization point reads retain both tenant and
  organization keys. The surrogate `pk_organizations` makes an id-only lookup
  valid SQL, so RLS alone cannot establish the independent composite-key guard.
  The EF leg bans `Organizations.Find` and `FindAsync`, which cannot express that
  composite key. The validator's tenant announcement and composite predicate are
  checked separately.
- **SQL census:** eight direct runtime SQL subjects include the validator, job
  lifecycle readers, identity/self-discovery readers and the awaited
  `OrganizationWriteStore.FindForUpdateAsync` composite-key lock followed by
  tenant-bound EF materialization. ADR-0047's bounded lifecycle inventory and
  bounds are the named enumeration exception. Two further composed page owners
  use an interpolated cursor inside bounded, tenant-filtered EF queries; their
  complete read-method token fingerprints and helper signatures pin the observed
  execution shape. Four exact migration installer owners are checked separately,
  including the administration and retained-default DDL batches.
- **Controls and limits:**
  `Organization_reader_census_rejects_missing_keys_extra_readers_and_unbounded_pages`
  exercises the same census on changed source, including key and binding removal,
  extra or unexecuted SQL, changed locks, page bounds and DDL guard changes. The
  read-method fingerprints do not bind helper bodies, using directives or migration
  callers. These source checks complement rather than replace the separate
  authorization, database column-grant, RLS and runtime caller proofs; they do not
  establish runtime behavior on their own.
- **Source:** [ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md)
  § The reconciliation matrix; [ADR-0047](../decisions/0047-transactional-background-job-runtime.md);
  [ADR-0079](../decisions/0079-bounded-identity-administration.md).
- **Type:** xUnit and source scan. **Kind:** structural.
- **Status:** **Implemented** (`TenantContextConstructionTests`); the
  [Phase 03 Status block](../roadmap/phase-03-identity-admin.md) owns qualification.
- **Phase:** 02a (Packet 7); 02b-4 (bounded lifecycle inventory); 03 (bounded
  administration and own-context navigation).

#### `Tenant_Scope_Widening_Is_Never_Set_From_Request_Input`

- **Asserts:** for human reads, `app.scope = 'tenant'` requires eligible Tenant
  capability and an explicitly declared tenant-wide read. Its immutable operation witness binds
  the actor, tenant/org, invocation and physical owner generation; request input,
  HostOnly and branch grants cannot mint it. SQL/EF use the same read authority;
  nested mismatch and pooled cleanup preserve its lifetime. Restrictive
  organization writes never widen, and `ITenantContext` has no writable scope
  authority. ADR-0052's immutable HostScope only narrows public reads.
- **Implemented P03-2 extension:** ADR-0076's witness-consuming announcement,
  mint/consume caller census and real SQL/EF/read-lifetime controls replace the
  earlier no-carrier premise. The
  [operation-bound read contract](05-database.md#operation-bound-read-authority)
  owns the canonical behavior.
- **P03-3 extension:** ADR-0080's separate fixed-seed source and
  scope-only sites are covered by
  [`Fixed_Seed_Prerequisites_Use_A_Closed_Temporary_Read_Scope`](#fixed_seed_prerequisites_use_a_closed_temporary_read_scope).
  ADR-0081 adds the distinct one-consumer
  [`Fixed_Seed_Origin_Adoption_Requires_Completed_Foundation`](#fixed_seed_origin_adoption_requires_completed_foundation),
  reusing the same two SQL sites through one private typed transition.
  Those canonical entries own their implementation status and test evidence.
  The existing Protected-human witness and Implemented controls remain unchanged.
- **Source:** ADR-0036 § The reconciliation matrix;
  [ADR-0076](../decisions/0076-live-scoped-authorization.md).
- **Type:** compiled/source/reflection controls plus actual app-role SQL/EF tests.
  **Kind:** structural and behavioural.
- **Test owners:** `TenancyConventionTests`, `OperationPolicyOwnershipTests`,
  `OperationReadScopeTests`, `MembershipDirectoryIntegrationTests`.
- **Status:** **Implemented** — `TenancyConventionTests`; the exact announcement
  scan and planted setter controls complement the witness and real scoped-read
  owners above. `The_Scope_Setter_Scan_Can_Actually_Fail` retains its dirty controls.
- **Phase:** 02a (Packet 7 registers it; Packet 10 implements the negative fence);
  03 (P03-2 implements the operation-bound carrier).
- **Note:** Packet 7 shipped without an `app.scope` carrier. ADR-0076 now supplies
  the operation-bound witness while `ITenantContext` remains without writable
  scope authority. The
  [Phase 03 Status block](../roadmap/phase-03-identity-admin.md)
  owns source-bound qualification.

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

  *Subject, vacuous:* there is no Platform permission to hold. The canonical
  `AuthorizationBehavior` uses explicit live policies, while the retained
  assignable manifest contains only Tenant/Organization pairs. API authentication
  is delivered under ADR-0064; the Platform-scope permission remains Identity work in
  [Phase 03](../roadmap/phase-03-identity-admin.md). So
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

## P02d-4 public-read obligations

These names are registered against
[ADR-0052](../decisions/0052-anonymous-public-read-boundary.md) and the
[accepted P02d-4 package](../roadmap/phase-02d-delivery-log.md#p02d-4-accepted-answers).
Every structural mechanism requires a clean control and planted offenders for its
claimed paths. Individual Status rows distinguish registration from passing tests. Runtime proofs are independent
of structural detection.

#### `HostScope_Is_Constructed_Only_By_The_Factory`

- **Asserts:** immutable host provenance has no public constructor or mutation path;
  its only construction site is TenantContextFactory. Ambient, unresolved and
  claim-only contexts carry none.
- **Source:** ADR-0036 Amendment 8; ADR-0052.
- **Type:** xUnit + reflection + IL. **Kind:** structural.
- **Status:** **Implemented** (`TenantContextConstructionTests`, P02d-4 Step 1); shape and second-call-site planted controls accompany it.
- **Phase:** 02d (P02d-4 Step 1).

#### `PublicSurface_Requests_Are_Registered_Off`

- **Asserts:** every marked public request is explicitly Off in the merged audit
  catalogue, with a non-empty request set and a planted non-Off classification.
- **Source:** ADR-0052; API Standards § Public surface.
- **Type:** xUnit + reflection + audit catalogue. **Kind:** structural.
- **Status:** **Implemented** (`PublicSurfaceTests`, P02d-4 Step 2); planted bypass and
  clean controls accompany the production census.
- **Phase:** 02d (P02d-4 Steps 2–3).

#### `PublicSurface_Endpoints_Dispatch_Through_The_Pipeline`

- **Asserts:** the exact public endpoint set dispatches marked requests through
  ISender once per action with a directly constructed marked argument; direct
  handler, helper, unused marked construction, mixed/unverifiable sends and
  minimal-API bypass probes are detected. The controller-wide/generated Send
  census excludes extra sends in private controller helpers.
- **Source:** ADR-0052; Backend Coding Standards § Pipeline Behaviors.
- **Type:** xUnit + endpoint metadata + IL/source scan. **Kind:** structural.
- **Status:** **Implemented** (`PublicSurfaceTests`, P02d-4 Step 2); planted bypass and
  clean controls accompany the production census.
- **Phase:** 02d (P02d-4 Steps 2–3).

#### `PublicSurface_Controllers_Do_Not_Access_Persistence`

- **Asserts:** public controllers and reachable helpers neither acquire persistence
  nor open independent connections, including concrete, interface/abstract
  implementation and service-locator paths. Helpers cannot acquire ISender or
  IMediator to dispatch a second request after the public action completes.
- **Source:** ADR-0052; Security Standards § Tenant Context.
- **Type:** xUnit + IL/source scan. **Kind:** structural.
- **Status:** **Implemented** (`PublicSurfaceTests`, P02d-4 Step 2); planted bypass and
  clean controls accompany the production census.
- **Phase:** 02d (P02d-4 Steps 2–3).

#### `PublicSurface_Response_Schemas_Exclude_Internal_Fields`

- **Asserts:** DTO and served/snapshot OpenAPI response trees obey the closed public
  allowlist and recursive private-field denylist, with nested offending controls.
- **Source:** ADR-0052; accepted P02d-4 G26/G31.
- **Type:** xUnit + reflection + served OpenAPI. **Kind:** structural + behavioural.
- **Status:** **Implemented** (`OpenApiContractTests.PublicSurface_Response_Schemas_Exclude_Internal_Fields`, P02d-4 Step 4); nested field/raw-shape controls and a literal DTO census accompany it.
- **Phase:** 02d (P02d-4 Steps 3–4).

#### `PublicSurface_Reads_Do_Not_Invoke_JsonSchema_Validation`

- **Asserts:** public reads never acquire or invoke the write-time schema validator,
  including transitive helper paths; bounded structural projection remains legal.
- **Source:** ADR-0043; ADR-0052.
- **Type:** xUnit + IL. **Kind:** structural.
- **Status:** **Implemented** (`PublicSurfaceTests`, P02d-4 Step 3); non-empty
  production handler census and direct/concrete/transitive/generic-interface
  offender controls accompany a clean helper control. In-memory production
  helper plants exercise Domain, Contracts and shared Infrastructure through
  the real handler and assembly census.
- **Phase:** 02d (P02d-4 Step 3).

#### `PublicSurface_Transactions_Refuse_Writes`

- **Asserts:** real app-role public frames reject EF and raw SQL writes; a writable
  control succeeds. Mixed-mode nesting and partial setup failures poison the unit
  despite absorption; rollback/cancellation reset physical mode without clearing
  poison. A successful read-only frame permits a later writable frame.
- **Source:** ADR-0040 Amendment 8; ADR-0052.
- **Type:** xUnit + PostgreSQL as learnstack_app. **Kind:** behavioural.
- **Status:** **Implemented** (`PublicReadTransactionTests`, P02d-4 Step 1); real app-role EF/SQL refusal, writable controls, same/mixed-mode joins, partial-setup faults/cancellation and reuse/reset cases.
- **Phase:** 02d (P02d-4 Step 1).

#### `PublicSurface_Contract_Matches_Served_OpenApi`

- **Asserts:** production composition and committed snapshot have the same document
  after object-key normalization, preserving array order; exactly eight public
  operations and four public GET wrappers exist, alongside the separate
  authenticated AccountSite and membership-directory GET contracts (ten operations
  and six GET contracts). The four public wrappers and AccountSite-only native
  authenticated transport remain closed. Drift and policy-breaking
  fixtures fail the pinned diff/generation checks.
- **Source:** ADR-0024; ADR-0052; ADR-0076; accepted P02d-4 G31.
- **Type:** xUnit + served OpenAPI + SDK/CLI fixtures. **Kind:** behavioural +
  structural.
- **Status:** **Implemented** (`OpenApiContractTests.PublicSurface_Contract_Matches_Served_OpenApi`, P02d-4 Step 4; P03-2 Step 5 extends the contract union); ten-operation, disjoint public/authenticated GET ownership, directory projection/403/pagination, missing-operation/parameter/status and array-drift controls; generated SDK coverage and pinned CLI fixtures accompany it.
- **Phase:** 02d (P02d-4 Step 4); 03 (P03-2 Step 5 contract extension).

## P02d-5 public server rendering controls

**ADR-0054 native/URL controls complete — 2026-10-09; both reviews passed.**
Remediation Step 2 extends the rules below. `verify-ingress.mjs` exercises the
real native launcher over TLS: GET/HEAD on ordinary, scaffold and matcher-exempt
health/assets; unsupported callback methods never reaching Next/bootstrap;
bodyless HEAD, no-store refusal, exact favicon matcher and no framework
identification. Production upgrades close at the server; development retains a
real HMR 101/frame beyond its handshake deadline. `development-hmr.test.ts` proves
bounded handshake ownership and failed/ignored delegation closure.
`ingress.test.ts` covers signed raw context, full-URL RSC/`_rsc` projection and
parsed/raw forwarding-header removal. Middleware/entry controls retain raw-route
refusal of suffix aliases and inert ordered redirect query data. The real-API
`PublicServerRenderingTests` fixture verifies restored Flight protocol inputs
through the pinned Next adapter, with five GET probes and HEAD causing exactly six
live bootstrap calls. The delivery record owns execution and independent review
results. Step 3 completes fixture reliability/containment after both review rounds
and verified fixes; Step 4 completes source/tooling proof after both review rounds.

`fixture-support.test.ts` exercises positive environment construction, sticky
split/early output verdicts, exact EOF boundaries, retained trees after cleanup
refusal and shared owner cancellation/disposal. Controls for
surviving descendants and retired handles live in `fixture-cleanup.test.ts`.
`verify-ingress-cleanup.mjs` runs real source-mutant cleanup/TLS/log/readiness/
upgrade controls through `verify-ingress-controls.mjs`; normal `verify-ingress.mjs`
remains its independent clean socket/framework control. The shutdown-only leak
mutant requires a final verdict after stopped-child output drains.
`PublicServerRenderingTests`
uses actual stdin/EPIPE failures and structured-log positive/negative controls;
its real production build checks configured private values against nonempty client
assets and rejects a successfully compiled leaking Client Component.

Step 4 extends `public-boundaries.test.ts` with named constructor/conversion,
converted-record spread/destructure, inherited alias/export and real render-helper
mutants, plus actual pinned Next SWC and separate TypeScript verbatim controls.
`lint-tooling.test.ts` exercises the shared real subject census/configuration and
actual staged hook invocations; `frontend-runner.test.ts` refuses symlinked packages
and test sources. `scripts/test-adr-status.py` exercises the lifecycle parser and
actual CI disclosure block against an Accepted record with a delivery banner.
`TrustedVisitorHttpTests` includes
`Repeated_raw_secret_headers_spend_the_exhausted_direct_fallback_budget`;
it observes two real Kestrel secret fields before proving fallback refusal and a fresh
single-field positive control. `PublicLocalLaunchTests` launches the actual local
API script with app credentials and an isolated empty user-secret root, without a
PlatformAdmin credential. Mapped-peer and strict dotted-tail unit controls pin
current framework behavior. Both independent Step 4 reviews and focused fix
verification pass; the delivery record owns execution evidence.

#### `Public_Renderer_Uses_Trusted_Ingress_And_Server_Only_Transport`

- **Asserts:** every public caller derives provenance from the native socket ingress;
  only one server-only adapter constructs API-hop headers, never client assets or
  the SDK package. Real sockets, forged/bypassed controls and import/header plants
  accompany the structural census; no empty subject can satisfy the rule.
- **Source:** ADR-0053; Accepted P02d-5 G30/G33/G35.
- **Type:** frontend structural + socket/runtime. **Kind:** structural.
- **Status:** **Implemented** — `public-boundaries.test.ts` names this rule and
  pins the nonempty production TypeScript graph, marker, direct/global/aliased
  fetches, hop setters and transitive Client Component dependencies. Clean/planted
  controls cover imports, reexports and literal dynamic imports. P02d-6 Step 1
  includes server configuration and bundled JSON catalogues in the graph and
  plants direct Client Component imports of each bundle; ordinary public JSON
  remains admissible. Arbitrary eval,
  runtime reassignment, external package bodies and native MJS are outside the
  source scan; production socket/TLS and `PublicServerRenderingTests` prove the
  runtime boundary, actual client-import rejection and private-value containment.
- **Phase:** 02d (P02d-5).

#### `Public_Renderer_Does_Not_Share_Tenant_Representations`

- **Asserts:** institution public paths use dynamic/no-store transport and no shared
  route/data/ISR/bootstrap cache; request-local reuse never crosses an incoming
  request. Production-build controls prove same-path/different-host isolation.
- **Source:** ADR-0053; Accepted P02d-5 G37.
- **Type:** frontend AST + actual production Next/API/PostgreSQL. **Kind:**
  structural + behavioural.
- **Status:** **Implemented** — `public-boundaries.test.ts` names this rule, checks
  the public layout policy and follows public/helper imports for shared storage,
  forbidden Next cache APIs and static/revalidation overrides. Clean/planted
  controls accompany it; request-local React cache remains allowed. P6 remediation
  adds retained factory/collection, module/static/global storage and navigation
  import controls, including admitted Studio/portal roots. P02d-6 Step 1
  also constrains the complete AST shape of `request-memo.ts`: a private weak map
  keyed only by Next's exact request-store headers object, publish-before-load
  semantics and retained rejected work. Planted strong maps, copied/value/caller
  keys, eager loads, rejection retries and exposed/additional collections fail.
  Its unit controls distinguish overlapping equal-header requests and subsequent
  requests; separate P6 modes below supply actual product-page proof.
  `PublicServerRenderingTests` proves cold/interleaved same-path HTML/RSC host
  isolation and next-request publication freshness as `learnstack_app`. It owns
  disposable routes in transport mode and actual P6 product routes in separate
  modes; P7 browser delivery remains outside this fixture.
- **Phase:** 02d (P02d-5).

## P02d-6 public UI localization controls

The maintainer accepted ADR-0027 and the P6 decision package on 2026-10-09. The
rules below are commitments before the first implementation; none is a passing
test claim at acceptance. Existing P5 source/runtime controls remain mandatory,
and P6 product proofs supplement the isolated synthetic transport fixture.
P02d-6 implements the five rules below and completes its manual smoke on
2026-10-10; the [packet closeout](../roadmap/phase-02d-delivery-log.md#p02d-6-packet-closeout-2026-10-10)
owns execution, review and manual evidence.

### `Ui_Catalogues_Cover_Public_Call_Sites`

- **Asserts:** nonempty supported UI catalogues have equal key sets, valid ICU
  syntax and matching argument names/types; every used key exists. General UI
  feature keys are separate from backend `lockey_*` wire keys. Planted per-locale
  and absent-from-all keys, unknown callsites, bad ICU and mismatched arguments
  fail. Public graphs include configuration and JSON catalogue dependencies.
  ADR-0070 extends the same catalogue/ICU/callsite checks to the app-local `auth`
  namespace; only translated labels, never complete catalogues, cross to clients.
- **Source:** ADR-0027; Accepted P02d-6 G39; ADR-0070.
- **Type:** frontend catalogue/callsite + production build. **Kind:** structural.
- **Status:** **Implemented** — `frontend/apps/web/src/i18n/catalogues.test.ts`;
  actual request-configuration callbacks in `frontend/apps/web/src/i18n/request.test.ts`
  and typed translator argument forwarding in
  `frontend/apps/web/src/server/public-ui.test.ts`;
  `PublicServerRenderingTests` production build, P02d-6. The ADR-0070 extension
  executes `frontend/apps/web/src/i18n/auth-catalogues.test.ts` and the same
  checked helpers, P02b-7 Step 4.
- **Phase:** 02d (P02d-6); 02b (P02b-7 extension).

### `Public_Ui_Locale_Does_Not_Change_Content_Admission`

- **Asserts:** request configuration uses the existing verified signed target and
  live membership. Whole-catalogue UI fallback changes neither exact API locale
  nor admission. Document and UI groups retain their actual languages/directions;
  test-owned enabled RTL content and overlapping host/locale requests exercise
  the real configuration without another authority carrier or bootstrap call.
- **Source:** ADR-0027; Accepted P02d-6 G39 and G38(b,c).
- **Type:** frontend mapping + actual production rendering. **Kind:** behavioural.
- **Status:** **Implemented** — `PublicServerRenderingTests` actual foundation/
  product-isolation modes, `frontend/apps/web/src/i18n/locale.test.ts` and
  `frontend/apps/web/src/server/public-request.test.ts`, P02d-6.
- **Phase:** 02d (P02d-6).

### `Public_Theme_Emits_Only_Validated_Color_Tokens`

- **Asserts:** the complete four-color value is admitted atomically before fixed
  `--ls-*` CSS emission. Malformed/null input preserves the whole default palette;
  no raw style attributes, extra tokens, URLs or organization merges appear.
  Effective attribution alone controls the platform label, independently of theme.
- **Source:** Accepted P02d-6 G16(g)/G42; Standards 07 § Tenant Branding.
- **Type:** frontend pure mapping + rendered document. **Kind:** behavioural.
- **Status:** **Implemented** — `PublicServerRenderingTests` foundation/
  product-isolation modes and
  `frontend/apps/web/src/components/public/foundation.test.tsx`, P02d-6.
  `frontend/apps/web/src/test/public-rendering-assertions.test.ts` supplies
  clean/planted controls against the production theme assertion itself.
- **Phase:** 02d (P02d-6).

### `Public_Pages_Preserve_Approved_Response_States`

- **Asserts:** real product routes preserve shared request-local reads, opaque
  pagination and no-store isolation. Missing/hidden content follows the approved
  same-host 307→fixed localized 404 chain; known content-call errors use bounded
  HTTP 200/noindex states. The status page makes no Education call. Production
  tests observe actual status, visible HTML, call counts, freshness and no leaks.
- **Source:** Accepted P02d-6 G40/G38(b,c); ADR-0053/0054.
- **Type:** actual production Next/API/PostgreSQL. **Kind:** behavioural.
- **Status:** **Implemented** — `PublicServerRenderingTests` foundation/pagination/
  presentation/product-freshness modes and
  `frontend/apps/web/src/server/public-resource.test.ts`, P02d-6.
  `frontend/apps/web/src/test/public-rendering-assertions.test.ts` exercises the
  shared production status and whole-response containment predicates, including
  Next's nested HTML-safe Flight escaping. Raw ICU sentinels detect unchanged
  complete catalogue payloads; source fences independently restrict imports.
- **Phase:** 02d (P02d-6).

### `Public_Pages_Expose_Localized_Accessible_Semantics`

- **Asserts:** public states expose document language/direction, one main,
  descriptive headings/title, skip navigation and language-bearing fallback UI.
  Applicable jsx-a11y errors fail through the real lint config with planted
  controls. Automated semantics supplement mandatory manual keyboard, focus,
  320 CSS px reflow, contrast and screen-reader evidence; they do not replace it.
- **Source:** Accepted P02d-6 G43; Standards 16 § Testing.
- **Type:** frontend lint/DOM + actual production HTML. **Kind:** structural.
- **Status:** **Implemented** — `PublicServerRenderingTests` product-page HTML
  assertions, `frontend/apps/web/src/test/accessibility-lint.test.ts` and
  `frontend/apps/web/src/components/public/lesson.test.tsx`, P02d-6. Manual
  keyboard/focus/reflow/contrast and maintainer VoiceOver evidence remain separate
  in the packet closeout; these tests do not claim a full accessibility audit.
  Emitted viewport metadata permits zoom, tested by the shared production
  assertion controls; this does not prove browser zoom or WCAG conformance.
- **Phase:** 02d (P02d-6).

## P02d-6 request-local bootstrap admission controls

**Implemented — 2026-10-10; both independent review rounds complete.** Production
replacement proofs and planted controls pass for the five rules below. All three
steps completed both independent review rounds and focused fix verification.
Existing P5/P6 controls remain mandatory. The
[Step 3 record](../roadmap/phase-02d-delivery-log.md#adr-0055-step-3--production-admission-proof-and-closeout)
owns actual execution, completed reviews and verified fixes. Six existing
production modes pass with revised call accounting. The additional production Fact is
`Native_admission_preserves_wire_failures_request_lifetimes_and_browser_fallback` in
`backend/tests/LearnStack.Tests.Integration/Database/PublicAdmissionRenderingTests.cs`,
driven by `frontend/apps/web/scripts/verify-public-admission.mjs`. It passes its
wire, lifecycle and stock-browser/HMR phases against the real API/PostgreSQL;
test-only instrumentation stays in the disposable copied app.

### `Public_Admission_Uses_Only_The_Active_Native_Context`

- **Asserts:** the native-created, versioned server-only ALS holder binds a fresh
  request to its captured host/peer/method/signed target. Middleware and RSC share
  that actual holder/store through the pinned production Next build; missing,
  duplicated, disposed or mismatched context fails closed without header lookup,
  another site read or an alternate holder. Planted failures and clean controls
  exercise both facade admission and actual framework propagation.
- **Source:** ADR-0055; accepted P02d-6 G40 supersession.
- **Type:** frontend runtime + actual production Next/API. **Kind:** behavioural.
- **Status:** **Implemented** — `PublicAdmissionRenderingTests` proves actual
  context propagation and missing/mismatched/duplicate-context refusal with
  `verify-public-admission.mjs`. Unit controls live in
  `frontend/apps/web/src/server/public-admission-runtime.test.ts`,
  `frontend/apps/web/src/server/public-admission.test.ts` and existing
  `PublicServerRenderingTests` / `verify-public-rendering.mjs` integration.
- **Phase:** 02d (P02d-6 remediation Steps 1–3).

### `Public_Bootstrap_Refusals_Preserve_Exact_Http_Responses`

- **Asserts:** middleware owns first-wire bootstrap 404/429/503, no-store and
  bodyless HEAD. Only supported 429/503 cases forward parsed Retry-After from
  zero through sixty unchanged; malformed/date/negative/out-of-range values are
  omitted. Timeout, invalid/oversized responses and configuration/context faults
  are neutral 503. Failed bootstrap permits no Education operation, newly emitted
  tenant theme, private marker or raw diagnostic. Broken refusal and containment
  controls must fail beside positive controls.
- **Source:** ADR-0055; ADR-0054 Amendment 8.
- **Type:** frontend mapping + actual production HTTP. **Kind:** behavioural.
- **Status:** **Implemented** — `PublicAdmissionRenderingTests` and
  `verify-public-admission.mjs` prove first-wire refusals and a wrong-status mutant;
  `frontend/apps/web/src/server/configured-public-client.test.ts` proves success
  and Problem Details media-type admission. Mapping and lifetime units live in
  `frontend/apps/web/src/middleware.test.ts`,
  `frontend/apps/web/src/server/public-request.test.ts` and existing
  `PublicServerRenderingTests` / `verify-public-rendering.mjs` integration.
- **Phase:** 02d (P02d-6 remediation Steps 2–3).

### `Public_Admission_Reuses_One_Immutable_Snapshot_Per_Request`

- **Asserts:** one bounded, deeply immutable validated Site DTO is published only
  after successful middleware entry and shared by metadata/layout/page/UI/error
  consumers without another bootstrap. Completed product/status documents cost
  two/one calls; followed missing-detail GET costs three. Identical and differing
  host/locale requests overlap without shared values; the next request reads live
  state. HTML/RSC/prefetch and Flight-to-document fallback have separately proven
  counts, while content 200/noindex and missing 307-to-404 remain unchanged.
- **Source:** ADR-0055; accepted P02d-6 G40 supersession.
- **Type:** frontend snapshot/memo + production rendering. **Kind:** behavioural.
- **Status:** **Implemented** — `PublicAdmissionRenderingTests` and
  `verify-public-admission.mjs` prove overlapping HTML/RSC/prefetch stores, exact
  calls and keep-alive freshness; original product modes retain live-state proof.
  `frontend/apps/web/src/test/admission-browser.test.ts` supplies clean/planted
  Flight-to-document observation controls. Snapshot and consumer units live in
  `frontend/apps/web/src/server/public-admission-runtime.test.ts`,
  `frontend/apps/web/src/server/public-admission.test.ts`,
  `frontend/apps/web/src/server/public-request.test.ts` and
  `PublicServerRenderingTests` / `verify-public-rendering.mjs` integration.
- **Phase:** 02d (P02d-6 remediation Steps 1–3).

### `Public_Admission_Ends_With_The_Native_Request`

- **Asserts:** finish, early close and shutdown close each store once, clear its
  snapshot/listeners and cancel bootstrap/content transport. Publication and
  downstream work refuse late continuations, including valid HEAD work after
  response finish, without exposing the context/DTO in diagnostics. Keep-alive
  and HMR do not reuse snapshots. Active-context/snapshot observations return to
  zero; this does not claim all React work or captured local references ended.
- **Source:** ADR-0055.
- **Type:** runtime lifecycle + native production fixture. **Kind:** behavioural.
- **Status:** **Implemented** — `PublicAdmissionRenderingTests` and
  `verify-public-admission.mjs` prove abort, shutdown, valid HEAD late refusal and
  zero retained stores/snapshots. `frontend/apps/web/scripts/admission-completion.mjs`
  waits for native finish/close independently of runtime counts;
  `frontend/apps/web/src/test/admission-completion.test.ts` proves that client EOF
  cannot release it and planted missing cleanup still fails the zero-count check.
  `frontend/apps/web/scripts/admission-hmr.mjs`
  proves fresh admission after source/API change in the same development process;
  `frontend/apps/web/src/test/admission-hmr.test.ts` supplies clean/planted freshness,
  lifetime and cleanup controls. `admission-hmr-watch.mjs` waits for the pinned
  Next compiler's real completion frame before the sole refresh request;
  `frontend/apps/web/src/test/admission-hmr-watch.test.ts` proves that sync/client
  frames cannot release this source-change barrier. Unit and retained ingress
  controls live in
  `frontend/apps/web/src/server/public-admission-runtime.test.ts`,
  `frontend/apps/web/src/server/public-admission.test.ts`,
  `PublicServerRenderingTests`, `verify-public-rendering.mjs` and
  `frontend/apps/web/scripts/verify-ingress.mjs`.
- **Phase:** 02d (P02d-6 remediation Steps 1–3).

### `Public_Admission_Preserves_Production_Ingress_Boundaries`

- **Asserts:** actual product/status/scaffold HTML/RSC, navigation and error paths
  preserve provenance, API admission, READ ONLY/RLS and server-only containment.
  Invalid host/locale/stamp, stock-launch bypass and planted broken context are
  refused. Native method/upgrade, exempt asset/health fallback, no build bootstrap
  and development HMR controls remain effective. No header DTO carrier, shared
  representation cache, fallback context or private client asset is introduced.
- **Source:** ADR-0055; ADR-0052/0053/0054.
- **Type:** source/build + actual production Next/API/PostgreSQL. **Kind:**
  structural + behavioural.
- **Status:** **Implemented** — `PublicAdmissionRenderingTests` and
  `verify-public-admission.mjs` prove stock-browser fallback, development HMR,
  no build bootstrap and private-value containment. Browser/HMR assertion controls
  live in `frontend/apps/web/src/test/admission-browser.test.ts` and
  `frontend/apps/web/src/test/admission-hmr.test.ts`. Retained boundary controls are
  `frontend/apps/web/src/test/public-boundaries.test.ts`,
  `PublicServerRenderingTests`, `verify-public-rendering.mjs` and
  `frontend/apps/web/scripts/verify-ingress.mjs`, with facade/runtime unit controls.
- **Phase:** 02d (P02d-6 remediation Steps 1–3).

## P02d-7 public demo and Lighthouse controls

These behavioral rules are Implemented under Accepted ADR-0056 by
`frontend/apps/web/scripts/verify-public-demo.mjs`, its contract/gatherer/worker
and the always-running `public demo + lighthouse` job through `make demo`.
`src/test/public-demo.test.ts` and `src/test/demo-lifecycle.test.ts` execute
planted failing and clean controls in the required frontend job. Actual browser
mutants, TLS refusals and a real Lighthouse timeout also run in the full proof.
`src/test/demo-source.test.ts` uses disposable Git repositories for clean/mutated
source checkpoints; `src/test/public-demo-diagnostics.test.ts` covers closed
failure attribution with real children and provider canaries.
`src/test/public-demo-ci.test.ts` executes the actual workflow deadline and
browser-installation shells, including effective APT acquire settings and
invocation-owned fragment cleanup on success and failure, plus bounded tail
commands that preserve ordinary and forced-timeout failure statuses;
`scripts/ci-public-chromium.test.mjs` tests owned Linux sandbox preparation and
cleanup before the job runs the real managed-browser launch smoke.
The [Step 4 record](../roadmap/phase-02d-delivery-log.md#p02d-7-step-4--ci-enforcement-and-phase-exit-2026-10-11)
owns exact CI and protection receipts. These are Node behavioral rules, not
C# architecture-test methods or a full WCAG/production-performance claim.

### `Public_Demo_Uses_Seeded_Production_Pages`

- **Asserts:** the three seed-derived host/locale paths follow actual catalog,
  course and lesson links in the same production build/database; expected
  identity, ordered fields, computed themes and hidden-content exclusions hold.
- **Source:** [ADR-0056](../decisions/0056-public-demo-browser-and-lighthouse-enforcement.md).
- **Scope:** nine delivered pages; full Playwright/axe remains Phase 06.
- **Type:** Node browser proof with wrong-page/error-page controls.
  **Kind:** behavioural.
- **Status:** **Implemented** — `verify-public-demo.mjs` browser mode and
  `public-demo.test.ts` actual-page controls, executed by the jobs above.
- **Phase:** 02d (P02d-7 Step 3).

### `Public_Lighthouse_Reports_Enforce_Budgets`

- **Asserts:** all 27 current report pairs pass actual-page admission and exact
  invocation/configuration census; hard budgets and advisory timing medians are
  distinguished without permitting missing or invalid measurements. Both JSON
  and HTML byte digests bind each pair to its invocation and source commit/tree;
  missing, corrupt or substituted members fail the same pair validation.
- **Source:** [ADR-0056](../decisions/0056-public-demo-browser-and-lighthouse-enforcement.md#lab-profile-and-budgets).
- **Scope:** pinned Lighthouse/Chromium; every seed lane/URL/run.
- **Type:** Node report validation and real audits, with stale/missing/error and
  budget-offender controls. **Kind:** behavioural.
- **Status:** **Implemented** — named Node proof and planted controls above.
- **Phase:** 02d (P02d-7 Step 3).

### `Public_Initial_JavaScript_Gzip_Budget_Is_Measured`

- **Asserts:** the cold-navigation Script response and executable-inline census
  is complete; normalized level-9 gzip totals are strictly below 200000 bytes.
- **Source:** [ADR-0056](../decisions/0056-public-demo-browser-and-lighthouse-enforcement.md#normalized-initial-javascript-gzip-size).
- **Scope:** the ADR's collection window, origin coverage and exclusions.
- **Type:** Node source accounting, with omitted/empty/malformed/over-budget and
  clean-source controls. **Kind:** behavioural.
- **Status:** **Implemented** — named Node proof and planted controls above.
- **Phase:** 02d (P02d-7 Step 3).

### `Public_Demo_Preserves_Admission_And_Cleanup`

- **Asserts:** real quotas, positive API observation, stopped CI-owned Keycloak,
  scoped TLS trust, serial deadlines and confirmed owned cleanup remain intact.
  CI observes the owned repository's clean commit/tree before restore/build,
  entering proof and before success, without inherited Git environment overrides.
  These checkpoints do not attest ignored inputs or edits restored between them.
  Closed diagnostics retain failure origin and status ownership; intentional
  teardown signals do not become a cleanup failure's process status.
- **Source:** [ADR-0056](../decisions/0056-public-demo-browser-and-lighthouse-enforcement.md#pacing-runtime-and-artifacts).
- **Scope:** the shared demo lifecycle and full-stack CI job.
- **Type:** Node lifecycle/proof controls and real production navigation.
  **Kind:** behavioural.
- **Status:** **Implemented** — named Node proof and planted controls above.
- **Phase:** 02d (P02d-7 Steps 1, 3 and 4).


## P02b-7 tenant BFF obligations

The maintainer accepted [ADR-0070](../decisions/0070-owned-tenant-bff-sessions.md)
and the five-step package on 2026-10-23. Step 5 delivers the local baseline
controls below, including actual warm-provider/API and separate HTTPS browser
evidence, failure cleanup and nonzero-shutdown counters, and the stopped-provider
public demo. The phase Status and dated correction records own current review
and exact-source hosted qualification; local implementation alone claims no
hosted timing fit. P03-3 extends these same canonical rules with native entry,
administration and prepared-page controls; its
[Status block](../roadmap/phase-03-identity-admin.md) owns their qualification.
Prior receipts do not qualify subsequent source changes.

#### `Bff_Credentials_And_Projections_Are_Confined`

- **Asserts:** only the native owner/RP adapter holds credentials; public wrappers,
  browser bodies/Flight/session projections, diagnostics and non-web environment
  projections contain none. Exact AccountSite validates actor/scope independently;
  native observation exposes scalar logical occupancy only, never credential
  values, snapshots or a heap-zeroization claim. Browser-storage controls cover
  IndexedDB and Cookie Store; closed response-hook census includes not-found and
  instrumentation entries. Native preparation publishes a complete immutable
  page snapshot before rendering; the passive RSC facade exposes only that
  snapshot and its liveness assertion, never a credential, API operation or
  preparation capability. The exact seven protected page views and their shared
  render helpers have source-census and AST dirty controls against additional
  asynchronous work, IO and framework background work, including transitive
  aliases. Entry, member-list and current-scope continuations conceal upstream
  cursor material and authenticate their distinct purposes.
- **Source:** ADR-0070/0071/0078/0079; Security 11 § Tenant BFF sessions and native entry.
- **Type:** frontend source/production canaries. **Kind:** structural.
- **Test owner:** `frontend/apps/web/src/test/bff-boundaries.test.ts` and
  `src/test/public-boundaries.test.ts`, using the production graph and AST
  analysis in `src/test/public-boundary-analysis.ts`; projection and cursor
  controls in `src/server/auth/page-preload.test.ts` and
  `src/server/auth/store-page-preload.test.ts`.
- **Status:** **Implemented** — frontend boundary, native protocol/canary and
  fixture-output controls named above.
- **Phase:** P02b-7 Steps 1/3–5; P03-3 Steps 8–9 extend native entry and prepared views.

#### `Bff_Session_Refresh_And_Revocation_Have_One_Owner`

- **Asserts:** one shared native store owns global/source/subject credits,
  transactions/epochs, single-flight refresh, effective expiry, true extension,
  replacement/revocation and actual settlement. Expiry/stop/late work never
  resurrect a session or release an unsettled owner's credit. Logout-return
  owners retain subject credit; full expired-state scans reclaim settled slots.
  Independent clocks/provider windows bind lifetime and activity. Ordinary
  uncertain 401 retains without idle touch, while callback and failed refreshed
  package publication still refuse/revoke under ADR-0071. Independent long-provider
  windows isolate every availability category's no-idle-touch rule; publication
  clocks, caller cancellation and once-only local expiry/revocation diagnostics
  have separate controls. Session CSRF and logout state/proof remain independently
  random, rather than aliases of another capability. Entry-only callback and
  deliberate creation/selection never publish a tenant session prematurely.
  Switch choices use the old held scope's own-context API, including derived
  branch choices, with no global fallback. Fresh admission/AccountSite and actual
  old-work settlement precede handle/CSRF replacement; original lifetime stays.
  Same-label or Unicode-lookalike different subjects cannot replace a live owner;
  entry provider logout preserves the primary local owner and retains its own
  credit through actual settlement. Entry continuation values conceal upstream
  association material, authenticate the actual owner/host/epoch/purpose, and
  preserve concurrent tabs without a page registry. Key custody clears on
  publication/logout/expiry/stop, including before ignored work settles.
  A separate private snapshot pool reserves before page API work and admits at
  most 128 snapshots globally and eight per actual owner. Each complete UTF-8
  JSON payload, including Site and common data, is bounded to 256 KiB: at most
  32 MiB of serialized payload, not a heap-memory bound. Published copies are
  deeply frozen and pinned to the actual request, owner epoch, credential revision
  and effective expiry. Native mutation-recovery and account-change documents use
  the same pool and output fence; invalidation withholds retained edits and CSRF
  values even after the mutation itself has settled. Abort, finish and close
  invalidate access but retain credit until actual API/shared-refresh work,
  native handler, registered Next framework work and response-body/write-callback
  cleanup settle. Stop joins these owners; delayed chunks cannot disclose an old
  snapshot. Real destroyed-before-close sockets admit no new write callback credit;
  an earlier issued callback retains its existing frame/snapshot credit until
  actual settlement. The framework hook accounts for registered work, with the selected
  RSC source controls above closing additional application work.
- **Source:** ADR-0070/0071/0078/0079; Security 11 § Tenant BFF sessions and native entry.
- **Type:** unit and actual response-owning routes. **Kind:** behavioural.
- **Test owner:** `frontend/apps/web/src/server/auth/session-store.test.ts` and
  `frontend/apps/web/src/test/bff-session-routes.test.ts`, with composed caller/
  middleware controls in `src/server/auth/account-site-policy.test.ts` and real
  protocol cancellation in `src/server/auth/session-http-lifetime.test.ts` and
  authenticated cursor controls in `src/server/auth/entry-cursor.test.ts`.
  Snapshot bounds and actual cleanup are exercised by
  `src/server/auth/page-snapshot-lifecycle.test.ts`,
  `src/server/auth/store-page-preload.test.ts`,
  `src/server/auth/request-runtime-lifetime.test.ts` and
  `src/server/auth/page-response.test.ts`; action recovery by
  `src/server/auth/native-action-response.test.ts`; actual Next registered-work
  retention and late output by `scripts/verify-bff-protocol.mjs`.
- **Status:** **Implemented** — actual owner/route controls above and
  `TenantBffSessionTests` real refresh/maximum and shutdown/failure controls.
- **Phase:** P02b-7 Steps 2–5; P03-3 Steps 8–9 extend entry, operation and snapshot ownership.

#### `Bff_Auth_Admission_And_Csrf_Are_Closed`

- **Asserts:** only the approved method/path/field set reaches fixed-provider
  dispatch after native provenance, bounded input and the appropriate Origin/
  transaction/session-CSRF proof. Unknown hosts/state and disabled mode allocate
  no session/provider work. Read-only RSC, HEAD, assets/prefetch/passive reads
  perform no idle/refresh/cookie writes. Independent negatives reach real guards.
  The exact matcher/config/framework entry set, Origin for every POST and raw
  logout body suppression have independent dirty controls, including implicit
  write/end and a foreign origin with the same logout path. Fixed native port,
  POST return paths and exact provider/PKCE/logout wire parameters have isolated
  controls; fake-transport assertions run outside production exception conversion.
  Canonical cursor input refuses impossible lengths and nonzero pad bits before
  API work, while malformed upstream responses retain response-failure semantics.
  Authenticated cursor tampering or cross-purpose reuse refuses before private
  API work or snapshot reservation. The finite native preloader reads current
  capabilities and the exact own profile before conditional page data; unavailable
  Role-read capability causes zero Role API calls. A legitimate missing own
  profile on a derived branch uses the site fallback. Failed AccountSite authority
  publishes only public failure data, without cached private fields or CSRF.
  The exact twelve HTTP entries include seven protected views; the twelve
  POST actions remain closed. Ordinary passive documents still preload without
  idle/refresh writes, while HEAD and speculative prefetch avoid private preload.
- **Source:** ADR-0070/0078/0079; ADR-0054 dated auth extension; Security 11 § CSRF.
- **Type:** native listener and actual Next route controls. **Kind:** behavioural.
- **Test owner:** `frontend/apps/web/src/test/bff-auth-admission.test.ts` and
  `frontend/apps/web/src/test/bff-session-routes.test.ts`, with source census in
  `public-boundaries.test.ts` and raw TCP observation in
  `src/server/auth/logout-response.test.ts`, plus `src/server/ingress.test.ts`
  and `src/server/auth/provider.test.ts`. Prepared-page admission and conditional
  reads use `src/server/auth/page-preload.test.ts`,
  `src/server/auth/store-page-preload.test.ts` and `src/server/auth/auth.test.ts`;
  exact en/tr catalogue and preferred-locale selection use
  `src/i18n/administration-catalogues.test.ts` and `src/i18n/request.test.ts`.
  `src/components/auth/admin-pages.test.tsx` covers explicit replacement of an
  unavailable stored preference through the existing native field values; the
  no-script browser journey exercises that required choice and verifies its POST.
- **Status:** **Implemented** — native input/route controls above and the actual browser
  form/Origin and no-network startup cases in `TenantBffSessionTests`.
- **Phase:** P02b-7 Steps 3–5; P03-3 Steps 8–9 extend native forms and finite page preparation.

#### `Bff_Host_Cookies_And_History_Are_Isolated`

- **Asserts:** both retained HTTPS seed hosts actually store/send their own
  opaque cookies and cannot reuse another host's session/transaction; logout and
  browser history do not restore protected display. A separate real warm
  provider/BFF/API chain proves sign-in/AccountSite/refresh/extension/logout and
  stored provider events. Real paginated entry JSON/HTML, stock-browser next/Back
  URLs/history and second-page selection keep subject/authority material private;
  the original disclosure has a real old-source negative control. Stopped-provider
  public rendering remains independent. The seven prepared protected views retain
  native forms when JavaScript is disabled. Actual stock-browser page checks
  require ready views and specific visible contents; an unavailable view retaining
  the wrapper/h1 fails the same guard. No-script profile save/sign-out runs on both
  hosts/locales with an exact closed receipt. GET history restoration re-admits the
  document; native POST recovery replaces history with a validated fixed GET
  destination instead of resubmitting the mutation. Natural browser history
  observations and explicit persisted-event dirty controls remain distinct.
  Automated UI/browser checks do not qualify manual assistive-technology use;
  separate observed evidence is required, with qualification recorded by the
  [Phase 03 Status block](../roadmap/phase-03-identity-admin.md).
- **Source:** ADR-0070/0078/0079; Security 11 § Tenant BFF sessions and native entry.
- **Type:** native HTTPS browser and Docker integration. **Kind:** behavioural.
- **Test owner:** `frontend/apps/web/scripts/verify-bff-session.mjs`,
  `frontend/apps/web/scripts/verify-bff-browser.mjs` and backend
  `TenancyIdentity/TenantBffSessionTests.cs` using the app-role API/database.
  `src/components/auth/history.test.ts`, `src/components/auth/shell.test.tsx`
  and `src/components/auth/session.test.tsx` cover fixed GET recovery, native forms
  and session/unsaved-edit behavior; `scripts/verify-bff-protocol.mjs` exercises
  the actual Next prepared views and `verify-bff-browser.mjs` the no-script flow.
- **Status:** **Implemented** — `TenantBffSessionTests`, its real-chain/browser scripts
  and the stopped-provider public-demo lifecycle.
- **Phase:** P02b-7 Step 5; P03-3 Step 9 extends selected views, no-script and history
  controls. Phase 06 retains full browser OIDC end-to-end expansion. The Phase 03
  Status block owns current P03-3 qualification and review state.

## P02b-4 background-job obligations

Source: [Accepted ADR-0047](../decisions/0047-transactional-background-job-runtime.md#architecture-tests)
and the [nine-step package](../roadmap/phase-02b-delivery-log.md#p02b-4-decision-package-2026-10-15).
[Accepted ADR-0058](../decisions/0058-bounded-stock-hangfire-maintenance.md#architecture-tests)
extends the existing bootstrap and independent-capacity assertions below.
[Accepted ADR-0059](../decisions/0059-owned-hangfire-server-generations.md#architecture-tests)
adds public lifecycle/conditional-cleanup, closed storage and generation ownership
qualification to the existing bootstrap, credential and capacity obligations.
[Accepted ADR-0060](../decisions/0060-fatal-hangfire-startup-and-unsafe-unwind.md#architecture-tests)
adds the exceptional fatal-boundary assignments below.
[Accepted ADR-0061](../decisions/0061-known-external-job-failure-policy.md#architecture-tests)
extends seven existing assertions for completed external-failure policy.
[Step 7 qualification and both full reviews](../roadmap/phase-02b-delivery-log.md#p02b-4-step-7--external-stages-and-recovery-2026-10-17)
are complete. Step 9 implements and locally qualifies all nineteen complete
assertions below; each Implemented status names its actual canonical owner.
Acceptance alone promotes no rule, and a partial foundation does not enforce a
complete assertion. The
[Step 9 record](../roadmap/phase-02b-delivery-log.md#canonical-enforcement-reconciliation)
retains the original local enforcement scope; the
[merge closeout](../roadmap/phase-02b-delivery-log.md#p02b-4-merge-and-closeout-2026-10-19)
records final packet/review and PR-source CI/job-budget qualification.
Production business/recurrence registries remain empty; real framework subjects
and fixture definitions make the implemented controls non-vacuous.

### `BackgroundJob_Submission_Is_Transactional_And_Stamped`

- **Asserts:** own-module typed submission stamps trusted announcement/origin,
  inserts immediately on the same writable UoW and records only successful work;
  hostile getters, foreign/stale/read-only frames and absorbed failures cannot widen it.
- **Type:** architecture census and real PostgreSQL submission/rollback controls.
  **Kind:** structural + behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Integration`,
  `BackgroundJobSubmissionTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Integration/Database/BackgroundJobSubmissionTests.cs)).

### `BackgroundJob_Definitions_Stay_In_Their_Owning_Module`

- **Asserts:** construction-free frozen registries derive ownership from known
  module assemblies, bind exact typed contracts/requests/adapters and reject foreign
  shapes, mutable registration sources and caller-supplied authority.
- **Type:** registry unit controls and whole-production dependency/capability census.
  **Kind:** structural + startup + behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Architecture`,
  `OutboxDispatchConventionTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Architecture/BackgroundJobManifestBoundaryTests.cs)).

### `BackgroundJob_Execution_Restores_Tenant_And_Trace`

- **Asserts:** the sole JobExecutor context writer restores exact tenant/org,
  SystemActor and independent causal trace before business construction, then
  restores prior context through actual cleanup, cancellation and late completion.
- **Type:** context-writer census and actual executor integration controls.
  **Kind:** structural + behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Integration`,
  `BackgroundJobBusinessTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Integration/Database/BackgroundJobBusinessTests.Context.cs)).

### `BackgroundJob_Completion_Is_Owner_Atomic_And_Fenced`

- **Asserts:** closed root/stage participants preserve the existing owner/setters;
  business, produced work, MUST audit and current-token/time completion commit
  together. Lock-wait expiry and uncertain COMMIT cannot authorize a new attempt.
- **Type:** owner/capability census and real PostgreSQL fault/fence controls.
  **Kind:** structural + behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Architecture`,
  `OutboxDispatchConventionTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Architecture/BackgroundJobOwnerBoundaryTests.cs)).

### `BackgroundJob_Terminal_Audit_Is_Row_Bound`

- **Asserts:** only actual fenced UPDATE RETURNING mints the job witness; the two
  enumerated app/bypass callers bind stored identities and live transaction to the
  sixth audit write. Bind the actual selected normal outcome kind/policy, module
  reason, attempt counts and finalization status; the app path also binds its exact
  root/generation and current finalize attempt. Derive finite class/retry metadata
  solely from the stored policy; finalizer refusal/exhaustion preserves those facts
  and honestly reports incomplete business finalization. Substituted/draft-only
  facts refuse state and audit. Audit failure rolls back business and terminal
  writes; uncertain COMMIT reconciles the actual row without duplicate audit.
- **Type:** writer/minting census and real PostgreSQL terminal-atomicity controls.
  **Kind:** structural + behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Architecture`,
  `OutboxDispatchConventionTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Architecture/BackgroundJobTerminalBoundaryTests.cs)).

### `BackgroundJob_Retries_And_Terminal_Faults_Preserve_Progress`

- **Asserts:** six committed normal admissions and fixed backoffs, parity for
  Result/exception refusals and fair control quanta prevent a seventh start or
  starvation of healthy jobs during a terminal-audit backlog longer than a lease.
  Completed external failure uses ADR-0061's deterministic priority: Permanent,
  then TransientNotRepeatable, then repeatable-budget exhaustion, including on
  admission six. Only eligible TransientRepeatable outcomes permit normal retry;
  both retry gates refuse success, selected finalization, forbidden/incomplete
  policy or unresolved prior effects. JobId, immutable plan and OperationId remain
  stable while the next counted attempt receives a fresh AttemptId. Control
  progress never bypasses quiescence or prepared domain finalization.
- **Type:** actual worker/PostgreSQL retry, crash and backlog controls.
  **Kind:** behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Integration`,
  `BackgroundJobControlTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Integration/Database/BackgroundJobControlTests.cs)).

### `BackgroundJob_Storage_And_Credentials_Are_Confined`

- **Asserts:** exact table classes, canonical RLS, column grants, immutable facts
  and named guarded credential consumers hold; app/vendor/control denials and
  unclassified tables fail loudly. No arbitrary business bypass or runtime DDL.
  One nullable normal-attempt policy commits write-once with exact outcome kind
  and positive quiescence: absent before outcome/on success/finalize/no-start-only
  records, required on Failed/Cancelled, Permanent only on Failed. SQL NULL and
  partial-fact controls reject invalid shapes, admission seeding, foreign/stale
  facts and filling/clearing/overwrite. Database gates enforce eligible return to
  Pending and next admission, current-normal-outcome selection, exact module and
  terminal reasons and fresh database time after locking. App alone checkpoints
  outcome/release and initial FinalizePending selection. The policy extension adds
  only app checkpoint UPDATE and control status SELECT; no payload access or
  control attempt/reservation UPDATE is added.
  Locked fresh-statement probes retain uncertain receipts; typed outcome bytes
  require announced app reads. Forward migration refuses incomplete/incompatible
  retained history without guessing policy. Dirty predicate/grant controls prove
  each denial.
- **Type:** whole-schema/consumer census and real app/role/denied-column probes.
  **Kind:** structural + startup + behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Architecture`,
  `OutboxDispatchConventionTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Architecture/HangfireCredentialBoundaryTests.cs)).

### `BackgroundJob_Runtime_Has_No_Production_Definitions_Or_Dashboard`

- **Asserts:** production business and recurrence manifests are independently
  empty and no dashboard/recovery route exists; nonempty real runtime subjects
  and dirty controls prevent an absent registration or undiscovered module passing.
- **Type:** production composition/route/registry census with planted controls.
  **Kind:** structural + startup + behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Integration`,
  `BackgroundJobProductionManifestTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Integration/BackgroundJobProductionManifestTests.cs)).

### `Hangfire_Wakeups_Are_Recoverable_And_Generation_Fenced`

- **Asserts:** create/init/ACK loss and stale/early/duplicate callbacks preserve
  the durable obligation; same-generation repair never becomes the next attempt.
  Future-due scheduling, vendor loss and explicit retirement retain correct readiness.
- **Type:** real pinned client/worker and PostgreSQL fault/repair controls.
  **Kind:** behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Integration`,
  `BackgroundJobBridgeTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Integration/Database/BackgroundJobBridgeTests.cs)).

### `Hangfire_Invocations_And_Diagnostics_Are_Closed`

- **Asserts:** exact two wrapper signatures/arguments/queues are admitted before
  activation; below-filter storage guards and bounded logging contain private
  values even on stock filter-disabled fallback or direct malformed writes.
  Fatal diagnostics use a finite pool/stage set without exception objects; isolated
  child environment/resource/collector prerequisites, absent dump artifacts and
  finally/exit markers distinguish fatal from normal exit with a dirty gate control.
  ADR-0062 confines CI collector setup to the named exclusive hosted-VM owner,
  fixed literal write/readback and no restoration. Complete disjoint Docker
  partitions, owned process completion and fail-closed aggregate evidence retain
  all cases and the existing required check. Offline copied-owner controls verify
  the actual fixed seams without changing local host policy; hosted qualification
  remains a separate obligation in the owning CI record.
  Interrupted execution retains actual case IDs and durations without final TRX;
  contradictory skip/error/incomplete counters cannot be normalized into success.
- **Type:** invocation census, real worker fallback and stored/logged canary controls.
  **Kind:** structural + startup + behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Integration`,
  `HangfireStorageTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Integration/Database/HangfireStorageTests.cs)).
  The CI-only extension is owned by
  [`ci-native-provisioning.py`](../../scripts/ci-native-provisioning.py) and
  [`ci-native-tests.py`](../../scripts/ci-native-tests.py), with their matching
  [`provisioning`](../../scripts/test-ci-native-provisioning.py) and
  [`launcher`](../../scripts/test-ci-native-tests.py) subprocess controls in `meta`.

### `Hangfire_Bootstrap_Uses_Pinned_Schema_And_Confined_Role`

- **Asserts:** schema 23, exact stock objects/ACLs, guarded sync/async connections,
  disabled unguarded reconnect/schema preparation/degraded startup, bounded native
  SQL and the qualified dependency/licence graph are required before workers start.
  Exact function volatility is verified with body/language/search-path/ACL facts;
  a stale snapshot category cannot silently weaken the live recurrence-write guard.
  Prevalidation and conditional-join premises precede activation. A nonempty exact
  termination-capability census confines the sole FailFast(string) sink and callers;
  exception overloads, alternate Exit/Kill and forwarding/delegate/async escapes fail.
  Structural callers do not substitute for behavioral state-dominance proof.
  Exact pinned component/filter inventories selectively suppress aggregation/history
  while preserving expiration/heartbeat and required admission/retry filters, with
  a positive one-millisecond internal expiration interval and unchanged assemblies.
  Enabled commands retain finite read/write I/O; startup/idle deadlines survive warm
  reset. Actual stock cleanup under the granted role preserves required state/queue
  behavior; dirty inventory/filter/deadline controls refuse before workers start.
- **Type:** composition/source census and real boot/role/blocked-SQL controls.
  **Kind:** structural + startup + behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Architecture`,
  `OutboxDispatchConventionTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Architecture/HangfireRuntimeBoundaryTests.cs)).

### `BackgroundJob_Capacity_Is_Tenant_Wide_And_Quiescence_Bound`

- **Asserts:** tenant/group mutex and reservations enforce the effective operational/
  Hard LimitKey cap across orgs/instances. Refusal spends no attempt; downgrade,
  unknown effects, lease expiry and lookup faults never silently free occupancy.
  The app-owned outcome/quiescence checkpoint releases the exact old reservation
  atomically for retry, success finalization and failure finalization. Nonrepeatable
  policy or later finalizer fault cannot retain occupancy whose release committed.
  Uncertain COMMIT remains unresolved until a locked actual-row receipt decides it;
  actual task/callback cleanup stays owned separately. Any later normal attempt
  reacquires capacity through ordinary admission; no TTL supplies release proof.
- **Type:** actual two-instance/org app-role PostgreSQL admission controls.
  **Kind:** behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Architecture`,
  `OutboxDispatchConventionTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Architecture/BackgroundJobCapacityBoundaryTests.cs)).

### `BackgroundJob_External_Work_Has_No_Business_Transaction`

- **Asserts:** typed adapters expose exactly Execute/Reconcile and receive no UoW,
  DbContext, sender, locator or callback; prepare/arm/outcome/finalize checkpoints
  use separate short owners and external work follows confirmed ExecuteIntent.
  Both methods use distinct Succeeded, Failed and Cancelled factories: completed
  reports require typed outcome and positive quiescence; success has no policy,
  Failed explicitly supplies one of three nonzero values, and Cancelled permits
  only TransientRepeatable/TransientNotRepeatable. Running/Unknown/NeverStarted
  carry none of those completed facts. Default/undefined/missing policy and
  Cancelled/Permanent reject unchanged. Distinct factories replace generic
  Completed; no failure policy defaults. Adapter translation grants repeatability
  only for another fenced attempt of the same OperationId/plan. Direct completed
  reports through Execute
  and Reconcile require no prior exception to select the correct policy.
- **Type:** adapter dependency/capability census and real stage/transaction controls.
  **Kind:** structural + behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Architecture`,
  `OutboxDispatchConventionTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Architecture/BackgroundJobExternalActivationBoundaryTests.cs)).

### `BackgroundJob_External_Recovery_Never_Assumes_Quiescence`

- **Asserts:** paused old starts, NotFound after intent, lost acceptance ACK and
  exhausted probes preserve unknown effect/quarantine. Only positive fenced
  provider/supervisor or actual task/process-tree proof can release reservations.
  Invalid reports grant no release/retry/finalization. Acknowledged or locked-
  confirmed outcome/policy wins over later conflicting reports; the selected normal
  outcome pointer remains authority while the current finalize attempt supplies
  only its fresh fence. No late report causes overwrite, conflict quarantine, new
  Execute or provider validation of committed facts. Uncertain checkpoint COMMIT
  requires locking the actual row and reading its receipt in a fresh statement;
  unlocked NULL/probe timeout supplies no rollback proof. Winning lease-loss,
  deadline or shutdown stop forbids new outcome-checkpoint/COMMIT admission even
  with a live lease; already admitted COMMIT retains its actual acknowledged or
  locked-reconciled result. Preserve late completed facts for fresh eligible
  recovery without reopening stopped gates. Honored cancellation remains Cancelled
  with truthful repeatability across restart; token requests/exceptions prove no
  completion or quiescence. An independent Failed/Permanent racing stop stays
  representable, while a planted adapter causal misreport remains a protocol fault,
  not runtime-certified truth. Stop/checkpoint/lost-ACK/restart controls discriminate
  durable authority from ephemeral stop flags and uncommitted observations.
- **Type:** real engine with explicit fixture provider/process/fault controls.
  **Kind:** behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Integration`,
  `BackgroundJobPositiveSdkTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Integration/Database/BackgroundJobPositiveSdkTests.NativeUnresolved.cs)).

### `BackgroundJob_Finalization_Retries_Are_Bounded`

- **Asserts:** separate six committed finalize admissions never rerun Execute.
  ADR-0061's selected status/module/terminal reason mapping is exact: success
  selects Succeeded with no failure reason; every selected completed-failure row
  selects Failed.
  Failed/Permanent uses ExternalFailed/permanent_failure; nonrepeatable Failed or
  Cancelled uses matching ExternalFailed/ExternalCancelled and
  external_retry_forbidden; exhausted TransientRepeatable uses
  NormalAttemptsExhausted/retry_exhausted. Early repeatable failure selects no final
  status and retries only when eligible. Early finalization and false exhaustion
  refuse; stored outcome kind preserves Failed versus Cancelled.
  Domain failure finalization returns Result.Ok and commits domain state plus
  business/terminal MUST audits and terminal state on the app owner. Nonrepeatable
  honored cancellation may finally fail that same job during deploy through this
  audited finalizer. FinalizePending stays eligible; permanent refusal or transient
  six-finalize exhaustion selects finalize_refused/finalize_exhausted, preserves
  the exact outcome/policy and reports unapplied business finalization. Audit
  rollback, lost COMMIT ACK and conflicting reports never repeat a committed
  finalizer or invent business Failed state.
- **Type:** real app-role stage/uncertain-COMMIT/audit-rollback controls.
  **Kind:** behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Integration`,
  `BackgroundJobExternalCheckpointStoreTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Integration/Database/BackgroundJobExternalCheckpointStoreTests.FinalizerControl.cs)).

### `BackgroundJob_Recurrence_Is_Durable_And_Deduplicated`

- **Asserts:** monotonic manifests, revision-independent logical-slot uniqueness,
  CoalesceLatest, frozen target/policy and persisted jitter converge across mixed
  versions and duplicate ticks; page checkpoint and recipient INSERT are atomic.
- **Type:** real control-store/PostgreSQL revision, misfire and crash controls.
  **Kind:** behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Integration`,
  `BackgroundJobRecurrenceStoreTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Integration/Database/BackgroundJobRecurrenceStoreTests.cs)).

### `BackgroundJob_Fanout_Uses_Recipient_Bound_Tenant_Admission`

- **Asserts:** minimal named lifecycle reads and finite policies feed bounded
  seek pages; only an actual recipient witness can announce exact tenant/org and
  atomically submit its child/receipt. Forged routes and lost ACK cannot duplicate it.
- **Type:** grant/caller census and real cross-scope recipient/child fault controls.
  **Kind:** structural + behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Architecture`,
  `OutboxDispatchConventionTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Architecture/BackgroundJobRecipientBoundaryTests.cs)).

### `BackgroundJob_Ordinary_Execution_Has_No_Platform_Authority`

- **Asserts:** immutable sticky admission positively enumerates ordinary/framework
  requests and refuses Platform/provisioning/direct privileged/admin/bypass paths
  before work, independently of accessor mutation, new DI scopes or permissive gates;
  detached production method targets cannot hide caller authority.
- **Type:** request/capability census and actual sender/direct-entry/warm-source
  controls.
  **Kind:** structural + behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Architecture`,
  `OutboxDispatchConventionTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Architecture/BackgroundJobAuthorityBoundaryTests.cs)).

### `BackgroundJob_Worker_Pools_Reserve_Independent_Capacity`

- **Asserts:** finite disjoint queues and bounded server slots preserve other pools
  under long work, blocked bridge calls and simultaneous overruns, subject to the
  explicit ADR-0059 drain and ADR-0060 whole-process availability exceptions.
  Constructor/before-after-capture unwind, skipped removal, second announcement,
  outer completion, retained startup stalls and global gate/stop/final-exit races
  have decisive fatal and nonfatal controls; no missing join authorizes replacement.
  Atomic stop-start
  thresholds retain all admitted work within workers-plus-four ownership bounds.
  One actual shared expiration invocation across actual pools uses an owned
  background thread, completion-based cadence and cancelable pacing without duplicate
  starts or spin. Bounded wait/cleanup and the shared shutdown join retain resources
  through prolonged lock contention, disposal and late completion, with truthful
  unhealthy retained-maintenance state. The final simultaneous physical-acquisition
  census enforces a qualified supported pool configuration/floor and proves other
  pools progress at that boundary, including nested and retained acquisitions.
- **Type:** real pinned workers and production-engine saturation/shutdown controls.
  **Kind:** structural + startup + behavioural.
- **Status:** **Implemented** — P02b-4 Step 9; `LearnStack.Tests.Architecture`,
  `OutboxDispatchConventionTests` ([canonical owner](../../backend/tests/LearnStack.Tests.Architecture/BackgroundJobRuntimeCompositionBoundaryTests.cs)).

### `CI_Test_Shards_Are_Complete_And_Bounded`

- **Asserts:** complete actual Release populations have one selector-group owner;
  versioned finite plans, exact selected-filter discovery and executed case-ID
  unions agree. Current-attempt producer/API/artifact evidence, unique canonical
  required names, mandatory non-test gates and bounded aggregates refuse missing,
  stale, skipped, failed or incomplete work. Distinct complete A/B assignments
  qualify final-source independence and finite execution/meta/aggregate budgets;
  advisory duration estimates never establish membership or fit. Native and
  non-Docker authorities remain separated.
  Pending-only API acquisition validates each fresh complete snapshot under
  one finite deadline; it never combines responses or retries terminal,
  malformed, missing, duplicate or identity-invalid evidence.
  The ADR-0075 extension binds the ordinary-only twenty-five-minute whole-job
  and enclosing observation bounds to verified expected producer identity;
  other producers retain twenty minutes, operation caps and the 203-second
  reserve stay fixed. Forged modes/partitions cannot select a longer window.
- **Source:** [ADR-0063](../decisions/0063-complete-bounded-ci-test-shards.md),
  [ADR-0075](../decisions/0075-ordinary-ci-job-window.md).
- **Type:** actual Python plan/owner subprocess controls and workflow/API/hosted
  result proofs. **Kind:** structural + behavioural.
- **Status:** **Implemented** — `scripts/ci-test-shards.py`,
  `scripts/ci-test-shard-results.py`, `scripts/ci-native-tests.py` and the two
  workflow aggregates; actual-owner planted controls in
  `scripts/test-ci-test-shards.py` and `scripts/test-ci-test-shard-results.py`
  run together in meta's bounded allocation. They cover selector/plan closure,
  authority separation, startup/retained ownership, both case unions,
  current-attempt API jobs, immutable artifact IDs/digests, safe ZIP extraction,
  gates/toolchain/resource fields, partial-rerun refusals and canonical-name
  collisions. The phase's Step 3 record separately owns actual final-source
  two-assignment hosted qualification; source enforcement is not a fit claim.
  **ADR-0075 extension implemented** by the same owners and offline suites:
  exact mode/job/index policy, ordinary-only workflow/API/receipt/measurement
  windows, fixture-preparation identity, unchanged operation caps and forged
  identity/nonfinite/threshold counterexamples. The
  [Phase 03 closeout](../roadmap/phase-03-delivery-log.md#adr-0075-three-step-closeout-2026-10-27)
  owns implementation reviews and observed complete hosted A/B; local controls
  alone do not establish fit and later documentation identities remain distinct.
- **Phase:** 02b (P02b-4 CI follow-up).

## References

- [ADR-0003 Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md) (Amendment 3)
- [ADR-0018 Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md)
- [ADR-0032 Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md)
- [ADR-0033 Audit Durability Model](../decisions/0033-audit-durability-model.md)
- [ADR-0024 API Versioning Policy](../decisions/0024-api-versioning-policy.md)
- [ADR-0027 Frontend UI Localization](../decisions/0027-frontend-i18n.md)
- [ADR-0034 Hub Contract Surface Invariant](../decisions/0034-hub-contract-surface-invariant.md)
- [ADR-0035 Demand-Gated Infrastructure](../decisions/0035-demand-gated-infrastructure.md)
- [ADR-0036 Trusted Inputs for Tenant and Organization Resolution](../decisions/0036-tenant-resolution-trusted-inputs.md)
- [ADR-0047 Transactional Background Job Runtime](../decisions/0047-transactional-background-job-runtime.md)
- [ADR-0058 Bounded Stock Hangfire Maintenance](../decisions/0058-bounded-stock-hangfire-maintenance.md)
- [02-backend-coding.md § Pipeline Behaviors](02-backend-coding.md)
- [05-database.md § Tenant-Owned and Organization-Scoped Tables](05-database.md)
- [09-error-handling.md](09-error-handling.md)
- [10-observability.md](10-observability.md)
- [11-security.md § Tenant Context](11-security.md)
- [20-infrastructure-stack.md](20-infrastructure-stack.md)
- [Phase 02a Roadmap § Architecture Tests](../roadmap/phase-02a-kernel-tenancy.md)
- [Phase 02b Roadmap § Architecture Tests](../roadmap/phase-02b-events-auth.md)
- [add-architecture-test skill](../../.claude/skills/add-architecture-test/SKILL.md)
