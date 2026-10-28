# ADR-0079: Bounded Identity Administration

## Status

Proposed — no gate is accepted and no dependent runtime is implemented.
Joint acceptance with ADR-0078 and the P03-3 packet is required.

**Date:** 2026-10-28
**Deciders:** @cemil

**Bounded supersession:** Only the named delegation/bootstrap, seed-repeat and
protected replay rules change. Existing Accepted text and metadata remain;
acceptance adds dated Amendments. This is not a factual erratum.

## Decision Drivers

- Live policy and SQL/EF scope are implemented, but member/profile/Role screens,
  native write transport and Organization lifecycle endpoints are absent.
- Tenant authority must not bypass an exact branch write scope, target User
  admission, last-admin protection or another person's profile ownership.
- New Tenancy keys cannot be granted by a ceiling requiring the actor already
  to hold them. Initial release adoption needs exact bounded authority.
- A successful replay must not disclose a protected outcome to a now-revoked
  caller; the first keyed endpoint triggers the durable adapter now.
- Actual administration must survive a warm demo rerun without silently resetting
  edits or reinstalling revoked authority.

## Considered Options

1. **Finite operations, release adoption and authenticated native forms**
   (proposed). Reuse existing roots, live policy, audit and generated contracts;
   bound every route and preserve resource-level checks.
2. **Generic proxy/Server Actions/raw token client** (rejected). Opens authority
   and transport beyond the enumerated operation and native owner.
3. **Reuse seed commands as human authority** (rejected). Seed admission is a
   different finite source; a user cannot inherit it by routing through a wrapper.
4. **Automatically expand all default Roles** (rejected). Registration is not
   consent to new grants; it also restores legitimate nonkernel removals.
5. **Keep stale-permission replay or volatile outcomes** (rejected here).
   Sensitive administration needs current disclosure authority; the accepted
   durable-store trigger fires before these routes activate.
6. **Preserve strict v1 equality on every warm seed** (not selected). It truthfully
   refuses edited state today, but makes ordinary demo restart fail after the
   management actions this packet exposes. Preserve valid installed state instead.
7. **Unbounded expired rows or privileged purge now** (not selected). The first
   does not bound storage; the second adds an unnecessary Platform prerequisite.
   Reclaim expired physical slots using existing app UPDATE authority.

## Decision

LearnStack exposes an exact P03-3 administration set through ordinary live
HTTP/application/resource policies and response-owned native SDK operations.
Membership retains its contained Profile/Assignments; Role retains its grants;
Tenancy alone owns Organization/default lifecycle. Finite release adoption is
an explicit audited act. Protected writes use durable actor/scope/precondition-
bound idempotency, with fresh disclosure authorization and MUST audit on replay.
Warm seed preserves valid installed authority and edits without repair or grants.

## Context

### Existing subjects and exact scope

Verified at `575d8e32`: Membership lifecycle and last-admin guards exist; profile
and assignment administration does not. Role grant/adoption commands exist but
have no routes. Every current Tenancy writer has Seed-only policy. The native
caller and ingress remain AccountSite/auth-POST only. SDK success assumes 200 and
carries no ETag. None of those declarations implicitly admits a new operation.

| Resource | Admitted operation family | Exclusions |
|---|---|---|
| Membership | Existing detail, profile edit, suspend/reactivate/revoke, exact assignment replacement | Creation and revoked successor remain invitation-owned P03-4; no guessed-UserId addition. |
| Self profile | Exact current Membership read/edit, only DisplayName and PreferredLocale | No NULL-org profile fallback into branch state, email, global User, consent or custom fields. |
| Role | Eight finite template list/detail, grant edit and explicit manifest adoption | No custom Role/key/scope CRUD or per-organization template copy. |
| Organization | List/detail/create/rename/archive and explicit default selection | No nested authority, arbitrary status, host/domain/secret or cross-module cascade. |
| Existing Tenancy settings | Human application admission for AddTenantLocale, SetDefaultTenantLocale, explicit nondefault locale removal/enablement and whole-theme SetTenantBranding | No generic settings route/editor; existing translation rows are preserved. |

The packet owns the exact route/DTO/audit/permission matrix. Organization CRUD
screens and full locale/branding editors remain Phase 06; the API/SDK and identity
scope switcher ship here. Phase 06's Organization editor owns first
reporting-parent authoring; the
existing additive field is not needed by this packet's scope/lifecycle flow and
enters no P03-3 DTO. Acceptance reconciles that explicit owner in code/spec/plan.
Member directory uses bounded paging without search in this packet; this explicitly
narrows the phase criterion's ambiguous paging/search wording, not an existing API.

### Scoped policy and mutation ownership

Finite compiled policy requirements can express exact alternative/conjunctive
pairs; there is no user-authored policy language or global same-key implication.
A declared branch act can accept its Tenant pair from eligible NULL-org authority
or the same Organization pair from the exact branch. Another branch contributes
nothing. A declared Tenant-admin read clause may mint the existing tenant read
authority; an Organization clause stays scoped. No request field or successful
Org clause promotes its SQL/EF scope. Self profile requires profile-write and exact
target.UserId = actor;
another person's profile additionally requires membership-admin. Removing the
nonkernel profile grant remains effective. Orgless acts require orgless session.

HTTP checks coarse operation admission. Application and target checks independently
validate exact scope/ownership/delegation; every post-lock check uses current
committed authority. Each contributor/source and alternative is enumerated.
Existing Seed request policies remain; separately classified human acts share
module business logic without fabricating Seed provenance or sending Seed commands.

Every mutation uses the canonical User-before-Tenant and strongest complete plan.
Creation/reactivation and every assignment addition take the target User fence;
profile-only edits and removals do not automatically take it. Shared/exclusive
mode is derived from actual eligible administrator/Role effects, never a request
Boolean. Reactivation/additions check current User/lifecycle after waiting.
Assignments may be prepared on Active or Suspended Memberships; they grant no live
authority until Active. Revoked is terminal. Scope, User and parent IDs never move.

Assignment delegation checks the target Role's whole current grant set. New grants
must lie within the actor's current ceiling, including only the expressly admitted
same-key Tenant-to-target-Organization delegation. Serialize the actual Role grant
graph against edits with retained Role-root locks, acquired in canonical sorted
order after User/Tenant fences and before Membership-root mutation locks. Role
grant writers acquire the conflicting root lock before changing children. Recheck
actual target/version/complete plan after waits; a changed strongest plan refuses,
never upgrades a shared lock. Preserve deferred graph/last-admin validation and
same-transaction MUST rollback. Existing root boundaries and ADR-0042 stay intact.

### First Tenancy authority and release adoption

Add exactly these assignable **Tenant** pairs:

- `tenancy.organization.admin`: Organization administration and default selection.
- `tenancy.locale.write`: only the named locale acts.
- `tenancy.setting.write`: only the whole `branding.theme` act.

No Organization Tenancy pair or catch-all settings power is introduced. An exact
v2 release delta adds these three pairs to Tenant Admin, preserving prior nonkernel
removals. Keep the existing two-pair admin kernel; profile-write is outside it.
No other template gains grants automatically. Ordinary edits retain their live
ceiling and cannot restore a grant merely because it was once a default.

A separately declared first v1-to-v2 adoption permits only that exact new-key
delta without requiring the actor already to hold the three new keys. It requires
an eligible current Tenant Admin, orgless scope, exact release fingerprint,
expected Role version, exclusive tenant fence, fresh post-wait reauthorization
and MUST audit. Monotonic adopted-release version/fingerprint lives on the same
Role root, with forward migration and no-reset guards. Removing new grants does
not reset adoption or let an administrator repeat the initial exception. New
registrations alone grant nothing. Unknown releases/deltas refuse. Preserve v1
artifacts and generate/equivalence-check the SQL finite registry from its source.

### Tenancy lifecycle

For a completed nondeleted Trial/Active/Suspended tenant, default Organization
belongs to it and is Active/nondeleted. Archive refuses its current default and
refuses leaving zero Active organizations. Default change writes Tenant only;
archive writes Organization only. The person selects another Active default in
one explicit transaction before archiving the old one in another. No automatic
multi-root replacement or new provisioning exception is introduced.

Default selection/archive take the canonical exclusive tenant fence, freshly
read Tenancy facts and reauthorize before mutation. Ordinary branch mutations
retain their shared/exclusive fences through commit. Archive either waits for
already admitted work, or commits first and causes waiting fresh lifecycle checks
to refuse. Same-module invoker guards enforce actual fencing, default eligibility
and legal terminal archive without Identity SQL or another definer function.
Provisioning's sanctioned temporary-NULL/default completion sequence remains.

Archive preserves Organization, Membership/Profile/Assignments, content and host
mapping history; it withdraws ordinary branch work through live lifecycle checks.
There is no per-organization last-admin invariant. The existing globally eligible
NULL-org Tenant Admin invariant stays total. Archived tenant termination/global
closure remain P03-9/10, not a hidden exemption.

Locale removal/disable is included as human application acts now, under
Tenant locale-write and one Tenant root/version. Invalid existing configuration,
missing locale and default removal/disable refuse before stamping; the person
first selects another enabled default. No last-enabled/default loophole is
created. Translation content remains untouched; public readers and profile
preference fallback recheck enabled membership. Full editor/HTTP transport for
these locale acts stays Phase 06, matching the named settings boundary.

### Warm installation and preservation

Fresh seed installs the unchanged finite v1 foundation through existing witnessed
acts; it never automatically adopts v2. A repeat distinguishes initial missing
facts from valid installed mutable state. Exact permanent provider/actor bindings,
finite template identities/scopes, natural keys, lifecycle and database/admin
invariants remain mandatory. Valid profile, status, assignment and adopted-grant
changes are preserved read-only; historical Revoked Membership prevents automatic
successor/rejoin. Retired roots are not recreated. Closed/conflicting bindings or
invalid/unknown authority still refuse.

Only genuinely missing, never-installed facts in a recognized incomplete v1
installation can be initialized from the original declarations and trusted seed
witnesses. No current/historical root may be overwritten, reactivated or granted
because a desired seed manifest differs. Incompatible partial/adopted state refuses
before remaining writes; earlier committed facts stay. Proof must distinguish
fresh completion, interrupted v1 resume, ordinary edited-state no-write repeat,
revocation preservation and malformed/foreign state refusal. This replaces the
old warm exact-v1-equality refusal rule; it is not an implicit repair mode.

### Durable idempotency and protected replay

The first keyed routes activate PostgresIdempotencyStore under ADR-0035/0037's
existing trigger. It uses existing app-role table/RLS and short own transactions,
not ambient business ownership or Platform credentials. Preserve five-minute
fenced claims, 24-hour completed/unreplayable retention, mismatch precedence,
whole-response fidelity and 256 KiB headers-inclusive capture bounds.

Select **1,000 physical retained slots per tenant** as an initial durable budget;
no global cap is inferred from an RLS tenant count. Check an existing requested key
before new-key admission. A short distinct tenant capacity fence serializes new
insertion/expired-slot recycling. At capacity, UPDATE/rekey/reset only a database-
expired row; never evict a live outcome. Old key/token completion cannot overwrite
its replacement. Existing-key service survives saturation. No app DELETE, new
Platform purge or falsely bounded unexpired-only census is introduced. Phase 11
still owns deletion maintenance; expiry reclamation supplies ongoing admission.

The new protected fingerprint is versioned and binds tenant/org, actor, exact
method/path/query/body and one required canonical strong numeric If-Match where
applicable. This bounded set rejects wildcard/list/weak/missing preconditions;
other endpoints keep Standard 04's existing syntax. Stale version remains 409
concurrency_conflict. Organization creation has no If-Match. Fenced store failures
cannot turn an unknown business outcome into asserted rollback or exactly-once.
Business version/natural-key guards remain necessary across the COMMIT-to-outcome
recording crash window. No automatic mutation retry is added.

Mutation success bodies contain only a bounded version receipt, with ETag and
Location for 201 creation. Before completed response replay, separately classified
MUST receipt-read queries recheck the current caller, capability, exact immutable
target relation and self predicate where applicable. They may inspect a terminal
target only to authorize its historical receipt; ordinary directories stay
current-only. Original expected version, lifecycle transition and desired-grant
checks do not rerun: replay delegates nothing and mutates nothing. Fresh success
read audit commits before stored bytes; denial uses central evidence. A failed
replay guard/audit leaves the completed row untouched. Original mutation audit
and exchange headers are never replayed as new evidence.

This explicitly supersedes ADR-0037's accepted stale-handler-authorization replay
for only the packet's protected set. Different actor/scope/body/precondition still
gets key reuse. Each replay has fresh evidence/correlation, with original outcome
headers and `Idempotency-Replayed: true`. Stored responses are not permissions.

### Native/UI execution and failure

The response owner holds credentials and current immutable session context while
invoking only exact generated operations. RSC auth() stays read-only. Exact native
POST routes check captured host/source, Origin and current CSRF before API/body
work; count at most 16 KiB strict JSON/form input before Next dispatch. Native
never forwards arbitrary method/path/headers or a submitted tenant selector.
Admin API/SDK responses have a 256 KiB bound and one ten-second operation deadline
including refresh and body consumption. Retain actual cleanup/epoch checks and
no-store on every response, including early refusals.

Server-rendered forms carry bounded resource/version/nonsecret idempotency nonce;
native constructs headers. Success uses fixed 303 same-host destinations and fresh
reads. Unknown outcome offers deliberate same-key/body retry and lookup; edited
requests receive new keys. Do not store another unbounded server-side form cache.
Sensitive late responses fail publication after scope/logout/replacement.

| Observation | Owner/UI rule |
|---|---|
| Ordinary administration 403/404 | Retain a live session; deny this operation and render localized scoped recovery. These are not AccountSite identity refusals. |
| Ordinary administration 401/availability failure | Withhold result; preserve live ownership without claiming invalid identity; no automatic retry. |
| AccountSite/publication/refresh check | Keep ADR-0071 and ADR-0078's separate retain/refuse/revoke rules. |
| 409 | Preserve edits/key as applicable; distinguish stale version, in-flight request and key reuse. |
| 429/503 | Show bounded retry/recovery; no success or rollback claim. |
| Successful security change | Pessimistic fresh read; no optimistic grants or authority inferred from a hidden button. |

Use plain anchors/native forms and full document navigation, without SPA/TanStack
protected caches or arbitrary Server Actions. Extend protected history, nested
route and ingress censuses together. Protected pages start veiled and revalidate
before history/restoration disclosure; scripts/no-script behavior needs explicit
real browser proof. No-JavaScript recovery remains usable; script-based expiry/
history withholding is not claimed for a browser that cannot execute it.
New en/tr admin/profile namespaces, safe themes and keyboard/focus/form/error/
reflow/contrast/assistive-technology checks ship with the screens. PreferredLocale
is an optional enabled-locale UI preference for that exact Membership, with explicit
site-default fallback; it does not select tenant/org or rewrite content locale.

## Consequences

### Positive

- Member, Role and self-profile administration use existing isolation/audit owners
  with exact capability and target rules, rather than UI-only authorization.
- Explicit release adoption is usable without granting a general self-escalation
  exception. Warm seed does not undo legitimate administration.
- Durable replay survives restart and cannot disclose solely on old authority;
  expired-slot reuse avoids indefinite physical-row growth or permanent saturation.

### Negative

- Durable replay, target guards, adoption markers, lock plans and lifecycle races
  are substantive work; existing unit/schema declarations are insufficient proof.
- Each tenant has a shared 1,000-slot receipt budget; active bursts can refuse new
  operations. Claim leases still permit at-least-once retry after interruption.
- Strict protected preconditions differ from generic wildcard/list behavior.
  New permission adoption is an explicit extra administrator action.
- Valid warm edits stop matching original showcase values by design. Seed proves
  installed invariants, not that the operator left the demonstration unchanged.

### Neutral

- No new application module, database login, Organization cross-module FK,
  provisioning exception, custom Role model or generic settings editor is added.
- Invitation, dynamic fields, federation, operator recovery and DSAR remain
  P03-4/5/8/9/10. Full Studio/browser-matrix expansion remains Phase 06.

## Implementation Notes

The joint packet owns the exact endpoint/native route matrix, preconditions,
finite manifests, expected-source ledger, migration and proof partition.
Acceptance appends bounded dated Amendments to ADR-0037 (protected replay,
fingerprint and expired reclamation), ADR-0073 (finite adoption fields/warm seed),
ADR-0076 (first release adoption/policy conjunctions), and ADR-0053/0054/0070
(exact native operations). ADR-0042/0074/0077 receive navigation only where needed;
root boundaries, ordered admission and evidence durability remain unchanged.
Reconcile Standards 03/04/05/07/11/12/16/18/19/20/21, Architecture 13/23/31/33,
Identity/Tenancy matrices, glossary and affected workflow skills on acceptance.
Reopen before custom Roles, broader settings, implicit grants, another replay
class, live eviction, archived-tenant exemptions or failed concurrency proofs.

## Architecture Tests

Extend nonempty policy/permission/HTTP/native/SDK and audit catalogue censuses.
Real app-role controls cover target self/admin/branch denial, v2 adoption and
repeated-exception refusal, Role-edit/assignment races, User/tenant ordering,
last-admin/default/archive winners, source cancellation and MUST rollback.
Durable store controls include two owners/restart, saturation/existing-key service,
expired-slot races, old-fence completion, precondition fingerprint, interrupted
outcome recording, revoked-caller replay and failed read-audit retention.
Actual native HTTPS/browser proof covers no browser bearer, scoped forms, stale
ETag/replay, session transition and late responses, translated states, history and
manual accessibility. Existing A/B full-source CI qualification remains required;
no new subject qualifies by declaration or a skipped/empty test population.

## References

- [ADR-0078](0078-native-session-scope-and-self-entry.md),
  [Phase 03](../roadmap/phase-03-identity-admin.md),
  [Identity](../modules/identity/README.md),
  [Tenancy](../modules/tenancy/README.md).
- [ADR-0037](0037-idempotency-key-contract.md),
  [ADR-0042](0042-tenant-provisioning-cross-aggregate-transaction.md),
  [ADR-0073](0073-scoped-membership-and-identity-data-foundation.md),
  [ADR-0074](0074-membership-admission-under-ordered-locks.md),
  [ADR-0076](0076-live-scoped-authorization.md),
  [ADR-0077](0077-trusted-authorization-evidence.md).
- [API](../standards/04-api-design.md),
  [Database](../standards/05-database.md),
  [Permissions](../standards/19-permissions.md),
  [Audit](../standards/18-audit-coverage.md),
  [Frontend](../standards/07-frontend-architecture.md),
  [Accessibility](../standards/16-accessibility.md).
