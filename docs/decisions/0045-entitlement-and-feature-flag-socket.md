# ADR-0045: The Entitlement and Feature-Flag Socket

## Status

Accepted (**Amendment 1: 2026-09-08** — read against the Hub repository's merged code:
the limit-key vocabulary is the Hub's, `expires_at` and `valid_until` are nullable, the
generation guard admits the equal case, `platform_killswitches` ships **unwritten**, and
every key descriptor carries its fail-open/fail-closed class and its killswitch by name.)

**Date:** 2026-09-07
**Deciders:** @platform
**Accepted:** 2026-09-07

## Decision Drivers

- **A Phase 02a completion criterion names a type no packet ships.**
  [Phase 02a § Completion Criteria](../roadmap/phase-02a-kernel-tenancy.md) requires
  that "`IFeatureFlags.IsEnabledAsync(FeatureKey)` reads the `NullEntitlementProvider`'s
  default of *all enabled*", and
  [Feature Flags § Roadmap Touchpoints](../architecture/21-feature-flags.md) assigns
  `IFeatureFlags`, the three typed key catalogs and the `ICacheService`-backed L1 cache
  to Phase 02a explicitly. Packet 9's scope paragraph names only `IEntitlementProvider`
  and `NullEntitlementProvider`, and Packet 10 is architecture tests and phase exit. The
  gap is owned by nobody.
- **The port has no member list anywhere in the corpus.** `IEntitlementProvider` is
  named in twenty-odd documents and declared in none. The two shipped call-site claims
  disagree: `GetAsync(tenantId)` in [ADR-0019](0019-learnstack-hub.md), and a
  context-resolved read in [ADR-0003](0003-tenant-isolation-defense-in-depth.md) and
  [Database Standards](../standards/05-database.md). Phase 02c writes the Hub-backed
  implementation against whatever Packet 9 declares, from the other repository.
- **"Unlimited" is encoded two incompatible ways in two Accepted documents.**
  [ADR-0021](0021-feature-based-entitlement.md) says `-1` is unlimited and `0` is "feature
  off / not available"; [Feature Flags § Rules](../architecture/21-feature-flags.md) says
  the default `0` means "no limit imposed by this layer". Read together, ADR-0021's own
  degraded mode — "past `grace_until`, limits return `0` → read-only" — grants
  **unlimited** usage. The value lands in the persisted `jsonb`, in the Hub repository's
  plan schema, and at every call site.
- **The read interface has three names and two shapes.** ADR-0021's Decision names
  `IFeatureFlagService` and `IUsageLimitService` with `string` keys and `long?`; its own
  Amendment 1 replaces both with a single `IFeatureFlags` taking typed `FeatureKey` /
  `LimitKey` and returning `long`. The superseded names still appear in the Decision
  section, which is what an implementer reads first.
- **Two shipped tables have no legal reader.** `platform_entitlement_cache` and
  `tenant_feature_flags` landed in Packet 6 with RLS and grants and have been unread
  since. `Modules_Do_Not_Read_Entitlement_Cache_Directly` — a Packet 10 rule — is
  vacuous until something is allowed to read them.
- **The killswitch, as designed, cannot be written and cannot be read.** It is stored
  "for the sentinel platform tenant" in `tenant_feature_flags` — a table the Packet 6
  migration binds with `fk_tenant_feature_flags_tenant FOREIGN KEY (tenant_id)
  REFERENCES tenants (id)`, so a sentinel that is deliberately absent from `tenants`
  cannot satisfy it, and no role or `BYPASSRLS` attribute changes that because a foreign
  key is a constraint rather than a policy. Even granting the row existed, that table's
  policy is `tenant_id = app.tenant_id`, so a request serving tenant A could not read it.
  And `CacheKey.EnsureValid` admits exactly one platform key family today — the host
  mapping — so there is no legal cache key to hold the answer in either.

## Considered Options

1. **Ship the whole socket in Packet 9** (chosen): the port, the null implementation,
   `IFeatureFlags` composing over it, the three typed catalogs, and the killswitch
   overlay on a platform-scoped table of its own.
2. **Ship only `IEntitlementProvider` + `NullEntitlementProvider`** (rejected). The
   roadmap paragraph's literal scope. It leaves the Phase 02a completion criterion
   unsatisfiable, keeps two shipped tables unread and one Packet 10 rule vacuous, and
   defers the wire contract that Phase 02c — in a different repository — has to build
   against. The one-way-door test answers it: `IFeatureFlags`'s precedence and the limit
   encoding are read by every future gated feature, and adding them later means touching
   code already written.
3. **Ship `IFeatureFlags` only, and let Phase 02c introduce the provider** (rejected).
   Under § 2 a plan-projected key resolves *through* `IEntitlementProvider`, so an
   `IFeatureFlags` without one has nothing to ask and no answer to give — and the
   completion criterion it exists to satisfy is precisely that swapping the provider
   changes the answer. The two are one decision.

## Decision

### 1. `IEntitlementProvider` — the projection source

```csharp
namespace LearnStack.SharedKernel.Entitlements;

public interface IEntitlementProvider
{
    Task<EntitlementProjection> GetAsync(TenantId tenantId, CancellationToken ct = default);
    Task<EntitlementRefreshOutcome> RefreshAsync(
        EntitlementProjection projection, CancellationToken ct = default);
}

public sealed record EntitlementProjection(
    TenantId TenantId,
    string PlanCode,                                     // wire: `tier`
    IReadOnlyDictionary<string, bool> Features,
    IReadOnlyDictionary<string, long> Limits,
    ComplianceCaps Compliance,
    DateTimeOffset? ExpiresAt,                           // column: `valid_until`
    DateTimeOffset? GraceUntil,
    long Generation);

public sealed record ComplianceCaps(IReadOnlyDictionary<string, ComplianceCap> Caps);

public sealed record ComplianceCap(bool Allowed, bool Forced, string? Value);

public enum EntitlementRefreshOutcome { Applied, IgnoredAsStale }
```

**`Compliance` and `Generation` are carried because the contract requires them.**
`entitlement-v1.schema.json` — the wire shape
[ADR-0034](0034-hub-contract-surface-invariant.md) pins in **both** repositories — lists
`tenant_id, tier, features, limits, compliance, expires_at, grace_until, generation` as
required, and `platform_entitlement_cache` already carries a `compliance jsonb NOT NULL`
column and a `generation bigint NOT NULL` column, shipped in Packet 6. A record that
dropped either would make the only sanctioned writer structurally unable to persist a
column its own table declares `NOT NULL`, and would silently discard the compliance caps
that gate data residency and recording consent. The two field names that differ from the
wire — `PlanCode` for `tier`, `ExpiresAt` for the `valid_until` column — follow the
shipped `PlatformEntitlement` entity and the mapping
[the glossary](../glossary.md) already records.

**`RefreshAsync` is generation-guarded and atomic.** `generation` is monotonic per
tenant, and pushes can arrive out of order — a retried delivery, two operator edits in
flight, a Hub redeploy. The write compares against the stored generation **inside** the
same statement that performs it (`… WHERE platform_entitlement_cache.generation <
@generation`), never as a read-then-write, and reports `IgnoredAsStale` when the
incoming projection is not newer. A stale push must not resurrect a revoked plan. The
binding case: after generation 42 is applied, a push carrying generation 41 leaves every
column unchanged and returns `IgnoredAsStale`.

`GetAsync` takes the tenant **explicitly**. The two readings in the corpus are not
equivalent: `RefreshAsync` is driven by `PUT /api/internal/tenants/{id}/entitlements`,
which carries a tenant in its path and runs with no tenant context of its own, so a
context-resolved port could not serve its own writer. The context-resolved read that
ADR-0003 and Database Standards describe is `IFeatureFlags`'s behaviour, one level up,
and it stays exactly as they describe.

**The projection is push-primary, and that is not the same as push-only.**
`RefreshAsync` takes a pushed projection because the Hub pushes it; but
[ADR-0034 § The entitlement read path](0034-hub-contract-surface-invariant.md) makes a
demand-driven `POST /api/v1/internal/license/verify` the **last** step of a normative
resolution order — L1, then `ICacheService`, then `platform_entitlement_cache` honouring
its grace window, then the Hub — and that fallback is preserved here, unchanged, for
`HubEntitlementProvider` to implement in Phase 02c. What LearnStack does not do is
**poll**. The absolute prohibition on depending upon a reachable Hub belongs to *host
resolution* (`IHostToTenantResolver` reads `platform_host_to_tenant` and nothing else);
borrowing that sentence for entitlement, as an earlier draft of this ADR did, would have
deleted a fallback ADR-0034 calls normative.

### 2. `IFeatureFlags` — the only module-facing read

```csharp
namespace LearnStack.SharedKernel.Entitlements;

public interface IFeatureFlags
{
    Task<bool> IsEnabledAsync(FeatureKey key, CancellationToken ct = default);
    Task<long> GetLimitAsync(LimitKey key, CancellationToken ct = default);
}
```

Exactly as [ADR-0021 Amendment 1](0021-feature-based-entitlement.md) fixed it. The
`IFeatureFlagService` / `IUsageLimitService` pair in that ADR's § Runtime service
contracts is **withdrawn**, not renamed; ADR-0021 gains a dated Amendment saying so, so
the superseded shape stops being the first thing a reader meets.

Resolution precedence keeps
[Feature Flags § Evaluation](../architecture/21-feature-flags.md)'s order and corrects
its second step:

1. Resolve the tenant from `ITenantContext`; no tenant throws
   `TenantContextMissingException`.
2. A **plan-projected** key resolves through **`IEntitlementProvider.GetAsync`** — not
   by reading `platform_entitlement_cache`.
3. A **tenant-flag** key reads `tenant_feature_flags`. Never the other way: a
   plan-projected key is never served from the tenant table, and
   `PlanProjected_Keys_NotInTenantFlags` is the guard.
4. The killswitch overlay is applied last and wins over both.

**Step 2 is the correction, and it is what the completion criterion actually asks for.**
Phase 02a requires that "swapping the registered `IEntitlementProvider` implementation
changes the answer without touching module code". A direct table read cannot satisfy
that: the registered provider would be bypassed, `NullEntitlementProvider`'s "all
enabled" would never be consulted, and the L1 → L2 → durable → Hub order that
[ADR-0034](0034-hub-contract-surface-invariant.md) declares normative — including the
grace window and the fail-open/fail-closed decision per key class — would be evaluated
by nobody. The projection's storage belongs to the provider that owns it: today
`NullEntitlementProvider` answers from constants and touches no table, and in Phase 02c
`HubEntitlementProvider` implements that whole order.

`IFeatureFlags` therefore composes rather than queries: it asks the provider for the
plan half, reads `tenant_feature_flags` for the tenant half, applies the overlay, and
caches. Its implementation lives in **`LearnStack.Modules.Tenancy.Infrastructure`**,
because `tenant_feature_flags` is that module's table, and it is registered at the
composition root.

`Modules_Do_Not_Read_Entitlement_Cache_Directly` gains its subject from the other side:
the only sanctioned reader **and** writer of `platform_entitlement_cache` is an
`IEntitlementProvider` implementation, and no module — Tenancy included — may query it.
[Feature Flags § Rules](../architecture/21-feature-flags.md)'s bullet forbidding direct
SQL "outside the Tenancy module's infrastructure" is corrected to name the provider
instead, because after this decision Tenancy's infrastructure is not a sanctioned reader
of that table either.

### 3. Limits: `-1` is unlimited, `0` is denied

The sentinel table is normative:

| Value | Meaning |
|---|---|
| `-1` | Unlimited |
| `0` | Denied — the plan grants no allowance at all |
| `> 0` | The allowance |

`GetLimitAsync` returns `long`, never `long?`: "not projected" is not a third state a
caller can act on, and a nullable return would push a `?? what` decision to every call
site. A key absent from the projection resolves to its **catalog default**, which each
`LimitKey` declares.

`NullEntitlementProvider` therefore returns `true` for every feature and **`-1`** for
every limit. ADR-0021's "all limits return `null`" and
[Feature Flags § Rules](../architecture/21-feature-flags.md)'s "default `0` means no
limit imposed by this layer" are both corrected — the second is the inversion that would
have made the degraded read-only mode grant unlimited usage.

### 4. `NullEntitlementProvider` is registered in every deployment mode

Not `Development` only. [ADR-0035](0035-demand-gated-infrastructure.md)'s table names it
the **working default implementation** for the `IEntitlementProvider` gate, with Phase
02c as the owning phase and "a tenant must be billed or plan-gated" as the trigger —
which is the whole shape of demand-gating, and is a later decision than ADR-0020's
mode switch. Until that trigger fires there is no billing to enforce and no Hub to ask,
so a mode-conditional registration would only make four of the five modes unbootable for
a capability none of them yet uses.

ADR-0020 gains a dated Amendment recording that generalisation. Its
`IEntitlementProvider_Implementations_Are_Three` rule is unaffected and stays a Phase
02c/Phase 11 obligation: one implementation exists today, and the rule bounds the ceiling
rather than requiring the count.

### 5. The killswitch overlay is an out-of-band projection

**A killswitch is not tenant data and does not live in `tenant_feature_flags`.**
[Feature Flags § Killswitch Pattern](../architecture/21-feature-flags.md) says it is
"flipped to `false` for the sentinel platform tenant", and that is unimplementable
against the shipped schema: the Packet 6 migration puts
`fk_tenant_feature_flags_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id)` on that
table, and a sentinel with no `tenants` row — which
[ADR-0044](0044-audit-write-path.md) § 1 guarantees by CHECK — fails it. A foreign key
admits no exception: not for `learnstack_platform`, and not under `BYPASSRLS`, because it
is a constraint and not a policy. Provisioning a real `tenants` row for the sentinel to
satisfy it would make the platform enumerable as a tenant, joinable from every
tenant-owned table, and countable in every operator screen — a far larger change than the
one it is meant to enable.

Killswitches ship instead as **`platform_killswitches`**, a new **platform-scoped** table
in the Tenancy migration chain: `key text PRIMARY KEY`, `is_enabled boolean NOT NULL`,
`reason text NULL`, `toggled_at timestamptz NOT NULL`, `toggled_by uuid NULL`. It carries
no `tenant_id`, no foreign key, and no tenant semantics, because a killswitch has none —
it is one platform-wide switch per key.

[Database Standards § Table classes](../standards/05-database.md) says a second
platform-scoped table "is a decision, not a convenience", so this is that decision, and
the class is a fit for the stated reason rather than by analogy: the rows belong to no
tenant, so there is nothing for a tenant predicate to isolate. Policies are
role-qualified exactly as `platform_host_to_tenant`'s are — `ENABLE` **and** `FORCE`;
`FOR SELECT TO learnstack_app USING (true)`, since a killswitch is global by
construction and hiding it from the role that must honour it would only fail open; every
write reserved to `learnstack_platform` through the audited
`EnterPlatformAdminScope(reason)` path, which is what makes `tenancy.killswitch.toggle`
a MUST-class row with a real actor.

The overlay is read through the **L1 cache**, not per request from the table, and
invalidated on toggle. Holding it needs a **second** allowed platform key family in
`CacheKey`: `platform:tenancy:killswitch`. `EnsureValid` admits exactly one family today
— the host mapping — and widening a closed guard is a decision rather than an edit; this
ADR is where it is made, and the family is enumerated, not opened.

A killswitch read failure resolves to the key's default (`true`, the enabled state) and
is logged at `Error`. A cache outage must not disable every gated path platform-wide.

### 6. What Packet 9 ships, and what it does not

Ships: `IEntitlementProvider` + `EntitlementProjection` + `NullEntitlementProvider`;
`IFeatureFlags` + its Tenancy implementation + the L1 cache; `FeatureKey` / `LimitKey`
value objects; `FeatureKeys`, `LimitKeys` and `KillswitchKeys` carrying **only the keys
the corpus already names**, each with its `Source` descriptor, its default, and — for a
`LimitKey` — its `LimitEnforcement` (`Soft` | `Hard`); `platform_killswitches`, the
overlay and its cache family.

Does not ship, each with an owner: `HubEntitlementProvider`
([Phase 02c](../roadmap/phase-02c-hub-foundation.md));
`SignedLicenseKeyEntitlementProvider` (skeleton in Phase 02c, hardened in
[Phase 11](../roadmap/phase-11-production-hardening.md));
`IEntitlementAdminQuery`, the cross-tenant operator read (Phase 02c, with the operator
surface that needs it); the `entitlement-v1.schema.json` snapshot test, which
[Phase 02c](../roadmap/phase-02c-hub-foundation.md) already lists as a deliverable in
both repositories; the Studio editor
([Phase 06](../roadmap/phase-06-renderer-admin-studio.md)).

**Limit enforcement is demand-gated, and Phase 09b is not its owner.** An earlier draft
of this ADR assigned soft/hard enforcement and the usage probe to
[Phase 09b](../roadmap/phase-09b-hub-billing.md); that phase is a pointer into the Hub
repository whose LearnStack-side scope is "deliberately almost nothing", consumes the
`IUsageReporter` stream that ships in Phase 02c, and states as an exit condition that
LearnStack does not change. Under [ADR-0035](0035-demand-gated-infrastructure.md)'s four
requirements the honest assignment is: the **port** is `IFeatureFlags.GetLimitAsync`,
shipped here; the **default** is the catalog default every key declares, shipped here;
the **owning phase** for the enforcement path — the `403` refusal and the
`usage.alert.soft_limit_reached` signal — is **Phase 02c**, which is where
`IUsageReporter` and `POST /api/v1/usage/report` land and therefore the first phase in
which a soft limit has anywhere to report to; and the **trigger** is the first limit key
with a consumer that can exceed it. Each individual gate ships with the feature it
gates, never speculatively. Phase 02c's deliverables gain that line.

No key is invented for this packet. A registry that lists a capability nothing gates is
a list that will be wrong before anything reads it.

## Context

### Why the provider takes a tenant and the flags interface does not

They serve different callers. `IEntitlementProvider` is an adapter over a control-plane
projection: its writer is an internal endpoint carrying a tenant in its path, and its
future Hub implementation is the only type allowed to hold a Hub client. `IFeatureFlags`
is what module code calls, and module code must never be able to name another tenant —
so it resolves from `ITenantContext` and throws when there is none. Collapsing them into
one context-resolved interface would leave `RefreshAsync` unable to serve the request
that drives it; collapsing them into one tenant-taking interface would hand every handler
a cross-tenant read.

### Why `0` could not stay "no limit"

Both readings are defensible in isolation; they are not defensible together, and the
corpus contains both. The tiebreaker is what already exists outside this repository: the
Hub's plan payload, the licence payload in
[Hybrid License Model](../architecture/26-hybrid-license-model.md), and ADR-0021's own
plan editor all write `-1` for unlimited. `0` as "no limit" appears in one bullet of one
architecture document. Choosing the majority costs one corrected bullet; choosing the
minority costs a schema change in another repository and silently inverts a degraded
mode that is meant to restrict.

### What would change our minds

If the Hub's plan model ever needs "unlimited" and "unspecified" as distinct states, the
return type becomes `long?` and this decision is revisited **with** that need rather than
in anticipation of it. Nothing in the corpus asks for the distinction today.

## Consequences

### Positive

- The Phase 02a completion criterion becomes satisfiable by the packet that owns it, and
  Phase 02a can exit on its own written terms.
- Two tables that have been unread since Packet 6 get their reader, and a Packet 10
  architecture rule gets a subject.
- Phase 02c's cross-repository work starts against a declared contract instead of
  inferring one from twenty prose mentions — one that carries every field
  `entitlement-v1.schema.json` requires, so the snapshot test that phase owes can pass
  in both repositories.
- A stale entitlement push cannot resurrect a revoked plan.
- The degraded read-only mode restricts, which is what it was written to do.
- The killswitch becomes writable at all. As designed it was blocked by a foreign key,
  which no amount of role privilege would have moved.

### Negative

- **Packet 9 grows by roughly a step.** The socket is no longer one interface and one
  null class; it is the read path, the catalogs, a cache family and a small table.
- **A second platform-scoped table.** Database Standards closes that class at one and
  says a second is a decision. This is the decision, and it is the shape of the thing
  rather than a convenience: `platform_killswitches` has no tenant to isolate.
- **`CacheKey`'s closed platform family opens to two.** A guard that exists to stop
  cross-tenant cache collisions is widened, deliberately and by enumeration. Every future
  family is another decision.
- **The killswitch is eventually consistent** with respect to its own toggle, bounded by
  the cache TTL and the invalidation event. A flip is not instantaneous across instances,
  which is stated here rather than discovered during the incident the killswitch exists
  for.

### Neutral

- ADR-0021's Decision — feature-based entitlement, plan-tier projection, eager
  invalidation — is untouched. This ADR fixes the contract it left as prose.

## Implementation Notes

- **Dated Amendments owed by this ADR:**
  - [ADR-0021](0021-feature-based-entitlement.md) **Amendment 2** — the `-1` / `0`
    sentinel table is normative; `IFeatureFlagService` and `IUsageLimitService` are
    withdrawn in favour of `IFeatureFlags`; `NullEntitlementProvider` returns `-1`, not
    `null`.
  - [ADR-0020](0020-triple-deployment-hybrid-license.md) **Amendment** —
    `NullEntitlementProvider` is the registered default in **every** deployment mode
    until ADR-0035's Phase 02c trigger fires, not in `Development` only.
- **Architecture tests** (canonical names in
  [the catalogue](../standards/21-architecture-tests-catalogue.md)):
  `Modules_Do_Not_Read_Entitlement_Cache_Directly`,
  `FeatureKey_AllReferences_AreInRegistry`,
  `PlanProjected_Keys_NotInTenantFlags`,
  `IEntitlementProvider_Implementations_Are_Three` (bounds the ceiling; one
  implementation today).
- **Carriers this decision changes:**
  [Feature Flags](../architecture/21-feature-flags.md) (§ Rules, § Evaluation,
  § Killswitch Pattern, § Roadmap Touchpoints),
  [Hybrid License Model](../architecture/26-hybrid-license-model.md),
  [Database Standards](../standards/05-database.md) (§ Table classes and the GRANT
  matrix gain `platform_killswitches`),
  [Infrastructure Stack Standards](../standards/20-infrastructure-stack.md),
  [the glossary](../glossary.md),
  [Phase 02a](../roadmap/phase-02a-kernel-tenancy.md), and
  [Phase 02c](../roadmap/phase-02c-hub-foundation.md), whose deliverables gain the limit
  enforcement path alongside `IUsageReporter`.


## Amendment 1 — What the other repository already shipped (2026-09-08)

**Status: Accepted.** Raised by the cross-corpus review, which read this ADR against the
Hub repository's merged code rather than only against LearnStack's documents. Four of the
five items below are cases where LearnStack's corpus and the Hub's *implementation*
disagree, and the Hub is the side that has shipped. **§ Decision is unchanged.**

### 1. The limit-key vocabulary is the Hub's

§ 6 tells Packet 9 to ship `LimitKeys` "carrying only the keys the corpus already names".
Measured, the intersection between what this corpus names and what the Hub sends is
**empty**: LearnStack's four documents name `tenancy.max_learners`,
`classroom.minutes_per_month`, `media.storage_gb` and four more; the Hub's
`LearnStack.Hub.SharedKernel/FeatureFlags/LimitKeys.cs` names nine keys under a `limits.`
prefix — `limits.max_users`, `limits.max_organizations`,
`limits.classroom_minutes_per_month`, and six others — and its plan validators reject a
plan whose limits are not from that set.

**LearnStack adopts the Hub's spelling.** Not because it is better: because the Hub has
merged code, a plan editor and two validators built on it, and LearnStack has a
declaration in four documents and no implementing line. Freezing our spelling would make
`GetLimitAsync` miss on every real projection and fall through to the catalog default —
a paid tenant silently reading its plan as absent, which is the failure mode hardest to
see from inside.

The same check on **feature** keys found a narrower gap and it is left as it is: the two
sides agree on most, and Packet 9 ships only keys with a consumer.

### 2. `expires_at` is nullable, and so is `valid_until`

§ 1 declares `DateTimeOffset? ExpiresAt` against a shipped `valid_until timestamptz NOT
NULL`. The pinned wire schema makes `expires_at` required **and** nullable, the Hub's DTO
carries `DateTimeOffset?`, and the Hub sends `null` for every tenant with no scheduled
expiry — trials and perpetual licences, which is the cohort it creates first.

A null `expires_at` means "no scheduled expiry". It persists as `valid_until NULL` and is
never coerced to a sentinel, because a far-future date would silently become an expiry
somebody eventually has to explain. **Packet 9 alters the column to `NULL` on the Tenancy
chain** and makes `PlatformEntitlement.ValidUntil` nullable with it. This is a change to a
table Packet 6 shipped, and it is cheap now precisely because no row exists.

### 3. The generation guard admits the equal case

§ 1 fixes the guard as `… WHERE generation < @generation`. Four other places — including
the shipped entity's own remarks, the Hub's architecture document and the Hub's delivery
doc — say a push applies when its generation is **at least** the stored one. The
difference is only the equal case, and the equal case is the provisioning flow: the
provisioning insert writes `generation` **default 1**, and the Hub's first real projection
for that tenant also carries 1. Under strict `>` that projection is discarded and the
tenant keeps an empty row while `RefreshAsync` reports `IgnoredAsStale` — a paid tenant
reading as unentitled, reported as success.

Read as `>=`: a push applies when its generation is greater than or equal to the stored
one. Replay at the same generation is idempotent, which is what the Hub's own reasoning
assumes.

### 4. `platform_killswitches` ships unwritten, and says so

§ 5 gives the table, the policies, the overlay and the cache family, and no document names
the command, endpoint or operator surface that flips a switch. Packet 9 ships the table
and the **read** path; it ships no writer, and the corpus stops claiming one.

The reason is not scheduling, it is reachability: every killswitch write runs inside
`EnterPlatformAdminScope(reason)`, and the registered `IPlatformAdminGate` is
`DenyAllPlatformAdminGate` — nothing can enter that scope until the Platform-scope
permission arrives with the registry in
[Phase 03](../roadmap/phase-03-identity-admin.md). A toggle command shipped now would be
unreachable code with a permission key nothing registers.

`tenancy.killswitch.toggle` therefore carries `(planned)` in the Tenancy matrix under
[ADR-0044 Amendment 3](0044-audit-write-path.md), and **Phase 03** owns the toggle command,
its permission and its runbook. Every gated read still honours a flipped switch the day
one exists; what is absent is the flipping.

### 5. Two things every key descriptor carries

- **Fail-open or fail-closed.** [ADR-0034](0034-hub-contract-surface-invariant.md)
  § The entitlement read path requires that "each feature key class declares fail-open or
  fail-closed explicitly". § 6's descriptor list omitted it. Every `FeatureKey` declares
  it, and it is what the provider's degraded path reads when the projection is unavailable
  past its grace window.
- **Its killswitch, by name.** § 2's precedence applies "the corresponding killswitch"
  and nothing said how a `FeatureKey` corresponds to one. The correspondence is declared
  on the feature key's descriptor — a nullable `KillswitchKey` — not inferred from the
  string, because inference would make a renamed key silently ungated.

### Carriers changed

[Feature Flags](../architecture/21-feature-flags.md),
[Hybrid License Model](../architecture/26-hybrid-license-model.md),
[ADR-0021](0021-feature-based-entitlement.md) (its own dated amendment, for the registry
its 2026-05-18 amendment fixed),
[Database Standards](../standards/05-database.md),
[Phase 02a](../roadmap/phase-02a-kernel-tenancy.md),
[Phase 02c](../roadmap/phase-02c-hub-foundation.md),
[the glossary](../glossary.md) and `.claude/skills/add-feature-key/SKILL.md`. The Hub
repository changes nothing.

## References

- [ADR-0021 Feature-Based Entitlement Model](0021-feature-based-entitlement.md)
- [ADR-0019 LearnStack Hub](0019-learnstack-hub.md)
- [ADR-0020 Triple Deployment + Hybrid License](0020-triple-deployment-hybrid-license.md)
- [ADR-0034 Hub Contract Surface Invariant](0034-hub-contract-surface-invariant.md)
- [ADR-0035 Demand-Gated Infrastructure](0035-demand-gated-infrastructure.md)
- [ADR-0044 The Audit Write Path](0044-audit-write-path.md) — the platform sentinel
  tenant id, whose `tenants` CHECK is why killswitches cannot live in
  `tenant_feature_flags`; and the MUST-class row a `tenancy.killswitch.toggle` writes
- [Feature Flags & Entitlements](../architecture/21-feature-flags.md)
- [Infrastructure Stack Standards](../standards/20-infrastructure-stack.md)
