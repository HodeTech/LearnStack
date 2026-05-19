# Feature Flags & Entitlements

LearnStack distinguishes two related-but-separate concerns at the same read interface:

- **Plan-level features and limits** are projected from the **Hub** into the
  `platform_entitlement_cache` table per
  [ADR-0021](../decisions/0021-feature-based-entitlement.md). They answer: *what does
  this tenant's plan include?* Examples: `FeatureKeys.ClassroomRecording`,
  `FeatureKeys.SsoSaml`, `FeatureKeys.CustomDomain`,
  `LimitKeys.MaxConcurrentLiveSessions`. They are managed by **operators in the Hub
  plan editor**, not by tenants.
- **Per-tenant feature flags** are owned by the Tenancy module's
  `tenant_feature_flags` table. They answer: *should this tenant get this code path
  right now?* — gradual rollouts, killswitches, and per-tenant experimental opt-ins.
  They are managed by **platform admins** (and, where the flag is explicitly tenant-
  overridable, by tenant admins).

A single `IFeatureFlags` interface reads from both sources with a defined precedence so
caller code doesn't care which storage backs the answer. This document defines the
catalog, the runtime, the lifecycle, and the rules that prevent flags from becoming
permanent technical debt.

## Scope

Feature flags + entitlements are used for:

- **Plan-level gating** (e.g. `FeatureKeys.ClassroomRecording` on/off per plan;
  `LimitKeys.MaxLearners` capped per plan).
- **Tenant enablement of optional code paths** that aren't plan-tied (e.g. an
  experimental feature opt-in).
- **Gradual rollout of new code paths** (new lesson player UI behind a flag while it is
  validated on a few tenants).
- **Killswitches for expensive paths** (disable recording globally during a cost
  incident).

Out of scope:

- **A/B experimentation** at the learner level — that is an analytics concern and lives
  in a separate `Experiment` aggregate (post-MVP).
- **Per-request feature toggling via headers** — flags resolve once per request
  context; ad-hoc overrides are platform-admin only.
- **Branch-by-feature for incomplete work** — a flag is for a feature the team has
  decided to ship; partially-built code lives behind branches, not flags.

## Typed Catalog

The catalog is **code-defined**, **typed**, and **enumerated in one place**. Free-form
string flag keys are forbidden. Two static registries:

```csharp
public static class FeatureKeys
{
    // Plan-level (entitlement-projected from Hub):
    public static readonly FeatureKey ClassroomRecording = new("classroom.recording");
    public static readonly FeatureKey ClassroomBreakoutRooms = new("classroom.breakout_rooms");
    public static readonly FeatureKey CustomDomain = new("tenancy.custom_domain");
    public static readonly FeatureKey SsoSaml = new("identity.sso.saml");
    public static readonly FeatureKey SsoOidc = new("identity.sso.oidc");
    public static readonly FeatureKey AdvancedReporting = new("analytics.advanced_reporting");
    public static readonly FeatureKey BulkImport = new("admin.bulk_import");
    public static readonly FeatureKey ApiAccess = new("integrations.api_access");

    // Tenant-flag-level (experimental / rollout / opt-in):
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

public static class KillswitchKeys
{
    public static readonly FeatureKey RecordingEnabled = new("killswitch.classroom.recording");
    public static readonly FeatureKey EmailDispatchEnabled = new("killswitch.notifications.email");
    public static readonly FeatureKey AnalyticsIngestEnabled = new("killswitch.analytics.ingest");
}
```

Rules:

- Keys are dotted: `{scope}.{feature}.{name}`.
- Plan-level keys (`FeatureKeys.*` whose catalog descriptor marks them as
  plan-projected) **never** appear in `tenant_feature_flags`; they read only from
  `platform_entitlement_cache`. A direct write to `tenant_feature_flags` for such a key
  fails an architecture test.
- Tenant-flag-level keys default to `false` and can be set per tenant.
- Limit keys are `long`; default `0` means "no limit imposed by this layer" — the API's
  policy can still cap.
- Killswitch keys default to `true` (the safe / enabled state); flipping to `false`
  short-circuits the gated path.
- Adding a key requires a comment in the catalog file linking to the ADR / phase that
  introduced it.
- Removing a key follows a deprecation cycle (one release with a warning log when read;
  removal in the next major).

## Storage

Two tables, both in the Tenancy module schema:

```sql
-- Tenant-level flag overrides (experimental, rollout, opt-in).
CREATE TABLE tenant_feature_flags (
    tenant_id   uuid NOT NULL,
    key         text NOT NULL,
    value       jsonb NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid NOT NULL,
    PRIMARY KEY (tenant_id, key)
);

-- Hub-projected entitlement cache (plan-level features + limits + compliance caps).
-- One row per tenant; replaced on `learnstack.hub.entitlement` Dapr pub/sub event.
CREATE TABLE platform_entitlement_cache (
    tenant_id        uuid PRIMARY KEY,
    plan_code        text NOT NULL,
    features         jsonb NOT NULL,    -- Dictionary<string, bool>
    limits           jsonb NOT NULL,    -- Dictionary<string, long>
    compliance       jsonb NOT NULL,    -- caps, regions, retention overrides
    valid_until      timestamptz NOT NULL,
    refreshed_at     timestamptz NOT NULL DEFAULT now(),
    source           text NOT NULL      -- 'hub' | 'signed-license-key' | 'null-provider'
);
```

Rules:

- `tenant_id = NULL` is **not** allowed. Platform-wide flags use a sentinel "platform"
  tenant id, never `NULL`.
- The Hub is the **owner** of `platform_entitlement_cache`; the LearnStack core only
  reads + invalidates. Writes happen through `IEntitlementProvider.RefreshAsync` on
  the inbound Dapr event from Hub. See
  [ADR-0021](../decisions/0021-feature-based-entitlement.md) and
  [29-dapr-integration.md](29-dapr-integration.md).
- A short-TTL Valkey cache (60 s) fronts both tables for hot-path reads. Eager
  invalidation flows from `learnstack.cache.invalidation` (intra-instance) and from
  `learnstack.hub.entitlement` (cross-deployment).

## Evaluation

```csharp
public interface IFeatureFlags
{
    Task<bool> IsEnabledAsync(FeatureKey key, CancellationToken ct);
    Task<long> GetLimitAsync(LimitKey key, CancellationToken ct);
}
```

Resolution precedence for `IsEnabledAsync(FeatureKey key, ct)`:

1. Resolve the current tenant from `ITenantContext`. No tenant → throws
   (`TenantContextMissingException`). Hub admin / Self-Hosted operator paths that
   genuinely need to read cross-tenant go through a separate
   `IEntitlementAdminQuery` interface.
2. **If the key's catalog descriptor says `Source = PlanProjected`:** read from
   `platform_entitlement_cache.features` (via Valkey cache → Postgres). A missing entry
   resolves to the catalog default. Per-tenant `tenant_feature_flags` are **never**
   consulted for plan-projected keys.
3. **If the key's catalog descriptor says `Source = TenantFlag`:** read from
   `tenant_feature_flags` (via Valkey cache → Postgres). Missing entry → catalog
   default.
4. **Killswitch overlay** (last word): if the corresponding killswitch is flipped
   `false` platform-wide, the answer becomes `false` regardless of the per-tenant
   value. Killswitches override the projection.
5. Resolution is logged at `Debug` (sampled) with `flag_key`, `tenant_id`, `value`,
   `source` (`plan`, `tenant`, `killswitch`, `default`).

`GetLimitAsync` reads only from `platform_entitlement_cache.limits` and ignores
`tenant_feature_flags` — limits are always plan-projected.

Architecture tests:

- Direct SQL reads against `tenant_feature_flags` or `platform_entitlement_cache`
  outside the Tenancy module's infrastructure are forbidden.
- A key must exist in `FeatureKeys` / `LimitKeys` before `IFeatureFlags` can reference
  it (compile-time guarantee through `FeatureKey` / `LimitKey` value objects).
- Tests that depend on a flag use `FeatureFlagsFixture` (overrides), not direct DB
  writes.
- `PlanProjected_Keys_NotInTenantFlags` ensures no plan-projected key has ever been
  written to `tenant_feature_flags`.

## Soft vs Hard Limits

Each `LimitKey` in the catalog declares a `LimitEnforcement` (`Soft` | `Hard`):

- **Hard** — the gated operation refuses with `403 ProblemDetails`
  `type=urn:learnstack:errors:limit-exceeded` when current >= limit. Example:
  `MaxLearners`.
- **Soft** — the operation succeeds; a banner is surfaced and a Hub-side
  `usage.alert.soft_limit_reached` event is emitted to the Hub via
  `POST /api/v1/usage/report`. Example: `MaxClassroomMinutesPerMonth`.

The frontend's `useLimit(key)` hook surfaces both `current` / `limit` and the
enforcement mode so the UI can present the right message.

## Killswitch Pattern

A killswitch is a `KillswitchKeys.*` entry whose default is `true` and that gates an
expensive code path. When triggered, it is flipped to `false` for the sentinel
"platform" tenant. Examples:

- `KillswitchKeys.RecordingEnabled` — flip off during a storage incident.
- `KillswitchKeys.EmailDispatchEnabled` — flip off during an upstream email provider
  outage.
- `KillswitchKeys.AnalyticsIngestEnabled` — flip off when the analytics pipeline is
  back-pressured.

A killswitch always pairs with a runbook entry in `docs/runbooks/` describing when to
use it and how to restore. The killswitch overlay in `IFeatureFlags` resolution wins
over **both** plan projection and tenant flag — a killswitch can disable a feature even
on a plan that nominally includes it.

## Lifecycle and Hygiene

Keys accumulate. The team reviews the catalog quarterly:

- **Plan-projected keys** stay for the life of the plan; their removal is a Hub-side
  plan change and a coordinated catalog edit.
- **Tenant flags for rollout** — once 100% of tenants are flipped on for one release,
  the flag is removed in the next release. CI surfaces tenant flags that have been at
  100% for > 90 days as candidates.
- **Killswitches** stay forever, but their runbooks must remain accurate; runbook
  freshness is part of the quarterly review.
- **Stale flags** — a tenant flag at its default for > 1 year with no tenant overrides
  is flagged for removal.

## Audit

Both surfaces are MUST-audit security-events (see
[Audit Coverage Standard](../standards/18-audit-coverage.md)):

- `tenancy.feature_flag.write` permission is required to write `tenant_feature_flags`.
- Entitlement projection writes happen only via `IEntitlementProvider.RefreshAsync`;
  the inbound Hub event carries `hub_event_id` and is mirrored into an audit entry
  (`tenancy.entitlement.refresh`) with `before` and `after` snapshots of the
  `features` / `limits` jsonb.
- Killswitch flips are logged as `tenancy.killswitch.toggle` with the platform-admin
  actor and a free-text reason field that the operator console makes required.

## Risks

- **Plan-projected vs tenant-flag confusion.** A future engineer adds
  `FeatureKeys.ClassroomRecording` to `tenant_feature_flags` because they don't know it
  is plan-projected. The architecture test `PlanProjected_Keys_NotInTenantFlags` blocks
  this in CI; the catalog descriptor (`Source`) makes the intent obvious in code.
- **Permanent rollout flags.** Treated as drift. The quarterly review is the
  discipline.
- **Branching on flag identity instead of capability.** Code that reads
  `if (locale === "tr")` is bad ([08-localization.md](../standards/08-localization.md));
  code that reads `if (FeatureKeys.SomeArbitraryName)` for unrelated branching is the
  same bug. Flags gate features; if you find yourself branching on multiple unrelated
  flags in one function, refactor.
- **Flag drift between code and database.** A flag whose key is renamed in code but
  not migrated in the DB silently returns the default for every tenant. Renaming is a
  deprecation cycle, not a refactor.
- **Stale entitlement projection.** A tenant upgraded on Hub but whose projection
  hasn't refreshed sees the old feature set. Eager invalidation via the Dapr event
  keeps the typical refresh within seconds; the 15-min TTL is the upper bound.
- **Performance.** Hot paths that read flags per call become DB-bound without the
  Valkey cache; the 60s TTL is the default trade-off.

## Roadmap Touchpoints

- **Phase 02a** — `tenant_feature_flags` table created in the Tenancy module; the
  `FeatureKeys` / `LimitKeys` / `KillswitchKeys` catalogs land here. `IFeatureFlags`,
  the Valkey cache, and the architecture tests ship here.
- **Phase 02c** (parallel Hub Foundation) —
  `platform_entitlement_cache`, `IEntitlementProvider` with `NullEntitlementProvider`
  default + `HubEntitlementProvider` + `SignedLicenseKeyEntitlementProvider`
  implementations. The Dapr-event-driven projection refresh ships here.
- **Phase 06** — Admin Studio surface for editing per-tenant flag overrides and
  viewing the entitlement projection. The Studio screen for `platform_entitlement_cache`
  is **read-only** — actual plan edits happen in the operator portal
  (`learnstack-hub-web`).
- **Phase 09** — Audit + observability hooks for both flag writes and entitlement
  refreshes plug into the audit + analytics pipeline.
- **Phase 11** — Quarterly hygiene review and CI surfacing of stale flags become
  operational.

## References

- [ADR-0021 Feature-Based Entitlement Model](../decisions/0021-feature-based-entitlement.md)
- [ADR-0019 LearnStack Hub](../decisions/0019-learnstack-hub.md)
- [ADR-0020 Triple Deployment + Hybrid License](../decisions/0020-triple-deployment-hybrid-license.md)
- [24-learnstack-hub.md](24-learnstack-hub.md) — Hub plan editor and the source of the
  entitlement projection.
- [25-deployment-models.md](25-deployment-models.md) — how each deployment mode loads
  the projection.
- [29-dapr-integration.md](29-dapr-integration.md) — `IEventBus` / `ICacheService` /
  `ISecretProvider` wiring.
