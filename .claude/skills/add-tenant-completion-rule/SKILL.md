---
name: add-tenant-completion-rule
description: >
  Author a `TenantCompletionRule` boolean DSL expression — "when is a lesson /
  module / course complete?" — for a specific tenant. Data, not code. USE FOR: a
  tenant whose completion semantics differ from the built-in default ("all
  required lessons complete"). Examples: English tenant requires speaking session
  attended + vocab drill ≥ 70%; yoga tenant requires session attendance + breath
  exercise streak. DO NOT USE FOR: built-in primitive completion checks (those
  ship with LearnStack), domain-specific completion logic in code (forbidden by
  ADR-0018), or referencing the DSL engine before ADR-0025 is Accepted.
---

# Adding a `TenantCompletionRule`

## Purpose

`TenantCompletionRule` is the per-tenant override of "is this lesson / module /
course complete?" semantics. The Progress module evaluates the rule against the
learner's lesson + attempt + session state via the sandboxed DSL engine (ADR-0025
reserved). Lives in `tenant_completion_rules` per
[ADR-0018](../../../docs/decisions/0018-tenant-driven-customization-model.md).

## When to use

- A tenant needs completion that's more than "all required lessons completed".
- Different course types within one tenant need different completion semantics
  (one rule per `CourseVersion` or per `LessonPackage`).
- Tightening / loosening completion for a regulated tenant.

## When not to use

- Built-in completion primitives (`AllRequiredLessons`, `VideoWatched`,
  `QuizPassed`). Those ship with LearnStack and serve the default rule.
- Cross-tenant generic completion logic. That's a core primitive, not a tenant
  rule.
- Stateful completion that needs to look across multiple courses or enrollments.
  The DSL is pure-local; if you need cross-course logic, you need a `LearningPath`
  aggregate concept.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Tenant id | Yes | Owner of the rule. |
| Key | Yes | PascalCase: `EnglishLessonPackageCompletion`. |
| Schema version | Yes | Starts at 1. |
| Target scope | Yes | `Lesson` / `Module` / `Course` / `LessonPackage`. |
| DSL expression | Yes | Boolean expression returning `true` / `false`. |
| Input contract | Yes | What signals the rule may read (see below). |

## Workflow

### Step 1: Confirm ADR-0025 status

Same gate as
[add-tenant-scoring-rule](../add-tenant-scoring-rule/SKILL.md). Both DSLs share
the engine; don't ship production rules until ADR-0025 is Accepted.

### Step 2: Understand the input contract

The Progress module exposes a fixed input shape to the DSL. The rule sees:

```jsonc
{
  "enrollment": {
    "id": "<uuid>",
    "learnerId": "<uuid>",
    "courseVersionId": "<uuid>",
    "status": "active",
    "startedAt": "<iso>",
    "completedAt": null
  },
  "progress": {
    "lessons": {
      "<lesson-id>": {
        "completed": true,
        "completedAt": "<iso>",
        "viewedAt": "<iso>"
      }
    },
    "moduleProgress": [
      { "moduleId": "<uuid>", "complete": false, "percent": 75 }
    ]
  },
  "attempts": [
    {
      "assessmentKey": "vocab_drill_b2",
      "score": 82,
      "passed": true,
      "completedAt": "<iso>"
    }
  ],
  "liveSessions": [
    {
      "sessionId": "<uuid>",
      "attended": true,
      "minutesPresent": 38
    }
  ],
  "customFields": {
    "speakingPracticeStreak": 5
  }
}
```

You cannot read outside this snapshot. If you need an additional signal, extend
the input contract on the Progress module side (a core change, not a per-tenant
rule).

### Step 3: Author the rule

```text
# rule key: EnglishLessonPackageCompletion v1

# All required lessons in the package are complete.
let allRequiredLessons = progress.lessons.values().all(l => l.completed);

# A speaking session attended in the last 14 days.
let recentSpeakingSession = liveSessions.any(s =>
  s.attended && s.minutesPresent >= 30 && s.completedAt > now() - days(14));

# Vocabulary drill score ≥ 70%.
let vocabPass = attempts.any(a =>
  a.assessmentKey.startsWith("vocab_drill_") && a.score >= 70);

return allRequiredLessons && recentSpeakingSession && vocabPass;
```

Notes:

- Boolean is the only valid return type.
- The DSL allows pure helpers (`startsWith`, `all`, `any`, `now()`, `days(n)`)
  decided by ADR-0025.
- `now()` is the sandbox's deterministic clock; in tests it's frozen.

### Step 4: Seed the rule

```csharp
await mediator.Send(new RegisterTenantCompletionRuleCommand(
    TenantId: tenantId,
    Key: "EnglishLessonPackageCompletion",
    SchemaVersion: 1,
    TargetScope: CompletionScope.LessonPackage,
    Expression: File.ReadAllText("rule-v1.dsl"),
    Description: "English lesson-package: lessons + speaking + vocab pass."));
```

Attach the rule to a specific `CourseVersion` / `LessonPackage` via that
aggregate's `CompletionRuleKey` reference. Multiple courses can share the same
rule by referencing the same key.

### Step 5: Built-in default rule

Every `CourseVersion` falls back to the built-in default if no rule is attached:

```text
return progress.lessons.values()
  .filter(l => l.required).all(l => l.completed);
```

The default is sufficient for tenants with no special semantics. **Add a rule only
when the default is wrong**, not as boilerplate.

### Step 6: Progress integration

The Progress module's projection job evaluates the rule after each progress
state change:

```csharp
var inputs = await progress.GetCompletionInputsAsync(enrollmentId, ct);
var rule = await completionRules.GetAsync(tenantId, courseVersion.CompletionRuleKey, ct);
var isComplete = await dslEngine.EvaluateBoolAsync(rule, inputs, ct);

if (isComplete && !enrollment.IsComplete)
{
    enrollment.MarkComplete();
    await outbox.EnqueueAsync(new EnrollmentCompletedIntegrationEvent { ... });
}
```

The job runs:

- On every `LessonCompleted` integration event consumer.
- On every `AttemptScored` integration event consumer.
- On every `LiveSessionAttended` integration event consumer.
- (Periodically, hourly, as a safety net for missed events.)

### Step 7: Tests

```csharp
[Fact]
public async Task English_rule_completes_when_all_conditions_met()
{
    var inputs = MakeInputs(
        allLessonsComplete: true,
        recentSpeakingSession: TimeSpan.FromDays(3),
        vocabPassScore: 82);
    var result = await engine.EvaluateBoolAsync(rule, inputs, ct);
    Assert.True(result);
}

[Fact]
public async Task English_rule_blocks_when_vocab_below_threshold()
{
    var inputs = MakeInputs(vocabPassScore: 68);
    Assert.False(await engine.EvaluateBoolAsync(rule, inputs, ct));
}

[Fact]
public async Task English_rule_blocks_when_speaking_session_too_old()
{
    var inputs = MakeInputs(recentSpeakingSession: TimeSpan.FromDays(30));
    Assert.False(await engine.EvaluateBoolAsync(rule, inputs, ct));
}
```

Test every short-circuit condition independently; the AND-chain hides regressions
otherwise.

## Validation

- The expression compiles in the sandbox.
- The rule returns boolean (engine rejects otherwise).
- Each conjunct of the boolean expression has its own passing/failing test.
- `now()` is testable (the engine accepts an injected clock for tests).
- The course / lesson-package referencing the rule has `CompletionRuleKey` set.
- Architecture test `Completion_Rules_Are_Boolean_Pure` is green.

## Common pitfalls

- **Returning a non-boolean.** The engine rejects; the rule is invalid.
- **Reading outside the input contract.** Forbidden. Extend the input contract
  centrally if you need a new signal.
- **`now()` non-determinism in tests.** The sandbox accepts an injected clock;
  use it.
- **Forgetting `MarkComplete()` idempotency.** Set guards on the Progress side so
  re-evaluating a completed enrollment doesn't re-publish
  `EnrollmentCompletedIntegrationEvent`.
- **Versioning skipped.** Semantic changes are a `v+1` rule; existing enrollments
  finish under `v1` until migrated.
- **Mixing scoring + completion logic.** A scoring rule returns a structured
  result; a completion rule returns a boolean. Don't conflate.
- **One rule per course.** A rule can be shared across many `CourseVersion`s by
  referencing the same key. Don't duplicate unless the semantics genuinely
  differ.
