# ADR-0068: Development Identity Visibility Preparation

## Status

Proposed — requires explicit maintainer approval before dependent runtime changes.
The existing visibility guard remains authoritative while this proposal is open.

**Date:** 2026-10-22
**Deciders:** @cemil

This proposal replaces one bounded preflight requirement in the Accepted P02b-6
package. It does not supersede ADR-0004, ADR-0064 or ADR-0067, or correct their
historical decisions. Implementation and final-source provider proof are pending.

## Decision Drivers

- An untouched pre-P02b-6 realm can use Keycloak's stock profile, with no protected
  identity definitions and disabled unmanaged attributes. Its stored legacy
  identity values are therefore hidden from the Admin user projection.
- Treating hidden values as absent can overwrite a conflicting actor. The
  existing refusal prevents that defect but also blocks this stock warm upgrade.
- Upgrade must retain account IDs, credentials and unrelated state. Revealing
  only the three protected attributes is narrower than enabling unmanaged
  attributes across the realm or replacing existing profile policy.
- A metadata write can remain durable even when later account validation fails.
  That exception needs explicit authority, bounded execution and independent proof.

## Considered Options

1. **One bounded identity-definition preparation** (proposed choice). Append only
   absent canonical definitions, verify the complete profile, then build a fresh
   complete plan before any ordinary reconciliation mutation.
2. **Manual operator prerequisite** (viable alternative). Retain the current
   zero-write refusal and require an administrator to prepare the profile before
   rerunning. This preserves the current contract but makes stock upgrade manual.
3. **Enable unmanaged attributes globally** (rejected). Changes unrelated
   attribute visibility or editability and exceeds the three-attribute need.
4. **Replay the export or reset the realm** (rejected). Risks existing identities,
   credentials and unrelated configuration; conflicts with preservation ownership.
5. **Remove the visibility guard** (rejected). Restores the demonstrated path
   from hidden conflicting identity to an unsafe account update.

## Decision

LearnStack permits at most one preparation `PUT /admin/realms/learnstack/users/profile`
per development reconciliation pass, solely to append absent canonical admin-only
definitions for `user_id`, `tenant_id` and `organization_id` when needed to qualify
identity visibility. It preserves every existing definition, profile field, group
and unmanaged policy, verifies the exact resulting profile, and obtains a fresh
complete bounded snapshot and plan before any account, client, scope, mapper, role
or TTL mutation. Any refusal or uncertain result stops the pass without automatic
retry or rollback; a subsequent explicit invocation reads the durable state anew.

## Context

### Existing authority and the bounded replacement

The Accepted package's
[Reconciler ownership, failure and security boundaries](../roadmap/phase-02b-events-auth.md#reconciler-ownership-failure-and-security-boundaries)
requires: “Read and validate the complete bounded change plan before the first
write.” Its following sentences require conflict detection before writes,
preservation, independent readback and no destructive replay.

Acceptance replaces that first-write ordering only for the preparation defined
here: validate the bounded preparation before its single metadata write; verify
it; then read and validate the complete bounded reconciliation plan before any
other write. The remaining preservation, ownership and failure rules survive.
The dated acceptance record must name this exception beside that clause. It must
retain the original package and review chronology rather than describe the old
guard as an erroneous policy or backdate this new authority.

The [Step 2 review](../roadmap/phase-02b-events-auth.md#step-2-review-round-1--identity-visibility-and-plan-feasibility)
records an actual hidden-actor overwrite before the guard was added. Its warm
fixture explicitly enabled unmanaged attributes, so it did not establish stock
profile upgrade compatibility. The current guard's refusal is correct under the
Accepted contract; expanding that contract is the purpose of this proposal.

### Pinned provider seam and evidence limits

Keycloak 26.6.2's
[stock profile](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/resources/org/keycloak/userprofile/config/keycloak-default-user-profile.json)
declares ordinary account fields, without these identity definitions or an
unmanaged policy. The public
[unmanaged-attributes endpoint](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/UserResource.java#L1153-L1169)
uses the USER_API profile; it is not a raw-storage escape hatch. Its
[attribute collection](https://github.com/keycloak/keycloak/blob/26.6.2/server-spi-private/src/main/java/org/keycloak/userprofile/DefaultAttributes.java#L366-L398)
is gated by the [unmanaged policy](https://github.com/keycloak/keycloak/blob/26.6.2/server-spi-private/src/main/java/org/keycloak/userprofile/DefaultAttributes.java#L456-L471),
which treats null as disabled. A missing projected value cannot prove absence.

The public [profile configuration PUT](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/UserProfileResource.java#L97-L128)
sets the full configuration and returns its provider representation. The proposed
append therefore requires preservation of the full read representation and a
separate GET verification; a successful receipt alone is insufficient.

A separate pinned-provider feasibility probe observed that, after test-owned
metadata exposure, a hidden conflicting actor still produces
`plan/identity_conflict` with zero reconciler mutations. Its source-bound receipt
is pending in the phase record. A stock-main probe confirms the initial zero-write
visibility refusal;
after test-owned definition exposure a separate account-console scope check still
refuses with `plan/unsupported`, also before ordinary writes. Complete stock
upgrade compatibility is therefore unproved; preparation is not a waiver of that
independent prerequisite. The phase record must retain both observations. These
probes do not implement or qualify an automatic preparation
path, and approval cannot be recorded as a passing runtime proof.

## Consequences

### Positive

- Stock warm upgrade can qualify stored identity without resetting an account or
  enabling a general unmanaged-attribute policy.
- Conflicts remain refusals before all account and ordinary configuration writes.
  Existing hidden/defaulted/selected definitions receive no automatic repair.

### Negative

- A later identity, secret, inventory or configuration conflict can leave the
  appended profile definitions durable. A failed pass no longer always means
  zero Admin REST mutations; it still means zero account/client/TTL mutations
  when the fresh plan refuses.
- The metadata affects the realm's profile projection, including nonmanifest
  users, and increases repeated user-profile metadata in responses.
- A lost response can leave a committed preparation. There is no rollback or
  inference that timeout means the provider did not commit.

### Neutral

- The local lock remains local; concurrent external administration is unsupported.
- Exact username ownership, legacy discrimination and duplicate-actor checks
  remain unchanged. This does not adopt an account by alternate email, choose an
  email-renaming policy, change account grants or change issuance lifetimes.

## Implementation Notes

1. Retain selected Compose target/image and private-source agreement, exact
   loopback issuer discovery, authenticated **26.6.2** version qualification,
   secret confinement and the same local lock through joined cleanup. Preparation
   shares the existing pass; it receives no extra deadline or separate owner.
2. Qualify the profile's structure, unique names, bounded collections and storage
   projection before preparation. At least one protected definition must be
   absent and needed for visibility. An already defined identity that is hidden,
   defaulted, selected, not admin-editable or otherwise unqualified refuses
   before preparation. Never replace such a definition to make it qualify.
3. Append only the missing members of the three-name set, using the canonical
   definitions in the committed tenant export: scalar, matching display name,
   admin-only view/edit, no default, selector or additional semantics. Preserve
   existing attribute order and all existing definitions, groups, unmanaged
   policy and other profile fields. Do not materialize or change a null/absent
   unmanaged policy. No arbitrary profile import or operator-supplied patch.
4. Before sending, check the complete proposed body, projected profile response,
   attribute/group counts and conservative metadata growth in bounded user
   reads. A read-only capacity census cannot treat hidden identity absence as
   ownership proof. Refuse a known overflow before preparation. Previously hidden
   stored payload can still exceed a later read budget; that stops the pass
   after metadata preparation without widening limits or allowing account writes.
5. Preserve **5s** per HTTP wait, **120s** for the entire pass, **256 KiB** for
   every outbound body and ordinary decoded response, and existing collection
   limits. The accepted fixed server-info read exception remains **512 KiB**.
   Reject redirects; retain bounded proactive authentication refresh without
   replaying a failed request. No automatic preparation/token/mutation retry.
6. Require the expected successful status and valid profile response, then an
   independent GET matching the complete expected profile structurally, including
   retained fields and ordered arrays. Unexpected status, malformed response,
   readback drift, cancellation or uncertain transport outcome stops immediately.
   No second preparation attempt occurs in that pass.
7. Discard pre-preparation account projections and pending actions. Read a full
   fresh snapshot and run every existing ownership, visibility, identity, secret,
   mapper, group/storage and projected-capacity check before ordinary apply.
   A conflict permits only the completed preparation; it permits zero account,
   credential, client, scope, mapper, role or TTL mutations. Preserve independent
   post-apply verification and a subsequent zero-mutation converged pass.
8. Report fixed stage/reason diagnostics without upstream bodies, identity values
   or secrets. A failure is nonzero; metadata may remain. An explicit rerun reads
   the actual current profile and starts from that state, without automatic
   rollback, replay or assumptions from the previous receipt.

## Architecture Tests

Use the existing canonical
[`Development_Realm_Reconciliation_Preserves_Accounts`](../standards/21-architecture-tests-catalogue.md#development_realm_reconciliation_preserves_accounts)
obligation. Acceptance records its expanded preparation requirement as
**Registered** until the new controls ship, preserving the prior implemented
scope and receipts in phase history. This is no new canonical rule; reconcile
registry status/count assertions together. Approval alone proves no new behavior.

- `TenantRealmReconciliationTests` owns actual PostgreSQL-backed Keycloak stock
  warm upgrade, changed-password/ID/unrelated-state preservation, exact metadata
  delta, hidden-actor refusal after preparation and explicit rerun/no-op proof.
  Existing hidden-managed/default/selector controls must still refuse before any
  write; add partial-definition and admin-no-edit controls independently.
- `development-realm-plan.test.mjs` and `development-realm-core.test.mjs` own
  finite preparation ordering, preservation, count/byte boundaries, fresh census,
  malformed/status/readback failures and committed-but-uncertain rerun controls.
  `development-realm-http.test.mjs`, `development-realm-owner.test.mjs` and
  `reconcile-development-realm.test.mjs` retain real transport, deadline, lock,
  cleanup and CLI evidence. Fixtures alone cannot establish provider semantics.
- The existing
  [`Tenant_Realm_Issuance_Matches_The_Api_Profile`](../standards/21-architecture-tests-catalogue.md#tenant_realm_issuance_matches_the_api_profile)
  owners `TenantRealmIssuanceTests`, `TenantRealmProfileProtectionTests` and
  `TenantRealmApiTests` retain issuance, account-edit protection and actual API
  regression coverage. `TenantRealmSelfServiceTests` retains account-grant proof.

Acceptance also updates the phase clause and delivery record, Security 11,
Architecture 13, the Keycloak README, ADR index and repository/phase front doors
with the same narrow exception and pending implementation status. Later delivery
records exact source, proof receipts and review closure before claiming Implemented.

## References

- [Accepted P02b-6 package](../roadmap/phase-02b-events-auth.md#p02b-6-decision-package-2026-10-20).
- [Security 11 — Authentication](../standards/11-security.md#authentication).
- [Architecture 13 — Identity and Authentication](../architecture/13-identity-and-auth.md).
- [Development Keycloak guide](../../infra/keycloak/README.md).
- [ADR-0004](0004-authentication-strategy.md),
  [ADR-0064](0064-tenant-api-authentication-boundary.md) and
  [ADR-0067](0067-development-tenant-access-token-issuance.md).
