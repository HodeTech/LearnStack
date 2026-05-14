# Feature Flags

LearnStack uses tenant-scoped feature flags to roll out capabilities gradually, gate vertical-only features, and let tenants opt into experimental functionality. This document defines the flag catalog, runtime evaluation, lifecycle, and rules that prevent flags from becoming permanent technical debt.

## Scope

Feature flags are used for:

- **Tenant enablement of optional core capabilities** (e.g., enable in-app recording, enable AI pronunciation feedback when it ships).
- **Vertical enablement per tenant** (e.g., enable the English Learning vertical for tenant A but not tenant B).
- **Gradual rollout of new code paths** (e.g., new lesson player UI behind a flag while it is validated on a few tenants).
- **Killswitches for expensive paths** (e.g., disable recording globally during a cost incident).

Out of scope:

- **A/B experimentation** at the learner level — that is an analytics concern and lives in a separate `Experiment` aggregate (post-MVP).
- **Per-request feature toggling via headers** — flags resolve once per request context; ad-hoc overrides are platform-admin only.
- **Branch-by-feature for incomplete work** — a flag is for a feature the team has decided to ship; partially-built code lives behind branches, not flags.

## Catalog

The flag catalog is **code-defined**, **typed**, and **enumerated in one place**. Free-form string flag keys are forbidden.

```csharp
public static class FeatureFlags
{
    public static readonly FlagKey<bool> RecordingEnabled =
        new("classroom.recording.enabled", default: false);

    public static readonly FlagKey<bool> EnglishVerticalEnabled =
        new("vertical.english.enabled", default: false);

    public static readonly FlagKey<int> MaxConcurrentLiveSessions =
        new("classroom.max_concurrent_sessions", default: 100);

    public static readonly FlagKey<string> SearchProvider =
        new("platform.search.provider", default: "meilisearch");
}
```

Rules:

- Flag keys are dotted: `{scope}.{feature}.{name}`.
- Flag default values are part of the code; a missing tenant entry resolves to the default.
- Adding a flag requires an ADR-style note in the flag catalog file (link to the ADR for the decision the flag gates).
- Removing a flag follows a deprecation cycle (one release with a warning log when read; removal in the next major).

## Storage

A single global table `tenant_feature_flags` lives in the Tenancy module:

```sql
CREATE TABLE tenant_feature_flags (
    tenant_id   uuid NOT NULL,
    key         text NOT NULL,
    value       jsonb NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  uuid NOT NULL,
    PRIMARY KEY (tenant_id, key)
);
```

Rules:

- `tenant_id = NULL` is **not** allowed; platform-wide flags use a sentinel "platform" tenant id, never `NULL`.
- `value` is JSONB so booleans, integers, strings, and small JSON payloads share one schema.
- A read fan-out cache (Redis) holds resolved flag sets per tenant with a 60s TTL; invalidation is event-driven.

## Evaluation

`IFeatureFlags` is the only sanctioned read path:

```csharp
public interface IFeatureFlags
{
    Task<T> ResolveAsync<T>(FlagKey<T> key, CancellationToken ct);
}
```

Inside, the helper:

1. Resolves the current tenant from `ITenantContext`. No tenant → throws (`TenantContextMissingException`).
2. Reads from the Redis cache, falling back to PostgreSQL.
3. Falls back to the catalog default if the tenant has no override.
4. Logs the resolution at `Debug` (sampled) with `flag_key`, `tenant_id`, `value`, `source` (`tenant`, `default`).

Architecture tests:

- Direct SQL reads against `tenant_feature_flags` outside the Tenancy module are forbidden.
- A flag must exist in the catalog before `ResolveAsync` can reference it (compile-time guarantee through `FlagKey<T>`).
- Tests that depend on a flag use a test-only fixture (`FeatureFlagsFixture`) that surfaces overrides; they never mutate the catalog default.

## Vertical Enablement

Each vertical extension exposes a feature flag at the catalog: `vertical.<key>.enabled`. The platform's extension dispatcher checks the flag at handler resolution; a disabled vertical's handlers do not run for that tenant.

Page-block resolution follows the same rule: a block registered by a disabled vertical renders as the `UnknownBlock` placeholder ([17-page-builder.md](17-page-builder.md)).

## Audit

Flag changes are MUST-audit security-events (see [18-audit-coverage.md](../standards/18-audit-coverage.md)):

- `tenancy.feature_flag.write` permission required.
- Audit entry includes `before` and `after` values.
- Tenant admins see their own tenant's flag history; platform admins see cross-tenant.

## Killswitch Pattern

A killswitch is a flag whose default is `true` and that gates an expensive code path. When triggered, it is flipped to `false` for an explicit tenant or platform-wide via the sentinel tenant id. Examples:

- `classroom.recording.enabled` — flip off during a storage incident.
- `notifications.email.enabled` — flip off during an upstream email provider outage.
- `analytics.ingest.enabled` — flip off when the analytics pipeline is back-pressured.

A killswitch always pairs with a runbook entry in `docs/runbooks/` describing when to use it and how to restore.

## Lifecycle and Hygiene

Flags accumulate. The team reviews the catalog quarterly:

- **Permanent flags** (e.g., `vertical.english.enabled`) — stay forever.
- **Rollout flags** — once 100% of tenants are flipped on for one release, the flag is removed in the next release. CI surfaces flags that have been at 100% for > 90 days as candidates.
- **Killswitches** — stay forever, but their runbooks must remain accurate; runbook freshness is part of the quarterly review.

A flag that has been at its default for > 1 year with no tenant overrides is flagged for removal.

## Risks

- **Permanent rollout flags.** Treated as drift. The quarterly review is the discipline.
- **Branching on flag identity instead of capability.** Code that reads `if (locale === "tr")` is bad ([08-localization.md](../standards/08-localization.md)); code that reads `if (FeatureFlags.SomeArbitraryName)` for unrelated branching is the same bug. Flags gate features; if you find yourself branching on multiple unrelated flags in one function, refactor.
- **Flag drift between code and database.** A flag whose key is renamed in code but not migrated in the DB silently returns the default for every tenant. Renaming is a deprecation cycle, not a refactor.
- **Performance.** Hot paths that read flags per call become DB-bound without the Redis cache; the 60s TTL is the default trade-off.
