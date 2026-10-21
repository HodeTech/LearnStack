# ADR-0067: Development Tenant Access-Token Issuance

## Status

Proposed — 2026-10-21. The dedicated ADR requires explicit maintainer acceptance.
The earlier P02b-6 approval of 900s development issuance remains recorded in its
[decision package](../roadmap/phase-02b-events-auth.md#p02b-6-decision-package-2026-10-20);
that approval does not make this newly authored ADR Accepted.

**Date:** 2026-10-21
**Deciders:** @cemil

This draft formalizes the already approved issuance choice. It proposes no new
runtime behavior, provider boundary or supersession. ADR-0004 retains provider
ownership; ADR-0064 retains API token acceptance.

## Decision Drivers

- The configured issuance lifespan is a security-sensitive choice carried by
  Security 11 and an approved phase package. Documentation Standards 13 requires
  an ADR for that choice; ADR-0064 deliberately delegates actual issuance.
- The selected 900s development value reduces the ordinary stale-scope window
  relative to the previous 1800s configuration, while increasing renewal demand.
  The phase package does not claim measured production capacity.
- Provider timestamps, API admission and browser session lifetimes have different
  owners. An issuance decision must not silently change any of those contracts.
- Fresh import and preserved-account warm reconciliation must use one executable
  value, with independent evidence that a client override cannot conceal drift.

## Considered Options

1. **Dedicated ADR for the approved 900s development configuration** (chosen for
   approval). Supply a durable decision carrier and rationale; Security 11 keeps
   the ongoing rule and the committed realm keeps its executable value.
2. **Retain only the phase package and Security 11** (rejected). They preserve the
   maintainer's actual approval but leave this security-sensitive choice without
   a dedicated ADR and invite mistaken attribution to ADR-0064.
3. **Retain the earlier 1800s development configuration** (rejected in the approved
   phase package). Less frequent renewal comes with a longer stale-scope window;
   it does not match the explicitly approved development default.
4. **Use the API's one-hour ceiling as the issuance default** (rejected). A maximum
   accepted token lifetime does not select how long the provider issues tokens.

## Decision

LearnStack configures the development `learnstack` realm's access-token lifespan
as **900 seconds**, inherited by `learnstack-web` and `learnstack-api` without an
`access.token.lifespan` client override. Security 11 owns the ongoing issuance
rule, `infra/keycloak/realms/learnstack.json` owns the executable value, and the
existing shared development reconciler enforces it. The configured lifespan is
distinct from observed token timestamps, ADR-0064's API acceptance bound and BFF
session lifetimes.

## Context

### Earlier approval and this follow-up

The maintainer approved the P02b-6 package at `6d75e532` on 2026-10-21, including
900s development issuance and its four implementation steps. Commit `285db56b`
records that acceptance before implementation. The package closes G13's
issuance-TTL part while leaving its session part Open; G12's development
reconciliation and callback choices have their own approved phase vehicle.

The package originally states that no new ADR is proposed. This follow-up
supplies the missing dedicated issuance carrier under
[Documentation Standards 13](../standards/13-documentation.md#adrs), without
reopening the approved value or transferring G12 into this ADR. Acceptance of
this record needs its own explicit maintainer decision. It does not backdate
ADR acceptance or change the approval provenance of existing implementation.

[ADR-0004](0004-authentication-strategy.md) delegates authentication and issuance
to Keycloak. [ADR-0064](0064-tenant-api-authentication-boundary.md#access-token-profile)
defines the resource server's at-most-one-hour `exp - iat` acceptance bound and
30-second clock skew, and expressly leaves actual issuance to P02b-6. Neither
record selects the 900s configured value; both retain their Accepted decisions.

### Configured lifespan and observed issuance

Keycloak 26.6.2 reads the issuance and expiration clocks separately. A second
rollover can yield `exp - iat == 901` with a 900s configuration; a scheduling
pause can widen that difference. Provider session limits can also shorten
late-session issuance. Neither exact `exp - iat == 900` nor a fixed 901s ceiling
expresses this decision. The pinned upstream
[issuance/expiration implementation](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/protocol/oidc/TokenManager.java),
[issuance timestamp](https://github.com/keycloak/keycloak/blob/26.6.2/core/src/main/java/org/keycloak/representations/JsonWebToken.java#L140)
and [clock implementation](https://github.com/keycloak/keycloak/blob/26.6.2/common/src/main/java/org/keycloak/common/util/Time.java#L31)
support the phase package's distinction.

The existing proof contract checks the exact realm value and absence of both
client overrides independently. Fresh signed expiration must fit the observed
UTC/monotonic code-exchange window plus the configured lifespan, within its
finite HTTP deadline. Any explicit fixture/provider clock tolerance is at most
the API's existing 30-second skew; an indeterminate clock/window fails the proof.
The current fixture uses two seconds. This is qualification evidence, not a new
API acceptance rule. Separate late-session controls allow shorter issuance;
wrong-configuration and wrong-expiration controls prevent tolerance hiding drift.

Changing the realm does not revoke earlier 1800s tokens. Their validity remains
subject to ADR-0064's acceptance bound and skew. The shorter configured default
does not supply immediate revocation or live membership checks.

### Scope retained by other decisions

- P02b-7 owns BFF idle/absolute lifetime, refresh custody and serialization, CSRF,
  callback implementation and logout; G13's session part stays Open.
- Phase 03 owns durable identity, membership, permission and tenant-switching
  decisions. This record creates no application identity-provider adapter.
- Phase 11 owns production realm provisioning, refresh-token rotation and
  deployment hardening. The 900s development choice is no production readiness
  or capacity claim.
- The separate `learnstack-hub` realm and Hub contract are outside this decision.

## Consequences

### Positive

- Issuance has a dedicated decision carrier without attributing its value to the
  API acceptance ADR or concealing the earlier explicit phase approval.
- One configured default converges on fresh and existing development realms.
- Timestamp evidence remains compatible with the pinned provider and preserves
  independent configuration and session-clamping controls.

### Negative

- A shorter default requires more frequent renewal than 1800s; production
  throughput and session design still require their own qualification.
- Accepted-token staleness remains possible until expiry; reducing new issuance
  does not immediately invalidate already issued tokens.

### Neutral

- This documentation follow-up introduces no runtime change, new test rule,
  supersession, BFF session decision or additional G12 scope.
- Delivery and hosted qualification remain source-bound in the P02b-6 records;
  an ADR approval does not qualify a later PR head or authorize merge.

## Implementation notes

The existing implementation is the subject of this draft, not a new work plan:

- [Tenant realm export](../../infra/keycloak/realms/learnstack.json) supplies 900s.
- [Development realm planner](../../scripts/development-realm-plan.mjs) validates
  that declaration, converges the realm value, removes the named client overrides
  and independently verifies the resulting state.
- [Security 11](../standards/11-security.md#authentication) keeps the ongoing rule;
  [Keycloak setup](../../infra/keycloak/README.md) links to it for local operation.

### Acceptance carriers

These lifecycle/cross-link actions require explicit acceptance of this dedicated
record. Proposed references may identify this draft but must not present it as
Accepted authority. No runtime change is required by the proposed decision.

| Carrier | Action upon explicit maintainer acceptance |
|---|---|
| This ADR § Status and decisions README | Record this ADR's actual acceptance date and approval, then move its single Proposed index row to Active. Retain the earlier phase approval provenance. |
| Security 11 § Authentication and authority header | Cite this ADR as the dedicated authority for the existing development issuance rule; retain ADR-0064 for API acceptance and the phase package for implementation/approval history. |
| P02b-6 decision package's vehicle, G13 answer, test-source and handoff passages | Disclose this later dedicated carrier and link it for issuance only. Preserve the original package acceptance, G12 vehicle, implementation receipts and Open G13 session portion. |
| Standards 21 issuance obligation's source references | Add this ADR's issuance authority beside the existing API/profile sources; retain the canonical rule, test owners and implementation status. |
| Architecture 13 and Keycloak README issuance pointers | Keep Security 11 as the ongoing-rule pointer; reconcile only a claim that ADR-0064 or the phase package alone supplies dedicated issuance authority, if present. |

## Architecture tests

No new canonical obligation is introduced. Existing controls remain required:

- [`Tenant_Realm_Issuance_Matches_The_Api_Profile`](../standards/21-architecture-tests-catalogue.md#tenant_realm_issuance_matches_the_api_profile)
  owns real signed issuance/API compatibility. `TenantRealmIssuanceTests` checks
  live realm/no-override state, bounded fresh expiration, wrong TTL/override and
  expiration controls, and shorter late-session issuance. `TenantRealmExportTests`
  independently checks the committed declaration and drift controls.
- [`Development_Realm_Reconciliation_Preserves_Accounts`](../standards/21-architecture-tests-catalogue.md#development_realm_reconciliation_preserves_accounts)
  owns warm convergence/preservation through `TenantRealmReconciliationTests`
  and the existing Node tooling controls, including TTL restoration/readback and
  zero-write completed reruns.

The phase delivery records own historical results; this draft claims no new
runtime or current-head hosted test result.

## References

- [ADR-0004 — Authentication Strategy](0004-authentication-strategy.md).
- [ADR-0064 — Tenant API Authentication Boundary](0064-tenant-api-authentication-boundary.md).
- [P02b-6 decision package and approval](../roadmap/phase-02b-events-auth.md#p02b-6-decision-package-2026-10-20).
- [Security 11 — Authentication](../standards/11-security.md#authentication).
- [Documentation 13 — ADRs](../standards/13-documentation.md#adrs).
- [Identity and authentication architecture](../architecture/13-identity-and-auth.md).
