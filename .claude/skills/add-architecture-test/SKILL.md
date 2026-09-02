---
name: add-architecture-test
description: >
  Encode a structural rule as a non-skippable test in
  `backend/tests/LearnStack.Tests.Architecture`. USE FOR: turning a written rule
  (cross-module ban, naming, marker presence, dependency direction) into a
  mechanical check; backing a new ADR with the rule it implies. DO NOT USE FOR:
  business-logic tests (those go in Unit / Integration), runtime behavior
  checks, or "this would be nice to enforce" rules without an ADR — architecture
  tests carry weight; only land them after the rule is in the corpus.
---

# Adding an architecture test

## Purpose

Move a rule from documentation prose into a CI-enforced check using NetArchTest /
ArchUnitNET / Roslyn analyzers / migration scans
([01-architecture-standards.md § Architecture Tests](../../../docs/standards/01-architecture-standards.md)).

## When to use

- A standard or ADR introduces a rule that is mechanically checkable.
- A pull-request review keeps catching the same drift, suggesting the rule should
  be enforced.
- An ADR explicitly lists an architecture test under its **Architecture tests**
  section.

## When not to use

- Business logic that varies by context — tests it as a unit or integration test
  instead.
- A rule without a written source. Write the standard or ADR first; the test
  enforces the corpus, not the other way around.
- Performance / observability checks — those live in their own pipelines.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Rule | Yes | Plain-English statement of the invariant. |
| Source | Yes | ADR / standard that defines the rule. |
| Detection mechanism | Yes | NetArchTest / ArchUnitNET (project graph) / Roslyn (syntactic) / migration scan (SQL pattern). |
| Failure message | Yes | Actionable: tell the author what to do, not just what is wrong. |

## Workflow

### Step 1: Confirm the source

The rule must be written down somewhere in `docs/standards/` or `docs/decisions/`.
If it isn't, **stop and write that first** (use [write-adr](../write-adr/SKILL.md)
for non-trivial new rules). Architecture tests don't invent rules; they enforce
them.

### Step 2: Pick the mechanism

| Mechanism | Use when |
|-----------|----------|
| **NetArchTest** | Project-graph reference bans, naming conventions on types / namespaces. |
| **ArchUnitNET** | Slightly richer rules (constraints across assemblies). Pick when NetArchTest can't express it. |
| **Roslyn analyzer** | Syntactic patterns inside code (`IgnoreQueryFilters` call, raw `Guid` in command, …). |
| **Migration scan** | SQL patterns in `.cs` migration files (RLS-enable, policy presence, partition declaration). |
| **Reflection over conventions** | Marker-attribute presence + matching property / migration shape. |
| **String / regex scan** | When all else fails — explicit "no file under `Verticals/`", "no `english.*` permission key". |

### Step 3: Author the test

In `backend/tests/LearnStack.Tests.Architecture/`:

```csharp
public sealed class ModuleDependencyTests
{
    [Fact]
    public void Modules_DoNot_Reference_OtherModule_Domain()
    {
        // pseudo-code; replace `GetOwningModuleNamespace(...)` with the real
        // owning-namespace resolver helper that lives next to the test. The
        // helper isn't a corpus contract — pick a shape that fits the test
        // harness (e.g. parse the consumer type's namespace and extract the
        // `LearnStack.Modules.<Name>` prefix).
        var rule = Types.InAssemblies(ModuleAssemblies)
            .That()
            .ResideInNamespaceMatching(@"LearnStack\.Modules\.\w+\.")
            .Should()
            .NotHaveDependencyOnAny(
                Types.That().ResideInNamespaceMatching(
                    @"LearnStack\.Modules\.\w+\.Domain")
                    .GetTypes()
                    .Where(t => !t.FullName!.Contains(GetOwningModuleNamespace(...))) // pseudo
                    .Select(t => t.FullName)
                    .ToArray());

        var result = rule.GetResult();

        Assert.True(result.IsSuccessful,
            $"Modules must not reference another module's Domain namespace. " +
            $"Offending types: {string.Join(", ", result.FailingTypeNames ?? [])}. " +
            $"Fix: depend on the target module's Application.Contracts instead.");
    }
}
```

Patterns to follow:

- **One assertion per test.** Easier to triage when CI fails.
- **Descriptive failure message.** Include "Fix:" guidance so the next author
  doesn't have to read the rule's source to act.
- **Reference the source.** Link the standard / ADR in an XML doc comment on the
  test method.

### Step 4: Common architecture-test families

**The shipped set is eight files, not a family per topic.** Add yours to the one whose
subject it shares:

| File | What it covers |
|------|----------------|
| `ModuleDependencyTests.cs` | Dependency direction between module packages, plus a planted-violation meta test that proves the scanner still detects one. |
| `PersistenceConventionTests.cs` | `row_version` mapping, ambient-unit-of-work enlistment, the Docker trait, and the `migrate` recipe's chain coverage and credential redaction. |
| `TenancyConventionTests.cs` | The ADR-0036 **request-edge** rules — what may read a host, where the effective host and `app.resolving_host` are computed, and the assertion budget's independence from `ICacheService`. Source scans, because three of the four banned inputs appear only as string literals. |
| `TenantScopingTests.cs` | The correspondence between the `[TenantOwned]` / `[OrganizationScoped]` markers, the EF global query filters, and the Row Level Security policies. |
| `TenantContextConstructionTests.cs` | How a tenant context comes into existence and who may write it: the factory's single entry point, the constructor's one call site, the enumerated accessor writers, and the composite-key organization read. |
| `ApiConventionTests.cs` | Live majors, forwarded headers, required `Deployment:Mode`, unversioned route prefixes. |
| `CrossCuttingFoundationTests.cs` | Pipeline order, `Result<T>` returns, topic naming, and the direct-reference bans (Sentry, `DeploymentMode`, `IEventBus`, provider SDK exceptions). |
| `RepositoryLayoutTests.cs` | `No_Source_Folder_Named_Verticals` and the single-frontend-app rule. |

Rules for surfaces no file covers yet — audit, permissions, entitlement, event bus,
Hub contract — are **Registered** in
[the catalogue](../../../docs/standards/21-architecture-tests-catalogue.md) against
the phase that ships the code they inspect. Check its Status line before assuming a
net is under you, and create a new file only when your rule's subject is not one of
the eight above.

> **The tenancy rules live in three files, and the split is by subject, not by ADR.**
> All three cite ADR-0036, so "put it with the other ADR-0036 rules" is not a usable
> instruction. Ask what the rule is *about*: the request edge and what may be read from
> it (`TenancyConventionTests`), the marker-to-filter-to-policy correspondence
> (`TenantScopingTests`), or the construction and writing of the context itself
> (`TenantContextConstructionTests`).

### Step 5: Stability of the test

Architecture tests are **non-skippable**. That means:

- They cannot be marked `[Skip]` or `[Fact(Skip = ...)]`.
- A flaky architecture test is a contradiction — fix the rule or the
  implementation, not the test.
- Failure messages should be **deterministic**: same offending types on every run.

### Step 6: Migration-scan tests

When the rule is about migration content (RLS, partition):

```csharp
/// <summary>The migration-scan arm of the rule; see ADR-0003 Amendment 3.</summary>
[Fact]
public void Every_TenantOwned_Entity_HasFilterAndRlsPolicy()
{
    // RepositoryPaths.BackendSrc() — the shipped helper. A relative "backend/src"
    // is resolved against the TEST HOST's working directory (bin/Debug/net10.0),
    // where it does not exist, so the query silently yields nothing and the
    // foreach below asserts over an empty set: a green test that checks nothing.
    var migrationFiles = Directory
        .GetFiles(RepositoryPaths.BackendSrc(), "*.cs", SearchOption.AllDirectories)
        .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
        .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
        .ToList();

    // Guard one: the path resolved and found files. Necessary, and on its own
    // not sufficient — see guard two.
    Assert.NotEmpty(migrationFiles);

    var tenantOwned = migrationFiles
        .Select(f => (File: f, Content: File.ReadAllText(f)))
        // Both tokens, because the two shipped chains use one each. EF writes the
        // tenancy chain through migrationBuilder.CreateTable(name: "…"): measured,
        // "CREATE TABLE" occurs ZERO times in 20260828092437_create_tenancy_schema.cs,
        // which creates eight tables. The platform chain writes outbox_messages and
        // idempotency_keys through migrationBuilder.Sql("CREATE TABLE …") because
        // neither is an EF entity: "CreateTable(" occurs ZERO times in
        // 20260828085701_create_platform_infrastructure_tables.cs. Either token alone
        // classifies exactly one of the two chains, so the predicate is their union.
        .Where(x => (x.Content.Contains("CreateTable(") || x.Content.Contains("CREATE TABLE"))
                    && x.Content.Contains("tenant_id"))
        .ToList();

    // Guard two, and the reason this test is worth landing: it asserts on what the
    // scan CLASSIFIED, not on what it read. A detection predicate that matches
    // nothing runs the loop zero times and reports green over the exact migrations
    // the rule exists to cover, past a NotEmpty guard on the file list. It catches
    // an EMPTY classification, not a PARTIAL one: either token on its own leaves
    // this assertion green while a whole shipped chain goes unscanned, which is
    // why the predicate above is a union.
    Assert.NotEmpty(tenantOwned);

    foreach (var (file, content) in tenantOwned)
    {
        Assert.True(
            content.Contains("ENABLE ROW LEVEL SECURITY")
            // FORCE is the half that matters: without it the table owner bypasses
            // its own policy and the whole layer is inert while ENABLE stays green.
            // Matched as a regex because the canonical template writes two spaces.
            && Regex.IsMatch(content, @"FORCE\s+ROW LEVEL SECURITY")
            // Must match the canonical template's exact shape. A bare
            // current_setting('app.tenant_id') assertion FAILS against every
            // correct migration and PASSES against the superseded one-argument
            // form — see ADR-0003 Amendment 3 and 05-database.md.
            && content.Contains("NULLIF(current_setting('app.tenant_id', true), '')"),
            $"{Path.GetFileName(file)} creates a tenant-owned table without the "
            + "canonical policy block. Fix: copy it from docs/standards/05-database.md "
            + "§ Tenant-Owned and Organization-Scoped Tables — that file is the only "
            + "place the template exists.");
    }
}
```

File granularity is what makes the predicate above safe: the two **table classes**
that key their policy on something else — `tenants` on `id`, `platform_host_to_tenant`
on `app.resolving_host` — ship in a file that also creates ordinary tenant-owned
tables, so the file-level `tenant_id` assertion holds. A per-table version of this
scan needs the table classes from
[Database Standards § Table classes](../../../docs/standards/05-database.md) before
it is correct.

### Step 7: Test the test

Before merging:

1. Run the test against the current codebase — it should pass.
2. **Introduce a deliberate violation** in a scratch branch — the test should fail
   with the right message.
3. Revert the deliberate violation; confirm green.

If step 2 doesn't fail, the test is silently passing and provides no value.

## Validation

- `dotnet test backend/tests/LearnStack.Tests.Architecture` passes.
- Deliberately introducing a violation produces a failing test with the expected
  message.
- The CI workflow runs the architecture test project on every PR (it's in
  `dotnet test` matrix; nothing to wire).
- The rule's source ADR / standard cites the test by name under its
  **Architecture tests** section.

## Common pitfalls

- **Test that doesn't actually fail on violation.** Always run step 7 to confirm.
- **Failure message without "Fix:" guidance.** A red CI without a clear next step
  burns time.
- **Catching too much.** A rule that fails on legitimate cases produces
  resistance and gets skipped (which is forbidden) or weakened. Scope tightly.
- **Marking as `[Skip]`.** Forbidden. Architecture tests are the safety net for
  the standards corpus.
- **String matching that's too loose.** "Vertical" appears in "VerticalAlignment";
  match `LearnStack\.Verticals\.` not `Vertical`.
- **Rule without a written source.** Stop and write the rule down first.
