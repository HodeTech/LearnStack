# Feature Flags & Entitlements

LearnStack distinguishes three related-but-separate concerns behind one read interface:

- **Plan-level features and limits** are projected from the **Hub** into the
  `platform_entitlement_cache` table per
  [ADR-0021](../decisions/0021-feature-based-entitlement.md). They answer: *what does
  this tenant's plan include?* Examples: `FeatureKeys.ClassroomRecording`,
  `FeatureKeys.SsoSaml`, `FeatureKeys.CustomDomain`,
  `LimitKeys.ClassroomMinutesPerMonth`. They are managed by **operators in the Hub
  plan editor**, not by tenants.
- **Per-tenant feature flags** are owned by the Tenancy module's
  `tenant_feature_flags` table. They answer: *should this tenant get this code path
  right now?* — gradual rollouts and per-tenant experimental opt-ins. They are managed
  by **platform admins** (and, where the flag is explicitly tenant-overridable, by
  tenant admins).
- **Killswitches** belong to neither source. They answer: *is this code path safe to run
  at all right now?* — one platform-wide row per key in `platform_killswitches`, with no
  tenant column and no foreign key, flipped by **platform admins** during an incident
  ([ADR-0045 § 5](../decisions/0045-entitlement-and-feature-flag-socket.md)). See
  § Killswitch Pattern.

A single `IFeatureFlags` interface reads both sources and applies the killswitch overlay
last, in a defined precedence, so caller code doesn't care which storage backs the
answer. This document defines the catalog, the runtime, the lifecycle, and the rules
that prevent flags from becoming permanent technical debt.

## Scope

Feature flags + entitlements are used for:

- **Plan-level gating** (e.g. `FeatureKeys.ClassroomRecording` on/off per plan;
  `LimitKeys.MaxUsers` capped per plan).
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
string flag keys are forbidden. Three static registries. The fence below lists the key
**strings** — the vocabulary, of which Packet 9 registers the members that have a
consumer ([ADR-0045 § 6](../decisions/0045-entitlement-and-feature-flag-socket.md)) —
while each registry entry is a descriptor, and the members every descriptor carries are
fixed by the rules that follow.

```csharp
public static class FeatureKeys
{
    // Plan-level (entitlement-projected from Hub):
    public static readonly FeatureKey ClassroomRecording = new("classroom.recording");
    public static readonly FeatureKey ClassroomBreakoutRooms = new("classroom.breakout_rooms");
    public static readonly FeatureKey CustomDomain = new("tenancy.custom_domain");
    public static readonly FeatureKey WhiteLabelBranding = new("tenancy.white_label_branding");
    public static readonly FeatureKey UnlimitedContentTypes = new("customization.unlimited_content_types");
    public static readonly FeatureKey SsoSaml = new("identity.sso.saml");
    public static readonly FeatureKey SsoOidc = new("identity.sso.oidc");
    public static readonly FeatureKey Scim = new("identity.scim");
    public static readonly FeatureKey AdvancedReporting = new("analytics.advanced_reporting");
    public static readonly FeatureKey BulkImport = new("admin.bulk_import");
    public static readonly FeatureKey ApiAccess = new("integrations.api_access");
    public static readonly FeatureKey Webhooks = new("integrations.webhooks");
    public static readonly FeatureKey AuditExport = new("audit.export");
    public static readonly FeatureKey DataResidencySelection = new("compliance.data_residency");

    // Tenant-flag-level (experimental / rollout / opt-in):
    public static readonly FeatureKey LessonPlayerV2 = new("learning.lesson_player.v2");
    public static readonly FeatureKey AiPronunciationFeedback = new("ai.pronunciation_feedback");
}

public static class LimitKeys
{
    public static readonly LimitKey MaxUsers = new("limits.max_users");
    public static readonly LimitKey MaxOrganizations = new("limits.max_organizations");
    public static readonly LimitKey ClassroomMinutesPerMonth = new("limits.classroom_minutes_per_month");
    public static readonly LimitKey RecordingStorageGb = new("limits.recording_storage_gb");
    public static readonly LimitKey MediaStorageGb = new("limits.media_storage_gb");
    public static readonly LimitKey MediaBandwidthGbPerMonth = new("limits.media_bandwidth_gb_per_month");
    public static readonly LimitKey ApiRatePerMinute = new("limits.api_rate_per_minute");
    public static readonly LimitKey MaxCustomContentTypes = new("limits.max_custom_content_types");
    public static readonly LimitKey MaxPageBlockDefinitions = new("limits.max_page_block_definitions");
}

public static class KillswitchKeys
{
    public static readonly KillswitchKey RecordingEnabled = new("killswitch.classroom.recording");
    public static readonly KillswitchKey EmailDispatchEnabled = new("killswitch.notifications.email");
    public static readonly KillswitchKey AnalyticsIngestEnabled = new("killswitch.analytics.ingest");
}
```

Rules:

- Key **shape** is fixed once, in
  [26-hybrid-license-model.md § 0](26-hybrid-license-model.md#0-canonical-key-vocabulary),
  and not restated here. The one change worth flagging beside the fence: the limit-key
  vocabulary is the **Hub's**, under the `limits.` prefix
  ([ADR-0045 Amendment 1 § 1](../decisions/0045-entitlement-and-feature-flag-socket.md),
  [ADR-0021 § Amendments (2026-09-08)](../decisions/0021-feature-based-entitlement.md)).
  The earlier subject-area spellings — `tenancy.max_learners`,
  `classroom.minutes_per_month`, `media.storage_gb` and the rest — are withdrawn.
- Plan-level keys (`FeatureKeys.*` whose catalog descriptor marks them as
  plan-projected) **never** appear in `tenant_feature_flags`; they resolve only through
  `IEntitlementProvider`, which owns the projection's storage. A direct write to
  `tenant_feature_flags` for such a key fails an architecture test.
- Tenant-flag-level keys default to `false` and can be set per tenant.
- Every `FeatureKey` descriptor declares its **failure class** — fail-open or
  fail-closed — because
  [ADR-0034 § The entitlement read path](../decisions/0034-hub-contract-surface-invariant.md#the-entitlement-read-path)
  puts the decision in the registry and not at the call site, and
  [ADR-0045 Amendment 1 § 5](../decisions/0045-entitlement-and-feature-flag-socket.md)
  makes it a member Packet 9 ships. It is what the provider's degraded path reads when
  the projection is unavailable; the classes are tabulated in
  [26-hybrid-license-model.md § Failure policy by key class](26-hybrid-license-model.md#failure-policy-by-key-class).
- Every `FeatureKey` descriptor also names **its killswitch, or none** — a nullable
  `KillswitchKey` reference, never a string derived from the feature key
  ([ADR-0045 Amendment 1 § 5](../decisions/0045-entitlement-and-feature-flag-socket.md)).
  Inference would leave a renamed key silently ungated.
- Limit keys are `long`, and the sentinel encoding is normative
  ([ADR-0045 § 3](../decisions/0045-entitlement-and-feature-flag-socket.md)): `-1` is
  unlimited, `0` is denied — the plan grants no allowance at all — and any value `> 0`
  is the allowance. A key absent from the projection resolves to its catalog default,
  which each `LimitKey` declares.
- Killswitch keys default to `true` (the safe / enabled state); flipping to `false`
  short-circuits the gated path.
- Adding a key requires a comment in the catalog file linking to the ADR / phase that
  introduced it.
- Removing a key follows a deprecation cycle (one release with a warning log when read;
  removal in the next major).

## Storage

Two tables, both in the Tenancy module schema. A third, `platform_killswitches`, joins
them in Packet 9 and is described with the pattern it serves — see § Killswitch
Pattern.

```sql
-- Tenant-level flag overrides (experimental, rollout, opt-in).
CREATE TABLE tenant_feature_flags (
    tenant_id   uuid NOT NULL,
    key         varchar(200) NOT NULL,
    value       jsonb NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid NOT NULL,
    PRIMARY KEY (tenant_id, key)
);

-- Hub-projected entitlement cache (plan-level features + limits + compliance caps).
-- One row per tenant. Despite the name this is a DURABLE projection store, not a
-- cache: it is the layer that makes the grace window real. Written only by
-- `IEntitlementProvider.RefreshAsync`, from PUT /api/internal/tenants/{id}/entitlements
-- or from a signed licence key. The `learnstack.hub.entitlement` event is the eager
-- INVALIDATION signal for the L1/L2 caches in front of it, not the write path.
CREATE TABLE platform_entitlement_cache (
    tenant_id        uuid PRIMARY KEY,
    plan_code        varchar(100) NOT NULL, -- wire field `tier`
    features         jsonb NOT NULL,        -- Dictionary<string, bool>
    limits           jsonb NOT NULL,        -- Dictionary<string, long>
    compliance       jsonb NOT NULL,        -- caps, regions, retention overrides
    valid_until      timestamptz NULL,      -- wire field `expires_at`; null = no
                                            -- scheduled expiry
    grace_until      timestamptz NULL,      -- null unless in grace; bounds the window
    generation       bigint NOT NULL DEFAULT 1,  -- monotonic; a push is accepted only
                                            -- when received.generation >= stored
    refreshed_at     timestamptz NOT NULL DEFAULT now(),
    source           text NOT NULL,         -- closed set, bounded by the CHECK below
    CONSTRAINT ck_platform_entitlement_cache_source
        CHECK (source IN ('hub', 'signed-license-key', 'null-provider'))
);
```

The migration in `LearnStack.Modules.Tenancy.Infrastructure` is the source for these
two tables; the fences above are kept in step with it, with one column shown in the
state Packet 9 leaves it — `valid_until`, called out below. The length caps on `key` and
`plan_code` are the migration's, and are a bound the bare `text` this document first
declared did not carry. `source` stays `text` because it is a closed set, and
[Database Standards § Column types](../standards/05-database.md) fixes `text` with a
`CHECK` as the form for those. Row-security clauses are in the migration and
deliberately not restated here.

`valid_until` shipped in Packet 6 as `NOT NULL`, and **Packet 9 alters it to `NULL`**
([ADR-0045 Amendment 1 § 2](../decisions/0045-entitlement-and-feature-flag-socket.md)),
with `PlatformEntitlement.ValidUntil` becoming nullable alongside it. The wire field is
required and nullable, the Hub's DTO carries a nullable value, and the Hub sends null
for every tenant with no scheduled expiry — trials and perpetual licences, the cohort it
creates first. Altering the column is cheap precisely because no row exists yet.

The wire shape and the column names differ in two places, and the mapping is normative:
the projection field `tier` persists to `plan_code`, and `expires_at` persists to
`valid_until`. `grace_until` and `generation` keep their wire names. Both timestamps are
nullable, and they mean different things: a null `grace_until` is a tenant not in grace,
a null `valid_until` is a tenant with no scheduled expiry — never coerced to a far-future
sentinel, which would silently become an expiry somebody has to explain. `generation`
defaults to 1 so the tenant-provisioning insert at `POST /api/internal/tenants` needs no
special case; every later push must carry a value greater than or equal to the stored
one ([ADR-0045 Amendment 1 § 3](../decisions/0045-entitlement-and-feature-flag-socket.md)).
Equality is what makes the provisioning flow work — the provisioned row and the Hub's
first real projection both carry generation 1 — and a replay at the same generation is
idempotent, because the Hub is the single writer and bumps by one per recompute. A
strictly older push changes no column and reports `IgnoredAsStale`, so a reordered
delivery cannot downgrade the row. The wire shape is pinned by
`entitlement-v1.schema.json` in both repositories; the Hub-side rendering is in the
`learnstack-hub` repository's `docs/architecture/entitlement-projection.md`.

Rules:

- `tenant_id = NULL` is **not** allowed, and neither is the platform sentinel:
  `tenant_feature_flags` carries `fk_tenant_feature_flags_tenant FOREIGN KEY (tenant_id)
  REFERENCES tenants (id)`, which a sentinel with no `tenants` row cannot satisfy.
  Platform-wide switches live in `platform_killswitches` instead.
- The Hub is the **owner** of `platform_entitlement_cache`; the LearnStack core only
  reads + invalidates. Writes happen through `IEntitlementProvider.RefreshAsync`
  only, driven by the HTTP push (`PUT /api/internal/tenants/{id}/entitlements`) or by a
  signed licence key. The inbound `learnstack.hub.entitlement` event invalidates the
  L1 / L2 layers; it is not the write path
  ([ADR-0034](../decisions/0034-hub-contract-surface-invariant.md)). See
  [ADR-0021](../decisions/0021-feature-based-entitlement.md) and
  [29-dapr-integration.md](29-dapr-integration.md).
- `ICacheService` fronts `tenant_feature_flags` and the killswitch overlay for hot-path
  reads. Today that is the process-local `InMemoryCacheService`; Phase 11 adds
  Valkey-backed L2 and cross-instance invalidation when ADR-0035's replica trigger fires.
  Caching of the plan half belongs to the `IEntitlementProvider` implementation instead:
  `NullEntitlementProvider` answers from constants and touches no table, and Phase 02c's
  `HubEntitlementProvider` implements the L1 → L2 → durable → Hub order that
  [ADR-0034 § The entitlement read path](../decisions/0034-hub-contract-surface-invariant.md#the-entitlement-read-path)
  declares normative.

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
2. **If the key's catalog descriptor says `Source = PlanProjected`:** resolve through
   `IEntitlementProvider.GetAsync(tenantId)` — never by reading
   `platform_entitlement_cache`, whose storage belongs to the provider that owns it
   ([ADR-0045 § 2](../decisions/0045-entitlement-and-feature-flag-socket.md)). A missing
   entry resolves to the catalog default. Per-tenant `tenant_feature_flags` are **never**
   consulted for plan-projected keys.
3. **If the key's catalog descriptor says `Source = TenantFlag`:** read from
   `tenant_feature_flags` (via `ICacheService` → Postgres). Missing entry → catalog
   default.
4. **Killswitch overlay** (last word): if the key's descriptor names a
   `KillswitchKeys.*` entry and that switch is flipped `false` platform-wide, the answer
   becomes `false` regardless of the per-tenant value. The overlay consults **exactly**
   the named key; a key whose descriptor names none has no overlay, and no `killswitch.`
   prefix is derived from the feature key's own string
   ([ADR-0045 Amendment 1 § 5](../decisions/0045-entitlement-and-feature-flag-socket.md)).
   Killswitches override the projection.
5. Resolution is logged at `Debug` (sampled) with `flag_key`, `tenant_id`, `value`,
   `source` (`plan`, `tenant`, `killswitch`, `default`).

`IFeatureFlags` therefore **composes** rather than queries: it asks the provider for the
plan half, reads `tenant_feature_flags` for the tenant half, applies the overlay, and
caches. Swapping the registered `IEntitlementProvider` changes the answer without
touching module code.

`GetLimitAsync` resolves through the same provider and ignores `tenant_feature_flags` —
limits are always plan-projected. It returns `long`, never `long?`: "not projected" is
not a third state a caller can act on, so an absent key resolves to its catalog
default.

Architecture tests:

- `platform_entitlement_cache` is read **and** written by an `IEntitlementProvider`
  implementation and by nothing else — no module, Tenancy included
  (`Modules_Do_Not_Read_Entitlement_Cache_Directly`). Direct SQL against
  `tenant_feature_flags` outside the Tenancy module's infrastructure is forbidden.
- A key must exist in `FeatureKeys` / `LimitKeys` before `IFeatureFlags` can reference
  it, and in `KillswitchKeys` before a guarded path reads a switch directly
  (compile-time guarantee through the `FeatureKey` / `LimitKey` / `KillswitchKey` value
  objects).
- Tests that depend on a flag use `FeatureFlagsFixture` (overrides), not direct DB
  writes.
- `PlanProjected_Keys_NotInTenantFlags` ensures no plan-projected key has ever been
  written to `tenant_feature_flags`.

## Soft vs Hard Limits

Each `LimitKey` in the catalog declares a `LimitEnforcement` (`Soft` | `Hard`):

- **Hard** — the gated operation refuses with `403 ProblemDetails`
  `type=urn:learnstack:errors:limit-exceeded` when the limit is `> 0` and current usage
  has reached it. `-1` never refuses and `0` refuses outright. Example: `MaxUsers`.
- **Soft** — the operation succeeds; a banner is surfaced and a Hub-side
  `usage.alert.soft_limit_reached` event is emitted to the Hub via
  `POST /api/v1/usage/report`. Example: `ClassroomMinutesPerMonth`.

Packet 9 ships the descriptor and the read; the **enforcement path** — the `403` and the
usage signal — lands in [Phase 02c](../roadmap/phase-02c-hub-foundation.md), the first
phase in which `IUsageReporter` and `POST /api/v1/usage/report` exist for a soft limit to
report to. Each individual gate ships with the feature it gates, never speculatively.

The frontend's `useLimit(key)` hook surfaces both `current` / `limit` and the
enforcement mode so the UI can present the right message.

## Killswitch Pattern

A killswitch is a `KillswitchKeys.*` entry whose default is `true` and that gates an
expensive code path. When triggered, it is flipped to `false` platform-wide. Examples:

- `KillswitchKeys.RecordingEnabled` — flip off during a storage incident. Named by
  `FeatureKeys.ClassroomRecording`'s descriptor, so the overlay applies it.
- `KillswitchKeys.EmailDispatchEnabled` — flip off during an upstream email provider
  outage.
- `KillswitchKeys.AnalyticsIngestEnabled` — flip off when the analytics pipeline is
  back-pressured.

The last two gate a code path that no `FeatureKey` represents — email dispatch and
analytics ingest are not plan-gated capabilities — so no descriptor names them, the
`IFeatureFlags` overlay never fires for them, and the paths they guard read them
directly. A killswitch reaches the four-step precedence only through the descriptor that
names it, which is the point of declaring the reference rather than deriving it.

**A killswitch is not tenant data and does not live in `tenant_feature_flags`**
([ADR-0045 § 5](../decisions/0045-entitlement-and-feature-flag-socket.md)). That table's
`fk_tenant_feature_flags_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id)` cannot
be satisfied by the platform sentinel, which
[ADR-0044](../decisions/0044-audit-write-path.md) keeps out of `tenants` by `CHECK`. A
foreign key is a constraint and not a policy, so neither `learnstack_platform` nor
`BYPASSRLS` moves it.

Killswitches ship instead as `platform_killswitches`, a platform-scoped table Packet 9
adds to the Tenancy migration chain:

```sql
-- One platform-wide switch per key. No tenant column and no foreign key: a killswitch
-- belongs to no tenant, so there is nothing for a tenant predicate to isolate.
CREATE TABLE platform_killswitches (
    key         text PRIMARY KEY,
    is_enabled  boolean NOT NULL,
    reason      text NULL,
    toggled_at  timestamptz NOT NULL,
    toggled_by  uuid NULL
);
```

- **Policies** are role-qualified in the `platform_host_to_tenant` shape, with the read
  widened rather than keyed — a killswitch is global by construction, and hiding it from
  the role that must honour it would only fail open. Every write is reserved to
  `learnstack_platform` through the audited `EnterPlatformAdminScope(reason)` path, which
  is what gives `tenancy.killswitch.toggle` a real actor. The class and its policy DDL
  live in [Database Standards § Table classes](../standards/05-database.md); the clauses
  are deliberately not restated here.
- **Reads go through the L1 cache**, not to the table per request, and are invalidated on
  toggle. Holding the answer needs a second allowed platform cache-key family,
  `platform:tenancy:killswitch`; `CacheKey.EnsureValid` admits that family and the host
  mapping, and nothing else. That one entry holds the whole switch set, so a toggle
  invalidates a single key and there is no prefix sweep to run.
- **A read failure resolves to the key's default** — `true`, the enabled state — and
  logs at `Error`. A cache outage must not disable every gated path platform-wide, and
  the default is per key even though the entry is one.
- **The toggle is eventually consistent** across instances, bounded by the cache TTL and
  the invalidation event. A flip is not instantaneous, which is worth knowing before the
  incident the killswitch exists for.

**Packet 9 ships the table and the read path, and no writer**
([ADR-0045 Amendment 1 § 4](../decisions/0045-entitlement-and-feature-flag-socket.md)).
The reason is reachability rather than scheduling: every killswitch write runs inside
`EnterPlatformAdminScope(reason)`, and the registered `IPlatformAdminGate` is
`DenyAllPlatformAdminGate` until the Platform-scope permission arrives with the registry
in [Phase 03](../roadmap/phase-03-identity-admin.md). A toggle command shipped now would
be unreachable code with a permission key nothing registers. **Phase 03 owns the toggle
command, its permission and its runbook**; every gated read honours a flipped switch the
day one exists, and what is absent until then is the flipping.

A flippable killswitch pairs with a runbook entry describing when to use it and how to
restore, and the obligation attaches to the phase that makes it flippable, not to the
phase that declares the key. The killswitch overlay in `IFeatureFlags` resolution wins
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
  actor and a free-text reason field that the operator console makes required. The
  classification is written ahead of the command: Packet 9 ships no writer, and
  [Phase 03](../roadmap/phase-03-identity-admin.md) lands the toggle that raises the row
  ([ADR-0045 Amendment 1 § 4](../decisions/0045-entitlement-and-feature-flag-socket.md)).
  The operation is not a MediatR request, so its catalogue entry is registered by slug
  ([ADR-0044 Amendment 3 § 1](../decisions/0044-audit-write-path.md)).

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
  hasn't refreshed sees the old feature set. The Phase 02c projection push refreshes
  LearnStack directly; Phase 11's Dapr event becomes an additional eager-invalidation
  path when its trigger fires. The 15-min L2 TTL is the future upper bound.
- **Performance.** Hot paths that read flags per call become DB-bound without the
  cache; the 60s L1 TTL is the default trade-off.

## Roadmap Touchpoints

- **Phase 02a Packet 6** — `tenant_feature_flags` **and** `platform_entitlement_cache`
  created in the Tenancy migration chain, with their policies and grants. Both ship
  there; Packet 9 gives them their reader.
- **Phase 02a Packet 9** — the socket
  ([ADR-0045 § 6](../decisions/0045-entitlement-and-feature-flag-socket.md)):
  `IEntitlementProvider` + `EntitlementProjection` + `NullEntitlementProvider`;
  `IFeatureFlags` and its Tenancy implementation over the `ICacheService`-backed L1
  cache; the `FeatureKeys` / `LimitKeys` / `KillswitchKeys` catalogs, each key carrying
  its `Source`, its default, its failure class, its killswitch reference or none, and —
  for a `LimitKey` — its `LimitEnforcement`
  ([ADR-0045 Amendment 1 § 5](../decisions/0045-entitlement-and-feature-flag-socket.md));
  the `AlterColumn` that makes `valid_until` nullable; `platform_killswitches`, the
  overlay and its cache family, read-only until Phase 03 ships the toggle; the
  architecture tests.
- **Phase 03** — the killswitch toggle command, its Platform-scope permission, the
  permitting `IPlatformAdminGate` and the runbook that pairs with a flippable switch.
- **Phase 02c** (parallel Hub Foundation) — `HubEntitlementProvider` and the HTTPS
  projection push (`PUT /api/internal/tenants/{id}/entitlements`) that drives
  `RefreshAsync`. Limit **enforcement** lands here too — the `403` refusal and the
  `usage.alert.soft_limit_reached` signal — alongside `IUsageReporter` and
  `POST /api/v1/usage/report`. `IEntitlementAdminQuery` ships with the operator surface
  that needs it. `SignedLicenseKeyEntitlementProvider` lands here as a skeleton; its
  operational hardening is [Phase 11](../roadmap/phase-11-production-hardening.md).
- **Phase 06** — Admin Studio surface for editing per-tenant flag overrides and
  viewing the entitlement projection. The Studio screen for `platform_entitlement_cache`
  is **read-only** — actual plan edits happen in the operator portal
  (`operator-portal`).
- **Phase 09** — Audit + observability hooks for both flag writes and entitlement
  refreshes plug into the audit + analytics pipeline.
- **Phase 11** — Valkey/Dapr adapters and event-driven cross-instance invalidation land
  on ADR-0035's triggers; quarterly hygiene review and CI surfacing of stale flags
  become operational.

## References

- [ADR-0045 The Entitlement and Feature-Flag Socket](../decisions/0045-entitlement-and-feature-flag-socket.md)
  — the port, the read interface, the limit sentinel and the killswitch table.
  Amendment 1 (2026-09-08) settles the limit vocabulary, `valid_until`'s nullability,
  the generation guard's equal case, the unwritten killswitch table, and the two members
  every key descriptor carries.
- [ADR-0021 Feature-Based Entitlement Model](../decisions/0021-feature-based-entitlement.md)
  — Amendment 1 (2026-05-18) fixes the typed-registry shape; the 2026-09-08 amendment
  moves `LimitKeys` to the Hub's `limits.` vocabulary.
- [ADR-0019 LearnStack Hub](../decisions/0019-learnstack-hub.md)
- [ADR-0020 Triple Deployment + Hybrid License](../decisions/0020-triple-deployment-hybrid-license.md)
- [ADR-0044 The Audit Write Path](../decisions/0044-audit-write-path.md) — the platform
  sentinel tenant id, and the MUST-class row a `tenancy.killswitch.toggle` writes.
- [24-learnstack-hub.md](24-learnstack-hub.md) — Hub plan editor and the source of the
  entitlement projection.
- [25-deployment-models.md](25-deployment-models.md) — how each deployment mode loads
  the projection.
- [29-dapr-integration.md](29-dapr-integration.md) — `IEventBus` / `ICacheService` /
  `ISecretProvider` wiring.
