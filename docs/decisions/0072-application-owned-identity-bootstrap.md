# ADR-0072: Application-Owned Identity Bootstrap

## Status

Proposed — maintainer approval required before acceptance or dependent code.

**Date:** 2026-10-25
**Deciders:** @cemil

**Bounded supersession:** ADR-0064's required wire `user_id`, ADR-0070's
claim-derived actor association, ADR-0036's ordinary-only HTTP resolution path,
and ADR-0044's unresolved/global audit restrictions, only as identified below.
This proposal changes no runtime, Accepted ADR or grant.

## Decision Drivers

- A first nonseed user cannot satisfy a claim that only development tooling
  prepares. The current API requires `user_id`; the BFF requires it before its
  AccountSite witness. Allocating a User after that witness cannot break the cycle.
- Issuer plus opaque subject is the provider identity. Application actor allocation,
  uniqueness and mutation audit can share one PostgreSQL commit without provider
  extensions, administrator credentials or a cross-system write protocol.
- Global identity is not tenant content or a public directory. Its first writer
  needs a subject-scoped database class and central audit authority; an unresolved
  tenant and the existing deny-all Platform gate supply neither.
- Existing actor/provider identities, credential custody, host reconciliation,
  anonymous public reads and the no-email-merging boundary must survive migration.
- Closure must block delayed bootstrap and membership creation without recycling an
  actor or claiming erasure of retained audit/event references.

## Considered Options

1. **Application-owned binding and explicit self bootstrap** (chosen). Allocate one
   actor in the application database, after provider verification, through a closed
   mutating operation. Normal reads resolve existing mappings and never create one.
2. **Provider-local actor allocator before token issuance** (rejected). Preserves
   the required claim but needs a custom provider extension, durable concurrent
   allocation, warm/broker/admin-account coverage and mapper/tooling changes. It
   splits application identity ownership across two databases without a required
   product benefit here. A mapper that generates a fresh UUID is insufficient.
3. **Runtime Admin API write and token reissue** (rejected). Adds realm-management
   credentials and partial-write/reissue reconciliation to every new identity.
   Ordinary subject authentication does not require those provider powers.
4. **Invitation-only preprovisioning** (not selected). A valid future enrollment
   policy, but it brings invitation admission/delivery into the foundation and
   does not solve provider-created accounts generally. Invitation acceptance still
   needs its own current-email/delegation decision in P03-4.
5. **Trust any actor claim, email match, subject UUID or hash** (rejected). These
   either permit substitution, conflate distinct accounts or make actor allocation
   depend on provider identifier format. A new per-login UUID is also rejected.
6. **Reuse PlatformAdminScope or a new identity login** (not selected). Operator
   authority is inappropriate for self bootstrap. A separate no-bypass credential
   adds role/pool/provisioning ownership without removing subject isolation or
   audit requirements. The four login roles remain unchanged.

## Decision

LearnStack owns a permanent, one-to-one `(issuer, opaque sub) → UserId` binding.
It allocates a UUIDv7 once through an authenticated global-self bootstrap,
independently of membership. Provider validation establishes the subject;
the database establishes the application actor. A present `user_id` claim is
optional compatibility evidence and must agree with the stored binding.

### Credential and binding matrix

Keep separate immutable provider-subject and normalized application-identity
projections. Only stock signature validation and the remaining ADR-0064 profile
produce the former; only successful binding resolution produces the latter.

| Verified credential and durable binding | Ordinary tenant API | Explicit bootstrap |
|---|---|---|
| Active binding; actor absent | Resolve stored actor | Return stored actor |
| Active binding; actor equal | Resolve stored actor | Return stored actor |
| Binding; actor conflicts | 401, no write | 401, no write |
| Unknown subject; actor absent | 401, no write | Allocate once and audit |
| Unknown subject; actor present | 401, no write | 401, no adoption |
| Closing/closed binding | 401, no write | 401, no recreation |
| Malformed/reserved actor or invalid provider credential | 401 before binding access | 401 before binding access |

`user_id` remains a duplicate-checked security property. Present nil/SystemActor,
wrong shape or non-D UUID fails even though absence is now permitted. Issuer,
audience, purpose, JOSE, time, scope and tenant/org validation retain ADR-0064.
Opaque `sub` remains exact, ordinal, nonempty and bounded at 255 characters;
neither trimming nor case folding creates an identity alias. No email uniqueness
or account-link operation is introduced. Two subjects with the same email remain
distinct; attempting to attach either to the other's actor refuses.

Binding-store unavailability is an undecidable credential on ordinary authenticated
requests, preserving the existing 401 contract. Anonymous public requests perform
no identity lookup. This adds a database dependency to authenticated admission;
ADR-0071's uncertain ordinary-read policy remains in effect.

### Exact global-self operation

Admit only `POST /api/v1/account/bootstrap`, with an empty body and no query,
through a separately enumerated global-self surface. It is not a PublicSurface,
ordinary unresolved command, tenant selector or general account administration API.

- Early visitor/peer admission, body bounds and unknown-host masked 404 precede
  token work. The existing validated `(issuer, sub)` quota also applies before
  any bootstrap database access; it does not require an application actor.
- After successful provider verification, this exact operation skips ordinary
  tenant reconciliation/assertions and never installs `ITenantContext`. Scope
  claims are validated for shape but grant no bootstrap or tenant authority.
  Headers cannot select the subject or actor. Unclassified/mismatched methods
  cannot enter this path. Ordinary reconciliation remains route-independent.
- The only mutation is the authenticated subject's first User. It creates no
  membership, role, tenant, invitation or provider attribute. Repeat requests
  return the existing mapping without refreshing provider/person-owned fields.
- Return 200 with exactly `{issuer, subject, actorId}`, JSON and `no-store`;
  at most 4 KiB, no redirect, email or profile. Missing/invalid credentials are
  401, malformed operation input 400, admission 429/503 under existing rules,
  and failed required storage/audit 503. Fixed errors disclose no claim values.
- Use a five-second whole-operation deadline through response consumption,
  within the existing callback deadline. Cancellation before commit rolls back;
  a lost response after commit can leave a durable User. Retry resolves the same
  mapping; cancellation or a timeout never proves the commit failed.
- The native caller exposes this one generated SDK operation to its response
  owner, preserving the fixed API origin, verified host/source, hop secret and
  omitted browser credentials. No browser bearer, generic proxy or Server Action
  is added. Browser sign-in remains same-origin POST with existing CSRF/state.

### Global storage, transaction and audit ownership

Declare User as a **global self-identity** database class, separately from tenant
tables. Use `learnstack_app`, ENABLE/FORCE RLS and an exact subject predicate.
An owning transaction announces verified issuer/sub before any User SQL; a narrow
read owner is physically READ ONLY. No tenant sentinel is announced, no accessor
is fabricated, and no Platform/bypass credential participates.

Normal requests hold no transaction across the request: their self-lookup owns and
closes a short read-only transaction before tenant resolution. Bootstrap owns a
separate transaction through its User INSERT and central MUST audit. Explicit
global-self owner mode, its immutable announcement and exact call-site census
extend the common transaction/capture machinery; an exemption based only on a
table name or a request attribute is insufficient. Nested tenant/global owners
cannot join or retarget each other. Pool reuse, errors and cancellation must clear
identity authority. Identity setters are enumerated separately from tenant setters.

Application grants permit only subject-qualified reads and the declared first
INSERT fields. Mapping identity and creation-audit reference are immutable;
ordinary self bootstrap has no UPDATE, DELETE or global-list capability. Lifecycle
and later person/provider writes require separately admitted owners and grants.
Store global PII only under the classifications and diagnostic restrictions in
[ADR-0073](0073-scoped-membership-and-identity-data-foundation.md).

The new central audit seam accepts the enrolled global-self transaction, not a
standalone write. Its one P03-1 mutation operation is `identity.user.create`, MUST.
`TenantId.PlatformSentinel` identifies this global row's storage attribution only;
it is never request authority or a value passed to a tenant setter.

- Bind the row to the actual User/actor and an immutable, unique creation-audit
  reference. Database constraints validate both directions at commit: no first
  User without its matching row, no claimed bootstrap row without that User.
  RLS/write guards admit only this named global operation and subject. They do
  not widen the existing tenant audit policy or admit arbitrary sentinel INSERTs.
- Record the verified requesting subject's normalized actor as actor, and the
  bootstrap service as executor/source. Seed adoption records SystemActor as
  executor and the adopted actor as subject. Do not claim an operator permission,
  membership or person-provided email. Redact issuer/sub and personal values in
  snapshots; retain only the declared surrogate linkage in causal metadata.
- Reconcile concurrent INSERTs through unique constraints on exact issuer/sub
  and actor. Losing work rolls back its audit; the owner reads the committed
  winner and returns it. A conflicting actor binding never becomes an alias.
- Global rows remain invisible to ordinary tenant audit readers. P03-9/G13 owns
  a privileged global audit read; this proposal adds no such endpoint.

This is a new central global mutation class, explicitly replacing the affected
ADR-0044 restriction. It is not an exemption from same-transaction MUST durability,
not a new Identity audit table and not an extension of the standalone refusal budget.

### BFF association and publication

The BFF independently validates its ID token, permitting an absent actor claim but
rejecting malformed/reserved values. During initial callback, the fixed server
bootstrap request uses that grant's exact access token. Compare returned issuer/sub
with the independently validated ID token and every present actor claim. Then
AccountSite must positively witness the same normalized actor and admitted scope
before publishing an ordinary tenant session. Bootstrap proof is never client data.

Refresh never allocates. It resolves the existing binding and checks the original
normalized actor, issuer/sub, protected-claim presence/values and resolved scope,
then obtains the existing positive AccountSite witness. Preserve absence as well
as value: adding/removing a protected actor/scope claim is not a silent transition.
Closed/disappeared/conflicting identity and failed refreshed publication revoke;
ordinary AccountSite reads retain ADR-0071's unchanged classification.

An actor-only/no-tenant bootstrap can complete without membership. P03-1 returns
a bounded sign-in-unavailable/no-membership outcome and publishes no tenant BFF
session or authenticated product content. P03-3/G4 owns usable zero/multiple-
membership discovery and scope reissue. This foundation proves nonseed actor
establishment, not completed invitation enrollment or tenant switching.

### Seed adoption and closure fence

The four declared development actors are not allocated again. A distinct trusted
development adoption path binds the credential-free canonical seed manifest to
actual provider issuer/sub readback; username/email is a lookup aid, never identity
proof or an HTTP adoption credential. Conflicting existing actor/provider facts
refuse without repair. Preserve passwords, MFA, provider subjects and realm data.
The normal bootstrap endpoint cannot reserve or adopt a declared seed actor.

Run adoption before ordinary actor-bearing seed tokens are admitted. The shared
seed/demo owner passes only bounded, validated, credential-free facts to the
application adoption owner; the direct data-only Seeder gains no Keycloak Admin
credential. Adoption and each central audit commit together, survive retry and
perform no duplicate write on completed rerun. Qualification must cover warm
real-provider readback; fresh export equality alone is insufficient.

User identity uniqueness is permanent, including after closure. A retained
Closing/Closed tombstone prevents delayed bootstrap, provider mirror or invitation
acceptance from reopening or reallocating it. Retained opaque issuer/sub and actor
are linkable data, not anonymous data; P03-10 decides permissible transformations
and fulfillment before its workflow. Tenant erasure/last membership removal never
closes the global account or provider implicitly.

All membership-creation paths serialize against the same User admission fence.
The narrow database guard may lock/test one target User through a schema-qualified
SECURITY DEFINER function owned by `learnstack_migration`, NOBYPASSRLS, with an
explicit owner read policy, fixed search path, no dynamic SQL, no identity-setting
side effect and no returned PII. Revoke PUBLIC execution; grant only the exact
app capability. Its transaction-held lock linearizes against global closure.
This function is a declared confined owner read, not ordinary app-role User
isolation. Direct app global SELECT, arbitrary target projection and generic
definer functions remain forbidden. Closure's privileged writer remains P03-10.

## Context

Verified at `46da68132987759b7ca6a19230b525b5485c7441`: Identity contains four
assembly-marker scaffolds; `TenantTokenProfile` requires `user_id`; BFF
`provider-core.ts` requires actor and tenant before publication; development
reconciliation prepares exactly four actors. Self-registration supplies neither
an application mapping nor a tenant membership. No runtime identity-provider
adapter currently establishes either.

### Relationship and acceptance carriers

From ADR-0064:

> Require one nonempty opaque `sub` of at most 255 characters and one `user_id`
> UUID.

Only the required wire actor is replaced; the stable normalized application actor
remains mandatory. Its remaining rejection/security/host/claimless rules remain.

From ADR-0070:

> match the validated ID token's protected `user_id`/`tenant_id` to its actor/
> tenant.

The actor association gains the explicit bootstrap/binding proof above; tenant
scope, cookie, refresh, capacity and late-publication fences remain.

From ADR-0064's pipeline:

> No middleware may reconstruct a tenant
> from headers or select a different resolution outcome by endpoint.

Ordinary resolution is unchanged. The exact global-self bootstrap is outside it,
not a different tenant-resolution outcome; acceptance discloses this addition in
both ADR-0036 and ADR-0064 before activation.

From ADR-0044:

> It is carried by exactly one class of row: a platform-scope operation with no
> resolvable tenant, written standalone.

Its unresolved-not-provisioning table also says no row. Only the named,
transactional global-self mutation class is added. Existing platform entry,
rejected assertions, tenant writes and standalone semantics remain intact.

Acceptance appends dated disclosures to ADR-0036/0040/0044/0064/0070, without
changing Accepted bodies, statuses or dates. ADR-0003's tenant policy remains;
its disclosure names the new non-tenant class. These are bounded new decisions,
not ADR-0041 factual corrections. Standards 04/05/07/11/18/21, Architecture
09/13/23/31/33, glossary and Identity specs receive the exact implementing
contracts. ADR-0023's injected UUIDv7 allocation remains unchanged.

Reopen for a changed issuer/account-link model, multiple application actors per
provider identity, a new bootstrap producer, non-self global profile access, or
a different lifecycle/closure authority. Their first consumers own new decisions;
P03-3 scope transition, P03-6 provider mirroring and P03-10 closure remain named.

## Consequences

### Positive

- One database serializes actor allocation, permanent identity and its audit.
- New users need no provider extension, administrator write or email-based merge.
- Ordinary GETs cannot allocate identity; a guessed UUID exposes no directory.

### Negative

- This deliberately changes the API/BFF required-claim contract and needs
  coordinated migration/fixtures. Actor-bearing unadopted accounts refuse.
- Authenticated API admission gains a binding-store dependency and bounded lookup
  cost. Anonymous public availability remains independent of identity lookup.
- A callback may leave a durable, audited User without a tenant session. Restart
  and response loss do not undo that commit; no-membership UX is completed in P03-3.
- Global subject RLS, a new transaction/audit class and a narrow owner function
  need genuine database/dirty controls. They add no operator bypass authority.
- Permanent anti-recreation bindings retain personal linkage after closure;
  this is not an erasure-compliance claim.

### Neutral

- Keycloak still owns credentials, authentication and token issuance. No runtime
  provider SDK, new login role, Hub crossing or session database is introduced.
- P03-2 owns live membership/permission activation; a stored User proves no grant.

## Implementation Notes

The [P03-1 package](../roadmap/phase-03-delivery-log.md#p03-1-proposed-decision-package-2026-10-25)
owns implementation steps and proof population. Acceptance precedes every
dependent schema/adapter, including a dormant one. No code is qualified here.

Extend named existing catalogue owners for context writers/setters, transaction
ownership, app-role isolation, audit append-only/durability, HTTP surface and BFF
publication. Register new global-binding/audit obligations with actual owners in
the acceptance commit; mark them Registered until their controls land. Do not
claim a provider SDK backfill from direct HTTP.

## Tests and Proofs

- Real pinned Keycloak nonseed actorless access/ID tokens through actual HTTP;
  absent/invalid/unknown/conflicting/closed matrix cases, genuine positive calls
  and no mapping write on ordinary GET or failed validation.
- Concurrent first bootstrap, forced audit/commit failure and response loss:
  exactly one actor and matching durable row; no replacement on retry.
- Actual app-role self/global isolation with missing, foreign, cancelled and
  pooled authority; planted broad-policy, missing-audit and wrong-user controls.
- Exact global admission before ordinary reconciliation, unknown-host/quota
  precedence and preactor confinement. Scope/header input cannot select identity.
- Real seed adoption preserves all four actors/subjects/passwords/MFA, completed
  no-write rerun, conflicting readback refusal and no seed HTTP impersonation.
- Actual native callback/refresh through fixed SDK calls, wrong-subject proof,
  claim-presence conflict, lost/late callback and closed-account controls;
  no tenant session from actor-only bootstrap.
- Membership-versus-closure lock races and definer privilege/search-path/method
  controls; invoking the guard cannot enumerate a profile or bypass tenant RLS.

## References

- [ADR-0003](0003-tenant-isolation-defense-in-depth.md),
  [ADR-0023](0023-strongly-typed-id-source-generator.md),
  [ADR-0033](0033-audit-durability-model.md),
  [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
  [ADR-0040](0040-ambient-unit-of-work.md), [ADR-0044](0044-audit-write-path.md).
- [ADR-0064](0064-tenant-api-authentication-boundary.md),
  [ADR-0070](0070-owned-tenant-bff-sessions.md),
  [ADR-0071](0071-uncertain-account-site-refusals.md),
  [ADR-0073](0073-scoped-membership-and-identity-data-foundation.md).
- [Database](../standards/05-database.md), [Security](../standards/11-security.md),
  [Audit](../standards/18-audit-coverage.md),
  [Architecture Tests](../standards/21-architecture-tests-catalogue.md).
- [TenantTokenProfile](../../backend/src/LearnStack.Api/Authentication/TenantTokenProfile.cs),
  [provider core](../../frontend/apps/web/src/server/auth/provider-core.ts),
  [IAuditStore](../../backend/src/LearnStack.SharedKernel/Audit/IAuditStore.cs),
  [seed manifest](../../backend/src/LearnStack.Tools.Seeder/SeedIdentityManifest.cs).
