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

The current set lives across these files; add yours to the right one:

| File | What it covers |
|------|----------------|
| `ModuleDependencyTests.cs` | Cross-module reference bans, contracts-only access. |
| `TenantIsolationTests.cs` | `[TenantOwned]` + filter + RLS, `[OrganizationScoped]` + org RLS. |
| `EventBusTests.cs` | Topic naming, integration-event base, inbox guard usage. |
| `AuditTests.cs` | `AuditEntry` inheritance, no direct `audit_log` writes. |
| `EntitlementTests.cs` | Plan-projected vs tenant-flag separation, FeatureKey registry. |
| `PermissionTests.cs` | Closed action set, scope correctness, denied-test presence. |
| `HubContractTests.cs` | No direct Hub-URL references; Hub clients only inside the named adapters (ADR-0034). |
| `DomainGenericTests.cs` | `Core_Modules_HaveNo_DomainSpecific_Names`, no `Verticals/`. |
| `DaprDirectInjectionTests.cs` | No `IConnectionMultiplexer` / `KafkaProducer` / `VaultClient` in modules. |
| `ConventionTests.cs` | Strongly-typed ids in commands, validator pairing, etc. |

If your rule doesn't fit any file, create a new file with a focused name.

### Step 5: Stability of the test

Architecture tests are **non-skippable**. That means:

- They cannot be marked `[Skip]` or `[Fact(Skip = ...)]`.
- A flaky architecture test is a contradiction — fix the rule or the
  implementation, not the test.
- Failure messages should be **deterministic**: same offending types on every run.

### Step 6: Migration-scan tests

When the rule is about migration content (RLS, partition):

```csharp
[Fact]
public void Every_TenantOwned_Table_HasRls_With_AppTenantId()
{
    var migrationFiles = Directory
        .GetFiles("backend/src", "*.cs", SearchOption.AllDirectories)
        .Where(f => f.Contains("/Migrations/"));

    foreach (var file in migrationFiles)
    {
        var content = File.ReadAllText(file);
        if (content.Contains("CREATE TABLE") && content.Contains("tenant_id"))
        {
            Assert.Contains("ENABLE ROW LEVEL SECURITY", content);
            // FORCE is the half that matters: without it the table owner bypasses
            // its own policy and the whole layer is inert while ENABLE stays green.
            // Matched as a regex because the canonical template writes two spaces.
            Assert.Matches(@"FORCE\s+ROW LEVEL SECURITY", content);
            // Must match the canonical template's exact shape. A bare
            // current_setting('app.tenant_id') assertion FAILS against every
            // correct migration and PASSES against the superseded one-argument
            // form — see ADR-0003 Amendment 3 and 05-database.md.
            Assert.Contains("NULLIF(current_setting('app.tenant_id', true), '')", content);
        }
    }
}
```

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
