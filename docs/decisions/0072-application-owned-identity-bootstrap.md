# ADR-0072: Application-Owned Identity Bootstrap

## Status

Proposed — joint approval with ADR-0073 is required before acceptance or code.

**Date:** 2026-10-25
**Deciders:** @cemil

**Bounded supersession:** The carrier table identifies each changed rule. This
is a new decision, not an ADR-0041 factual correction. Today only proposals
change; acceptance replaces named rules through dated Amendments pointing here.
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
   subject receives one actor; ordinary reads never allocate. Database ownership
   makes concurrency and mutation evidence one commit.
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
  values refuse. Only explicit self bootstrap creates a binding.
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
Identity remains scaffolded. The following is a proposed replacement.

New API-owned `VerifiedProviderSubject` contains the stock-verified issuer/sub,
optional actor and optional scalar tenant/org claims. TenantCredentialFeature
carries this stage separately from normalized ValidatedTenantIdentity. Only
existing-binding resolution creates the latter. Quota consumes the former
before **any** ordinary/bootstrap binding SQL. Preserve 600/minute, 4096-entry
ownership/fault protocol, visitor/peer floors, once-per-request debit and
reexecution behavior. No request payload selects a subject or resets its quota.

All remaining JOSE/signature/purpose/audience/time/security-property checks stay.
API and native pre-session ID projection both use exact ordinal opaque sub,
bounded to 255 characters, without trimming/case folding. Actor/tenant can be
absent before bootstrap; organization still requires tenant. Normalized ordinary
session identity remains complete and separate.

| Verified subject / binding | Ordinary API | Bootstrap |
|---|---|---|
| Active; actor absent/equal | Resolve stored actor | Return stored actor |
| Actor conflict/malformed/reserved | 401, no mutation | 401, no mutation |
| Unknown; actor absent | 401, no mutation | Serialize and allocate once |
| Unknown; actor present | 401, no adoption | 401, no adoption |
| Closing/Closed | 401, no recreation | 401, no recreation |
| Binding unavailable | Uniform 401 | Typed 503, no asserted success |

No P03-1 identity/eligibility cache is installed. Each authenticated admission
reads committed binding state; provider key caching remains separate. Anonymous
public reads do no identity lookup. Closed diagnostic reasons distinguish
binding storage/authority faults without enriching the 401 body/challenge.
Bootstrap explicitly translates storage/audit failures: raw DbException mapping
to 500 is not its contract. Preserve already refused/cancelled/uncertain outcomes.

### Exact surface and native association

| Host / operation | Absent credential | Invalid present credential | Valid provider subject |
|---|---|---|---|
| Unknown host | Masked 404 before token work | Same | Same |
| Known, exact POST bootstrap | Masked 404 | 401 | Quota, input gate, global-self owner |
| Known, reserved path/wrong method | Masked 404 | 401 | Masked 404, no binding SQL |
| Ordinary surface | Existing rule | 401 | Binding, then existing tenant reconciliation |

Admit only POST `/api/v1/account/bootstrap`, empty body/no query; return at most
4 KiB JSON `{issuer, subject, actorId}`, no-store, without profile. Malformed
operation input is 400 after credential/admission; existing 429/503 quota
outcomes remain. Preserve ten-second native API response budget, fixed SDK path/
origin, verified host/source, hop secret and omitted browser credentials.

Exact endpoint metadata selects this global operation before ordinary resolution.
It installs no ITenantContext. A new exact `GlobalSelfIdentity` descriptor admits
only its internal self-read/create requests to the global owner; it does not
expand AllowsUnresolvedTenantContext. Reject all other global/unresolved types.
Ordinary resolution stays route-independent. No browser bearer/proxy/Server Action
is added. The canonical pipeline table is in
[Security](../standards/11-security.md#proposed-p03-1-identity-admission-annex).

The new native pre-session projection beside provider-core.ts permits absent
actor/tenant after independent ID verification. Bootstrap uses the same grant's
access token; match returned issuer/sub and any actor claim. AccountSite still
positively witnesses actor/resolved scope before ordinary publication. Tenantless
callback can create User but publishes no tenant session. P03-3 owns usable
discovery/reissue. Refresh never allocates, preserves original claim presence/
values, normalized identity and scope, and retains positive-witness/late-result
fences. ADR-0071 ordinary-read classification is unchanged.

### Authority, retention and concurrency

[Database Standards](../standards/05-database.md#proposed-p03-1-identity-storage-annex)
is the proposed canonical owner of columns, GUCs, digest encoding, grants,
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

Bootstrap locks the subject key and freshly rechecks **before** allocating actor
or declaring MUST intent. Expected concurrent callers return the winner without
failed mutation intents. Uniqueness remains a backstop; genuine failure/unknown
COMMIT reconciles. Every valid provider account can establish membershipless
identity; email verification is not required for that limited act. Provider
registration and HTTP budgets do not cap total new-subject cardinality.

Creation, reactivation and assignment additions take the User fence before the
tenant invariant lock. READ COMMITTED, post-wait fresh VOLATILE reads and
incompatible-isolation refusal apply in database guards, not just handlers.
Unknown and nonadmitting ids produce the same false result. No function projects
a profile. Later closure/status writers share the tenant invariant locks before
making an administrator ineligible; their workflow remains P03-10.

### Audit and owner privilege

PlatformSentinel ceases to identify operator events uniquely. Operation/source
distinguishes platform-scope standalone rows from `identity.user.create` global
MUST SecurityEvent. No tenant setter announces the sentinel. Ordinary audit
methods still reject it; only the named global pending/reconcile seams gain
app-role permission. Existing sentinel announcement guards remain unchanged.

The audit isolation policy explicitly departs from the unmodified template:
retain its tenant/org branch, add a guarded global INSERT branch, and add
owner-qualified evidence-read authority. App SELECT gains no global/self-audit
branch. Fixed evidence functions make reciprocal creation and duplicate checks
possible under FORCE RLS. Function execution under table-owner privilege is a
new capability, despite no migration credential in runtime configuration.

The prior host-lookup definer rejection remains true for its old policies.
Here explicit owner SELECT policies plus enumerated fixed functions are chosen.
No BYPASSRLS, fifth role, PUBLIC execution, dynamic SQL, generic target projection
or mutable search path is allowed. Migrations own installation; runtime call
sites and policy/function bodies have positive and dirty controls.

User and success audit share one commit. Deferred insertion-time reciprocity is
not a permanent cross-module FK blocking audit purge. Central non-success
reconciliation uses the captured subject, fresh standalone timestamp and existing
possible indeterminate pair. Failed evidence needs no User and proves no User
committed. Exact duplicate checking includes outcome; a unique error alone is
insufficient. Before binding, actor is NULL and proposed target is distinct.
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

Both ADRs are accepted together or neither closes gates. The
[revised package](../roadmap/phase-03-delivery-log.md#p03-1-revised-decision-package-2026-10-25)
contains append-ready Amendment drafts and proof owners. Those drafts are not
inserted as Accepted changes before approval.

| Carrier / clause | Named adoption instrument |
|---|---|
| ADR-0003; Standard 05 six classes/unmodified audit/global unique exceptions | Dated bounded Amendment for new class and exact audit/owner-policy departure; ordinary template unchanged |
| ADR-0033 including Amendment 5 | Dated Amendment extending central success/reconcile to the named global population, preserving durability |
| ADR-0036 HTTP resolution/marker boundary | Dated bounded-supersession Amendment for exact global-self surface, no fifth TenantContext writer |
| ADR-0040 owners/nesting/setters | Dated Amendment for subject owners and separately registered candidate reader; no tenant/global join |
| ADR-0044 §§1/2/9/10 | Dated bounded-supersession Amendment for second sentinel population, app INSERT/evidence reads and new central seams |
| ADR-0064 actor/GET-HEAD-only/no-unresolved-endpoint | Dated bounded-supersession Amendment for optional actor/global POST; absent-token 404 preserved |
| ADR-0070 association/publication | Dated bounded-supersession Amendment for pre-session/bootstrap proof; session custody unchanged |
| ADR-0071 | Dated navigation Amendment only; ordinary-read status table unchanged |
| Standards 04/05/07/10/11/18/19/21; Architecture 09/13/23/31/33 | Promote approved Proposed contracts and authority headers; no duplicate SQL |
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
- Durable actors can lack membership/session; provider registration can multiply
  subjects beyond one subject quota.
- Permanent digest/actor and retained failed-attempt keys remain personal data.
  Offline guessing and immutable retained copies are explicit accepted costs.

### Neutral

- No provider SDK, new login, Hub crossing, live permission enforcement or session
  database is introduced. No erasure/production-readiness proof follows.

## Implementation Notes

The revised package owns six steps, exact proof/status and required controls.
These authorities are not implemented by this proposal. Four seed actors,
provider subjects and credentials survive; ordinary HTTP cannot invoke adoption.

## References

- [ADR-0033](0033-audit-durability-model.md), [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
  [ADR-0040](0040-ambient-unit-of-work.md), [ADR-0044](0044-audit-write-path.md),
  [ADR-0064](0064-tenant-api-authentication-boundary.md),
  [ADR-0070](0070-owned-tenant-bff-sessions.md),
  [ADR-0071](0071-uncertain-account-site-refusals.md).
- [ADR-0073](0073-scoped-membership-and-identity-data-foundation.md),
  [Identity spec](../modules/identity/README.md),
  [Data Protection](../architecture/23-data-protection.md).
- [PostgreSQL 18 policies](https://www.postgresql.org/docs/18/sql-createpolicy.html),
  [functions](https://www.postgresql.org/docs/18/sql-createfunction.html),
  [isolation](https://www.postgresql.org/docs/18/transaction-iso.html),
  [function snapshots](https://www.postgresql.org/docs/18/xfunc-volatility.html).
