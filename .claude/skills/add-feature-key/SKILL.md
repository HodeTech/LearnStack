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

Extend the typed entitlement catalogue safely. Three storages, one read interface
([ADR-0045](../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md) —
the socket; [ADR-0021](../../../docs/decisions/0021-feature-based-entitlement.md);
[21-feature-flags.md](../../../docs/architecture/21-feature-flags.md)):

- **Plan-projected** keys resolve through **`IEntitlementProvider.GetAsync`** — the
  provider owns the projection's storage, and no caller reads
  `platform_entitlement_cache` itself.
- **Tenant-flag** keys read `tenant_feature_flags` (per-tenant rollout / opt-in).
- **Killswitches** read `platform_killswitches`, a platform-scoped table with no
  tenant column and no foreign key, through the L1 cache.

`IFeatureFlags` is the **only** module-facing read and it **composes** over the port
rather than querying a table; the catalogue's `Source` descriptor says which half a key
resolves through. Composing is what makes the Phase 02a criterion true — swapping the
registered `IEntitlementProvider` changes the answer without touching module code.

Resolution precedence, in order: tenant from `ITenantContext` (no tenant throws
`TenantContextMissingException`) → plan-projected through the provider → tenant-flag
from `tenant_feature_flags` → **killswitch overlay last, and it wins**.

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
- A key nothing gates yet. Packet 9 carries **only the keys the corpus already
  names**; a registry listing a capability no code reads is a list that is wrong before
  anything reads it. Each gate ships with the feature it gates.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Key name | Yes | C# field name (`ClassroomRecording`) + wire-format string (`classroom.recording`). |
| Source | Yes | `PlanProjected` (through `IEntitlementProvider`), `TenantFlag` (`tenant_feature_flags`), or killswitch (`platform_killswitches`). |
| Default | Yes | The catalogue default when the key is absent from the projection. For a `LimitKey`: `-1` unlimited, `0` denied, `> 0` the allowance. Killswitch default is `true`. |
| Limit enforcement | Limit only | `Soft` (banner + `usage.alert.soft_limit_reached`) or `Hard` (403 ProblemDetails). Declared here in Packet 9; **enforced** from Phase 02c. |
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

Pair each key with a **catalogue descriptor** so the runtime knows whether the key is
plan-projected, tenant-flag-level or a killswitch, its default, and — for a `LimitKey` —
its `LimitEnforcement`. The three catalogs, the two value objects and the descriptors
ship in **Phase 02a Packet 9**
([ADR-0045 § 6](../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md));
the property names below are illustrative, the **values** are not.

```csharp
// Illustrative shape — the descriptor type names land with Packet 9.
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
    Default = -1,                                 // -1 = unlimited; 0 = denied
    Enforcement = LimitEnforcement.Soft,
    Description = "Total classroom participant minutes per calendar month",
    Phase = "02c",
    OwningAdr = "0021",
});
```

**The limit sentinel, normative
([ADR-0045 § 3](../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md),
[ADR-0021 Amendment 2](../../../docs/decisions/0021-feature-based-entitlement.md)):**

| Value | Meaning |
|---|---|
| `-1` | Unlimited |
| `0` | Denied — the plan grants no allowance at all |
| `> 0` | The allowance |

`0` never means "no limit". Read the other way round, ADR-0021's degraded mode — past
`grace_until`, every limit returns `0` → read-only — would grant unlimited usage at the
moment the platform means to restrict it. `GetLimitAsync` returns `long`, never `long?`;
a key absent from the projection resolves to its catalog default.

The descriptor's `Source` field is what the architecture test
`PlanProjected_Keys_NotInTenantFlags` reads to enforce disjoint storage: a plan-projected
key is never served from `tenant_feature_flags`, and never the other way round.

### Step 3: Read at the call site

Backend:

```csharp
if (!await featureFlags.IsEnabledAsync(FeatureKeys.ClassroomRecording, ct))
    return Result.Fail<RecordingDto>(
        new Error(LocalizedMessage.Of("lockey_classroom_recording_disabled")));
```

```csharp
var limit = await featureFlags.GetLimitAsync(LimitKeys.MaxConcurrentLiveSessions, ct);
// -1 is unlimited, 0 is denied, > 0 is the allowance. Handle -1 explicitly:
// `currentConcurrent >= -1` is true for every non-negative count, which would
// refuse exactly the tenants the plan means to let through.
if (limit != -1 && currentConcurrent >= limit)
    return Result.Fail<LiveSessionDto>(
        new Error(LocalizedMessage.Of("lockey_limit_exceeded_classroom_concurrent")));
```

The gate ships **with the feature it gates**, never speculatively. The enforcement path
itself — the `403` refusal and the `usage.alert.soft_limit_reached` signal — lands in
[Phase 02c](../../../docs/roadmap/phase-02c-hub-foundation.md) with `IUsageReporter` and
`POST /api/v1/usage/report`, the first phase in which a soft limit has anywhere to
report to. Packet 9 ships the port, the catalog default and the `LimitEnforcement`
descriptor.

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

### Step 5: Refresh and eager invalidation

A push arrives on `PUT /api/internal/tenants/{id}/entitlements` and reaches
`IEntitlementProvider.RefreshAsync(EntitlementProjection)`, which is
**generation-guarded inside the write statement** (`… WHERE generation < @generation`,
never a read-then-write) and returns `IgnoredAsStale` for a projection that is not newer.
A retried or reordered delivery therefore cannot resurrect a revoked plan. No new wiring
is required per key — the projection carries them all.

Invalidation rides `IEventBus` — `InProcessEventBus` today, the Dapr/Kafka adapter on its
[ADR-0035](../../../docs/decisions/0035-demand-gated-infrastructure.md) trigger. The
provider that owns the projection is the only component that reads or writes
`platform_entitlement_cache`; `HubEntitlementProvider` itself lands in
[Phase 02c](../../../docs/roadmap/phase-02c-hub-foundation.md), and until then
`NullEntitlementProvider` — all features enabled, every limit `-1` — is the registered
implementation in **every** deployment mode, not `Development` only
([ADR-0020 Amendment, 2026-09-07](../../../docs/decisions/0020-triple-deployment-hybrid-license.md)).

### Step 6: Killswitch storage and runbook (if Killswitch)

**A killswitch is not tenant data and does not live in `tenant_feature_flags`.** That
table has `fk_tenant_feature_flags_tenant REFERENCES tenants (id)`, and the platform
sentinel is kept out of `tenants` by a `CHECK` — a foreign key is a constraint, so no
role and no `BYPASSRLS` moves it. Killswitches ship as **`platform_killswitches`**
(`key text PRIMARY KEY`, `is_enabled boolean NOT NULL`, `reason text NULL`,
`toggled_at timestamptz NOT NULL`, `toggled_by uuid NULL`), a platform-scoped table in
the Tenancy migration chain with role-qualified policies like
`platform_host_to_tenant`'s: `ENABLE` **and** `FORCE`,
`FOR SELECT TO learnstack_app USING (true)`, every write reserved to
`learnstack_platform` through the audited `EnterPlatformAdminScope(reason)` path — which
is what makes `tenancy.killswitch.toggle` a MUST-class audit row with a real actor
([ADR-0045 § 5](../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md)).

The overlay is read through the **L1 cache** and invalidated on toggle, under a second
allowed platform `CacheKey` family — `platform:tenancy:killswitch`. `EnsureValid` admits
one platform family today (`platform:hub:host-map:*`); Packet 9 enumerates the killswitch
family as the second, and a third one is another decision. A read failure resolves to
the key's default (`true`, enabled) and logs at `Error`: a cache outage must not disable
every gated path platform-wide. A flip is eventually consistent across instances,
bounded by the TTL and the invalidation event.

Every killswitch ships with a runbook entry in `docs/runbooks/` describing:

- When to use it (the incident shape).
- How to flip it — the operator action through `EnterPlatformAdminScope(reason)`, never
  a hand-written `UPDATE` as `learnstack_app`, which holds no write privilege on the
  table.
- How to restore (revert + observability check).

A killswitch without a runbook is incomplete.

### Step 7: Tests

```csharp
[Fact]
public async Task FeatureKey_PlanProjected_ResolvesThroughTheProvider()
{
    // arrange: a stub IEntitlementProvider returning features.classroom.recording = true
    //          — NOT a row seeded into platform_entitlement_cache. The provider owns
    //          the projection's storage; IFeatureFlags composes over it.
    // act:     IFeatureFlags.IsEnabledAsync(FeatureKeys.ClassroomRecording)
    // assert:  true — and swapping the registered provider changes the answer with
    //          no module code touched, which is the Phase 02a completion criterion.
}

[Fact]
public async Task FeatureKey_Killswitch_OverridesProjection()
{
    // arrange: features.classroom.recording = true, platform_killswitches row
    //          'killswitch.classroom.recording' with is_enabled = false
    // act:     IFeatureFlags.IsEnabledAsync(FeatureKeys.ClassroomRecording)
    // assert:  false  (the overlay is applied last and wins)
}

[Fact]
public async Task NullEntitlementProvider_ReturnsUnlimited()
{
    // assert: IsEnabledAsync -> true for every feature,
    //         GetLimitAsync  -> -1 for every limit. Not 0, and not null.
}

[Fact]
public async Task RefreshAsync_IgnoresAStalePush()
{
    // arrange: generation 42 applied
    // act:     RefreshAsync with generation 41
    // assert:  EntitlementRefreshOutcome.IgnoredAsStale, every column unchanged
}
```

## Validation

- `dotnet build` and `dotnet test` pass.
- Architecture tests, canonical names from
  [21-architecture-tests-catalogue.md](../../../docs/standards/21-architecture-tests-catalogue.md):
  - `FeatureKey_AllReferences_AreInRegistry` (Roslyn) — every `IFeatureFlags` call
    references a registered key. There is no separate `LimitKey_*` spelling; this rule
    covers both key types.
  - `PlanProjected_Keys_NotInTenantFlags` — plan-projected keys never appear in
    `tenant_feature_flags`.
  - `Modules_Do_Not_Read_Entitlement_Cache_Directly` — the only sanctioned reader **and**
    writer of `platform_entitlement_cache` is an `IEntitlementProvider` implementation;
    no module, Tenancy included, may query it.
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
- **Reading `platform_entitlement_cache` from a module.** Also forbidden, and Tenancy is
  not the exception it used to be: the provider owns that table, and a direct read
  bypasses the registered implementation, the grace window and the fail-open decision
  per key class.
- **`0` for "no limit".** It is the inverse. `-1` is unlimited, `0` is denied.
- **`long?` at a call site.** `GetLimitAsync` returns `long`; "not projected" resolves to
  the catalog default rather than to `null`.
- **Hot path without the cache stack.** Each `IsEnabledAsync` call could become
  DB-bound. The L1 in-process layer (`ICacheService` — `InMemoryCacheService` today) is
  load-bearing; the Valkey-backed L2 adapter is demand-gated to its
  [ADR-0035](../../../docs/decisions/0035-demand-gated-infrastructure.md) trigger. See
  Standards 20 § Configuration / Eager invalidation.
- **Killswitch without runbook.** The runbook is part of the deliverable. CI does
  not enforce its presence today; review must.
- **Removing a key without a deprecation cycle.** A rename / remove follows the
  same one-release-deprecation-warning rule as permissions.
