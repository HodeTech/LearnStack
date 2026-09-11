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
  tenant column and no foreign key, through the L1 cache. Packet 9 ships that table
  **unwritten** — read path only, no toggle — and Phase 03 owns the command that flips a
  switch (Step 6).

`IFeatureFlags` is the **only** module-facing read and it **composes** over the port
rather than querying a table; the catalogue's `Source` descriptor says which half a key
resolves through. Composing is what makes the Phase 02a criterion true — swapping the
registered `IEntitlementProvider` changes the answer without touching module code.

Resolution precedence, in order: tenant from `ITenantContext` (no tenant throws
`TenantContextMissingException`) → plan-projected through the provider → tenant-flag
from `tenant_feature_flags` → **killswitch overlay last, and it wins**. The overlay
consults exactly the `KillswitchKeys` entry the key's descriptor names, and a key whose
descriptor names none has no overlay — the correspondence is declared, never derived
from the string
([ADR-0045 Amendment 1 § 5](../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md#amendment-1--what-the-other-repository-already-shipped-2026-09-08)).

## When to use

- A plan now includes / excludes a capability (`FeatureKeys.SsoSaml`,
  `FeatureKeys.CustomDomain`).
- A new numeric limit must be enforced (`LimitKeys.MaxCustomContentTypes`).
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
- A key neither the Hub contract nor the corpus names. A registry declares every key the
  contract names whether or not anything gates it yet — enforcement is what waits for a
  consumer, not membership
  ([ADR-0045 Amendment 2](../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md)).
  Each gate ships with the feature it gates.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Key name | Yes | C# field name (`ClassroomRecording`) + wire-format string (`classroom.recording`). |
| Source | Feature only | `FeatureSource.PlanProjected` (through `IEntitlementProvider`) or `FeatureSource.TenantFlag` (`tenant_feature_flags`). A limit is always plan-projected, and a killswitch is its own registry (`platform_killswitches`). |
| Default | Yes | The catalogue default when the key is absent from the projection. For a `LimitKey`: `-1` unlimited, `0` denied, `> 0` the allowance. Killswitch default is `true`. |
| Failure posture | Feature only | `DegradedPosture.FailClosed` or `DegradedPosture.FailOpenToLastKnown`, per [26-hybrid-license-model.md § Failure policy by key class](../../../docs/architecture/26-hybrid-license-model.md#failure-policy-by-key-class). Lives in the registry, never at the call site ([ADR-0034](../../../docs/decisions/0034-hub-contract-surface-invariant.md)). A killswitch declares none — a failed read already resolves to its default, `true`. |
| Killswitch | Feature only | The `KillswitchKeys` entry that can override this key, or none. Declared on the descriptor, never derived from the key string. |
| Limit enforcement | Limit only | `Soft` (banner + `usage.alert.soft_limit_reached`) or `Hard` (403 ProblemDetails). Declared here in Packet 9; **enforced** from Phase 02c. |
| Affected modules | Yes | Where the key is read. |

## Workflow

### Step 1: Pick the key

A `FeatureKey` is `{scope}.{feature}.{name}`, with no `.enabled` suffix (every
`FeatureKey` is implicitly boolean). **A `LimitKey` is not free-form: its vocabulary
belongs to the Hub**, under a `limits.` prefix
([ADR-0045 Amendment 1 § 1](../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md#amendment-1--what-the-other-repository-already-shipped-2026-09-08),
[ADR-0021, 2026-09-08](../../../docs/decisions/0021-feature-based-entitlement.md)).

Examples:

```
classroom.recording                    # FeatureKey, PlanProjected
classroom.breakout_rooms               # FeatureKey, PlanProjected
tenancy.custom_domain                  # FeatureKey, PlanProjected
identity.sso.saml                      # FeatureKey, PlanProjected
learning.lesson_player.v2              # FeatureKey, TenantFlag (rollout)

limits.max_users                       # LimitKey
limits.max_organizations               # LimitKey
limits.classroom_minutes_per_month     # LimitKey, Soft enforcement

killswitch.classroom.recording         # KillswitchKey, overrides classroom.recording
killswitch.notifications.email         # KillswitchKey, gates a path with no FeatureKey
```

Rules:

- Snake_case for multi-word fragments.
- No domain term (English, yoga, kyu/dan, asana).
- No trailing `.enabled` — booleans are implicit (this rule was clarified by
  [ADR-0021 Amendment 1](../../../docs/decisions/0021-feature-based-entitlement.md)).
- **A limit key comes from the Hub's set, and inventing one is a cross-repository
  change.** The nine the Hub ships are `limits.max_users`, `limits.max_organizations`,
  `limits.classroom_minutes_per_month`, `limits.recording_storage_gb`,
  `limits.media_storage_gb`, `limits.media_bandwidth_gb_per_month`,
  `limits.api_rate_per_minute`, `limits.max_custom_content_types` and
  `limits.max_page_block_definitions`; its plan validators reject a plan whose limits
  are not from that set. LearnStack's earlier spellings — `tenancy.max_learners`,
  `classroom.minutes_per_month`, `media.storage_gb` and the rest — are **withdrawn**: a
  key the Hub never sends misses on every real projection and silently falls through to
  its catalog default, which reads as a paid tenant having no plan.

### Step 2: Register in the catalogue

The socket's ports are declared in `LearnStack.SharedKernel.Entitlements`
([ADR-0045 § 1–2](../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md)),
and **so are the registries and the key value objects**. Packet 9 settled the pair that
way: the ports, the records, `FeatureKey` / `LimitKey` / `KillswitchKey`, their
descriptors and all three registries live in one folder,
`backend/src/LearnStack.SharedKernel/Entitlements/`.

[ADR-0021](../../../docs/decisions/0021-feature-based-entitlement.md)'s typed-registry
fence still reads `LearnStack.SharedKernel.FeatureFlags`. That is history rather than a
contradiction — it was true as intent when it was accepted, so the ADR's body is not
edited for it — and the reasoning for choosing `.Entitlements` over it is in the Packet 9
delivery record: it matches the shipped `SharedKernel/Audit` precedent, where the value
types sit beside their ports because a module that declared them would be a project
cycle; and it is the namespace the ADR that is current already prints in a normative
fence, which is what Phase 02c writes `HubEntitlementProvider` against from the other
repository. After that point the namespace is a cross-repository contract.

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

For limits — the strings are the Hub's, not ours (Step 1):

```csharp
public static class LimitKeys
{
    public static readonly LimitKey MaxUsers =
        new("limits.max_users");

    public static readonly LimitKey ClassroomMinutesPerMonth =
        new("limits.classroom_minutes_per_month");
}
```

For killswitches:

```csharp
public static class KillswitchKeys
{
    public static readonly KillswitchKey RecordingEnabled =
        new("killswitch.classroom.recording");   // default true
}
```

Each feature and limit key has a **descriptor** in its registry's `All` map, and a
killswitch has only its default. The shapes are the positional records in
`LearnStack.SharedKernel.Entitlements/Keys.cs` — `FeatureDescriptor(Key, Source, Default,
Degraded, Killswitch = null)` and `LimitDescriptor(Key, Default, Enforcement)` — and the
three value objects are `FeatureKey`, `LimitKey` and `KillswitchKey`. The entries below are
the shipped ones, verbatim:

```csharp
// FeatureKeys.All
new(ClassroomRecording, FeatureSource.PlanProjected, false,
    DegradedPosture.FailOpenToLastKnown, KillswitchKeys.RecordingEnabled),

// LimitKeys.All — the floor, never -1 (a gift) and never 0 (an outage). The Hub's
// Starter row carries 0 for this key; 60 is LearnStack's smallest working allowance, and
// LimitKeys.cs says why beside it.
new(ClassroomMinutesPerMonth, 60, LimitEnforcement.Soft),

// KillswitchKeys.All
[RecordingEnabled] = true,
```

**Two `FeatureDescriptor` members carry
[ADR-0045 Amendment 1 § 5](../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md#amendment-1--what-the-other-repository-already-shipped-2026-09-08):
`Degraded`, a required `DegradedPosture`, and `Killswitch`, a nullable `KillswitchKey`.** A
`LimitDescriptor` carries neither: every limit falls back to its `Default` floor.

- **The failure posture**, because
  [ADR-0034](../../../docs/decisions/0034-hub-contract-surface-invariant.md) requires
  every key class to declare fail-open or fail-closed *in the registry, not at the call
  site*. Which posture a key takes comes from
  [Hybrid License Model § Failure policy by key class](../../../docs/architecture/26-hybrid-license-model.md#failure-policy-by-key-class)
  — do not restate that table here, read it. Packet 9 ships the **declaration**; what
  the provider does with it past `grace_until` ships with `HubEntitlementProvider` in
  [Phase 02c](../../../docs/roadmap/phase-02c-hub-foundation.md).
- **The killswitch, by name.** Precedence step 4 applies "the corresponding killswitch",
  and the correspondence is this member — a nullable reference to the `KillswitchKeys`
  entry that gates the key. It is never inferred from the string: a renamed key would go
  silently ungated, and two of the shipped killswitches guard code paths that have no
  `FeatureKeys` counterpart at all, so no prefix rule could reach them. A key with no
  declared killswitch has no overlay.

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
var limit = await featureFlags.GetLimitAsync(LimitKeys.MaxUsers, ct);
// -1 is unlimited, 0 is denied, > 0 is the allowance. Handle -1 explicitly:
// `currentUsers >= -1` is true for every non-negative count, which would
// refuse exactly the tenants the plan means to let through.
if (limit != -1 && currentUsers >= limit)
    return Result.Fail<UserDto>(
        new Error(LocalizedMessage.Of("lockey_limit_exceeded_max_users")));
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
const { current, limit, soft } = useLimit(LimitKeys.ClassroomMinutesPerMonth);
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

**A new *limit* key starts on the Hub side, not this one.** The Hub owns the `limits.`
registry and its plan validators reject a plan carrying a key they do not know, so a
`LimitKeys` member added here alone would never appear in a projection. Land the Hub PR
first, then mirror the string verbatim.

### Step 5: Refresh and eager invalidation

A push arrives on `PUT /api/internal/tenants/{id}/entitlements` and reaches
`IEntitlementProvider.RefreshAsync(EntitlementProjection)`, which is
**generation-guarded inside the write statement** (`… WHERE
platform_entitlement_cache.generation <= @generation`, never a read-then-write) and
returns `IgnoredAsStale` for a **strictly older** projection. A reordered delivery
therefore cannot resurrect a revoked plan. No new wiring is required per key — the
projection carries them all.

**The guard admits the equal case, and that is deliberate**
([ADR-0045 Amendment 1 § 3](../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md#amendment-1--what-the-other-repository-already-shipped-2026-09-08)).
A push applies when its generation is greater than or **equal to** the stored one:
`generation` defaults to `1` on the provisioning insert and the Hub's first real
projection for that tenant also carries `1`, so a strictly-newer guard would discard it
and leave a paid tenant reading as unentitled — reported as success. Replay at the same
generation rewrites the same bytes, because the Hub is the single writer and increments
once per recompute.

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
two platform families, `platform:hub:host-map:*` and, since Packet 9,
`platform:tenancy:killswitch`; a third one is another decision. A read failure resolves to
the key's default (`true`, enabled) and logs at `Error`: a cache outage must not disable
every gated path platform-wide. A flip is eventually consistent across instances,
bounded by the TTL and the invalidation event.

**Packet 9 ships the table unwritten, and nothing in it flips a switch**
([ADR-0045 Amendment 1 § 4](../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md#amendment-1--what-the-other-repository-already-shipped-2026-09-08)).
The table, the policies, the overlay, the cache family and the three `KillswitchKeys`
ship; no writer does. The reason is reachability rather than scheduling: every killswitch
write runs inside `EnterPlatformAdminScope(reason)`, the registered `IPlatformAdminGate`
is `DenyAllPlatformAdminGate`, and nothing can enter that scope until the Platform-scope
permission arrives with the registry in
[Phase 03](../../../docs/roadmap/phase-03-identity-admin.md). A toggle command shipped
now would be unreachable code with a permission key nothing registers. **Phase 03 owns
the toggle command, its permission and its runbook**; until it lands,
`tenancy.killswitch.toggle` carries `(planned)` in the Tenancy matrix beside the
`(off-path)` marker it keeps permanently
([Audit Coverage § The join](../../../docs/standards/18-audit-coverage.md),
[ADR-0044 Amendment 3](../../../docs/decisions/0044-audit-write-path.md#amendment-3--what-the-join-binds-to-and-the-types-the-ports-carry-2026-09-08)).
Every gated read honours a flipped switch the day one exists; what is absent is the
flipping.

So a killswitch key added before Phase 03 is read-only, and its runbook lands with the
command that makes it flippable. The runbook entry — in `docs/runbooks/`, a directory
[Phase 11](../../../docs/roadmap/phase-11-production-hardening.md) owns — describes:

- When to use it (the incident shape).
- How to flip it — the operator action through `EnterPlatformAdminScope(reason)`, never
  a hand-written `UPDATE` as `learnstack_app`, which holds no write privilege on the
  table.
- How to restore (revert + observability check).

A flippable killswitch without a runbook is incomplete.

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
    // arrange: features.classroom.recording = true; the descriptor for
    //          FeatureKeys.ClassroomRecording names KillswitchKeys.RecordingEnabled;
    //          that switch's platform_killswitches row has is_enabled = false.
    //          Assert against the DECLARED reference, never the shared name fragment —
    //          a test that passes on the string coincidence passes on nothing.
    // act:     IFeatureFlags.IsEnabledAsync(FeatureKeys.ClassroomRecording)
    // assert:  false  (the overlay is applied last and wins)
}

[Fact]
public async Task FeatureKey_WithNoDeclaredKillswitch_HasNoOverlay()
{
    // arrange: a flipped-off killswitch whose key shares a name fragment with the
    //          feature key, but which no descriptor names.
    // act:     IFeatureFlags.IsEnabledAsync(that feature key)
    // assert:  the projection's answer, unchanged. This is the case that fails if
    //          anyone re-derives the correspondence from the string.
}

[Fact]
public async Task NullEntitlementProvider_ReturnsUnlimited()
{
    // assert: IsEnabledAsync -> true for every feature,
    //         GetLimitAsync  -> -1 for every limit. Not 0, and not null.
}

[Fact]
public async Task RefreshAsync_IgnoresAStrictlyOlderPush()
{
    // arrange: generation 42 applied
    // act:     RefreshAsync with generation 41
    // assert:  EntitlementRefreshOutcome.IgnoredAsStale, every column unchanged
}

[Fact]
public async Task RefreshAsync_AppliesAnEqualGenerationPush()
{
    // arrange: generation 1 written by provisioning (the column default)
    // act:     RefreshAsync with the Hub's first projection, also generation 1
    // assert:  Applied, and the plan is readable. This is the case the two spellings
    //          of the guard disagree on, and the one a paid tenant loses under `<`.
}
```

## Validation

- `dotnet build` and `dotnet test` pass.
- Architecture tests, canonical names from
  [21-architecture-tests-catalogue.md](../../../docs/standards/21-architecture-tests-catalogue.md):
  - `FeatureKey_AllReferences_AreInRegistry` (an IL scan) — every `FeatureKey`,
    `LimitKey` and `KillswitchKey` a call site names is a member of its registry. There
    is no separate `LimitKey_*` spelling; this one rule covers all three key types.
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
- **A limit key in LearnStack's old spelling.** `tenancy.max_learners` and its siblings
  are withdrawn; the `limits.` vocabulary is the Hub's. A key it never sends resolves to
  the catalog default on every projection, so the tenant reads as unplanned and nothing
  errors.
- **Deriving a killswitch from the feature key's string.** The descriptor names it or
  nothing does. Two of the three shipped killswitches gate paths with no `FeatureKeys`
  entry, so no prefix rule reaches them, and a renamed key would go silently ungated.
- **Omitting the failure posture.** ADR-0034 puts it in the registry; a key that leaves
  it to the call site has no answer for the unresolved case, which is the one case the
  member exists for.
- **A killswitch without a runbook, once one can be flipped.** The runbook belongs with
  the command Phase 03 lands, not with the key Packet 9 registers. CI does not enforce
  its presence; review must.
- **Writing a killswitch toggle in Packet 9.** The table ships unwritten and
  `DenyAllPlatformAdminGate` is registered, so the command would be unreachable code
  gated by a permission nothing registers.
- **Removing a key without a deprecation cycle.** A rename / remove follows the
  same one-release-deprecation-warning rule as permissions.
