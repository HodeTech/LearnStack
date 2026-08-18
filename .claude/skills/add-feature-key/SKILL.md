---
name: add-feature-key
description: >
  Add a typed `FeatureKey` or `LimitKey` to the LearnStack registry and wire reads
  through `IFeatureFlags` / `useFeatureFlag` (FE). USE FOR: introducing a new
  plan-projected feature (`FeatureKeys.NewCapability`), a new numeric limit
  (`LimitKeys.MaxX`), a new killswitch (`KillswitchKeys.X`), or a tenant-flag-level
  rollout / opt-in. Includes the Hub plan-editor update and the entitlement
  invalidation event. DO NOT USE FOR: per-request toggling (forbidden), branching
  on unrelated flags inside one function (refactor instead), or domain-flavoured
  keys (forbidden).
---

# Adding a feature key / limit key

## Purpose

Extend the typed entitlement catalogue safely. Two registries
([21-feature-flags.md](../../../docs/architecture/21-feature-flags.md),
[ADR-0021](../../../docs/decisions/0021-feature-based-entitlement.md)):

- **Plan-projected** keys live in `platform_entitlement_cache` (Hub-owned).
- **Tenant-flag-level** keys live in `tenant_feature_flags` (per-tenant overrides
  for experimental / rollout features).

Both surfaces read through one `IFeatureFlags` API; the catalogue's `Source`
descriptor determines which storage the key reads from.

## When to use

- A plan now includes / excludes a capability (`FeatureKeys.SsoSaml`,
  `FeatureKeys.CustomDomain`).
- A new numeric limit must be enforced (`LimitKeys.MaxConcurrentLiveSessions`).
- A killswitch is needed for a new expensive code path
  (`KillswitchKeys.RecordingEnabled`).
- A code path is rolling out gradually (`FeatureKeys.LessonPlayerV2`).

## When not to use

- Per-request feature toggling via header — forbidden.
- Branching on unrelated flags in one function — refactor the function.
- A flag whose key contains a domain term (`english.*`, `cefr.*`) — forbidden by
  ADR-0018.
- Backend-only or frontend-only flags — every flag is readable from both surfaces
  through the same `IFeatureFlags` contract.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Key name | Yes | C# field name (`ClassroomRecording`) + wire-format string (`classroom.recording`). |
| Source | Yes | `PlanProjected` (Hub-side plan) or `TenantFlag` (per-tenant rollout). |
| Default | Yes | The catalogue default when no plan / tenant override exists. |
| Limit enforcement | Limit only | `Soft` (banner + Hub usage alert) or `Hard` (403 ProblemDetails). |
| Affected modules | Yes | Where the key is read. |

## Workflow

### Step 1: Pick the key

Format: `{scope}.{feature}.{name}`, no `.enabled` suffix (every `FeatureKey` is
implicitly boolean).

Examples:

```
classroom.recording                    # FeatureKey, PlanProjected
classroom.breakout_rooms               # FeatureKey, PlanProjected
tenancy.custom_domain                  # FeatureKey, PlanProjected
identity.sso.saml                      # FeatureKey, PlanProjected
learning.lesson_player.v2              # FeatureKey, TenantFlag (rollout)

tenancy.max_learners                   # LimitKey
classroom.max_concurrent_sessions      # LimitKey
classroom.minutes_per_month            # LimitKey, Soft enforcement

killswitch.classroom.recording         # FeatureKey, KillswitchKeys
killswitch.notifications.email         # FeatureKey, KillswitchKeys
```

Rules:

- Snake_case for multi-word fragments.
- No domain term (English, yoga, kyu/dan, asana).
- No trailing `.enabled` — booleans are implicit (this rule was clarified by
  [ADR-0021 Amendment 1](../../../docs/decisions/0021-feature-based-entitlement.md)).

### Step 2: Register in the catalogue

In `LearnStack.SharedKernel.FeatureFlags`:

```csharp
public static class FeatureKeys
{
    public static readonly FeatureKey ClassroomRecording =
        new("classroom.recording");

    public static readonly FeatureKey LessonPlayerV2 =
        new("learning.lesson_player.v2");   // TenantFlag — describe via catalog descriptor

    // ... grows over time
}
```

For limits:

```csharp
public static class LimitKeys
{
    public static readonly LimitKey MaxLearners =
        new("tenancy.max_learners");

    public static readonly LimitKey MaxClassroomMinutesPerMonth =
        new("classroom.minutes_per_month");
}
```

For killswitches:

```csharp
public static class KillswitchKeys
{
    public static readonly FeatureKey RecordingEnabled =
        new("killswitch.classroom.recording");   // default true
}
```

Pair each key with a **catalogue descriptor** so the runtime knows whether the
key is plan-projected vs tenant-flag-level, its default, enforcement, etc. The
exact shape is an implementation detail of the `LearnStack.SharedKernel` types
that ship in Phase 02a; the corpus describes the *intent* in
[21-feature-flags.md § Storage / Evaluation](../../../docs/architecture/21-feature-flags.md)
and [ADR-0021 Amendment 1](../../../docs/decisions/0021-feature-based-entitlement.md).
**Illustrative shape (final names land with the Phase-02a SharedKernel PR):**

```csharp
// Illustrative — actual property names land with the SharedKernel
// FeatureKeyDescriptor / LimitKeyDescriptor types in Phase 02a.
descriptors.Add(FeatureKeys.ClassroomRecording, new FeatureKeyDescriptor
{
    Source = FeatureSource.PlanProjected,
    Default = false,
    Description = "Enable in-app classroom recording",
    Phase = "02c",
    OwningAdr = "0021",
});

descriptors.Add(LimitKeys.MaxClassroomMinutesPerMonth, new LimitKeyDescriptor
{
    Default = 0,                                  // 0 = no limit
    Enforcement = LimitEnforcement.Soft,
    Description = "Total classroom participant minutes per calendar month",
    Phase = "02c",
    OwningAdr = "0021",
});
```

The descriptor's `Source` field is what the architecture test
`PlanProjected_Keys_NotInTenantFlags` (per
[21-feature-flags.md § Evaluation](../../../docs/architecture/21-feature-flags.md))
reads to enforce disjoint storage. Whatever the final property name is, the
disjoint-storage invariant is binding.

### Step 3: Read at the call site

Backend:

```csharp
if (!await featureFlags.IsEnabledAsync(FeatureKeys.ClassroomRecording, ct))
    return Result.Fail<RecordingDto>(
        new Error(LocalizedMessage.Of("lockey_classroom_recording_disabled")));
```

```csharp
var limit = await featureFlags.GetLimitAsync(LimitKeys.MaxConcurrentLiveSessions, ct);
if (currentConcurrent >= limit)
    return Result.Fail<LiveSessionDto>(
        new Error(LocalizedMessage.Of("lockey_limit_exceeded_classroom_concurrent")));
```

Frontend (`apps/web`):

```tsx
const recordingEnabled = useFeatureFlag(FeatureKeys.ClassroomRecording);
const { current, limit, soft } = useLimit(LimitKeys.MaxClassroomMinutesPerMonth);
```

See [add-feature-gated-ui](../add-feature-gated-ui/SKILL.md) for hook usage.

### Step 4: Hub-side plan editor (if PlanProjected)

For plan-projected keys, the Hub operator portal (`operator-portal` in the
separate repo) lists every key declared in the `FeatureKeys` catalogue. The Hub
plan editor surfaces them as toggle checkboxes. The Hub publishes the resulting
JSON entitlement projection to LearnStack via
`PUT /api/internal/tenants/{id}/entitlements`.

When you add a new plan-projected key:

1. Open a PR in `learnstack-hub` to extend the plan editor with the new key.
2. Update default plan templates (`Starter` / `Growth` / `Scale` / `Enterprise`) to
   set the default state for the new key.
3. Coordinate the LearnStack PR with the Hub PR to land in the same release window.

### Step 5: Eager invalidation

When the Hub pushes a new projection, it publishes
`learnstack.hub.entitlement` via Dapr pub/sub. The LearnStack core
`HubEntitlementProvider` listens and invalidates the cache. No new wiring is
required when you add a new key — the projection refresh covers all keys.

### Step 6: Killswitch runbook (if Killswitch)

Every killswitch ships with a runbook entry in `docs/runbooks/` describing:

- When to use it (the incident shape).
- How to flip it (the operator action — usually a SQL update against
  `tenant_feature_flags` for the sentinel "platform" tenant, or an admin endpoint).
- How to restore (revert + observability check).

A killswitch without a runbook is incomplete.

### Step 7: Tests

```csharp
[Fact]
public async Task FeatureKey_PlanProjected_ReadsFromCache()
{
    // arrange: seed platform_entitlement_cache with features.classroom.recording = true
    // act:     IFeatureFlags.IsEnabledAsync
    // assert:  true
}

[Fact]
public async Task FeatureKey_Killswitch_OverridesProjection()
{
    // arrange: features.classroom.recording = true, killswitch.classroom.recording = false
    // act:     IFeatureFlags.IsEnabledAsync(FeatureKeys.ClassroomRecording)
    // assert:  false  (killswitch wins)
}

[Fact]
public async Task LimitKey_Hard_BlocksAt_Boundary() { ... }

[Fact]
public async Task LimitKey_Soft_SurfaceBanner_DoesNotBlock() { ... }
```

## Validation

- `dotnet build` and `dotnet test` pass.
- Architecture tests:
  - `FeatureKey_AllReferences_AreInRegistry` (Roslyn) — every
    `IFeatureFlags.IsEnabledAsync` call references a registered key.
  - `LimitKey_AllReferences_AreInRegistry` — same for limits.
  - `PlanProjected_Keys_NotInTenantFlags` — plan-projected keys never appear in
    `tenant_feature_flags`.
- The 21-feature-flags doc lists the new key under the right section.
- For PlanProjected keys: the Hub-side PR has merged the corresponding plan
  editor update.

## Common pitfalls

- **`.enabled` suffix.** Removed by ADR-0021 Amendment 1. Use bare names.
- **`const string` instead of `FeatureKey`.** Loses type safety. Use the value
  object.
- **Writing a plan-projected key to `tenant_feature_flags`.** Architecture test
  rejects. Plan keys belong to the entitlement projection only.
- **Reading the key from raw SQL.** Forbidden; use `IFeatureFlags`.
- **Hot path without the cache stack.** Each `IsEnabledAsync` call could become
  DB-bound. The two cache layers (L1 in-process `IMemoryCache` = 60s TTL;
  L2 Dapr state → Valkey = 15-min upper bound; both eager-invalidated by the
  `learnstack.hub.entitlement` Dapr event) are load-bearing — see Standards 20
  § Configuration / Eager invalidation.
- **Killswitch without runbook.** The runbook is part of the deliverable. CI does
  not enforce its presence today; review must.
- **Removing a key without a deprecation cycle.** A rename / remove follows the
  same one-release-deprecation-warning rule as permissions.
