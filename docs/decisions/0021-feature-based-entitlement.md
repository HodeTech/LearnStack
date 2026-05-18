# ADR 0021: Feature-Based Entitlement Model

## Status

Accepted

## Date

2026-05-18

## Decision

LearnStack uses a **feature-based entitlement model** — not module-based.

- LearnStack core is one codebase with no installable / uninstallable modules. Every
  tenant gets the same set of LearnStack modules (Identity, Tenancy, Organization, Content,
  Catalog, Enrollment, Progress, Classroom, Scheduling, Media, Notification, Audit,
  Reporting). Tenants do not "install Module X for plan Y."
- Instead, **fine-grained features** within those modules are gated by the tenant's plan:
  recording on/off, custom domain on/off, SSO/SAML on/off, advanced analytics on/off,
  unlimited custom content types on/off, etc.
- **Numeric limits** (max users, max organizations, classroom minutes per month, recording
  storage GB, media bandwidth GB/month, API rate per minute) are also part of the
  entitlement projection.
- The runtime queries via `IFeatureFlagService.IsEnabledAsync(FeatureKeys.X, …)` and
  `IUsageLimitService.GetLimitAsync(LimitKeys.Y, …)`. Both read from the cached entitlement
  projection (ADR-0020).
- **Feature flag keys are typed** — a static `FeatureKeys` class lists every feature; a
  static `LimitKeys` class lists every limit. Magic strings are rejected by code review +
  architecture test.

## Context

Nexora chose a **module-based licensing** model (see
`Nexora/docs/decisions/0023-nmp-billing-model.md`,
`Nexora/docs/decisions/0016-module-tier-classification.md`,
`Nexora/docs/architecture/MODULE_SYSTEM.md`): plans grant access to modules
(`identity`, `contacts`, `crm`, `fundraising`, ...), modules
are installable / uninstallable per tenant at runtime, and the `ILicenseVerifier` checks
"is this module licensed for this tenant?" at module-install time.

That model fits Nexora because Nexora has **distinct business modules** that customers may
or may not need — a tenant running a CRM doesn't need a Fundraising module; a tenant
running an NGO doesn't need an HR module. The module's *presence* is the user-visible
unit of value.

LearnStack's product shape is different. Per ADR-0018, the core is 100% domain-agnostic;
the same modules ship to every tenant. The user-visible units of value are **features**
within the always-present modules:

- A yoga studio doesn't need "the Classroom module" disabled; they need "classroom
  recording" priced differently from a coding bootcamp that records every session.
- A small language school doesn't need "the Audit module" disabled; they need "audit
  retention" capped to 1 year while an enterprise corporate L&D needs 7 years.
- Every tenant uses the Content module; the differentiator is "unlimited custom content
  types" vs "5 custom content types max."

Module-based licensing in this shape would mean "every plan includes every module"
trivially, with no differentiation — which is to say it doesn't model the product
correctly.

Feature-based licensing also better fits the **PaaS positioning**: a tenant building a
yoga platform and a tenant building a coding bootcamp use **the same modules and the
same features**, just at different volumes (yoga session-minutes vs coding-bootcamp
streaming hours). Limits, not modules, are the natural axis of price differentiation.

## Decision drivers

1. **All tenants use the same modules** (per ADR-0018). Module-based licensing has nothing
   to gate.
2. **Plans differentiate by feature and limit**, not by module presence.
3. **Operator agility**: adding a new pricing dimension (e.g. "AI-generated content
   suggestions" as a Premium feature) should be a Hub configuration change, not a
   LearnStack code release.
4. **Tenants can grow gradually**: starting on Starter, hitting a feature wall, upgrading
   to Growth — without "installing a new module."
5. **Audit clarity**: "tenant X tried to use feature Y; entitlement denied" is a clearer
   audit shape than "tenant X tried to install module Z."
6. **Typed feature keys for compile-time safety.** Stringly-typed feature flags (Nexora
   pattern) produce silent failures on typos. LearnStack ships a `FeatureKeys` class +
   architecture test enforcing it.

## Considered options

### Option A — Feature-based entitlements (chosen)

`Entitlement.features: Dictionary<string, bool>` + `Entitlement.limits: Dictionary<string, long>`.
Each plan defines feature enablement and numeric limits. Runtime checks via
`IFeatureFlagService` (boolean checks) and `IUsageLimitService` (numeric limit checks).

**Pros:**
- Matches LearnStack's product shape (same modules, different plan-level features).
- Operators can author plans flexibly.
- Tenants upgrade by paying more, no install ceremony.
- Typed key registry possible (`FeatureKeys.ClassroomRecording`).

**Cons:**
- Hub plan editor UI is more complex (feature checkboxes per plan vs module checkboxes).
- Need to maintain `FeatureKeys` / `LimitKeys` static classes in sync with the plan editor.

### Option B — Module-based entitlements (Nexora pattern, rejected for LearnStack)

`Entitlement.modules: string[]`. Plans grant module access. Runtime checks at module
install time.

**Pros:**
- Cleaner conceptual model: "you have this module or you don't."
- Matches Nexora pattern exactly.

**Cons:**
- LearnStack has no installable modules; every tenant gets the same set.
- "Module" is not the user-visible axis of value for LearnStack customers.
- Would force LearnStack to invent fake "modules" (e.g. "the Recording module") that map
  one-to-one to features — adding indirection with no benefit.

### Option C — Hybrid (modules + features) — rejected

Plans grant both modules (presence) and features (within modules).

**Pros:**
- Most expressive.

**Cons:**
- Double the modelling complexity for zero gain in LearnStack's case (no real modules to
  toggle).
- Two surface areas to author plans against.

## Decision outcome

Adopt **Option A**: feature-based entitlements.

### Typed key registries

```csharp
namespace LearnStack.SharedKernel.FeatureFlags;

public static class FeatureKeys
{
    // Classroom
    public const string ClassroomRecording = "classroom.recording.enabled";
    public const string ClassroomBreakoutRooms = "classroom.breakout_rooms.enabled";
    public const string ClassroomLiveTranscription = "classroom.live_transcription.enabled";

    // Customization
    public const string CustomDomain = "custom_domain.enabled";
    public const string WhiteLabelBranding = "white_label_branding.enabled";
    public const string UnlimitedCustomContentTypes = "customization.unlimited_content_types";

    // Identity / SSO
    public const string SsoSaml = "sso.saml.enabled";
    public const string SsoOidc = "sso.oidc.enabled";
    public const string Scim = "identity.scim.enabled";

    // Analytics / API
    public const string AdvancedAnalytics = "analytics.advanced.enabled";
    public const string ApiAccess = "api_access.enabled";
    public const string Webhooks = "webhooks.outbound.enabled";

    // Compliance / Security
    public const string AuditExport = "audit.export.enabled";
    public const string DataResidencySelection = "compliance.data_residency.enabled";
    // ... grows over time
}

public static class LimitKeys
{
    // User caps
    public const string MaxUsers = "limits.max_users";
    public const string MaxOrganizations = "limits.max_organizations";

    // Classroom usage
    public const string ClassroomMinutesPerMonth = "limits.classroom_minutes_per_month";
    public const string ClassroomConcurrentParticipants = "limits.classroom_concurrent_participants";

    // Storage
    public const string RecordingStorageGb = "limits.recording_storage_gb";
    public const string MediaStorageGb = "limits.media_storage_gb";

    // Bandwidth
    public const string MediaBandwidthGbPerMonth = "limits.media_bandwidth_gb_per_month";

    // API
    public const string ApiRatePerMinute = "limits.api_rate_per_minute";
    public const string WebhookDeliveriesPerMonth = "limits.webhook_deliveries_per_month";

    // Customization
    public const string MaxCustomContentTypes = "limits.max_custom_content_types";
    public const string MaxPageBlockDefinitions = "limits.max_page_block_definitions";
}
```

### Runtime service contracts

```csharp
public interface IFeatureFlagService
{
    Task<bool> IsEnabledAsync(string featureKey, CancellationToken ct = default);
    Task<bool> IsEnabledAsync(string featureKey, Guid tenantId, CancellationToken ct = default);
}

public interface IUsageLimitService
{
    Task<long?> GetLimitAsync(string limitKey, CancellationToken ct = default);

    /// <summary>Checks usage vs limit; returns &lt;true, currentUsage&gt; when usage is at or above limit.</summary>
    Task<(bool LimitReached, long CurrentUsage, long? Limit)> CheckLimitAsync(
        string limitKey, Func<CancellationToken, Task<long>> usageProbe, CancellationToken ct = default);
}
```

Backend implementation reads from `platform_entitlement_cache` (ADR-0020); cache is
refreshed on `tenant.entitlement.updated` integration event.

### Plan definition (Hub side)

A `Plan` row in Hub's database carries:

```json
{
  "id": "plan-growth-monthly",
  "name": "Growth Monthly",
  "tier": "growth",
  "billing_cycle": "monthly",
  "base_price_usd": 199.00,
  "currency": "USD",
  "features": {
    "classroom.recording.enabled": true,
    "classroom.breakout_rooms.enabled": false,
    "custom_domain.enabled": true,
    "white_label_branding.enabled": true,
    "customization.unlimited_content_types": true,
    "sso.saml.enabled": false,
    "analytics.advanced.enabled": false,
    "api_access.enabled": true,
    "webhooks.outbound.enabled": true,
    "audit.export.enabled": true
  },
  "limits": {
    "limits.max_users": 500,
    "limits.max_organizations": 10,
    "limits.classroom_minutes_per_month": 50000,
    "limits.recording_storage_gb": 500,
    "limits.media_storage_gb": 1000,
    "limits.media_bandwidth_gb_per_month": 1000,
    "limits.api_rate_per_minute": 6000,
    "limits.max_custom_content_types": -1,
    "limits.max_page_block_definitions": -1
  },
  "is_active": true
}
```

`-1` denotes "unlimited"; `0` denotes "feature off / not available." The Hub plan editor
UI surfaces features as toggle checkboxes and limits as numeric inputs with `-1` allowed.

### Hub-runtime entitlement flow

Same flow as Nexora `NmpLicenseVerifier` (see
`Nexora/docs/decisions/0030-license-hot-reload-mechanism.md` and
`Nexora/docs/operations/license-and-helm-upgrade.md`):

```
Tenant action (e.g. "start recording")
  → handler calls IFeatureFlagService.IsEnabledAsync(FeatureKeys.ClassroomRecording)
  → service reads platform_entitlement_cache by tenant_id
  → if cache miss: HTTP POST /api/v1/internal/license/verify against Hub
  → Hub returns Entitlement projection (15-min TTL)
  → service returns true/false to handler
  → handler enforces or denies the action
```

Numeric limit flow:

```
Tenant action (e.g. "start a classroom session")
  → handler calls IUsageLimitService.CheckLimitAsync(
       LimitKeys.ClassroomMinutesPerMonth,
       async _ => await aggregator.GetUsedMinutesThisMonthAsync(tenantId, ct))
  → service reads limit from entitlement cache
  → service invokes usage probe to get current consumption
  → service returns (limit_reached: true|false, current, limit)
  → handler enforces or denies
```

### What happens when a tenant exceeds a soft limit

Two soft-vs-hard limit tiers, configurable per limit key:

- **Soft limit (warning)**: tenant notified; admin sees a banner; usage continues with
  warnings; Hub flags for upgrade prompt.
- **Hard limit (rejection)**: action rejected with `lockey-style` error key
  `error.limits.classroom_minutes_exceeded`; UI surfaces upgrade CTA.

Plan defines `soft_limit_pct` (e.g. 80) and `hard_limit_pct` (e.g. 100). Above hard limit,
the action returns `Result.Failure(...)`.

### Feature-flag fallback semantics

When `NullEntitlementProvider` is registered (Development), all features return `true` and
all limits return `null` (no limit). This matches Nexora's `NullLicenseVerifier` behaviour.

When `HubEntitlementProvider` is registered and Hub is unreachable:
- Cached projection still valid: serve the cached values.
- Cached projection expired (past `expires_at`):
  - Within `grace_until`: log warning, serve cached, continue.
  - Past `grace_until`: feature-flag service returns `false` for all features; limit
    service returns `0` for all limits → read-only mode.

## Architecture tests

Three blocker-level architecture tests added in Phase 02:

1. `FeatureFlagKeys_AllReferences_AreInRegistry` — every `IsEnabledAsync(...)` call site
   has the string argument referencing a `FeatureKeys.*` constant (Roslyn-based source
   scan).
2. `LimitKeys_AllReferences_AreInRegistry` — same for `IUsageLimitService.GetLimitAsync` /
   `CheckLimitAsync`.
3. `EntitlementProjection_Shape_IsStable` — entitlement DTO JSON schema is snapshot-tested
   against `entitlement-v1.schema.json`; breaking change requires schema version bump.

## Consequences

### Positive

- Matches LearnStack's product shape.
- Operators can author plans flexibly without code changes.
- Tenant upgrade is a Hub configuration change; no module install ceremony.
- Typed key registry catches typos at compile time.
- Cleaner audit shape ("feature Y denied for tenant X").

### Negative

- The `FeatureKeys` / `LimitKeys` registries grow over time; release discipline
  required (every new feature gets a constant + a CI check).
- The Hub plan editor UI is more involved than a simple module-checkbox UI.
- Usage probes (current consumption per limit key) must exist; some are non-trivial
  (e.g. "classroom minutes used this month" requires a usage aggregation table populated
  by integration events from the Classroom module).

### Neutral

- Some "module-shaped" features (e.g. "SCIM provisioning module") still feel like modules.
  These are modelled as features that gate the **endpoint registration** for the
  corresponding capability — if `FeatureKeys.Scim` is `false`, the SCIM endpoint group
  returns 404 (or 403, decision TBD; consistent with how Nexora handles flag-gated routes
  per `Nexora/docs/architecture/MODULE_SYSTEM.md` +
  `Nexora/docs/architecture/portal-extensions.md`).

## Implementation notes

- Phase 02 — Platform kernel: `IFeatureFlagService` + `IUsageLimitService` interfaces in
  SharedKernel. `EntitlementBackedFeatureFlagService` + `EntitlementBackedUsageLimitService`
  implementations in Infrastructure (read from `platform_entitlement_cache`).
  `FeatureKeys` + `LimitKeys` static classes with initial set covering Phase 02-04
  features.
- Phase 02c — Hub Foundation (parallel with 02b): `Plan` entity carries `features` +
  `limits` JSON; Hub plan editor scaffold.
- Phase 04+ — As new features land (custom domain, white-label, SSO, etc.), corresponding
  `FeatureKeys` constants added in the same PR as the feature; architecture test enforces
  registry membership.
- Phase 08b — Classroom usage aggregation table + integration-event-driven counter for
  `LimitKeys.ClassroomMinutesPerMonth`.
- Phase 09 — Media bandwidth / storage aggregation.
- Phase 09b — Hub plan editor UI; feature-toggle grid; limit inputs.

The full feature key catalog, plan editor UX, soft/hard limit enforcement, and usage
aggregation flow live in [24-learnstack-hub.md](../architecture/24-learnstack-hub.md).

## References

- ADR-0017 — Tenant + Organization (org-scoped limits exist for orgs in addition to
  tenant-scoped limits).
- ADR-0018 — Tenant-Driven Customization (some customization caps are entitlement
  features, e.g. `customization.unlimited_content_types`).
- ADR-0019 — LearnStack Hub.
- ADR-0020 — Triple Deployment Model + Hybrid License.
- [24-learnstack-hub.md](../architecture/24-learnstack-hub.md) — Hub deep dive incl.
  entitlement projection schema.
- Nexora analogue: `Nexora/docs/architecture/portal-extensions.md`,
  `Nexora/docs/decisions/0025-org-scoped-compliance-config-with-platform-caps.md`,
  `Nexora/docs/decisions/0023-nmp-billing-model.md`.

## Amendments

### 2026-05-18 — Typed registry shape: `FeatureKey` / `LimitKey` value objects

The original Decision section's code sample showed the typed registries as
`public const string` constants. The implementation contract is being tightened to
**value objects**, matching
[21-feature-flags.md](../architecture/21-feature-flags.md):

```csharp
public readonly record struct FeatureKey(string Value);
public readonly record struct LimitKey(string Value);

public static class FeatureKeys
{
    public static readonly FeatureKey ClassroomRecording = new("classroom.recording");
    public static readonly FeatureKey ClassroomBreakoutRooms = new("classroom.breakout_rooms");
    public static readonly FeatureKey CustomDomain = new("tenancy.custom_domain");
    public static readonly FeatureKey SsoSaml = new("identity.sso.saml");
    public static readonly FeatureKey SsoOidc = new("identity.sso.oidc");
    public static readonly FeatureKey Scim = new("identity.scim");
    public static readonly FeatureKey AdvancedReporting = new("analytics.advanced_reporting");
    public static readonly FeatureKey BulkImport = new("admin.bulk_import");
    public static readonly FeatureKey ApiAccess = new("integrations.api_access");
    public static readonly FeatureKey Webhooks = new("integrations.webhooks");
    public static readonly FeatureKey AuditExport = new("audit.export");
    public static readonly FeatureKey DataResidencySelection = new("compliance.data_residency");
    // experimental / rollout flags (tenant-flag-level, not plan-projected):
    public static readonly FeatureKey LessonPlayerV2 = new("learning.lesson_player.v2");
    public static readonly FeatureKey AiPronunciationFeedback = new("ai.pronunciation_feedback");
}

public static class LimitKeys
{
    public static readonly LimitKey MaxLearners = new("tenancy.max_learners");
    public static readonly LimitKey MaxInstructors = new("tenancy.max_instructors");
    public static readonly LimitKey MaxOrganizations = new("tenancy.max_organizations");
    public static readonly LimitKey MaxConcurrentLiveSessions = new("classroom.max_concurrent_sessions");
    public static readonly LimitKey MaxClassroomMinutesPerMonth = new("classroom.minutes_per_month");
    public static readonly LimitKey MaxStorageGb = new("media.storage_gb");
    public static readonly LimitKey MaxApiRequestsPerHour = new("integrations.api_rate");
}
```

Two changes flow from this:

1. **Type-safety.** A `FeatureKey` is a distinct type; `IFeatureFlags.IsEnabledAsync`
   takes `FeatureKey`, not `string`. A typo at the call site produces a compile-time
   error rather than a silent runtime "false" answer.
2. **Key naming.** The trailing `.enabled` suffix is dropped — every `FeatureKey` is
   implicitly boolean, so the suffix was redundant. The wire-format key strings now
   match [21-feature-flags.md](../architecture/21-feature-flags.md):
   `classroom.recording`, `tenancy.custom_domain`, `identity.sso.saml`, etc.

The plan editor JSON shape (`features`, `limits` dictionaries) keeps using the
underlying string keys (snake_case dotted, no suffix). The Decision itself
(feature-based entitlement, plan-tier projection, eager invalidation via Dapr event)
is unchanged — only the implementation contract is tightened.

Architecture test renamed accordingly:
`FeatureKey_AllReferences_AreInRegistry` (no `s` after `Key`; the Roslyn analyzer
now matches on `FeatureKey` literal references instead of `FeatureKeys.*` string
constants).
