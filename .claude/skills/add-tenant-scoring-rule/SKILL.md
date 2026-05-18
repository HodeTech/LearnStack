---
name: add-tenant-scoring-rule
description: >
  Author a `TenantScoringRule` DSL expression for assessment scoring (placement
  test → CEFR level, code-challenge → track recommendation, …). Data, not code.
  USE FOR: a tenant-specific scoring formula, a new placement-test
  result-band-to-recommendation mapping, evolving a scoring rule (with versioning).
  DO NOT USE FOR: writing a scoring engine (LearnStack ships the engine), domain-
  specific scoring logic in any module (forbidden by ADR-0018), or referencing the
  scoring DSL before its engine ADR (ADR-pending; see decisions/README.md) is
  Accepted.
---

# Adding a `TenantScoringRule`

## Purpose

A `TenantScoringRule` is a sandboxed DSL expression that takes an attempt's answer
map and returns a structured result (typically a level recommendation or a
weighted score). It lives as a row in `tenant_scoring_rules` per
[ADR-0018](../../../docs/decisions/0018-tenant-driven-customization-model.md);
the **engine** that evaluates the DSL is decided by an ADR (reserved as
**ADR-0025** in [decisions/README.md § Open ADR Drafts](../../../docs/decisions/README.md))
and must be Accepted before this skill is used in production.

## When to use

- A placement test needs to produce a level recommendation against the tenant's
  `TenantLevelTaxonomy`.
- A self-assessment needs to map a score to a track / difficulty.
- A composite scoring formula needs section-weighting + threshold bands.

## When not to use

- Adding the **scoring engine itself**. That's a LearnStack core change gated by
  ADR-0025; not in scope here.
- Domain-specific scoring in code (`EnglishPlacementScorer`). Forbidden.
- Free-form code execution. The DSL is sandboxed; tenants don't bring code.
- Stateful scoring that depends on cross-attempt history. The DSL is pure:
  `f(answerMap, levelTaxonomy) → result`.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Tenant id | Yes | Owner of the rule. |
| Key | Yes | PascalCase, unique per tenant: `EnglishPlacementToCefr`. |
| Schema version | Yes | Starts at 1. |
| Input shape | Yes | JSON Schema for the answer map the rule expects. |
| Output shape | Yes | JSON Schema for the result (e.g. `{ levelKey: string, score: number }`). |
| DSL expression | Yes | The expression string (engine-specific syntax — Accepted ADR-0025 determines). |
| `TenantLevelTaxonomy` reference | If applicable | The taxonomy whose `key`s the rule will return. |

## Workflow

### Step 1: Confirm the engine ADR

Open [decisions/README.md § Open ADR Drafts](../../../docs/decisions/README.md).
**ADR-0025 — Scoring + completion DSL sandbox engine** must be Accepted before
authoring rules for production tenants. If it isn't, the rule can still be
sketched as design-time documentation but should not ship.

### Step 2: Sketch the input + output shapes

A scoring rule's input is the attempt's answer map. Define it as a JSON Schema:

```jsonc
{
  "title": "EnglishPlacementInput",
  "type": "object",
  "required": ["grammar", "vocabulary", "listening"],
  "properties": {
    "grammar": { "type": "number", "minimum": 0, "maximum": 100 },
    "vocabulary": { "type": "number", "minimum": 0, "maximum": 100 },
    "listening": { "type": "number", "minimum": 0, "maximum": 100 },
    "speaking": { "type": "number", "minimum": 0, "maximum": 100 }
  }
}
```

And the output:

```jsonc
{
  "title": "PlacementResult",
  "type": "object",
  "required": ["levelKey", "compositeScore"],
  "properties": {
    "levelKey": {
      "type": "string",
      "description": "Key from the tenant's TenantLevelTaxonomy (e.g. 'a1', 'b2')."
    },
    "compositeScore": { "type": "number", "minimum": 0, "maximum": 100 },
    "sectionBreakdown": {
      "type": "object",
      "additionalProperties": { "type": "number" }
    }
  }
}
```

### Step 3: Author the DSL expression

The exact syntax depends on ADR-0025's engine choice. Two illustrative shapes the
ADR is considering:

**Option A — restricted-CEL style (illustrative):**

```cel
let composite = (grammar * 0.3) + (vocabulary * 0.3) + (listening * 0.25) + (speaking * 0.15);

let levelKey =
  composite < 30 ? "a1" :
  composite < 50 ? "a2" :
  composite < 65 ? "b1" :
  composite < 80 ? "b2" :
  composite < 92 ? "c1" :
                   "c2";

{
  levelKey: levelKey,
  compositeScore: composite,
  sectionBreakdown: {
    grammar: grammar,
    vocabulary: vocabulary,
    listening: listening,
    speaking: speaking
  }
}
```

**Option B — restricted-Lua style (illustrative):**

```lua
local composite = (grammar * 0.3) + (vocabulary * 0.3) + (listening * 0.25) + (speaking * 0.15)
local levelKey =
  composite < 30 and "a1" or
  composite < 50 and "a2" or
  composite < 65 and "b1" or
  composite < 80 and "b2" or
  composite < 92 and "c1" or
                     "c2"
return { levelKey = levelKey, compositeScore = composite }
```

Until ADR-0025 lands, write the expression as design-time pseudocode and
document the intent — don't commit a production rule that depends on an unsettled
engine.

### Step 4: Sandbox guarantees the rule must respect

Regardless of engine choice, the sandbox enforces:

- **No I/O.** No file, network, time, env reads.
- **Deterministic.** Same inputs ⇒ same output.
- **Bounded time.** A rule that doesn't return within ~100 ms is killed.
- **Bounded memory.** Allocations capped.
- **Read-only access to tenant data.** The rule sees the answer map + the
  taxonomy items as data; it cannot mutate.

If your rule needs anything outside this contract, the DSL is the wrong tool —
the work belongs in a regular handler.

### Step 5: Seed the rule

```csharp
await mediator.Send(new RegisterTenantScoringRuleCommand(
    TenantId: tenantId,
    Key: "EnglishPlacementToCefr",
    SchemaVersion: 1,
    InputSchemaJson: File.ReadAllText("input-v1.json"),
    OutputSchemaJson: File.ReadAllText("output-v1.json"),
    Expression: File.ReadAllText("rule-v1.dsl"),
    LevelTaxonomyKey: "cefr",
    Description: "Map placement-test section scores to a CEFR level."));
```

The seed:

1. Compiles the expression against the sandboxed engine; rejects on parse error.
2. Validates that any `levelKey` literal in the expression exists in the named
   `TenantLevelTaxonomy`.
3. Inserts the row.

### Step 6: Wire to the attempt-grading pipeline

The Assessment module's grading handler reads the rule by `(tenantId, key)`:

```csharp
var rule = await scoringRules.GetAsync(tenantId, "EnglishPlacementToCefr", null /* latest */, ct);
var result = await dslEngine.EvaluateAsync(rule, answerMap, ct);
attempt.RecordResult(result);
```

No assessment-module code branches on the tenant. The grading pipeline is generic;
the per-tenant logic is the DSL row.

### Step 7: Tests

```csharp
[Fact]
public async Task EnglishPlacement_balanced_score_maps_to_b2()
{
    var input = new { grammar = 75, vocabulary = 80, listening = 70, speaking = 65 };
    var result = await engine.EvaluateAsync(rule, input, ct);
    Assert.Equal("b2", result["levelKey"]);
}

[Fact]
public async Task EnglishPlacement_floor_score_maps_to_a1()
{
    var input = new { grammar = 5, vocabulary = 5, listening = 5, speaking = 5 };
    var result = await engine.EvaluateAsync(rule, input, ct);
    Assert.Equal("a1", result["levelKey"]);
}

[Fact]
public async Task EnglishPlacement_ceiling_score_maps_to_c2() { ... }

[Fact]
public async Task EnglishPlacement_invalid_input_is_rejected()
{
    var input = new { grammar = -10 };   // out of range
    var result = await mediator.Send(new GradeAttemptCommand(...));
    Assert.False(result.IsSuccess);
}
```

Cover the band boundaries; the off-by-one in band thresholds is the most common
DSL bug.

## Validation

- The DSL expression parses (sandbox compile is part of the seed).
- The output's `levelKey` (when present) matches an item key in the referenced
  `TenantLevelTaxonomy`.
- Band-boundary tests cover transitions in both directions.
- A malformed input is rejected with the expected `LocalizedMessage`.
- Architecture test `Scoring_Rules_Compile_Against_Sandbox` is green.

## Common pitfalls

- **DSL with side effects.** Forbidden by sandbox; will fail at compile time. If
  you think you need a side effect, the DSL is the wrong tool.
- **Hardcoding level keys without checking the taxonomy.** A rule returning
  `"b2"` against a taxonomy that doesn't define `b2` will fail at evaluation.
- **Band boundary errors.** `< 30 → "a1"`, `< 50 → "a2"` excludes exactly 30
  from `a1` and includes it in `a2`. Decide explicitly and test the boundary.
- **Mutating the input.** The DSL operates on a snapshot; if your engine option
  allows mutation, sandbox should reject. Write rules as pure functions.
- **Long-running rules.** A DSL rule that's > 100 ms is a sign the rule belongs
  in a handler, not the sandbox.
- **Forgetting the version bump.** A change to scoring semantics is a `v+1`
  rule; existing attempts continue scoring against the older `v` until migrated.
- **Authoring before ADR-0025 lands.** Sketch the rule; don't ship to production
  until the engine ADR is Accepted.
