# ADR-0072: Application-Owned Identity Bootstrap

## Status

Accepted — jointly with ADR-0073 on 2026-10-25. Runtime qualification remains
pending the six implementation steps.

**Date:** 2026-10-25
**Deciders:** @cemil

**Bounded supersession:** The carrier table identifies each changed rule. This
is a new decision, not an ADR-0041 factual correction. Joint acceptance replaces
only the named rules through dated Amendments pointing here.
Existing Accepted bodies, statuses and dates remain historical.

## Decision Drivers

- New provider accounts have no application actor. Requiring `user_id` before
  allocating it creates a cycle that seed attributes cannot solve.
- One application database can allocate actor and central MUST audit without
  provider plugins, administrator credentials or cross-system identity writes.
- Global identity needs explicit self-confined SQL/audit authority; a missing
  tenant, marker or SystemActor supplies none.
- The first schema must preserve continuity, deny post-closure recreation,
  minimize raw identity and refuse accidental issuer replacement.
- Success, failure and unknown COMMIT need executable write/evidence-read
  contracts under FORCE RLS before migration.

## Considered Options

1. **Application binding and exact self bootstrap** (chosen). One verified
   subject receives one actor; ordinary reads never allocate. P03-1 implements
   storage/common creation authority and fixed seed adoption. P03-3 decides and
   activates new-user HTTP/BFF entry with its usable membership journey.
2. **Provider allocator or runtime Admin API/reissue** (rejected). Adds provider
   machinery, credentials and recovery merely to establish application identity.
3. **Invitation-only provisioning** (not selected). An enrollment policy rather
   than a complete identity mechanism. P03-4 owns its separate verified-email act.
4. **AllowsUnresolvedTenantContext alone** (rejected). It authorizes no provider
   credential, subject quota handoff, global SQL owner or global audit. Use a
   separate exact global-self descriptor; keep the provisioning marker closed.
5. **Separate global attribution UUID/audit table** (rejected). Another UUID adds
   permanent reservations and token/context/event/job guards without solving SQL
   authority. Another table splits central audit ownership. Instead explicitly
   extend PlatformSentinel with two distinguished populations and exact seams.
6. **Admission projection or fifth minimal role** (not selected). A projection
   needs atomic state synchronization/recovery; another role adds credentials and
   pools. Select enumerated owner functions, accepting their execution privilege.
7. **HMAC lookup or permanent raw subject** (not selected). HMAC adds key custody,
   rotation and restore; it does not anonymise linkage. Raw subject need not
   survive closure. Use an access-controlled digest with guessing risk explicit.
8. **503/refusal witness on all binding outages** (not selected). Preserve the
   ADR-0064/0071 uniform ordinary 401 contract. Bootstrap has its separate typed
   storage-failure result; this is not evidence of invalid credentials.

## Decision

- LearnStack binds one verified provider subject to one stable UUIDv7 actor.
  `user_id` becomes optional consistency evidence; present conflict/malformed
  values refuse. Only an admitted creation owner creates a binding; P03-1 exposes
  that authority to the fixed seed owner, not an HTTP operation.
- A deployment-pinned authority and permanent digest preserve identity independently
  of issuer URL. Active accounts retain exact subject for collision checking;
  authorized closure removes it and retains the nonauthorizing tombstone.
- One global-self transaction and central MUST audit commit together. Narrow
  success/reconcile seams and evidence functions extend central durability;
  PlatformSentinel gains a second, explicitly distinguished row class.
- Verified provider-subject admission precedes binding access. Bootstrap supplies
  no tenant context, membership, Platform permission or tenant BFF session.

## Context

### Source and credential handoff

At `92117893`, TenantTokenProfile requires actor; ValidatedTenantIdentity has a
nonnullable actor; quota consumes that type through TenantCredentialFeature.
Its key is issuer/sub, but the shipped path cannot accept actorless tokens.
Identity remains scaffolded at that source. The following is the accepted
replacement contract; implementation and database qualification are pending.

New API-owned `VerifiedProviderSubject` contains the stock-verified issuer/sub,
optional actor and optional scalar tenant/org claims. TenantCredentialFeature
carries this stage separately from normalized ValidatedTenantIdentity. Only
existing-binding resolution creates the latter. Quota consumes the former
before **any** authenticated request's binding SQL. Preserve 600/minute, 4096-entry
ownership/fault protocol, visitor/peer floors, once-per-request debit and
reexecution behavior. No request payload selects a subject or resets its quota.

All remaining JOSE/signature/purpose/audience/time/security-property checks stay.
API and native ID-token validation use exact ordinal opaque sub, bounded to 255
characters, without trimming/case folding. The native profile still requires
actor/tenant and the existing AccountSite witness in P03-1. Optional API actor
evidence does not make a tenantless native session valid.

| Verified subject / binding | Ordinary API | Internal creation owner |
|---|---|---|
| Active; actor absent/equal | Resolve stored actor | Return stored actor |
| Actor conflict/malformed/reserved | 401, no mutation | Typed identity refusal; no mutation |
| Unknown; actor absent | 401, no mutation | Serialize and allocate once when independently admitted |
| Unknown; actor present | 401, no adoption | Only fixed, separately witnessed seed adoption may preserve its actor |
| Closing/Closed | 401, no recreation | Typed nonadmitting-binding refusal; no recreation |
| Binding unavailable | Uniform 401 | Typed storage failure, no asserted success |

No P03-1 identity/eligibility cache is installed. Each authenticated admission
reads committed binding state; provider key caching remains separate. Anonymous
public reads do no identity lookup. Closed diagnostic reasons distinguish
binding storage/authority faults without enriching the 401 body/challenge.
Internal creation explicitly translates storage/audit failures to bounded typed
results. Its future HTTP mapping is a P03-3 decision. Preserve already refused,
cancelled and uncertain outcomes.

### Activation boundary

The maintainer selects P03-3 for new-user HTTP bootstrap and BFF entry. P03-1 adds
no POST, SDK operation, endpoint-dependent resolver, global MediatR marker or
native no-session completion. AllowsUnresolvedTenantContext and its provisioning
allowlist remain closed. Current public/AccountSite routes, missing-token rules,
wrong-method outcomes and complete native identity remain unchanged.

P03-1's actual consumers are fixed seed adoption and existing-session admission.
Its common creation/audit contract receives real PostgreSQL/provider proofs,
without exposing a new creation route. Every ordinary request follows one
route-independent credential/quota/binding sequence, canonically owned by
[Security](../standards/11-security.md#proposed-p03-1-identity-admission-annex).

P03-3/G1/G6 must decide provider registration and durable creation budgets before
activation, explicitly supersede ADR-0064's route-invariance prohibition if a
global operation needs it, and define its own API/native outcome table. It must
distinguish session publication from identity-created-without-session, including
cookie/transaction cleanup, and prove grant/issuer/subject association. No retry
may infer rollback from a missing response. Those are future acceptance gates,
not an approved transport design hidden in this foundation ADR. ADR-0053/0070
caller and publication changes wait for that package. Refresh never allocates.

### Authority, retention and concurrency

[Database Standards](../standards/05-database.md#proposed-p03-1-identity-storage-annex)
is the canonical owner of columns, GUCs, digest encoding, grants,
policy/function signatures, lock deadlines and evidence predicates. Do not copy SQL.

`identity_authority` is a singleton platform-scoped registration installed once
by deployment/migration tooling before bootstrap. Stable AuthorityId names the
exact issuer. Runtime app reads but cannot install/replace/edit it. Missing or
mismatched registration refuses before allocation. P03-9/G13 owns a frozen-
admission, privileged issuer rebind after proven provider continuity, preserving
AuthorityId, actors, keys and closed tombstones. Until it ships, rename is
unsupported; a changed URL never silently allocates a new identity domain.

User lookup is AuthorityId plus canonical subject digest. Active requires raw
subject; Closing keeps it only while fulfillment needs it; Closed removes it.
Active digest/raw mismatch refuses, never aliases. A closed matched key always
denies, including a hypothetical collision. Inherited soft-delete fields stay
NULL: closure is lifecycle, not recycling. No email/verification or unused global
profile fields land here. P03-6/G10, or an earlier proven consumer, owns the first
mirror schema/source/freshness decision.

The common prepared global owner acquires subject serialization and freshly
checks the binding before handler construction or intent declaration. Only
serialized absence opens the common audit frame and allocates a target plus
creation-audit reference. Ordinary AuditLog-before-Transaction execution cannot
supply that order unchanged; Standard 05 names the prepared-owner extension,
central attribution/key port and precommit constraint validation. Existing
bindings return without a mutation intent. Genuine failures/unknown COMMIT retain
central reconciliation. No unrestricted new-subject route is activated here;
the 4096 retained limiter partitions never bound permanent User cardinality.

Creation, reactivation and all assignment additions use the canonical User fence.
Standard 05 alone owns lock keys, shared/exclusive tenant modes, fresh reads and
deadlines; handlers and database guards must implement the same contract.
Unknown and nonadmitting ids produce the same false result. No function projects
a profile. Later closure/status writers share the tenant invariant locks before
making an administrator ineligible; their workflow remains P03-10.

### Audit and owner privilege

PlatformSentinel ceases to identify operator events uniquely. Operation/source
distinguishes platform-scope standalone rows from `identity.user.create` global
MUST SecurityEvent. No tenant setter announces the sentinel. Ordinary audit
methods must reject it explicitly before SQL; current WritePendingAsync lacks
that check and must gain it before the new policy branch is admitted. Only the
named global pending/reconcile seams gain permission; platform entry rejects the
new identity discriminator. Existing sentinel announcement guards remain.

The audit isolation policy explicitly departs from the unmodified template:
retain its tenant/org branch, add a guarded global INSERT branch, and add
owner-qualified evidence-read authority. App SELECT gains no global/self-audit
branch. Fixed evidence functions make reciprocal creation and duplicate checks
possible under FORCE RLS. Function execution under table-owner privilege is a
new capability, despite no migration credential in runtime configuration.

The prior host-lookup definer rejection remains true for its old policies.
Here role-qualified USING arms in single policies plus fixed functions are chosen.
Those arms are not command-specific SELECT policies. Explicit User DELETE and
TRUNCATE guards protect the permanent binding, including owner execution.
No BYPASSRLS, fifth role, PUBLIC execution, dynamic SQL, generic target projection
or mutable search path is allowed. Migrations own installation; runtime call
sites and policy/function bodies have positive and dirty controls.

User and success audit share one commit. Deferred reciprocity is forced IMMEDIATE
after all writes and before commit admission; it is not a permanent cross-module
FK blocking audit purge. A known precommit guard refusal rolls back; a missing
physical COMMIT acknowledgement remains uncertain. Central non-success
reconciliation uses the captured subject, fresh standalone timestamp and existing
possible indeterminate pair. Non-success evidence requires no User witness and
proves no business outcome; it may coexist with a committed success after an
unknown acknowledgement. Exact duplicate checking includes outcome; a unique
error alone is insufficient. Before binding, actor is NULL and proposed target
is distinct.
Seed adoption attributes SystemActor and its adopted subject. Catalogue baseline
is MUST; no tenant override or suppression allowance applies. Preserve existing
Critical/health, refusal and cancellation-independent joined cleanup semantics.
A timeout is neither no-COMMIT evidence nor a reason to allocate another actor.

Audit `metadata.identity_subject_key` uses the stable binding key, not raw
provider values. It remains immutable linkable PII, potentially guessable for
low-entropy subjects; SHA-256 provides no secrecy/anonymisation promise. Existing
security retention applies even without User. P03-10's Identity closure coordinator
and Audit participant locate successful/failed attempts by this key and report
retained immutable linkage. No metadata UPDATE grant is widened. Phase 11 owns
purge/backup operations; it does not own missing Phase 03 fulfillment.

### Acceptance vehicle and carriers

Both ADRs are accepted together. The
[revised package](../roadmap/phase-03-delivery-log.md#p03-1-second-maintainer-review-and-revised-package-2026-10-25)
records the approved scope, Amendment texts and proof owners. The dated
Amendments and canonical contract adoption accompany acceptance; they do not
claim that schema, runtime grants or their proofs are implemented.

| Carrier / clause | Named adoption instrument |
|---|---|
| ADR-0003; Standard 05 six classes/unmodified audit/global unique exceptions | Dated bounded Amendment for new class and exact audit/owner-policy departure; ordinary template unchanged |
| ADR-0010; Standard 01 cross-module SQL | Dated bounded Amendment for enumerated central persistence-witness guard SQL only; no module business join or fifth application mechanism |
| ADR-0033 including Amendment 5 | Dated Amendment extending central success/reconcile to the named global population, preserving durability |
| ADR-0036 pre-context identity read | Dated Amendment for route-independent binding normalization and future candidate reader; no new HTTP surface or TenantContext writer |
| ADR-0040 owners/nesting/setters | Dated Amendment for subject owners and separately registered candidate reader; no tenant/global join |
| ADR-0044 §§1/2/3/5/9/10 | Dated bounded Amendment for second sentinel population, prepared global declaration, app INSERT/evidence, exact duplicate proof and new central seams |
| ADR-0064 actor/projection/pipeline | Dated bounded Amendment for optional API wire actor, verified-subject quota and existing-binding normalization; route invariance/census preserved |
| ADR-0066 subject admission population | Dated clarification for verified-subject input before binding; numeric/ownership/fault semantics unchanged |
| ADR-0065 and ADR-0070 | Dated navigation only: host mismatch allowance never covers global MUST; native complete identity/AccountSite publication remain unchanged |
| ADR-0071 | Dated navigation Amendment only; ordinary-read status table unchanged |
| Standards 04/05/07/10/11/18/19/21; Architecture 09/13/23/31/33 | Adopt the approved canonical contracts and authority headers; no duplicate SQL |
| CLAUDE, glossary, IAuditStore/PostgresAuditStore XML and guard census | Reconcile sole-population/writer and method-count claims before dependent code; default refusal stays |

Reopen before issuer rebind, another producer, global profile disclosure,
digest/HMAC change, tombstone retirement or changed closure authority. No dormant
writer preempts P03-9 rebind/privileged reads or P03-10 retained-reference workflow.

## Consequences

### Positive

- One database owns allocation/audit without provider machinery.
- Serialization avoids benign racing failed audits. Stable authority/key supports
  approved URL migration; closure need not retain raw subject.

### Negative

- API/BFF claim and global audit authority deliberately change. Authenticated
  admission gains an uncached store dependency; anonymous reads do not.
- Authority installation is mandatory; unsupported issuer change fails closed.
- Fixed owner SQL/policies are load-bearing runtime privilege, without an owner
  password. They require independent controls and genuine database proof.
- Durable actors can lack membership/session. HTTP creation and its registration/
  capacity policy are withheld until P03-3, not solved by in-memory subject caps.
- Permanent digest/actor and retained failed-attempt keys remain personal data.
  Offline guessing and immutable retained copies are explicit accepted costs.

### Neutral

- No provider SDK, new login, Hub crossing, live permission enforcement or session
  database is introduced. No erasure/production-readiness proof follows.

## Implementation Notes

The revised package owns six steps, exact proof/status and required controls.
Acceptance authorizes these authorities; it does not implement them. Four seed actors,
provider subjects and credentials survive; ordinary HTTP cannot invoke adoption.

## References

- [ADR-0010](0010-cross-module-communication.md),
  [ADR-0033](0033-audit-durability-model.md), [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
  [ADR-0040](0040-ambient-unit-of-work.md), [ADR-0044](0044-audit-write-path.md),
  [ADR-0053](0053-trusted-public-server-rendering.md),
  [ADR-0064](0064-tenant-api-authentication-boundary.md),
  [ADR-0065](0065-bounded-tenant-assertion-diagnostics.md),
  [ADR-0066](0066-bounded-tenant-subject-admission.md),
  [ADR-0070](0070-owned-tenant-bff-sessions.md),
  [ADR-0071](0071-uncertain-account-site-refusals.md).
- [ADR-0073](0073-scoped-membership-and-identity-data-foundation.md),
  [Identity spec](../modules/identity/README.md),
  [Data Protection](../architecture/23-data-protection.md).
- [PostgreSQL 18 policies](https://www.postgresql.org/docs/18/sql-createpolicy.html),
  [functions](https://www.postgresql.org/docs/18/sql-createfunction.html),
  [isolation](https://www.postgresql.org/docs/18/transaction-iso.html),
  [function snapshots](https://www.postgresql.org/docs/18/xfunc-volatility.html).
- [PostgreSQL 18 constraint timing](https://www.postgresql.org/docs/18/sql-set-constraints.html).

## Amendments

### Amendment 1 — Ordered Membership admission timing (2026-10-26)

[ADR-0074](0074-membership-admission-under-ordered-locks.md) owns the bounded
canonical Membership eligibility-read timing change in
[Standard 05](../standards/05-database.md#lock-snapshot-and-adoption-conditions).
This is navigation to that change: this ADR delegates the algorithm and its
decision remains unchanged.
Creation, Suspended-to-Active and every assignment addition acquire the actual
target's exclusive User fence before tenant fences and retain the transaction
hold through completion. The actual mutation trigger must freshly verify that
target's eligibility even when the complete ordered plan is preheld. No missing
User key is first acquired beneath tenant authority.

P03-10's separately admitted closure/status owner must acquire the same User
fence, then all affected exclusive Tenant keys in canonical order, before making
the User nonadmitting. The final-admin recovery/termination gate remains. The
global subject owner, five-function confinement, absence of a generic User
reader and all other decisions remain unchanged. The Accepted body, status and
date above remain historical; ADR-0074 and Standard 05 own the current timing
contract and its pending Step 4 proofs.

### Amendment 2 — Bounded deliberate HTTP bootstrap and self discovery (2026-10-28)

Accepted [ADR-0078](0078-native-session-scope-and-self-entry.md) activates the
previously withheld new-user entry contract, not a runtime implementation claim.
Exactly three credentialed global-self descriptors follow provider validation,
subject quota and independent native-source validation, before ordinary existing-
binding admission. They retain prepared global owners, subject serialization,
central User/MUST audit atomicity and failed/indeterminate reconciliation; no
generic unresolved path, runtime provider administrator or email merge is added.

Bootstrap allocation is explicit deployment opt-in and person-confirmed CSRF
entry. It consumes a separately committed attempt debit only after initial
absence, then enters the prepared subject owner and freshly rechecks. Matching
existing bindings return 200 without a new creation intent, including a racing
winner. Acknowledged new binding is 201; unknown COMMIT is 503, not asserted
rollback or an automatic retry instruction. Nonadmitting/closed bindings never
become missing or receive successors. Creation alone supplies no Membership or
tenant cookie, and zero-Membership native completion is truthful recovery.

The singleton stable authority shares 10 attempts/UTC minute and 100/UTC day
across all tenants. Failed/cancelled/uncertain attempts do not refund. Capacity is
10,000 retained Users including fixed seed actors and closure tombstones; the
permanent counter is monotonic. User INSERT and central MUST audit enforce total
capacity atomically; earlier attempt debit is intentionally separate. Existing
bindings spend no allocation attempts. Deployment initializes the control row
from retained Users, and ordinary runtime cannot reset/read arbitrary counters.
Intentional multi-account exhaustion of shared onboarding budget is an accepted
availability cost, not tenant fairness or DDoS protection. Raising/resetting needs
reviewed migration/decision; closure does not refund lifetime capacity.

The exact eight-function definer census is the existing five plus subject-derived
own-membership discovery, creation-attempt admission and User-INSERT capacity
trigger functions. The platform control relation adds no database class/login.
[Standard 05](../standards/05-database.md#p03-3-storage-and-replay-contract) owns
signatures, owner/ACL/body/caller, RLS, counters, canonical key and real app-role
proofs. Discovery adds no arbitrary User reader: it derives only the Active
subject's own Membership scopes; short sequential tenant-label owners use their
separately enumerated tenth setter. Personal relationship/digest data remains
classified even where self-only navigation is audit Off.

The original body/status/date and previous timing Amendment remain historical.
P03-3 implementation closes only after durable budget/restart/concurrent creator,
central audit failure/unknown commit, terminal binding, exact function privilege,
provider/native association and real no-session/browser controls pass. P03-9
issuer/production onboarding and P03-10 closure/redaction ownership remain.
