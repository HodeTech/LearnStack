# ADR-0068: Development Warm-Realm Qualification

## Status

Accepted — 2026-10-22.
Existing guards remain active until the bounded implementation is qualified.

**Date:** 2026-10-22
**Deciders:** @cemil

Implementation and final-source automatic-path proof remain pending.
This decision replaces bounded parts of two Accepted P02b-6 clauses; it does not
supersede ADR-0004, ADR-0064 or ADR-0067,
correct their history or backdate new mutation authority.

## Decision Drivers

- The Accepted package requires migration of the two legacy `tenant-a` values.
  The historical export delegates profile/bootstrap to Keycloak; its stock
  profile hides those stored values, so the current guard blocks that migration.
  Stock console assignments also conflict with the approved narrow console policy.
- Treating hidden values as absent can overwrite an actor. Treating scope names
  or assignment summaries as complete definitions can misclassify customization.
- Warm upgrade must retain provider IDs, credentials and unrelated state while
  converging the intended development console configuration.
- Profile preparation precedes complete identity qualification. Scope detachment
  changes a shared client. Both costs need explicit authority and independent proof.

## Considered Options

1. **Bounded preparation and qualified stock-console normalization** (chosen).
   Reveal only missing protected definitions, then validate a complete
   fresh plan before detaching only the three incompatible stock console edges.
2. **Manual preparation and operator normalization** (viable alternative). Retain
   existing refusals; an administrator appends the definitions and reconciles the
   console assignments before rerunning. This preserves current tooling authority
   but makes this developer-only stock upgrade an explicit manual prerequisite.
   Profile preparation is already documented; deliberate console normalization
   would need the same full graph qualification, not silent name-based removal.
3. **Preserve the broad stock console assignments** (rejected). Retains broader
   scope allowances and role emitters that violate the accepted narrow capability.
4. **Global or temporary unmanaged visibility** (rejected). `ADMIN_VIEW` changes
   visibility without granting edit permission, but exposes unrelated attributes.
   `ADMIN_EDIT`/`ENABLED` also expand editability. Temporary opening/restoration
   adds writes and uncertain recovery; restoring cannot undo a disclosed value.
5. **Replay/reset the realm or drop the guards** (rejected). Reset risks account
   preservation; ignoring visibility or customization restores unsafe adoption.
6. **Explicit fixed opt-in** (viable alternative, not chosen). A dedicated target
   could authorize these same two mutations separately. Automatic shared seed/demo
   convergence is chosen to fulfill the approved warm-legacy migration without
   another prerequisite; every pass still qualifies the same narrow authority.
7. **Automate only preparation or only normalization** (viable alternatives).
   Each leaves the other observed stock obstacle as a named operator prerequisite.
   Choosing both is an upgrade workflow decision, not an indivisible technical need.
8. **Read full scope representations without mutation** (required corrective work).
   ID-bound enrichment restores the already Accepted compatible-scope promise and
   ships independently. It grants no preparation or DELETE authority. Permitting
   incompatible stock emitters without detachment would instead change the
   approved refusal policy; this decision keeps that policy.

## Decision

1. **Identity visibility preparation.** LearnStack permits at most one preparation
   `PUT /admin/realms/learnstack/users/profile` per pass, solely to append absent
   canonical admin-only definitions for `user_id`, `tenant_id` and
   `organization_id` when needed for qualified reads. It preserves all existing
   profile state, verifies the exact resulting profile, then discards earlier
   projections and obtains a fresh complete bounded snapshot and plan before any
   ordinary reconciliation mutation. All identity-independent refusal/capacity
   checks must pass before preparation. A fresh-plan conflict can leave this
   preparation durable but permits no other Admin REST mutation. Here and below,
   **ordinary mutation** means every Admin REST write except that preparation PUT,
   including the separately authorized profile update, mapper/grant changes and
   the named assignment DELETEs.
2. **Stock console assignment normalization.** In that complete ordinary plan,
   LearnStack detaches only independently qualified pinned-stock `roles` default
   and `microprofile-jwt`/`offline_access` optional edges from `account-console`.
   It preserves the other eight compatible stock edges, all global definitions
   and other clients' assignments. The remaining graph must satisfy the accepted
   compatible allowance/emitter policy. Warm state may differ from fresh import;
   zero console assignments is not a target. Normal complete-plan, readback and
   failure rules apply; detachment is never a second preparation exception.

Full-representation scope reads are an independent correction to existing behavior.
The two new mutation permissions share the existing target, owner and budgets.
A definitive or uncertain failure stops without automatic retry or rollback.
A subsequent explicit invocation reads the durable current state anew.

## Context

### Two bounded replacements of Accepted phase clauses

The Accepted package's
[Reconciler ownership, failure and security boundaries](../roadmap/phase-02b-delivery-log.md#reconciler-ownership-failure-and-security-boundaries)
requires: “Read and validate the complete bounded change plan before the first
write.” Decision 1 replaces that ordering only for its single metadata write:
qualify bounded preparation, write and verify it, then build the full fresh plan
before all other mutations. The remaining ownership and preservation rules survive.

The Accepted
[Account-console self-service capability](../roadmap/phase-02b-delivery-log.md#account-console-self-service-capability--proposal-2026-10-21)
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

The [dated acceptance record](../roadmap/phase-02b-delivery-log.md#adr-0068-acceptance-and-implementation-2026-10-22)
names both replacements beside their phase clauses and retains the original
package and review chronology. The
[Step 2 review](../roadmap/phase-02b-delivery-log.md#step-2-review-round-1--identity-visibility-and-plan-feasibility)
demonstrated an actual hidden-actor overwrite before the visibility guard. That
guard is protective, and the old warm fixture enabled unmanaged attributes.
The current bootstrap-parity fixture derives from today's desired export; it
cannot establish the historical export's provider-generated scope assignments.
The dated review's no-profile-write result remains historical; the adjacent
acceptance note records the changed remedy without rewriting that evidence.

### Pinned provider behavior and manual feasibility evidence

Keycloak 26.6.2's
[stock profile](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/resources/org/keycloak/userprofile/config/keycloak-default-user-profile.json)
lacks these protected definitions and an unmanaged policy. The public
[unmanaged-attributes read](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/UserResource.java#L1242-L1260)
uses USER_API; its [collection](https://github.com/keycloak/keycloak/blob/26.6.2/server-spi-private/src/main/java/org/keycloak/userprofile/DefaultAttributes.java#L399-L462)
is gated by a [policy treating null as disabled](https://github.com/keycloak/keycloak/blob/26.6.2/server-spi-private/src/main/java/org/keycloak/userprofile/DefaultAttributes.java#L500-L517).
It supplies no raw-storage escape hatch. The
[profile PUT](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/UserProfileResource.java#L106-L140)
sets the whole configuration, requiring preservation and independent GET verification.

The console assignment endpoints return
[only ID/name summaries](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/ClientResource.java#L391-L410),
using a [minimal representation](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/ClientResource.java#L908-L913).
An omitted summary protocol does not establish SAML or unsafe protocol. Complete
scope representations include
[protocol, attributes and mappers](https://github.com/keycloak/keycloak/blob/26.6.2/server-spi-private/src/main/java/org/keycloak/models/utils/ModelToRepresentation.java#L828-L842).

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


### Observed console impact and limits

A separate private pinned-provider measurement records actual code/S256 access
and ID tokens. Both stock clients report `fullScopeAllowed=false`. For the two
legacy users, stock ordinary `openid` access has 17 claim keys, no audience and
no realm/client roles; detaching the three edges leaves those claims unchanged.
Explicit `microprofile-jwt` adds only `upn` before detachment; afterward that
scope request is refused. Thus these users lose no observed role claim merely
from detachment. The purpose is to make the Accepted narrow console policy
satisfiable on the stock graph, not to assert a measured security improvement
relative to the separately approved BFF capability.

After the ordinary thirteen-write reconciliation, all four personas' console
access tokens add `aud=account` and the two approved account roles. ID-token
claims remain unchanged. A subsequent pass writes zero. The private Fact then
fails its overly strict role-equality harness check on those intended grant
additions, before a profile POST; no successful Fact or actual POST proof is
claimed for that experiment. Full before/after preservation and POST/readback
remain mandatory committed implementation evidence. The phase records source,
receipt and graph hashes, exclusions and exact owned cleanup.


## Consequences

### Positive

- The combined automatic path can support the required legacy migration while
  preserving accounts and the narrow console policy, once its end-to-end provider
  proof passes. Preparation alone establishes visibility, not complete upgrade.
- Unknown/customized state remains a refusal, with no name-only stock exception.

### Negative

- A later conflict can leave appended profile metadata durable. A failed pass
  need not mean zero Admin REST mutations; a fresh-plan conflict still means
  zero ordinary mutations, including console detachment and account updates.
- The same three assignment removals affect every console user. Actual role/claim
  or offline-token effects depend on scope selection, full scope and granted roles;
  no universal role-loss claim or security gain relative to the BFF is inferred.
  Actual before/after tokens and self-service actions must qualify the effect.
  Compatible profile/email/acr/basic edges, global definitions and grants remain.
- A whole-profile PUT turns an implicit default into a stored explicit
  configuration when the definitions change; it is not automatically reverted
  after a later conflict. The pinned [configuration setter](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/userprofile/DeclarativeUserProfileProvider.java#L197-L239)
  creates/persists the component for a non-null configuration.
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

- Preparation triggers only when at least one protected definition is absent and
  `unmanagedAttributePolicy` is outside `ENABLED`, `ADMIN_EDIT`, `ADMIN_VIEW`.
  If unmanaged values are already admin-readable, keep the existing ordinary path.
  Existing protected definitions must be plain and admin-readable; admin-view-only
  definitions remain admissible and are preserved exactly during preparation.
  Hidden/defaulted/selected/otherwise unqualified definitions refuse with
  `prepare/identity_conflict`; preparation does not repair them.
- Before preparation, complete every existing refusal/capacity check independent
  of hidden protected values: desired/manifest validation, realm/client/secret
  continuity, service-account safety, effective mappers, compatible or qualified
  stock console graph, groups/memberships, storage projection and known request/
  response bounds. Reuse those real qualifiers, not a fake identity snapshot.
  Only checks requiring newly visible protected values are deferred. All state
  is independently reread and revalidated afterward; external drift can still fail.
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
- At most one **preparation** PUT is permitted. A subsequent full ordinary plan
  may separately include its existing profile-normalization PUT, for example when
  a readable prior definition needs its approved permission/scalar normalization.
  Preparation preserves that definition; ordinary apply alone can normalize it.
  Both dispatched calls count independently in a successful pass's `mutations`;
  zero-write completion means no mutating Admin call was dispatched.
- Add the closed `prepare` diagnostic stage. Preserve a closed preparation state
  through later failures and cleanup. Once the PUT is dispatched, any failed pass
  emits a fixed notice that metadata may already be durable and requires current-
  state inspection before an explicit rerun. A lost acknowledgement never reports
  zero writes. No values, provider bodies or arbitrary messages enter the notice.
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
- Reproduce the baseline by importing the exact hashed historical export into
  a disposable pinned 26.6.2 provider, capturing full scope/role/composite reads,
  independently binding generated IDs and then canonicalizing only those IDs.
  Commit the extraction procedure, source/image/transformation hashes and reviewed
  expected shape. A later fresh historical bootstrap must match that fixed fixture;
  the observation under test cannot approve or regenerate its own expectation.
  Redact published example credentials if retained and record that transformation;
  never use private credentials or derive the baseline from today's desired export.
- Extend the closed Admin client with assignment-only DELETE, expected status
  **204**: `clients/{qualified-console-id}/default-client-scopes/{qualified-roles-id}`
  and `clients/{qualified-console-id}/optional-client-scopes/{qualified-id}` for
  `microprofile-jwt` or `offline_access` only, under `/admin/realms/learnstack/`.
  Qualified bindings authorize these exact paths; a general delete method is not
  exposed. Deleting scopes, clients, roles, users or other-client edges is forbidden.
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
  Maximum collection/request counts do not imply worst-case completion in 120s: a
  sequence of bounded 5s exchanges can exceed it and must refuse at the shared
  deadline. Record exact request/phase counts and elapsed automatic-path evidence;
  the private 28.95s experiment does not establish this fit.
  Reject redirects; retain bounded proactive refresh, without failed-request replay.
- Definitive/uncertain failure, cancellation or readback conflict exits nonzero.
  No automatic token, preparation or mutation retry; no rollback. Emit only fixed
  stage/reason diagnostics, never upstream bodies, arbitrary identity values or secrets.
- Reopen stock normalization on provider/image/bootstrap changes, altered desired
  identity definitions, repeated budget refusals, or after the legacy upgrade
  population is retired. Requalification or removal is an explicit reviewed change;
  no provider upgrade silently refreshes the baseline or broadens DELETE authority.
- The named phase clauses and dated Step 2 remedy note, Security 11,
  Architecture 13's account-preservation/assignment wording, Keycloak guide, ADR
  index and repository/phase front doors record these exceptions and pending
  delivery. Final source, receipts and reviews precede
  Implemented claims; existing Accepted ADR bodies remain unchanged.

## Architecture Tests

Reuse the existing canonical obligations
[`Development_Realm_Reconciliation_Preserves_Accounts`](../standards/21-architecture-tests-catalogue.md#development_realm_reconciliation_preserves_accounts)
and [`Tenant_Realm_Issuance_Matches_The_Api_Profile`](../standards/21-architecture-tests-catalogue.md#tenant_realm_issuance_matches_the_api_profile).
Retain their existing **Implemented** status and counts for shipped coverage.
The catalogue records dated **Accepted extension — pending implementation
and qualification** paragraphs with precise Asserts boundaries and owners.
Do not claim that extension implemented before final-source proofs/reviews; preserve
prior phase receipts and invent no new canonical rule.

- `TenantRealmReconciliationTests` owns genuine historical stock upgrade, exact
  metadata delta, old provider IDs/changed passwords/credential hashes/MFA and
  unrelated-state preservation, hidden-conflict refusal before ordinary writes,
  partial progress and explicit zero-write rerun. Retain hidden-managed/default/
  selector refusals; add partial definitions and admin-view-only preservation.
  Missing definitions combined with each independent conflict must prove zero
  writes; partial definitions prove one preparation plus a separate ordinary PUT.
  Exercise preparation notices/counts across lost responses and later failures.
- `TenantRealmSelfServiceTests` owns immutable bootstrap qualification, exact
  three-edge detachment/eight-edge preservation and console usage for manifest
  and eligible nonmanifest users. Verify full issued access/ID-token claim sets
  and successful console login/profile POST with independent stored-field readback,
  with original grants retained. Measure actual before/after token effects rather
  than inferring them from mapper names or full-scope settings.
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

- [Accepted P02b-6 package](../roadmap/phase-02b-delivery-log.md#p02b-6-decision-package-2026-10-20).
- [Security 11 — Authentication](../standards/11-security.md#authentication).
- [Architecture 13](../architecture/13-identity-and-auth.md) and [Keycloak guide](../../infra/keycloak/README.md).
- [ADR-0004](0004-authentication-strategy.md), [ADR-0064](0064-tenant-api-authentication-boundary.md)
  and [ADR-0067](0067-development-tenant-access-token-issuance.md).
