# ADR-0068: Development Warm-Realm Qualification

## Status

Proposed — requires explicit maintainer approval before dependent runtime changes.
Existing visibility and account-scope guards remain authoritative until acceptance.

**Date:** 2026-10-22
**Deciders:** @cemil

Acceptance, implementation and final-source automatic-path proof remain pending.
This proposal replaces bounded parts of two Accepted P02b-6 clauses; it does not
supersede ADR-0004, ADR-0064 or ADR-0067,
correct their history or backdate new mutation authority.

## Decision Drivers

- The historical realm export leaves profile and built-in console bootstrap to
  Keycloak. Its stock profile hides stored identity attributes; stock console
  assignments also exceed the later approved narrow account capability.
- Treating hidden values as absent can overwrite an actor. Treating scope names
  or assignment summaries as complete definitions can misclassify customization.
- Warm upgrade must retain provider IDs, credentials and unrelated state while
  converging the intended development console configuration.
- Profile preparation precedes complete identity qualification. Scope detachment
  changes a shared client. Both costs need explicit authority and independent proof.

## Considered Options

1. **Bounded preparation and qualified stock-console normalization** (proposed
   choice). Reveal only missing protected definitions, then validate a complete
   fresh plan before detaching only the three incompatible stock console edges.
2. **Manual preparation and operator normalization** (viable alternative). Retain
   existing refusals; an administrator appends the definitions and reconciles the
   console assignments before rerunning. This preserves current tooling authority
   but makes this stock warm upgrade an explicit manual prerequisite.
3. **Preserve the broad stock console assignments** (rejected). Retains broader
   scope allowances and role emitters that violate the accepted narrow capability.
4. **Enable unmanaged attributes globally** (rejected). Changes unrelated profile
   visibility or editability beyond the three protected definitions.
5. **Replay/reset the realm or drop the guards** (rejected). Reset risks account
   preservation; ignoring visibility or customization restores unsafe adoption.

## Decision

1. **Identity visibility preparation.** LearnStack permits at most one preparation
   `PUT /admin/realms/learnstack/users/profile` per pass, solely to append absent
   canonical admin-only definitions for `user_id`, `tenant_id` and
   `organization_id` when needed for qualified reads. It preserves all existing
   profile state, verifies the exact resulting profile, then discards earlier
   projections and obtains a fresh complete bounded snapshot and plan before any
   ordinary reconciliation mutation. A later conflict can leave only this metadata
   preparation durable; it permits no account, client, scope, mapper, role or TTL write.
2. **Stock console assignment normalization.** In that complete ordinary plan,
   LearnStack detaches only independently qualified pinned-stock `roles` default
   and `microprofile-jwt`/`offline_access` optional edges from `account-console`.
   It preserves the other eight compatible stock edges, all global definitions
   and other clients' assignments. The remaining graph must satisfy the accepted
   compatible allowance/emitter policy. Warm state may differ from fresh import;
   zero console assignments is not a target. Normal complete-plan, readback and
   failure rules apply; detachment is never a second preparation exception.

Both decisions share the existing target, owner and budgets. A definitive or
uncertain failure stops without automatic retry or rollback. A subsequent
explicit invocation reads the durable current state anew.

## Context

### Two bounded replacements of Accepted phase clauses

The Accepted package's
[Reconciler ownership, failure and security boundaries](../roadmap/phase-02b-events-auth.md#reconciler-ownership-failure-and-security-boundaries)
requires: “Read and validate the complete bounded change plan before the first
write.” Decision 1 replaces that ordering only for its single metadata write:
qualify bounded preparation, write and verify it, then build the full fresh plan
before all other mutations. The remaining ownership and preservation rules survive.

The Accepted
[Account-console self-service capability](../roadmap/phase-02b-events-auth.md#account-console-self-service-capability--proposal-2026-10-21)
requires:

> Refuse a wider/customized role allowance or
> an effective conflicting emitter/role-transforming mapper, without changing it.

Its following sentence preserves compatible prior scopes. Decision 2 permits
removing only the three fully qualified stock edges named above, eliminating their
broader emitters/allowances from the console's assigned graph. Customized state
still refuses; direct console mappers, role definitions and composites receive
no broader repair authority. The existing approved fixed-account mapper and
missing four-persona grant additions retain their separate authority.

Removing compatible stock edges too would lose the reason to continue once the
last incompatible edge disappeared. The three-edge decision needs no durable
marker: its final deletion naturally hands later passes to compatible preservation.

Acceptance must name both replacements beside their phase clauses in a dated
record. It retains the original package and review chronology. The
[Step 2 review](../roadmap/phase-02b-events-auth.md#step-2-review-round-1--identity-visibility-and-plan-feasibility)
demonstrated an actual hidden-actor overwrite before the visibility guard. That
guard is protective, and the old warm fixture enabled unmanaged attributes.
The current bootstrap-parity fixture derives from today's desired export; it
cannot establish the historical export's provider-generated scope assignments.

### Pinned provider behavior and manual feasibility evidence

Keycloak 26.6.2's
[stock profile](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/resources/org/keycloak/userprofile/config/keycloak-default-user-profile.json)
lacks these protected definitions and an unmanaged policy. The public
[unmanaged-attributes read](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/UserResource.java#L1153-L1169)
uses USER_API; its [collection](https://github.com/keycloak/keycloak/blob/26.6.2/server-spi-private/src/main/java/org/keycloak/userprofile/DefaultAttributes.java#L366-L398)
is gated by a [policy treating null as disabled](https://github.com/keycloak/keycloak/blob/26.6.2/server-spi-private/src/main/java/org/keycloak/userprofile/DefaultAttributes.java#L456-L471).
It supplies no raw-storage escape hatch. The
[profile PUT](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/UserProfileResource.java#L97-L128)
sets the whole configuration, requiring preservation and independent GET verification.

The console assignment endpoints return
[only ID/name summaries](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/ClientResource.java#L356-L374),
using a [minimal representation](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/ClientResource.java#L828-L832).
An omitted summary protocol does not establish SAML or unsafe protocol. Full
scope representations include
[protocol, attributes and mappers](https://github.com/keycloak/keycloak/blob/26.6.2/server-spi-private/src/main/java/org/keycloak/models/utils/ModelToRepresentation.java#L766-L778).

A private disposable-provider probe imported exactly the `24705a7a` tenant export
into 26.6.2. Missing definitions first caused `plan/identity_conflict`, zero writes.
Test-owned append of the three definitions preserved the complete profile by
independent deep-equal GET and exposed the legacy `tenant-a` value. The unchanged
reconciler then refused `plan/unsupported` at the account assignment-summary check.
Full by-ID reads found all eleven scopes were `openid-connect`, not SAML:

| Assignment | Exact historical names |
|---|---|
| Default (6) | `web-origins`, `acr`, `roles`, `profile`, `basic`, `email` |
| Optional (5) | `address`, `phone`, `organization`, `offline_access`, `microprofile-jwt` |

The `roles` scope contained realm/all-client role emitters; `microprofile-jwt`
contained a realm-role emitter; `offline_access` carried a realm-role allowance.
Test-owned DELETEs removed only those eleven console assignment edges. The
unchanged reconciler then succeeded with 13 writes, retained the old provider ID
and credential hash, and the next pass performed zero writes. That one-case probe
passed in 29.52s, qualified four strict signed personas and console Account REST
HTTP 200, and verified its two owned containers and network absent. A separate
hidden actor probe still refused after exposure without reconciler mutations.

The three-edge follow-up used test-owned definition append and only the three
proposed DELETEs, plus a private copied account module with full scope-by-ID reads
and strict ID/name binding. It retained five default/three optional assignments;
ordinary reconciliation wrote 13, preserved the old ID/hash, then wrote zero on
rerun. Signed code/S256 identity/scope checks passed for all four personas, as did
console code/S256 and Account REST GET 200. This was a private variant, not the
unchanged reconciler. Automatic preparation, canonical stock qualification and
planned DELETEs remain unimplemented/unproved. The phase record owns both source
hashes, receipts and limits. The follow-up passed in 28.95s; its two exact
fixture containers and network are independently confirmed absent.

## Consequences

### Positive

- Historical stock warm upgrade can preserve accounts while reaching the intended
  protected profile and narrow console scope configuration.
- Unknown/customized state remains a refusal, with no name-only stock exception.

### Negative

- A later conflict can leave appended profile metadata durable. A failed pass
  need not mean zero Admin REST mutations; a fresh-plan conflict still means
  zero ordinary mutations, including console detachment and account updates.
- Every console user loses broader role-claim emission through `roles` and
  optional `microprofile-jwt`/`offline_access` use through the console. Compatible
  profile/email/acr/basic and other retained scope behavior remain; global
  definitions and user grants remain. Shared console behavior needs actual proof.
- Metadata grows repeated user-profile responses. Preparation and DELETEs can
  commit despite a lost response; partial progress is durable, with no rollback.

### Neutral

- Approved fixed-account emission retains account self-service; no new grant is
  authorized by normalization. Existing approved missing grants remain separate.
- Global scopes and other client edges, users, passwords, MFA and prior grants
  are preserved. Current BFF full scope, API credentials and Hub remain unchanged.
- Exact username ownership and legacy discrimination remain. This chooses no
  alternate-email adoption, email-renaming policy or new issuance/session lifetime.

## Implementation Notes

### 1. Bounded metadata preparation

- Qualify profile structure, unique names, bounded collections and storage
  projection. At least one protected definition must be absent and needed for
  visibility. Existing hidden, defaulted, selected, non-admin-editable or otherwise
  unqualified protected definitions refuse before preparation, without repair.
- Append only absent canonical scalar definitions from the committed export:
  matching display name, admin-only view/edit, no default/selector/additional
  semantics. Preserve existing definition order, all fields/groups and the exact
  null/absent/explicit unmanaged policy; never widen that policy.
- Precheck complete body/profile-response size, collection counts and conservative
  metadata growth in bounded user reads. A capacity census cannot treat hidden
  identity absence as ownership proof. Known overflow refuses before preparation;
  newly exposed payload exceeding a later limit stops without ordinary writes.
- Require expected successful status and valid profile JSON, then a separate GET
  structurally equal to the complete expected profile, including ordered arrays.
  Drift, malformed data, failure or uncertainty stops; no second preparation.
- Discard earlier projections/actions. Read a full fresh snapshot and qualify
  identity ownership, duplicate actors, secrets, mappers, group/storage sources
  and projected read/write capacity before ordinary apply, including any DELETE.

### 2. Full-plan stock assignment qualification and detachment

- Resolve the complete graph first. If it satisfies the accepted compatible
  allowance/emitter policy, preserve it, even when a compatible customized scope
  uses a historical name. Only a definitively incompatible graph can enter stock
  normalization. HTTP/JSON failures or arbitrary exceptions never select a fallback.
- Normalization requires a subset of the eleven historical edges containing at
  least one of the three incompatible edges, each in its original category.
  Every complete definition/role graph must match the baseline below. Extra/custom
  edges, wrong categories or duplicates refuse. Never reattach missing edges.
  If neither qualifier passes, retain the refusal before ordinary writes.
- Resolve each summary ID with a full scope GET. Independently bind current ID,
  unique name, protocol and assignment references before comparing a canonical
  shape. Qualify complete definition fields, attributes, mapper models/configs,
  scope role allowances and every referenced role/composite edge against an
  immutable historical 26.6.2 baseline. Do not trust names or summary protocol.
- Bind mapper IDs, role IDs, container IDs and composite references to independent
  authoritative current reads before normalizing generated IDs for comparison.
  Unknown/extra/customized fields, emitters, roles or composites and ID/name/container
  mismatches refuse. Do not blanket-ignore attributes or generated-ID differences.
- The baseline fixture is derived from the exact historical export and pinned
  provider, with source hash/provenance and a separately reviewed canonical
  bootstrap shape. Redact published development credentials if retained and record
  the transformation/hash; never use private credentials. Deriving it from today's
  desired export or from the same observation under test is insufficient.
- Plan DELETEs only for the present intersection with default `roles` and optional
  `microprofile-jwt`/`offline_access`. Preserve every other edge, global definition,
  other-client assignment and existing user/grant state. Separately qualify direct
  console allowance/mappers; require the projected remaining graph to pass the
  accepted compatible policy before apply. No wider detachment fallback is allowed.
- Readback proves exactly that edge delta and unchanged retained state; verify
  existing approved mapper/grant deltas separately. Each DELETE needs its expected
  status. Error or uncertainty stops without replay. An explicit rerun requalifies
  while any incompatible edge remains; after the last is gone, compatible-mode
  priority preserves the remaining edges and resumes any other missing accepted
  acts. No marker is required. A completed pass followed by a new pass writes zero.

### 3. Common execution and acceptance boundaries

- Retain selected Compose target/image/private-source agreement, exact loopback
  issuer, authenticated **26.6.2**, secret confinement and the same local lock
  through joined cleanup. Concurrent external administration remains unsupported.
- Keep **5s** per HTTP wait, **120s** for the whole pass, **256 KiB** for every
  outbound body and ordinary decoded response, all collection caps and the fixed
  authenticated server-info **512 KiB** exception. No extra preparation budget.
  Reject redirects; retain bounded proactive refresh, without failed-request replay.
- Definitive/uncertain failure, cancellation or readback conflict exits nonzero.
  No automatic token, preparation or mutation retry; no rollback. Emit only fixed
  stage/reason diagnostics, never upstream bodies, arbitrary identity values or secrets.
- Acceptance updates both named phase clauses, Security 11, Architecture 13,
  Keycloak README, ADR index and repository/phase front doors with these bounded
  exceptions and pending delivery. Final source, receipts and reviews precede
  Implemented claims; existing Accepted ADR bodies remain unchanged.

## Architecture Tests

Reuse the existing canonical obligations
[`Development_Realm_Reconciliation_Preserves_Accounts`](../standards/21-architecture-tests-catalogue.md#development_realm_reconciliation_preserves_accounts)
and [`Tenant_Realm_Issuance_Matches_The_Api_Profile`](../standards/21-architecture-tests-catalogue.md#tenant_realm_issuance_matches_the_api_profile).
Acceptance marks expanded obligations **Registered** until shipped, preserving
prior implemented-scope receipts in phase history. Reconcile registry status/count
assertions together; invent no new canonical rule or approval-time implementation.

- `TenantRealmReconciliationTests` owns genuine historical stock upgrade, exact
  metadata delta, old provider IDs/changed passwords/credential hashes/MFA and
  unrelated-state preservation, hidden-conflict refusal before ordinary writes,
  partial progress and explicit zero-write rerun. Retain hidden-managed/default/
  selector refusals; add partial definitions and admin-no-edit counterexamples.
- `TenantRealmSelfServiceTests` owns immutable bootstrap qualification, exact
  three-edge detachment/eight-edge preservation and console usage for manifest
  and eligible nonmanifest users. Verify full issued access/ID-token claim sets
  and successful console login/profile self-service, with original grants retained.
  Plant stock lookalike names with changed definitions, emitters, allowances,
  composites or ID references; each must refuse before pending ordinary writes.
- `TenantRealmIssuanceTests`, `TenantRealmProfileProtectionTests` and
  `TenantRealmApiTests` retain actual token/API and protected-account proof. Show
  ordinary console editing succeeds while protected identity writes do not persist;
  do not substitute generic refusal, HTTP 204 alone or desired-export parity.
- `development-realm-plan.test.mjs` and `development-realm-core.test.mjs` own
  finite ordering, full-ID reads, independent corruptions, budget boundaries,
  subset/category/extra-edge refusals, compatible-mode priority with unchanged
  custom graphs, malformed/status failures without fallback and readback drift.
  `development-realm-http.test.mjs`, `development-realm-owner.test.mjs` and
  `reconcile-development-realm.test.mjs` retain actual transport/lock/CLI proof and
  add lost acknowledgements after each of the three DELETEs, cancellation and
  explicit reruns, including last-edge handoff to compatible preservation without
  a marker. Node fixtures cannot replace actual-provider qualification.

## References

- [Accepted P02b-6 package](../roadmap/phase-02b-events-auth.md#p02b-6-decision-package-2026-10-20).
- [Security 11 — Authentication](../standards/11-security.md#authentication).
- [Architecture 13](../architecture/13-identity-and-auth.md) and [Keycloak guide](../../infra/keycloak/README.md).
- [ADR-0004](0004-authentication-strategy.md), [ADR-0064](0064-tenant-api-authentication-boundary.md)
  and [ADR-0067](0067-development-tenant-access-token-issuance.md).
