# ADR-0076: Live Scoped Authorization

## Status

Accepted — jointly with ADR-0077 on 2026-10-27.
Runtime implementation remains pending at acceptance.

**Date:** 2026-10-27
**Deciders:** @cemil

**Relationship:** Completes P03-2 G2 execution, G5 enforcement and G6 policy
ordering alongside [ADR-0077](0077-trusted-authorization-evidence.md). Joint acceptance
of both records and the packet's decision package is complete. The bounded HTTP
extension replaces ADR-0064's AccountSite-only authenticated surface; its token,
resolution and independent AccountSite HostScope rules remain unchanged.

**Maintainer acceptance — 2026-10-27.** The maintainer explicitly approves
the revised records and six-step P03-2 package at `725d255a`, including
bounded independent authority/evidence sources, pre-success denial
durability and fixed occurrence reconciliation. The [acceptance record](../roadmap/phase-03-delivery-log.md#p03-2-joint-acceptance-and-step-1-2026-10-27)
owns the receipt and implementation/review progress. This acceptance grants
the named contract authority; it establishes no runtime, pool, crash or
production-capacity proof.

## Decision Drivers

- P03-1 stores Memberships and scoped grants, but production membership still
  denies everyone and `AuthorizationBehavior` still passes requests through.
- Resolution rows 7/10/14 need a confirmed candidate before ambient tenant SQL;
  matching rows 6/9 independently need live protected-act authorization.
- Permission revocation must affect subsequent admissions without relying on
  provider claims, token refresh, a browser cache or a previous resolver result.
- SQL's existing tenant read hatch needs equally narrow EF authority and an
  operation/transaction lifetime, without broadening organization writes.
- Real HTTP permission and sensitive-read proofs need a useful production
  consumer; AccountSite deliberately has no permission key and is audit Off.

## Considered Options

1. **Fresh candidate reader, shared policy evaluator and operation-bound read
   authority** (chosen). Preserve the accepted model and complete its execution.
2. **JWT/session-cached grants or resolver-only permission checks** (rejected).
   Revocation and matching-host rows would retain stale authority.
3. **Synthetic TenantContext, bypass credentials or global directory reads**
   (rejected). None is the ninth candidate-reader authority ADR-0072/0073 admit.
4. **Authentication-only fallback policy** (rejected). An authenticated actor
   could still dispatch an endpoint with no declared capability policy.
5. **Keep every new HTTP read in P03-3** (not selected). This leaves P03-2's
   required actual permission-denial and sensitive-disclosure proofs incomplete.
   Pull forward only the administrative directory read described below.
6. **Register a new directory-read key and auto-upgrade Tenant Admin** (rejected).
   The existing administrative capability suffices. Registration is not a grant;
   fixed v1 seed state remains independently verifiable.
7. **Borrow another business-pool connection with only a timeout** (rejected).
   Outer owners can hold every connector while awaiting their nested checks.
   Timeout bounds waiting but does not remove that circular dependency.
8. **Reuse the outer transaction for nested/post-lock checks** (not selected).
   READ COMMITTED sees that transaction's own uncommitted grant/lifecycle writes;
   a writable owner also cannot supply physical READ ONLY confinement. A clean
   ChangeTracker cannot establish committed-only authority after SaveChanges.

## Decision

- Every protected act independently checks current Membership, lifecycle and
  registered scoped capability. HTTP and direct application dispatch enforce
  explicit policy classification and default deny.
- `PreContextIdentityAuthorizationReader` uses the already accepted ninth
  logical setter in a fresh isolated, physically READ ONLY app-role owner on a
  dedicated authorization source with four retained-owner slots.
  It publishes no TenantContext, directory/profile or new global User projection.
- Successful policy evaluation mints immutable, invocation-bound read authority.
  Step 6 binds it to the actual transaction; EF and RLS consume the same scope.
  The existing write restrictions and public/AccountSite host ceilings remain.
- P03-2 includes one useful administrative member-directory GET, governed by
  existing Tenant `identity.membership.admin` and MUST sensitive-read audit.
  Native BFF operations, editing and UI remain P03-3.
- Finite explicit grant edits/adoption remain separate audited acts. No registry,
  seed rerun, realm role or SystemActor value implicitly grants authority.

## Context

Verified at `daf03a48`: the P03-1 foundation is merged. The existing five scoped
permission pairs, eight templates, two-pair admin kernel, contextual database
guards and subject-confined global binding are real subjects. There is no live
permission registry/evaluator, default-deny endpoint classifier or `app.scope`
carrier. The existing architectural prohibition on any such carrier is an
intentional pre-implementation fence, not a proof of its derivation.

### Candidate ownership and freshness

The API supplies normalized identity from the verified-subject/binding stage.
Direct application and asynchronous owners supply their separately enumerated
trusted execution descriptor; passing a UserId alone is insufficient. An exact
candidate includes actor, tenant and optional organization. An isolated child
scope opens READ COMMITTED, awaits physical READ ONLY, then announces that
candidate through a narrow UoW seam. It clears unrelated authority and closes
before the ordinary business owner. It never replaces the parent's unit or
installs an ambient context. Its physical owner/generation is also the one used
by Tenancy's existing `IIdentityScopeLifecycleReader` contract.

#### Physical capacity and progress

The authorization source is separate from the ordinary business source. Its
fixed cap is four owners and four physical connectors per API process, with no
waiting admission queue. Claim a slot before creating/opening a connection;
capacity refusal is immediate `503 dependency_unavailable`. Opening, statements,
cancellation and retained actual cleanup all occupy that slot. A five-second
deadline never releases capacity before confirmed disposal. Unconfirmed cleanup
retains its slot; source shutdown joins actual owners before disposal. Tests may
use a smaller cap; no deployment setting widens it. Four provides limited local
concurrency; one slot already removes the circular checkout dependency. Neither
number is a production throughput measurement or fairness guarantee.

The type-confined source uses the same `learnstack_app` role/grant guard, database
and tenant/organization RLS as ordinary work. It has no raw datasource alias in
general DI and no caller-supplied SQL/callback API. Its owner runs only the exact
Identity grant statement and Tenancy lifecycle statement on the same child unit.
It never acquires the business/evidence source, dispatches a handler, writes an
audit row, invokes a locking owner function, or takes advisory/row locks. Release
it before audit or business continuation. A parent can therefore hold business
connectors and invariant locks while an admitted nested/post-lock read completes.
MVCC reads do not consume the parent's own uncommitted authority.

The borrower census includes resolver, HTTP, application, nested and post-lock
checks. Trusted seed/event/job classifications remain finite. For job callers,
add an explicitly tracked authorization lane to the existing source owner and
actual-cleanup join; never suppress its ambient marker or masquerade as a second
main unit. Source lifetime and capacity ledgers include the additional four
connectors, independently of the existing business/job-source requirements.

Client-pool separation alone is insufficient through PgBouncer: both sources
must not share a saturated downstream user/database server pool. The admitted
transaction-pooling topology provides distinct business, authorization and
ADR-0077 evidence database aliases targeting the same database and `learnstack_app`
role, with independent backend lanes. Direct development PostgreSQL uses the
three bounded client sources. Alias/backend limits, PgBouncer cross-alias user/
database limits, PostgreSQL capacity and the deployed instance count must reserve
the authorization/evidence headroom outside business capacity. Application-name
differences or an extra client pool are not reservation evidence. No new database
login or BYPASSRLS privilege follows.

Composition uses `ConnectionStrings:Default` for business,
`ConnectionStrings:IdentityAuthorization` for authorization and
`ConnectionStrings:AuthorizationEvidence` for ADR-0077's evidence lane. The two
new sources use explicit guarded strings together; partial input refuses.
Development alone may derive both from the existing direct-PostgreSQL Default
string while keeping physically distinct bounded sources. Outside Development,
both explicit lane strings are required; no silent same-endpoint fallback is
allowed. Through PgBouncer they use the distinct database aliases
`learnstack_identity_authorization` and `learnstack_authorization_evidence`,
mapped to the same actual business database. Qualification checks application
role and the same installed identity authority for all sources; credentials
remain environment-only. A matching authority/alias string is not proof of a
backend reserve: topology evidence remains separately required before production
activation. Modules never branch on deployment mode or choose these endpoints.

P03-2 proves progress with small real PostgreSQL pools **and** a pinned
transaction-pooling PgBouncer fixture: hold every business connector, then run
admitted nested and post-lock checks before parents release anything. Hold all
authorization slots separately to prove immediate excess refusal and recovery
only after actual cleanup. A deliberate shared-downstream-pool dirty topology
must fail that progress control. These are correctness/activation proofs now;
through PgBouncer, hold actual open server transactions and verify distinct
backend PIDs, not merely checked-out client connectors that may own no server.
Phase 11 owns actual production topology/instance-capacity qualification before
deployment activation, not this packet's missing liveness test. General database
outage/DDL blocking and excess offered load still can refuse or time out.

#### Committed reads and outcomes

One Identity-owned statement reads active/nondeleted Memberships, assignments,
Roles and grants for that actor and exact tenant. NULL-org Tenant grants compose
with only the requested organization's grants. Unknown/inactive definitions,
retired Roles and Suspended/Revoked Memberships grant nothing. Coverage and
capability are separate results; membership without a grant can confirm a claim.
The query returns bounded authorization facts, never Profile or raw provider data.

Tenancy's lifecycle statement is separate: Trial/Active tenant and, when named,
Active/nondeleted belonging organization admit ordinary work. This is **not one
atomic snapshot across modules**. Each statement sees its own committed state.
Revocation committed before an act's grant query affects that admission;
already-admitted work may finish. The earlier subject-binding lookup remains the
global User eligibility check. P03-3 owns lifecycle-transition races and P03-10
owns closure-side fences before their writers activate.

Resolver-only evidence cannot authorize a later HTTP/application act. Row-7's
existing organization validator remains a separate narrowing check. AccountSite
gains live membership/lifecycle admission on matching rows 6/9 while retaining
its nonsensitive Off/READ ONLY shape and matching real HostScope requirement.
ClaimAndMembership alone still cannot reach AccountSite.

Store unavailability is a fixed `503 dependency_unavailable`, not false
noncoverage or an invalid-token 401. Actual noncoverage retains masked 404;
valid coverage without capability is 403. Request cancellation propagates through
owned cleanup. Binding failures retain ADR-0072's separate uniform 401 contract.
Candidate work has a five-second total admission deadline, including pool wait
and statements; cleanup retains ownership until actual completion. No new cache,
retry or configurable authority override is introduced.

### Policy classification and denial precedence

Composition freezes exact request/endpoint policies. Public reads, the special
AccountSite read, protected capabilities and finite trusted seed/system operations
are distinct classes. Unknown, conflicting or missing declarations never imply
authority. Application metadata remains in SharedKernel/contracts; module code
does not depend on ASP.NET attributes or API assemblies. HTTP policy metadata
uses registered named ASP.NET requirements and is checked against application
metadata. A missing-classification runtime guard supplements the census.
Infrastructure endpoints also need an explicit classification. No matched
endpoint means no handler to authorize; retain the existing routing 404/405
instead of applying a fallback that changes every route miss into a challenge.

Preserve early visitor/host/credential/subject/binding/assertion precedence.
On an admitted protected surface: absent credentials or noncoverage is masked
404; an admitted actor lacking capability gets 403 before handler/resource lookup.
Only an admitted capability may look up a resource. Missing/foreign-scope rows
are 404; a visible row failing an ownership policy is 403. Wrong-method requests
retain routing semantics after existing earlier refusal gates. No path changes
tenant resolution or turns HostOnly into protected authority.

Direct MediatR dispatch repeats classification/capability checks at step 5;
an HTTP allow is not a reusable permission grant. Unrouted existing production
requests receive exact classifications before pass-through is removed. Seed
authority and transport/job execution retain their existing finite validated
source/request contracts, not a blanket SystemActor exemption. Unknown sources,
requests and Platform capabilities deny. P03-9 still owns Platform admission.

Portal Public is virtual, with no stored Membership or Role template. Three
explicit Tenant definitions describe only existing anonymous reads:
`tenancy.public_site.read`, `education.public_course.read` and
`education.public_lesson.read`. They map to the four existing PublicSurface
queries, are unavailable as stored Role grants and cannot widen publication,
HostScope, tenant/org or physical READ ONLY bounds. Credential-free public
requests perform no identity/grant lookup. Registration of this virtual class
does not change the five-pair assignable foundation manifest.

### Operation-bound read scope

Only a declared tenant-wide read plus an eligible Tenant capability can mint the
tenant read hatch. Branch grants never mint it. The immutable witness binds the
actual actor, tenant/org, request policy and invocation; the step-6 owner binds
its generation and announced values before any data query. It is not an
`ITenantContext` setter, browser flag or handler-writable Boolean.

EF's parameterized filters and the actual transaction announce the same narrow
or tenant-wide read state. Completion, failure, cancellation and pool reuse clear
it. Mixed read-scope nesting, including a narrower invocation inside a widened
owner, refuses and poisons the unit; a join never retargets scope. Transaction
mode compatibility and ownership remain ADR-0040's existing controls.

### The minimum production consumer

`GET /api/v1/identity/memberships` is an administrative directory, not a
self-service profile API. Tenant `identity.membership.admin` authorizes a declared
tenant-wide read, including from a branch session with an eligible NULL-org
Tenant Admin Membership. No Organization grant authorizes it. It returns only
MembershipId, UserId, OrganizationId, status, contextual DisplayName and
PreferredLocale; no provider subject, email, grant graph or global profile.
Only current undeleted Active/Suspended Memberships appear; retired/revoked rows
are outside this initial directory. Module-local IDs cross contracts as Guid
under ADR-0023; UserId/TenantId/OrganizationId retain SharedKernel types.

Pagination uses an opaque versioned cursor bound to actor/tenant/org, the declared
operation and fixed ordering by MembershipId. Default limit is 20, clamped at 100.
The cursor is bounded to 1,024 encoded/768 decoded bytes with strict canonical
version/field validation, following the existing navigation-codec precedent.
It supplies neither authentication nor snapshot authority: every page rechecks
live policy and applies the actual scoped query, even for a modified anchor.
No search, arbitrary filters/sort or mutation is admitted. Responses are no-store;
success returns only after its MUST `ReadSensitive` row commits on the ordinary
ReadWrite owner. It is not added to the exact physical READ ONLY catalogue.
The fixed input/output contract is recorded in OpenAPI/generated SDK. The native
authenticated caller still permits only AccountSite; P03-3 owns its extension.
The directory admits HostAndClaim and ClaimAndMembership authority, including a
platform host with a confirmed claim. Its declared administrative read can cross
branches within that tenant; PublicSurface/AccountSite HostScope remains an
independent ceiling on those different operations. Host/claim disagreement still
refuses before authorization, and HostOnly never reaches this directory.

Ordinary grant changes require current Tenant Admin/role-admin authority,
orgless write scope, current delegation ceiling, concurrency and immutable kernel
checks. They cannot grant the actor an authority it does not hold. An explicit
authoring ceiling permits an eligible Tenant Admin with Tenant
`identity.role.admin` to add an exact registered Organization pair only when it
currently holds that same key's Tenant pair. This is an authoring relation, not
permission-identity equivalence or an evaluation fallback. Organization-to-Tenant,
other-key and unregistered-pair implications are forbidden. This preserves
Tenant Admin authorship of Organization templates from an orgless session.

A separately named release-manifest adoption act can apply only an approved finite
manifest/diff under the same admin gate and MUST audit, never a wildcard or
automatic upgrade. P03-2 proves this mechanism with the retained v1 manifest;
no v2 bundle or new assignable capability is selected here. Unknown manifests
refuse. Fixed v1 seed reruns refuse legitimate differing adopted state rather
than resetting it. Human-facing editing/adoption routes remain P03-3.
The actual production commands are `ChangeRoleGrantsCommand` and
`AdoptRoleGrantManifestCommand`, each with expected Role row version and one
Role root. Current actor/target authority is rechecked after the invariant-lock
wait and before mutation. Success and contained grant changes share their MUST
`identity.role.change_grants` / `identity.role.adopt_manifest` audit transaction.

Reopen for cached authority, atomic cross-module authorization, new read scopes,
different nested-owner semantics, another public capability or broader HTTP/BFF
operations. Phase 11 owns production load/capacity qualification; local deadline
and query evidence is not a production p95 guarantee.

## Consequences

### Positive

- Revocation and capability checks become real at both HTTP and application seams.
- An existing administrative grant supplies a useful sensitive-read proof.
- Read widening has one derived, disposable lifetime without broadening writes.

### Negative

- Protected acts depend on fresh database reads; edge and application checks may
  require separate short transactions before the business owner.
- Four authorization and ADR-0077's two evidence connectors add at most six
  retained auxiliary connectors per process, with explicit downstream reserves.
  Excess admissions can fail immediately; split pools do not guarantee capacity
  or fairness for every concurrent request.
- Blocked/failed cleanup can retain slots and delay joined shutdown indefinitely;
  no elapsed timeout is evidence that its physical resource was released.
- Lifecycle, binding and grant checks do not share one atomic snapshot; work
  admitted before a later revocation may finish.
- The directory exposes contextual personal data to an already privileged tenant
  administrator; audit outage prevents otherwise-successful disclosure.
- Mixed-scope nested calls require separate ownership or fail closed.

### Neutral

- No new login role, global User reader, RLS template or provider write is added.
- New-user entry, membership editing, native UI and production onboarding retain
  P03-3/P03-9 ownership. The admin kernel and fixed seed manifest remain intact.

## Implementation Notes

Acceptance appends dated navigation/extension Amendments to ADR-0036/0040/0073
and ADR-0047's tracked job authorization/evidence lanes, plus a
bounded-supersession Amendment to ADR-0064 for the new directory surface
and AccountSite live admission. Accepted bodies/status/dates remain historical.
Reconcile Standards 04/05/11/19/21, Identity permission/audit contracts,
Tenancy/Education public permission matrices, Architecture 09/13/33 and glossary
in the same first step; recheck the independently repaired `add-permission`
guidance rather than reintroducing its former IModule/default-grants sketch.
The packet record owns exact proposed edit text, call-site census and proofs.
Update Standard 05's source/capacity ledger, Standard 12/Phase 11's production
pool-qualification trigger, and environment/Compose/Seeder setup documentation
for the two explicit lane keys without adding credentials to restore/build.

## Tests and Proofs

- Architecture: complete endpoint/request/source census, actual scoped registry,
  generated grant/kernel parity and planted missing/forged declarations.
- PostgreSQL app role: exact candidate owner, physical READ ONLY, lifecycle,
  coverage/grants, transaction cleanup and real revocation interleavings.
- Liveness: all business connectors held while nested/post-lock reads complete;
  independent four-slot exhaustion/actual-cleanup recovery, no observation of
  uncommitted parent grants, tracked job cleanup and downstream-PgBouncer dirty/
  clean topologies. A timeout-only result cannot pass the progress control.
- HTTP/direct application: actual directory positive/denied, real resolution
  rows 6/7/9/10/14, retained AccountSite/public ceilings and unavailable storage.
- Real grant changes: Tenant Admin's same-key Tenant-to-Organization delegation
  succeeds; absent source pair, other-key, Organization-to-Tenant, self-escalation,
  kernel removal and post-wait revocation refuse. Finite v1 adoption is separate.
- EF/RLS: identical read widening, no cross-tenant access or org write widening,
  hostile/missing/reused witnesses, nested scope mismatch and pool cleanup.
- Existing canonical catalogue statuses advance only after actual proof; proposed
  tests do not become Implemented when this ADR is accepted.

## References

- [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
  [ADR-0040](0040-ambient-unit-of-work.md),
  [ADR-0064](0064-tenant-api-authentication-boundary.md),
  [ADR-0072](0072-application-owned-identity-bootstrap.md),
  [ADR-0073](0073-scoped-membership-and-identity-data-foundation.md).
- [Security](../standards/11-security.md), [Database](../standards/05-database.md),
  [Permissions](../standards/19-permissions.md), [Identity](../modules/identity/README.md).
- [Revised P03-2 package](../roadmap/phase-03-delivery-log.md#p03-2-maintainer-review-and-revised-package-2026-10-27).
- [ASP.NET Core 10 policy selection](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/policies?view=aspnetcore-10.0).
- [PostgreSQL 18 READ COMMITTED](https://www.postgresql.org/docs/18/transaction-iso.html#XACT-READ-COMMITTED).
- [Npgsql data sources and pools](https://www.npgsql.org/doc/basic-usage.html),
  [PgBouncer pool and database configuration](https://www.pgbouncer.org/config.html).

## Amendments

### Amendment 1 — Native candidates and finite administration requirements (2026-10-28)

Jointly accepted [ADR-0078](0078-native-session-scope-and-self-entry.md) and
[ADR-0079](0079-bounded-identity-administration.md) extend the exact candidate/policy
census for native application scope and the named member/Role/Tenancy operations.
Verified provider/binding facts remain immutable; distinct NativeScopeEvidence
supplies a separately credentialed candidate, confirmed against real host and live
Membership/lifecycle before HostAndNativeSessionScope. Candidate freshness retains
exact request/feature/source references, isolated committed reads and actual owner
cleanup. Legacy direct-bearer rules and the four-slot grant/lifecycle lane remain.

Policy declarations gain only finite registered scoped alternatives/conjunctions
and explicit self/target predicates. The exact operation matrix names each clause;
there is no wildcard, generic Organization-to-Tenant escalation or cached HTTP
allow. Tenant member-detail read clauses can mint their declared tenant-wide read
witness; Organization clauses and the current-branch directory stay scoped. No
read witness changes write authority. Direct application calls independently
recheck classification, current capability and exact target after ordered waits.

Exactly three Tenant Tenancy pairs form the v2 release delta; the original five
Identity pairs/kernel and eight templates remain. Explicit first adoption carries
its exact eligible Tenant Admin/role-admin/orgless/version/fingerprint/fence/MUST
contract and monotonic marker; ordinary registration/seed/edit cannot restore a
removed grant. Warm installed mutable authority is preserved under ADR-0079's
pristine-prefix/per-write-lock rules rather than repaired or exact-v1 refused.

Assignment writers serialize whole current Role grant graphs before checking the
actor's complete delegation ceiling, under canonical User/Tenant/Role/Membership
ordering. The admitted same-key Tenant-to-exact-Organization bridge stays explicit;
other-key/reverse escalation and kernel removal remain refused. Live target User,
lifecycle, terminal-state and last-admin guarantees are unchanged.

Own-capability previews are separately classified Off self-navigation, not attempted
protected acts: their nine advisory flags and addition-only canGrant choices mint
no denial event or reusable authority. Actual reads/mutations still authorize and
audit independently. Protected completed-receipt disclosure gets its own current
MUST read and immutable target; stale mutation authorization is never replay
permission. Denial evidence remains ADR-0077's common owner, without a new generic
suppression budget or changed audit durability.

[Standard 19](../standards/19-permissions.md) owns finite policies/releases and
[Standard 05](../standards/05-database.md#p03-3-storage-and-replay-contract) owns
source, lock and cleanup controls. Other grants, public/AccountSite ceilings,
auxiliary capacities and historical body/status/date remain. P03-3 owes actual
policy, HTTP/direct, app-role concurrency, replay and native proofs; acceptance
alone does not mark its new callers or controls implemented.

### Amendment 2 — Separate fixed-seed prerequisite source (2026-10-29)

Accepted [ADR-0080](0080-fixed-seed-prerequisite-read-scope.md) adds a bounded
exception to this ADR's human-capability-only read-hatch source. Exactly its eight
fixed seed consumers may request internal prerequisite verification after their
complete ordered fences and target preparation, on the same ordinary ReadWrite
owner. The private seed witness binds the actual invocation and independently
captured immutable declaration; it is default-denied in API composition.
`OperationReadWitness` remains sealed and Protected-human-only, with its existing
capability, actor, scope, invocation, nesting and one-use controls unchanged.

The separate owner state and scope-only enable/clear sites preserve Tenant,
organization and generation, returning narrow before any usable result or
mutation. No seed-shaped context, SystemActor attribution, supplied hash, nested
dispatch, human request or event inherits this authority. The metadata-only
operation is an internal prerequisite of the classified seed act, not a general
directory reader or separately successful sensitive-read event. Its physical
Tenant-wide read reach is constrained by closed application source/projection
and lifetime; it grants no wider write scope or SQL READ ONLY guarantee.

[Database Standards](../standards/05-database.md#operation-bound-read-authority)
and ADR-0080 own the source, state, barriers and proof obligations. The complete
seed-write census extends beyond these eight readers; the remaining writers
retain their separately specified narrow checks. Existing human authorization,
auxiliary capacities, evidence durability and historical Accepted body/status/
date remain unchanged. Acceptance registers the new controls; Step 3 proves them
before activation.

### Amendment 3 — Separate fixed completion read source (2026-10-29)

Accepted [ADR-0081](0081-fixed-seed-completion-before-origin-adoption.md) adds a
separate closed Seed-purpose exception for the fixed administration-origin
completion check. Its one-consumer completion census has its own private witness,
Seeder minter, owner entry and fixed owning-module projections. ADR-0080's exact
eight pristine consumers, source and predicates remain unchanged. Pristine and
completion authority cannot substitute for one another; neither a finite Seed
policy alone nor SystemActor attribution grants either capability.

The completion entry reuses the same ordinary ReadWrite owner, private typed
state transition, two exact scope-only SQL sites, write barriers and actual
cleanup policy. It restores narrow scope before returning usable admission or
allowing mutation. Protected-human `OperationReadWitness` sources, capability/
actor/invocation binding, one-use and nesting controls, live grant ceilings and
auxiliary source capacities remain unchanged. Human requests, nested work,
events and jobs receive no completion witness.

The internal admission/refusal result adds no independently callable directory
or separately successful sensitive-read event; the classified origin act retains
its existing plural MUST evidence. [Database Standards](../standards/05-database.md#operation-bound-read-authority)
owns the separate source and projection censuses and shared lifetime/barriers.
ADR-0081 supplies the additional authority; this amendment discloses it without
independently replacing this decision. Historical Accepted bytes, status, date,
Deciders and prior amendments remain unchanged. Acceptance alone qualifies no
new source or runtime control.
